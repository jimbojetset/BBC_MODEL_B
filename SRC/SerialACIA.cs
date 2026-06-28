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
        private const byte AciaStatusClearToSend = 0x08;
        private const byte AciaStatusInterruptRequest = 0x80;

        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE") == "1";
        private static readonly bool LoopbackEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_LOOPBACK") == "1";

        private readonly object sync = new object();
        private readonly Queue<byte> receiveBytes = new Queue<byte>();
        private readonly Queue<byte> transmitBytes = new Queue<byte>();
        private readonly Dictionary<ushort, byte> lastTracedReads = new Dictionary<ushort, byte>();
        private byte aciaControl;
        private byte serialUlaControl;

        public bool MotorRunning { get; private set; }

        public bool TapePlaying { get; private set; }

        public bool CarrierPresent { get; private set; } = true;

        public event Action<byte>? ByteTransmitted;

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

            serialUlaControl = value;

            // The BBC Serial ULA also handles cassette routing and motor state.
            // Bit 7 is the observable motor control used by the front-panel LED.
            MotorRunning = (value & 0x80) != 0;
            Trace($"serial ULA <= ${value:X2}");
        }

        public void QueueReceivedByte(byte value)
        {
            lock (sync)
            {
                receiveBytes.Enqueue(value);
            }

            Trace($"rx <= ${value:X2}");
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
                lastTracedReads.Clear();
            }

            MotorRunning = false;
            TapePlaying = false;
        }

        public void SetCarrierPresent(bool present)
        {
            CarrierPresent = present;
        }

        public void StopTape()
        {
            TapePlaying = false;
            MotorRunning = false;
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
                ReadQueue(reader, receiveBytes);
                ReadQueue(reader, transmitBytes);
            }
        }

        private byte ReadAciaStatus()
        {
            byte status = AciaStatusTransmitDataEmpty | AciaStatusClearToSend;

            lock (sync)
            {
                if (receiveBytes.Count > 0)
                    status |= AciaStatusReceiveDataFull;
            }

            if (!CarrierPresent)
                status |= AciaStatusDataCarrierDetect;

            if (ReceiveInterruptEnabled() && (status & AciaStatusReceiveDataFull) != 0)
                status |= AciaStatusInterruptRequest;

            return status;
        }

        private byte ReadAciaData()
        {
            lock (sync)
            {
                if (receiveBytes.TryDequeue(out byte value))
                    return value;
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

                // On a 6850, control bits 0 and 1 both set request a master reset.
                if ((value & 0x03) == 0x03)
                {
                    receiveBytes.Clear();
                    transmitBytes.Clear();
                }
            }

            if (value != previousControl)
                Trace($"control <= ${value:X2}");
        }

        private void WriteAciaData(byte value)
        {
            lock (sync)
                transmitBytes.Enqueue(value);

            Trace($"tx <= ${value:X2}");

            ByteTransmitted?.Invoke(value);

            if (LoopbackEnabled && value != 0x00)
                QueueReceivedByte(value);
        }

        private bool ReceiveInterruptEnabled()
        {
            return (aciaControl & 0x80) != 0;
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
                Console.WriteLine($"[serial] {message}");
        }

        private void TraceRead(ushort address, byte value)
        {
            if (!TraceEnabled)
                return;

            if (lastTracedReads.TryGetValue(address, out byte previous) && previous == value)
                return;

            lastTracedReads[address] = value;
            Console.WriteLine($"[serial] read ${address:X4} => ${value:X2}");
        }
    }
}
