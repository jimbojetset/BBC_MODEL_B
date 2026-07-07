// ============================================================================
// Project:     BBC
// File:        SerialACIA.cs
// Description: BBC cassette/RS423 6850 ACIA and Serial ULA state.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Diagnostics;

namespace BBC
{

    public sealed class SerialACIA
    {
        private const ushort AciaStart = 0xFE08;
        private const ushort AciaEnd = 0xFE0B;
        private const ushort SerialUlaStart = 0xFE10;
        private const ushort SerialUlaEnd = 0xFE17;

        private const byte AciaStatusReceiveDataFull = 0x01;
        private const byte AciaStatusTransmitDataEmpty = 0x02;
        private const byte AciaStatusDataCarrierDetect = 0x04;
        private const byte AciaStatusClearToSendInactive = 0x08;
        private const byte AciaStatusInterruptRequest = 0x80;

        private static readonly int[] SerialUlaBaudRates = { 19200, 9600, 4800, 2400, 1200, 300, 150, 75 };
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE") == "1";
        public static readonly string TracePath = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE_FILE") ?? "bbc-serial-trace.log";
        private static readonly bool LoopbackEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_LOOPBACK") == "1";
        private static readonly object TraceSync = new object();
        private static bool traceStarted;

        private readonly object sync = new object();
        private readonly Queue<byte> receiveBytes = new Queue<byte>();
        private readonly Queue<byte> transmitBytes = new Queue<byte>();
        private readonly Dictionary<ushort, byte> lastTracedReads = new Dictionary<ushort, byte>();
        private byte aciaControl;
        private byte serialUlaControl;
        private long transmitReadyAtTicks;
        private bool transmitInterruptEnabled;
        private bool tapeReadRequested;
        private bool dataCarrierDetectLatched;
        private bool receiveStatusOverflowPending;

        public bool MotorRunning { get; private set; }

        public bool TapePlaying { get; private set; }

        public bool TapeReadRequested
        {
            get
            {
                lock (sync)
                    return tapeReadRequested;
            }
        }

        public bool CarrierPresent { get; private set; } = true;

        public bool ClearToSend { get; private set; } = true;

        public bool RequestToSend { get; private set; } = true;

        public bool TransmitBreak { get; private set; }

        public int ReceiveBaudRate { get; private set; } = 9600;

        public int TransmitBaudRate { get; private set; } = 9600;

        public int DataBits { get; private set; } = 8;

        public int StopBits { get; private set; } = 1;

        public string Parity { get; private set; } = "None";

        public string FormatName => $"{DataBits}{Parity[0]}{StopBits}";

        public bool IrqAsserted { get; private set; }

        public bool CanReceiveByte
        {
            get
            {
                lock (sync)
                    return receiveBytes.Count == 0;
            }
        }

        public event Action<byte>? ByteTransmitted;

        public event Action<bool>? MotorChanged;

        public event Action<bool>? IrqChanged;

        public event Action? ByteReceived;

        public static bool IsAddress(ushort address)
        {
            return address is >= AciaStart and <= AciaEnd
                || address is >= SerialUlaStart and <= SerialUlaEnd;
        }

        public byte Read(ushort address)
        {
            if (address is >= AciaStart and <= AciaEnd)
            {
                byte value = (address & 1) == 0
                    ? ReadAciaStatus()
                    : ReadAciaData();
                lock (sync)
                    tapeReadRequested = true;
                TraceRead(address, value);
                return value;
            }

            TraceRead(address, serialUlaControl);
            return serialUlaControl;
        }

        public void Write(ushort address, byte value)
        {
            if (address is >= AciaStart and <= AciaEnd)
            {
                if ((address & 1) == 0)
                    WriteAciaControl(value);
                else
                    WriteAciaData(value);

                return;
            }

            bool previousMotorRunning = MotorRunning;
            serialUlaControl = value;
            DecodeSerialUla(value);

            // The BBC Serial ULA also handles cassette routing and motor state.
            // Bit 7 is the observable motor control used by the front-panel LED.
            MotorRunning = (value & 0x80) != 0;
            if (MotorRunning != previousMotorRunning)
                MotorChanged?.Invoke(MotorRunning);
            Trace($"serial ULA <= ${value:X2} RX {ReceiveBaudRate} TX {TransmitBaudRate}");
        }

        public void QueueReceivedByte(byte value)
        {
            QueueReceivedByte(value, latchDataCarrierDetect: false);
        }

        public void QueueTapeByte(byte value)
        {
            QueueReceivedByte(value, latchDataCarrierDetect: false);
        }

        private void QueueReceivedByte(byte value, bool latchDataCarrierDetect)
        {
            byte received = FormatReceivedByte(value);
            lock (sync)
            {
                receiveBytes.Enqueue(received);
                if (latchDataCarrierDetect)
                    dataCarrierDetectLatched = true;
                receiveStatusOverflowPending = true;
            }
            UpdateInterruptLine();
            Trace($"rx <= ${received:X2}");
        }

        public void QueueReceivedText(string text)
        {
            foreach (char c in text)
                QueueReceivedByte((byte)(c & 0x7F));
        }

        public bool TryDequeueTransmittedByte(out byte value)
        {
            lock (sync)
                return transmitBytes.TryDequeue(out value);
        }

        public bool TryDequeueReceivedByte(out byte value)
        {
            lock (sync)
            {
                return receiveBytes.TryDequeue(out value);
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                receiveBytes.Clear();
                transmitBytes.Clear();
                aciaControl = 0;
                serialUlaControl = 0;
                transmitReadyAtTicks = 0;
                tapeReadRequested = false;
                dataCarrierDetectLatched = false;
                receiveStatusOverflowPending = false;
                lastTracedReads.Clear();
            }

            SetMotorRunning(false);
            TapePlaying = false;
            SetInterruptLine(false);
        }

        public void SetCarrierPresent(bool present)
        {
            CarrierPresent = present;
            if (!present)
                dataCarrierDetectLatched = true;
            UpdateInterruptLine();
        }

        public void PulseTapeCarrierDetect()
        {
            lock (sync)
            {
                CarrierPresent = true;
                dataCarrierDetectLatched = true;
            }

            UpdateInterruptLine();
            ByteReceived?.Invoke();
        }

        public void SetClearToSend(bool clear)
        {
            ClearToSend = clear;
            UpdateInterruptLine();
        }

        public void StopTape()
        {
            TapePlaying = false;
            SetMotorRunning(false);
            SetCarrierPresent(true);
            ClearTapeReadRequest();
        }

        public void SetTapePlaying(bool playing)
        {
            TapePlaying = playing;
        }

        public void ClearTapeReadRequest()
        {
            lock (sync)
                tapeReadRequested = false;
        }

        public void ClearTapeCarrierDetect()
        {
            lock (sync)
            {
                CarrierPresent = true;
                dataCarrierDetectLatched = false;
            }

            UpdateInterruptLine();
        }

        public void SaveState(BinaryWriter writer)
        {
            lock (sync)
            {
                writer.Write(aciaControl);
                writer.Write(serialUlaControl);
                writer.Write(MotorRunning);
                writer.Write(TapePlaying);
                writer.Write(CarrierPresent);
                writer.Write(dataCarrierDetectLatched);
                writer.Write(receiveStatusOverflowPending);
                writer.Write(ClearToSend);
                WriteQueue(writer, receiveBytes);
                WriteQueue(writer, transmitBytes);
            }
        }

        public void LoadState(BinaryReader reader)
        {
            lock (sync)
            {
                aciaControl = reader.ReadByte();
                serialUlaControl = reader.ReadByte();
                MotorRunning = reader.ReadBoolean();
                TapePlaying = reader.ReadBoolean();
                CarrierPresent = reader.ReadBoolean();
                dataCarrierDetectLatched = reader.ReadBoolean();
                receiveStatusOverflowPending = reader.ReadBoolean();
                ClearToSend = reader.ReadBoolean();
                ReadQueue(reader, receiveBytes);
                ReadQueue(reader, transmitBytes);
                DecodeAciaControl(aciaControl);
                DecodeSerialUla(serialUlaControl);
            }

            UpdateInterruptLine();
        }

        private byte ReadAciaStatus()
        {
            byte status = BuildAciaStatus();
            bool signalOverflow = false;
            lock (sync)
            {
                if ((status & AciaStatusReceiveDataFull) != 0 && receiveStatusOverflowPending)
                {
                    receiveStatusOverflowPending = false;
                    signalOverflow = true;
                }
            }

            if (signalOverflow)
                ByteReceived?.Invoke();
            SetInterruptLine((status & AciaStatusInterruptRequest) != 0);
            return status;
        }

        private byte BuildAciaStatus()
        {
            byte status = 0;

            if (TransmitDataEmpty())
                status |= AciaStatusTransmitDataEmpty;

            // The 6850 status bit is /CTS: set means the external device is
            // not clear to receive data.
            if (!ClearToSend)
                status |= AciaStatusClearToSendInactive;

            lock (sync)
            {
                if (receiveBytes.Count > 0)
                    status |= AciaStatusReceiveDataFull;
            }

            if (!CarrierPresent || dataCarrierDetectLatched)
                status |= AciaStatusDataCarrierDetect;

            if (ReceiveInterruptEnabled() && (status & AciaStatusReceiveDataFull) != 0)
                status |= AciaStatusInterruptRequest;

            if (transmitInterruptEnabled && (status & AciaStatusTransmitDataEmpty) != 0)
                status |= AciaStatusInterruptRequest;

            return status;
        }

        private byte ReadAciaData()
        {
            lock (sync)
            {
                if (receiveBytes.TryDequeue(out byte value))
                {
                    dataCarrierDetectLatched = false;
                    UpdateInterruptLine();
                    return value;
                }
            }

            return 0x00;
        }

        private void WriteAciaControl(byte value)
        {
            byte previousControl;
            lock (sync)
            {
                previousControl = aciaControl;
                aciaControl = value;
                DecodeAciaControl(value);

                // On a 6850, control bits 0 and 1 both set request a master reset.
                if ((value & 0x03) == 0x03)
                {
                    receiveBytes.Clear();
                    transmitBytes.Clear();
                    transmitReadyAtTicks = 0;
                }
            }

            if (value != previousControl)
                Trace($"control <= ${value:X2} {FormatName} RTS={(RequestToSend ? 1 : 0)} break={(TransmitBreak ? 1 : 0)}");

            UpdateInterruptLine();
        }

        private void WriteAciaData(byte value)
        {
            byte transmitted = MaskDataBits(value);
            bool send;
            lock (sync)
            {
                transmitBytes.Enqueue(transmitted);
                transmitReadyAtTicks = Stopwatch.GetTimestamp() + CharacterTicks(TransmitBaudRate);
                send = ClearToSend && !TransmitBreak;
            }

            Trace($"tx <= ${transmitted:X2}");

            if (send)
                ByteTransmitted?.Invoke(transmitted);

            if (send && LoopbackEnabled && transmitted != 0x00)
                QueueReceivedByte(transmitted);

            UpdateInterruptLine();
        }

        private bool ReceiveInterruptEnabled()
        {
            return (aciaControl & 0x80) != 0;
        }

        private void UpdateInterruptLine()
        {
            SetInterruptLine((BuildAciaStatus() & AciaStatusInterruptRequest) != 0);
        }

        private void SetInterruptLine(bool asserted)
        {
            if (IrqAsserted == asserted)
                return;

            IrqAsserted = asserted;
            IrqChanged?.Invoke(asserted);
        }

        private void SetMotorRunning(bool running)
        {
            if (MotorRunning == running)
                return;

            MotorRunning = running;
            MotorChanged?.Invoke(running);
        }

        private bool TransmitDataEmpty()
        {
            return ClearToSend
                && !TransmitBreak
                && Stopwatch.GetTimestamp() >= transmitReadyAtTicks;
        }

        private long CharacterTicks(int baudRate)
        {
            int bits = 1 + DataBits + (Parity == "None" ? 0 : 1) + StopBits;
            return Math.Max(1, Stopwatch.Frequency * bits / baudRate);
        }

        private void DecodeAciaControl(byte control)
        {
            switch ((control >> 2) & 0x07)
            {
                case 0:
                    DataBits = 7;
                    Parity = "Even";
                    StopBits = 2;
                    break;
                case 1:
                    DataBits = 7;
                    Parity = "Odd";
                    StopBits = 2;
                    break;
                case 2:
                    DataBits = 7;
                    Parity = "Even";
                    StopBits = 1;
                    break;
                case 3:
                    DataBits = 7;
                    Parity = "Odd";
                    StopBits = 1;
                    break;
                case 4:
                    DataBits = 8;
                    Parity = "None";
                    StopBits = 2;
                    break;
                case 5:
                    DataBits = 8;
                    Parity = "None";
                    StopBits = 1;
                    break;
                case 6:
                    DataBits = 8;
                    Parity = "Even";
                    StopBits = 1;
                    break;
                case 7:
                    DataBits = 8;
                    Parity = "Odd";
                    StopBits = 1;
                    break;
            }

            int transmitControl = (control >> 5) & 0x03;
            transmitInterruptEnabled = transmitControl == 1;
            RequestToSend = transmitControl != 2;
            TransmitBreak = transmitControl == 3;
        }

        private void DecodeSerialUla(byte value)
        {
            TransmitBaudRate = DecodeBaud(value & 0x07);
            ReceiveBaudRate = DecodeBaud((value >> 3) & 0x07);
        }

        private static int DecodeBaud(int code)
        {
            int index = ((code & 0x01) << 2) | (code & 0x02) | ((code & 0x04) >> 2);
            return SerialUlaBaudRates[index];
        }

        private byte MaskDataBits(byte value)
        {
            return DataBits == 7 ? (byte)(value & 0x7F) : value;
        }

        private byte FormatReceivedByte(byte value)
        {
            if (DataBits != 7 || Parity == "None")
                return MaskDataBits(value);

            byte received = (byte)(value & 0x7F);
            int ones = CountLowBits(received, 7);
            bool setParityBit = Parity == "Even"
                ? (ones & 1) != 0
                : (ones & 1) == 0;

            return setParityBit ? (byte)(received | 0x80) : received;
        }

        private static int CountLowBits(byte value, int bits)
        {
            int count = 0;
            for (int bit = 0; bit < bits; bit++)
            {
                if ((value & (1 << bit)) != 0)
                    count++;
            }

            return count;
        }

        private static void WriteQueue(BinaryWriter writer, Queue<byte> queue)
        {
            writer.Write(queue.Count);
            foreach (byte value in queue)
                writer.Write(value);
        }

        private static void ReadQueue(BinaryReader reader, Queue<byte> queue)
        {
            queue.Clear();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                queue.Enqueue(reader.ReadByte());
        }

        private static void Trace(string message)
        {
            if (TraceEnabled)
                WriteTraceLine($"[serial] {message}");
        }

        private void TraceRead(ushort address, byte value)
        {
            if (!TraceEnabled)
                return;

            if (lastTracedReads.TryGetValue(address, out byte previous) && previous == value)
                return;

            lastTracedReads[address] = value;
            WriteTraceLine($"[serial] read ${address:X4} => ${value:X2}");
        }

        public static void WriteTraceLine(string line)
        {
            lock (TraceSync)
            {
                if (!traceStarted)
                {
                    File.WriteAllText(TracePath, string.Empty);
                    traceStarted = true;
                }

                File.AppendAllText(TracePath, line + Environment.NewLine);
            }

            Console.WriteLine(line);
        }
    }
}
