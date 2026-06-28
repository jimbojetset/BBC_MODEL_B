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

    public sealed class HayesModem
    {
        private const int DefaultTelnetPort = 23;
        private const int ConnectTimeoutMilliseconds = 5000;
        private const int EscapeGuardMilliseconds = 1000;
        private const int SerialBaud = 2400;
        private const int BitsPerSerialCharacter = 10;

        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE") == "1";

        private readonly SerialACIA serialAcia;
        private readonly StringBuilder commandLine = new StringBuilder();
        private readonly byte[] networkReadBuffer = new byte[1024];
        private readonly object serialOutputSync = new object();
        private readonly Queue<byte> serialOutputBytes = new Queue<byte>();
        private readonly long serialByteTicks = Stopwatch.Frequency * BitsPerSerialCharacter / SerialBaud;
        private TcpClient? tcpClient;
        private NetworkStream? networkStream;
        private Thread? receiveThread;
        private int online;
        private int closing;
        private long lastTransmitTicks;
        private long escapeFirstPlusTicks;
        private int escapePlusCount;
        private bool commandEcho = true;

        public HayesModem(SerialACIA serialAcia)
        {
            this.serialAcia = serialAcia;
            this.serialAcia.SetCarrierPresent(true);
            Thread serialOutputThread = new Thread(DrainSerialOutput)
            {
                IsBackground = true,
                Name = "BBC Hayes modem serial output"
            };
            serialOutputThread.Start();
        }

        public bool CommandEchoEnabled => commandEcho;

        public bool Online => Volatile.Read(ref online) != 0;

        public void Receive(byte value)
        {
            if (value == 0x00)
                return;

            if (Online)
            {
                HandleOnlineByte(value);
                return;
            }

            if (commandEcho)
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
                    commandEcho = true;
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
                        QueueSerialByte(networkReadBuffer[i]);
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

            while (true)
            {
                byte value;
                lock (serialOutputSync)
                {
                    while (serialOutputBytes.Count == 0)
                        Monitor.Wait(serialOutputSync);

                    value = serialOutputBytes.Dequeue();
                }

                WaitUntil(nextByteTicks);
                serialAcia.QueueReceivedByte(value);
                nextByteTicks = Stopwatch.GetTimestamp() + serialByteTicks;
            }
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
    }
}
