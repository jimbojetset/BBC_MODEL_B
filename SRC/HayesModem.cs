// ============================================================================
// Project:     BBC
// File:        HayesModem.cs
// Description: Functional Hayes-compatible modem attached to the BBC RS423 ACIA.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Text;
using System.Diagnostics;
using System.Net.Sockets;

namespace BBC
{

    public sealed class HayesModem : IDisposable
    {
        private const int DefaultTelnetPort = 23;
        private const int ConnectTimeoutMilliseconds = 5000;
        private const int EscapeGuardMilliseconds = 1000;
        private const int ActivityLedMilliseconds = 120;
        private const int SerialBaud = 2400;
        private const int RequiredDataBits = 8;
        private const string RequiredParity = "None";
        private const byte TelnetIac = 255;
        private const byte TelnetDont = 254;
        private const byte TelnetDo = 253;
        private const byte TelnetWont = 252;
        private const byte TelnetWill = 251;
        private const byte TelnetSubnegotiation = 250;
        private const byte TelnetSubnegotiationEnd = 240;

        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE") == "1";

        private readonly SerialACIA serialAcia;
        private readonly StringBuilder commandLine = new StringBuilder();
        private readonly byte[] networkReadBuffer = new byte[1024];
        private readonly object serialOutputSync = new object();
        private readonly Queue<byte> serialOutputBytes = new Queue<byte>();
        private readonly Thread serialOutputThread;
        private TcpClient? tcpClient;
        private NetworkStream? networkStream;
        private Thread? receiveThread;
        private int online;
        private int closing;
        private int disposed;
        private int loopbackEnabled;
        private long lastTransmitTicks;
        private long receiveDataLedUntilTicks;
        private long sendDataLedUntilTicks;
        private long escapeFirstPlusTicks;
        private int escapePlusCount;
        private int telnetState;
        private byte telnetCommand;
        private bool commandEcho;

        public HayesModem(SerialACIA serialAcia)
        {
            this.serialAcia = serialAcia;
            this.serialAcia.SetCarrierPresent(true);
            serialOutputThread = new Thread(DrainSerialOutput)
            {
                IsBackground = true,
                Name = "BBC Hayes modem serial output"
            };
            serialOutputThread.Start();
        }

        public bool CommandEchoEnabled => commandEcho;

        public bool Online => Volatile.Read(ref online) != 0;

        public bool LoopbackEnabled
        {
            get => Volatile.Read(ref loopbackEnabled) != 0;
            set => Volatile.Write(ref loopbackEnabled, value ? 1 : 0);
        }

        public HayesModemLedState GetLedState(long nowTicks)
        {
            bool connected = tcpClient is not null;
            return new HayesModemLedState(
                AutoAnswer: false,
                CarrierDetect: connected,
                OffHook: connected,
                ReceiveData: nowTicks < Volatile.Read(ref receiveDataLedUntilTicks),
                SendData: nowTicks < Volatile.Read(ref sendDataLedUntilTicks),
                TerminalReady: serialAcia.RequestToSend,
                ModemReady: Volatile.Read(ref disposed) == 0);
        }

        public void Receive(byte value)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            if (LoopbackEnabled)
            {
                SetSendDataActive();
                QueueSerialByte(value);
                return;
            }

            if (value == 0x00)
                return;

            if (!SerialConfigMatches())
            {
                Trace($"ignored ${value:X2}; BBC serial is {serialAcia.TransmitBaudRate}/{serialAcia.ReceiveBaudRate} {serialAcia.FormatName}");
                return;
            }

            SetSendDataActive();

            if (Online)
            {
                HandleOnlineByte(value);
                return;
            }

            if (commandEcho && IsCommandEchoByte(value))
                QueueSerialByte(value);

            if (value is 0x0D or 0x0A)
            {
                if (commandLine.Length > 0)
                    HandleCommandLine(commandLine.ToString());

                commandLine.Clear();
                return;
            }

            if (value == 0x08 || value == 0x7F)
            {
                if (commandLine.Length > 0)
                    commandLine.Length--;
                return;
            }

            if (value is >= 0x20 and <= 0x7E && commandLine.Length < 240)
                commandLine.Append((char)value);
        }

        private static bool IsCommandEchoByte(byte value)
        {
            return value is 0x0D or 0x0A or 0x08 or 0x7F
                || value is >= 0x20 and <= 0x7E;
        }

        private void HandleCommandLine(string line)
        {
            string command = line.Trim();
            if (command.Length == 0)
                return;

            Trace($"> {command}");

            if (!command.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            {
                Respond("ERROR");
                return;
            }

            string body = command.Length == 2 ? string.Empty : command[2..].Trim();
            if (body.Length == 0)
            {
                Respond("OK");
                return;
            }

            HandleCommandBody(body);
        }

        private void HandleCommandBody(string body)
        {
            string upper = body.ToUpperInvariant();

            if (upper is "Z" or "H" or "H0")
            {
                if (upper == "Z")
                    commandEcho = false;
                Disconnect(reportNoCarrier: false);
                Respond("OK");
                return;
            }

            if (upper is "O" or "O0")
            {
                if (tcpClient?.Connected == true)
                {
                    Volatile.Write(ref online, 1);
                    Respond("CONNECT");
                }
                else
                {
                    Respond("NO CARRIER");
                }

                return;
            }

            if (upper is "E0" or "E")
            {
                commandEcho = false;
                Respond("OK");
                return;
            }

            if (upper == "E1")
            {
                commandEcho = true;
                Respond("OK");
                return;
            }

            if (upper == "I")
            {
                Respond("BBC MODEL B HAYES MODEM");
                Respond("OK");
                return;
            }

            if (upper.StartsWith("D", StringComparison.Ordinal))
            {
                Dial(body[1..].Trim());
                return;
            }

            Respond("ERROR");
        }

        private void Dial(string target)
        {
            Disconnect(reportNoCarrier: false);

            if (!TryParseDialTarget(target, out string host, out int port))
            {
                Respond("NO CARRIER");
                return;
            }

            try
            {
                TcpClient client = new TcpClient();
                IAsyncResult connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds))
                {
                    client.Close();
                    Respond("NO CARRIER");
                    return;
                }

                client.EndConnect(connect);
                tcpClient = client;
                networkStream = client.GetStream();
                Volatile.Write(ref online, 1);
                Volatile.Write(ref closing, 0);
                serialAcia.SetCarrierPresent(true);
                receiveThread = new Thread(ReadNetwork)
                {
                    IsBackground = true,
                    Name = "BBC Hayes modem TCP receive"
                };
                receiveThread.Start();
                Respond($"CONNECT {host} {port}");
                Trace($"connected {host}:{port}");
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                Disconnect(reportNoCarrier: false);
                Respond("NO CARRIER");
                Trace($"connect failed {host}:{port}: {ex.Message}");
            }
        }

        private void HandleOnlineByte(byte value)
        {
            long now = Environment.TickCount64;

            if (value == (byte)'+')
            {
                if (escapePlusCount == 0)
                {
                    if (now - lastTransmitTicks >= EscapeGuardMilliseconds)
                    {
                        escapeFirstPlusTicks = now;
                        escapePlusCount = 1;
                        return;
                    }
                }
                else if (now - escapeFirstPlusTicks <= EscapeGuardMilliseconds)
                {
                    escapePlusCount++;
                    if (escapePlusCount == 3)
                    {
                        Volatile.Write(ref online, 0);
                        escapePlusCount = 0;
                        Respond("OK");
                        Trace("escaped to command mode");
                    }
                    return;
                }
            }

            FlushPendingEscape();
            SendNetworkByte(value);
            lastTransmitTicks = now;
        }

        private void SendNetworkByte(byte value)
        {
            try
            {
                networkStream?.WriteByte(value);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Disconnect(reportNoCarrier: true);
            }
        }

        private void FlushPendingEscape()
        {
            if (escapePlusCount == 0)
                return;

            for (int i = 0; i < escapePlusCount; i++)
                SendNetworkByte((byte)'+');

            escapePlusCount = 0;
        }

        private void ReadNetwork()
        {
            try
            {
                while (Volatile.Read(ref closing) == 0)
                {
                    NetworkStream? stream = networkStream;
                    if (stream is null)
                        break;

                    int read = stream.Read(networkReadBuffer, 0, networkReadBuffer.Length);
                    if (read <= 0)
                        break;

                    for (int i = 0; i < read; i++)
                        HandleNetworkByte(networkReadBuffer[i]);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                if (Volatile.Read(ref closing) == 0)
                    Disconnect(reportNoCarrier: true);
            }
        }

        private void Disconnect(bool reportNoCarrier)
        {
            bool wasOnline = Online || tcpClient is not null;
            Volatile.Write(ref online, 0);
            Volatile.Write(ref closing, 1);
            escapePlusCount = 0;
            ClearSerialOutput();
            serialAcia.SetCarrierPresent(true);

            networkStream?.Dispose();
            tcpClient?.Close();

            networkStream = null;
            tcpClient = null;

            if (reportNoCarrier && wasOnline)
                Respond("NO CARRIER");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            Disconnect(reportNoCarrier: false);

            lock (serialOutputSync)
                Monitor.PulseAll(serialOutputSync);

            if (receiveThread is not null && receiveThread.IsAlive)
                receiveThread.Join(TimeSpan.FromSeconds(2));

            if (serialOutputThread.IsAlive)
                serialOutputThread.Join(TimeSpan.FromSeconds(2));
        }

        public void Reset()
        {
            LoopbackEnabled = false;
            Disconnect(reportNoCarrier: false);
            commandLine.Clear();
            commandEcho = false;
            escapePlusCount = 0;
            telnetState = 0;
            telnetCommand = 0;
            Volatile.Write(ref receiveDataLedUntilTicks, 0);
            Volatile.Write(ref sendDataLedUntilTicks, 0);
            serialAcia.SetCarrierPresent(true);
        }

        private void HandleNetworkByte(byte value)
        {
            switch (telnetState)
            {
                case 0:
                    if (value == TelnetIac)
                    {
                        telnetState = 1;
                        return;
                    }

                    QueueSerialByte(value);
                    return;

                case 1:
                    if (value == TelnetIac)
                    {
                        QueueSerialByte(TelnetIac);
                        telnetState = 0;
                        return;
                    }

                    if (value is TelnetDo or TelnetDont or TelnetWill or TelnetWont)
                    {
                        telnetCommand = value;
                        telnetState = 2;
                        return;
                    }

                    if (value == TelnetSubnegotiation)
                    {
                        telnetState = 3;
                        return;
                    }

                    telnetState = 0;
                    return;

                case 2:
                    ReplyToTelnetOption(telnetCommand, value);
                    telnetState = 0;
                    return;

                case 3:
                    if (value == TelnetIac)
                        telnetState = 4;
                    return;

                case 4:
                    telnetState = value == TelnetSubnegotiationEnd ? 0 : 3;
                    return;
            }
        }

        private void ReplyToTelnetOption(byte command, byte option)
        {
            byte reply = command switch
            {
                TelnetDo => TelnetWont,
                TelnetWill => TelnetDont,
                TelnetDont => TelnetWont,
                TelnetWont => TelnetDont,
                _ => 0
            };

            if (reply == 0)
                return;

            try
            {
                NetworkStream? stream = networkStream;
                if (stream is not null)
                {
                    stream.WriteByte(TelnetIac);
                    stream.WriteByte(reply);
                    stream.WriteByte(option);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Disconnect(reportNoCarrier: true);
            }

            Trace($"telnet {(char)command} option {option} -> {(char)reply}");
        }

        private static bool TryParseDialTarget(string target, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultTelnetPort;

            string value = target.Trim();
            if (value.Length == 0)
                return false;

            if (value[0] is 'T' or 't' or 'P' or 'p')
                value = value[1..].TrimStart();

            value = value.Replace(',', ' ');
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out int parsedPort))
            {
                host = parts[0];
                port = parsedPort;
                return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
            }

            int colon = value.LastIndexOf(':');
            if (colon > 0 && colon < value.Length - 1 && int.TryParse(value[(colon + 1)..], out parsedPort))
            {
                host = value[..colon];
                port = parsedPort;
                return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
            }

            host = value;
            return !string.IsNullOrWhiteSpace(host);
        }

        private void Respond(string text)
        {
            QueueSerialText($"\r\n{text}\r\n");
            Trace($"< {text}");
        }

        private void QueueSerialText(string text)
        {
            foreach (char c in text)
                QueueSerialByte((byte)(c & 0x7F));
        }

        private void QueueSerialByte(byte value)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            lock (serialOutputSync)
            {
                serialOutputBytes.Enqueue(value);
                Monitor.Pulse(serialOutputSync);
            }
        }

        private void ClearSerialOutput()
        {
            lock (serialOutputSync)
                serialOutputBytes.Clear();
        }

        private void DrainSerialOutput()
        {
            long nextByteTicks = Stopwatch.GetTimestamp();

            while (Volatile.Read(ref disposed) == 0)
            {
                byte value;
                lock (serialOutputSync)
                {
                    while (serialOutputBytes.Count == 0 && Volatile.Read(ref disposed) == 0)
                        Monitor.Wait(serialOutputSync);

                    if (Volatile.Read(ref disposed) != 0)
                        return;

                    value = serialOutputBytes.Dequeue();
                }

                WaitUntil(nextByteTicks);
                if (!WaitForReadyToSend())
                    return;

                SetReceiveDataActive();
                serialAcia.QueueReceivedByte(value);
                nextByteTicks = Stopwatch.GetTimestamp() + ModemToBbcCharacterTicks();
            }
        }

        private bool WaitForReadyToSend()
        {
            while (Volatile.Read(ref disposed) == 0
                && (!serialAcia.RequestToSend || !serialAcia.CanReceiveByte || !SerialConfigMatches()))
            {
                Thread.Sleep(1);
            }

            return Volatile.Read(ref disposed) == 0;
        }

        private bool SerialConfigMatches()
        {
            return serialAcia.TransmitBaudRate == SerialBaud
                && serialAcia.ReceiveBaudRate == SerialBaud
                && serialAcia.DataBits == RequiredDataBits
                && serialAcia.StopBits is 1 or 2
                && serialAcia.Parity == RequiredParity;
        }

        private long ModemToBbcCharacterTicks()
        {
            int bits = 1 + RequiredDataBits + serialAcia.StopBits;
            return Math.Max(1, Stopwatch.Frequency * bits / SerialBaud);
        }

        private void SetReceiveDataActive()
        {
            Interlocked.Exchange(
                ref receiveDataLedUntilTicks,
                Stopwatch.GetTimestamp() + (ActivityLedMilliseconds * Stopwatch.Frequency / 1000));
        }

        private void SetSendDataActive()
        {
            Interlocked.Exchange(
                ref sendDataLedUntilTicks,
                Stopwatch.GetTimestamp() + (ActivityLedMilliseconds * Stopwatch.Frequency / 1000));
        }

        private static void WaitUntil(long targetTicks)
        {
            while (true)
            {
                long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                    return;

                double remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
                if (remainingMilliseconds > 2.0)
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(64);
            }
        }

        private static void Trace(string message)
        {
            if (TraceEnabled)
                Console.WriteLine($"[hayes] {message}");
        }

        public readonly record struct HayesModemLedState(
            bool AutoAnswer,
            bool CarrierDetect,
            bool OffHook,
            bool ReceiveData,
            bool SendData,
            bool TerminalReady,
            bool ModemReady);
    }
}
