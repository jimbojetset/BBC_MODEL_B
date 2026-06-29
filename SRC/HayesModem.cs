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
        private const int DefaultEscapeCharacter = '+';
        private const int DefaultEscapeGuardFiftieths = 50;
        private const int ActivityLedMilliseconds = 120;
        private const int SerialBaud = 9600;
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
        private bool resultCodesEnabled = true;
        private bool verboseResultCodes = true;
        private int speakerMode = 1;
        private int carrierDetectMode;
        private int dtrMode;
        private int flowControlMode;
        private int asyncMode;
        private int dataSetReadyMode;
        private int autoAnswerRings;
        private int escapeCharacter = DefaultEscapeCharacter;
        private int escapeGuardFiftieths = DefaultEscapeGuardFiftieths;

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

        private int EscapeGuardMilliseconds => Math.Max(0, escapeGuardFiftieths * 20);

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
            int index = 0;
            while (index < body.Length)
            {
                SkipWhiteSpace(body, ref index);
                if (index >= body.Length)
                    break;

                char command = char.ToUpperInvariant(body[index]);
                if (command == 'D')
                {
                    if (!TryGetDialTarget(body[index..], out string target))
                    {
                        Respond("ERROR");
                        return;
                    }

                    Dial(target);
                    return;
                }

                index++;
                if (command == '&')
                {
                    if (!HandleAmpersandCommand(body, ref index))
                    {
                        Respond("ERROR");
                        return;
                    }

                    continue;
                }

                if (command == 'S')
                {
                    if (!HandleSRegisterCommand(body, ref index))
                    {
                        Respond("ERROR");
                        return;
                    }

                    continue;
                }

                if (!HandleBasicCommand(command, body, ref index))
                {
                    Respond("ERROR");
                    return;
                }

                if (command == 'O')
                    return;
            }

            Respond("OK");
        }

        private bool HandleBasicCommand(char command, string body, ref int index)
        {
            switch (command)
            {
                case 'Z':
                    ResetConfiguration();
                    Disconnect(reportNoCarrier: false);
                    return true;

                case 'H':
                    if (!TryReadOptionalNumber(body, ref index, out int hookMode))
                        return false;

                    if (hookMode != 0)
                        return false;

                    Disconnect(reportNoCarrier: false);
                    return true;

                case 'O':
                    if (!TryReadOptionalNumber(body, ref index, out int onlineMode))
                        return false;

                    if (onlineMode != 0)
                        return false;

                    if (tcpClient?.Connected == true)
                    {
                        Volatile.Write(ref online, 1);
                        Respond("CONNECT");
                    }
                    else
                    {
                        Respond("NO CARRIER");
                    }

                    return true;

                case 'E':
                    if (!TryReadOptionalNumber(body, ref index, out int echoMode))
                        return false;

                    if (echoMode is not 0 and not 1)
                        return false;

                    commandEcho = echoMode == 1;
                    return true;

                case 'Q':
                    if (!TryReadOptionalNumber(body, ref index, out int quietMode))
                        return false;

                    if (quietMode is not 0 and not 1)
                        return false;

                    resultCodesEnabled = quietMode == 0;
                    return true;

                case 'V':
                    if (!TryReadOptionalNumber(body, ref index, out int verboseMode))
                        return false;

                    if (verboseMode is not 0 and not 1)
                        return false;

                    verboseResultCodes = verboseMode == 1;
                    return true;

                case 'M':
                    if (!TryReadOptionalNumber(body, ref index, out int mode))
                        return false;

                    if (mode is < 0 or > 2)
                        return false;

                    speakerMode = mode;
                    return true;

                case 'I':
                    if (!TryReadOptionalNumber(body, ref index, out int infoPage))
                        return false;

                    if (infoPage != 0)
                        return false;

                    RespondInfo("BBC MODEL B HAYES MODEM");
                    return true;

                default:
                    return false;
            }
        }

        private bool HandleAmpersandCommand(string body, ref int index)
        {
            if (index >= body.Length)
                return false;

            char command = char.ToUpperInvariant(body[index++]);
            switch (command)
            {
                case 'F':
                    if (!TryReadOptionalNumber(body, ref index, out int factoryProfile))
                        return false;

                    if (factoryProfile != 0)
                        return false;

                    ResetConfiguration();
                    return true;

                case 'V':
                    RespondInfo($"E{(commandEcho ? 1 : 0)} Q{(resultCodesEnabled ? 0 : 1)} V{(verboseResultCodes ? 1 : 0)} M{speakerMode}");
                    RespondInfo($"S0={autoAnswerRings} S2={escapeCharacter} S12={escapeGuardFiftieths}");
                    RespondInfo($"&C{carrierDetectMode} &D{dtrMode} &K{flowControlMode} &Q{asyncMode} &S{dataSetReadyMode}");
                    return true;

                case 'C':
                    return TrySetAmpersandMode(body, ref index, 0, 1, value =>
                    {
                        carrierDetectMode = value;
                        UpdateCarrierPresent();
                    });

                case 'D':
                    return TrySetAmpersandMode(body, ref index, 0, 2, value => dtrMode = value);

                case 'K':
                    return TrySetAmpersandMode(body, ref index, 0, 3, value => flowControlMode = value);

                case 'Q':
                    return TrySetAmpersandMode(body, ref index, 0, 0, value => asyncMode = value);

                case 'S':
                    return TrySetAmpersandMode(body, ref index, 0, 1, value => dataSetReadyMode = value);

                default:
                    return false;
            }
        }

        private bool HandleSRegisterCommand(string body, ref int index)
        {
            if (!TryReadRequiredNumber(body, ref index, out int register))
                return false;

            if (index < body.Length && body[index] == '?')
            {
                index++;
                if (!TryGetSRegister(register, out int value))
                    return false;

                RespondInfo(value.ToString());
                return true;
            }

            if (index < body.Length && body[index] == '=')
            {
                index++;
                if (!TryReadRequiredNumber(body, ref index, out int value))
                    return false;

                return TrySetSRegister(register, value);
            }

            return false;
        }

        private bool TrySetSRegister(int register, int value)
        {
            switch (register)
            {
                case 0 when value is >= 0 and <= 255:
                    autoAnswerRings = value;
                    return true;

                case 2 when value is >= 0 and <= 127:
                    escapeCharacter = value;
                    return true;

                case 12 when value is >= 0 and <= 255:
                    escapeGuardFiftieths = value;
                    return true;

                default:
                    return false;
            }
        }

        private bool TryGetSRegister(int register, out int value)
        {
            switch (register)
            {
                case 0:
                    value = autoAnswerRings;
                    return true;

                case 2:
                    value = escapeCharacter;
                    return true;

                case 12:
                    value = escapeGuardFiftieths;
                    return true;

                default:
                    value = 0;
                    return false;
            }
        }

        private bool TrySetAmpersandMode(string body, ref int index, int minimum, int maximum, Action<int> apply)
        {
            if (!TryReadOptionalNumber(body, ref index, out int value))
                return false;

            if (value < minimum || value > maximum)
                return false;

            apply(value);
            return true;
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
                UpdateCarrierPresent();
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

            if (value == (byte)escapeCharacter)
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

            networkStream?.Dispose();
            tcpClient?.Close();

            networkStream = null;
            tcpClient = null;
            UpdateCarrierPresent();

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
            ResetConfiguration();
            escapePlusCount = 0;
            telnetState = 0;
            telnetCommand = 0;
            Volatile.Write(ref receiveDataLedUntilTicks, 0);
            Volatile.Write(ref sendDataLedUntilTicks, 0);
        }

        private void ResetConfiguration()
        {
            commandEcho = false;
            resultCodesEnabled = true;
            verboseResultCodes = true;
            speakerMode = 1;
            carrierDetectMode = 0;
            dtrMode = 0;
            flowControlMode = 0;
            asyncMode = 0;
            dataSetReadyMode = 0;
            autoAnswerRings = 0;
            escapeCharacter = DefaultEscapeCharacter;
            escapeGuardFiftieths = DefaultEscapeGuardFiftieths;
            UpdateCarrierPresent();
        }

        private void UpdateCarrierPresent()
        {
            serialAcia.SetCarrierPresent(carrierDetectMode == 0 || tcpClient is not null);
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

        private static void SkipWhiteSpace(string value, ref int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;
        }

        private static bool TryReadOptionalNumber(string value, ref int index, out int number)
        {
            if (index >= value.Length || !char.IsDigit(value[index]))
            {
                number = 0;
                return true;
            }

            return TryReadRequiredNumber(value, ref index, out number);
        }

        private static bool TryReadRequiredNumber(string value, ref int index, out int number)
        {
            number = 0;
            int start = index;
            while (index < value.Length && char.IsDigit(value[index]))
            {
                int digit = value[index] - '0';
                if (number > (int.MaxValue - digit) / 10)
                    return false;

                number = (number * 10) + digit;
                index++;
            }

            return index > start;
        }

        private static bool TryParseDialTarget(string target, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultTelnetPort;

            string value = target.Trim();
            if (value.Length == 0)
                return false;
            if (ContainsWhiteSpace(value))
                return false;

            int colon = value.LastIndexOf(':');
            if (colon > 0 && colon < value.Length - 1 && int.TryParse(value[(colon + 1)..], out int parsedPort))
            {
                host = value[..colon];
                port = parsedPort;
                return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
            }

            host = value;
            return !string.IsNullOrWhiteSpace(host);
        }

        private static bool ContainsWhiteSpace(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]))
                    return true;
            }

            return false;
        }

        private static bool TryGetDialTarget(string body, out string target)
        {
            target = string.Empty;

            if (body.Length < 2 || body[0] is not ('D' or 'd'))
                return false;

            if (body[1] is not ('T' or 't' or 'P' or 'p'))
                return false;

            target = body[2..];
            return target.Length > 0;
        }

        private void Respond(string text)
        {
            if (!resultCodesEnabled)
            {
                Trace($"< suppressed {text}");
                return;
            }

            string response = verboseResultCodes ? text : ToNumericResultCode(text);
            QueueResponseLine(response);
            Trace($"< {response}");
        }

        private void RespondInfo(string text)
        {
            QueueResponseLine(text);
            Trace($"< {text}");
        }

        private void QueueResponseLine(string text)
        {
            QueueSerialText($"\r\n{text}\r\n");
        }

        private static string ToNumericResultCode(string text)
        {
            if (string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase))
                return "0";
            if (text.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
                return "1";
            if (string.Equals(text, "NO CARRIER", StringComparison.OrdinalIgnoreCase))
                return "3";
            if (string.Equals(text, "ERROR", StringComparison.OrdinalIgnoreCase))
                return "4";

            return text;
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
            int spins = 0;
            while (Volatile.Read(ref disposed) == 0)
            {
                if (serialAcia.RequestToSend
                    && serialAcia.CanReceiveByte
                    && SerialConfigMatches())
                {
                    return true;
                }

                if (++spins % 256 == 0)
                    Thread.Yield();
                else
                    Thread.SpinWait(64);
            }

            return false;
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
