// ============================================================================
// Project:     BBC
// File:        TubeUla.cs
// Description: Acorn Tube ULA register and FIFO bridge for second processors.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{

    public sealed class TubeUla
    {
        private const int R1 = 0;
        private const int R2 = 1;
        private const int R3 = 2;
        private const int R4 = 3;
        private const byte DataAvailable = 0x80;
        private const byte SpaceAvailable = 0x40;
        private const byte StatusQ = 0x01;
        private const byte StatusI = 0x02;
        private const byte StatusJ = 0x04;
        private const byte StatusM = 0x08;
        private const byte StatusV = 0x10;
        private const byte StatusP = 0x20;
        private const byte StatusT = 0x40;
        private const byte StatusS = 0x80;
        private const byte ControlMask = StatusQ | StatusI | StatusJ | StatusM | StatusV | StatusP | StatusT;
        private const int R1ParasiteToHostSize = 24;

        private readonly object sync = new object();
        private readonly byte[] hostStatus = new byte[4];
        private readonly byte[] parasiteStatus = new byte[4];
        private readonly byte[] parasiteToHostR1 = new byte[R1ParasiteToHostSize];
        private readonly byte[] parasiteToHost = new byte[4];
        private readonly byte[] hostToParasite = new byte[4];
        private readonly byte[] parasiteToHostR3 = new byte[2];
        private readonly byte[] hostToParasiteR3 = new byte[2];

        private byte internalStatus;
        private int parasiteToHostR1Head;
        private int parasiteToHostR1Tail;
        private int parasiteToHostR1Count;
        private int parasiteToHostR3Head;
        private int parasiteToHostR3Tail;
        private int parasiteToHostR3Count;
        private int hostToParasiteR3Head;
        private int hostToParasiteR3Tail;
        private int hostToParasiteR3Count;
        private bool hostIrqAsserted;
        private bool parasiteIrqAsserted;
        private bool parasiteNmiAsserted;
        private bool parasiteResetAsserted;

        public event Action<bool>? HostIrqChanged;

        public event Action<bool>? ParasiteIrqChanged;

        public event Action<bool>? ParasiteNmiChanged;

        public event Action<bool>? ParasiteResetChanged;

        public void Reset()
        {
            lock (sync)
            {
                internalStatus = 0;
                ResetFifos();
                SetHostIrq(false);
                SetParasiteIrq(false);
                SetParasiteNmi(false);
                SetParasiteReset(false);
            }
        }

        public static bool IsHostAddress(ushort address) => address is >= 0xFEE0 and <= 0xFEEF;

        public static bool IsParasiteAddress(ushort address) => address is >= 0xFEF8 and <= 0xFEFF;

        public string[] GetDebuggerState()
        {
            lock (sync)
            {
                return
                [
                    $"Control ${internalStatus:X2}",
                    $"Host R1 ${hostStatus[0]:X2}  R2 ${hostStatus[1]:X2}",
                    $"Host R3 ${hostStatus[2]:X2}  R4 ${hostStatus[3]:X2}",
                    $"Parasite R1 ${parasiteStatus[0]:X2}",
                    $"Parasite R2 ${parasiteStatus[1]:X2}",
                    $"Parasite R3 ${parasiteStatus[2]:X2}",
                    $"Parasite R4 ${parasiteStatus[3]:X2}",
                    $"R1 FIFO {parasiteToHostR1Count}/{R1ParasiteToHostSize}",
                    $"R3 P>H {parasiteToHostR3Count}/2",
                    $"R3 H>P {hostToParasiteR3Count}/2",
                    $"Host IRQ {(hostIrqAsserted ? "active" : "clear")}",
                    $"Parasite IRQ {(parasiteIrqAsserted ? "active" : "clear")}",
                    $"Parasite NMI {(parasiteNmiAsserted ? "active" : "clear")}",
                    $"Parasite reset {(parasiteResetAsserted ? "active" : "clear")}"
                ];
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            lock (sync)
            {
                writer.Write(internalStatus);
                WriteByteArray(writer, hostStatus);
                WriteByteArray(writer, parasiteStatus);
                WriteByteArray(writer, parasiteToHostR1);
                WriteByteArray(writer, parasiteToHost);
                WriteByteArray(writer, hostToParasite);
                WriteByteArray(writer, parasiteToHostR3);
                WriteByteArray(writer, hostToParasiteR3);
                writer.Write(parasiteToHostR1Head);
                writer.Write(parasiteToHostR1Tail);
                writer.Write(parasiteToHostR1Count);
                writer.Write(parasiteToHostR3Head);
                writer.Write(parasiteToHostR3Tail);
                writer.Write(parasiteToHostR3Count);
                writer.Write(hostToParasiteR3Head);
                writer.Write(hostToParasiteR3Tail);
                writer.Write(hostToParasiteR3Count);
                writer.Write(hostIrqAsserted);
                writer.Write(parasiteIrqAsserted);
                writer.Write(parasiteNmiAsserted);
                writer.Write(parasiteResetAsserted);
            }
        }

        public void LoadState(BinaryReader reader)
        {
            lock (sync)
            {
                internalStatus = reader.ReadByte();
                ReadByteArray(reader, hostStatus, "Tube host status");
                ReadByteArray(reader, parasiteStatus, "Tube parasite status");
                ReadByteArray(reader, parasiteToHostR1, "Tube R1 parasite-to-host FIFO");
                ReadByteArray(reader, parasiteToHost, "Tube parasite-to-host registers");
                ReadByteArray(reader, hostToParasite, "Tube host-to-parasite registers");
                ReadByteArray(reader, parasiteToHostR3, "Tube R3 parasite-to-host FIFO");
                ReadByteArray(reader, hostToParasiteR3, "Tube R3 host-to-parasite FIFO");
                parasiteToHostR1Head = reader.ReadInt32();
                parasiteToHostR1Tail = reader.ReadInt32();
                parasiteToHostR1Count = reader.ReadInt32();
                parasiteToHostR3Head = reader.ReadInt32();
                parasiteToHostR3Tail = reader.ReadInt32();
                parasiteToHostR3Count = reader.ReadInt32();
                hostToParasiteR3Head = reader.ReadInt32();
                hostToParasiteR3Tail = reader.ReadInt32();
                hostToParasiteR3Count = reader.ReadInt32();
                hostIrqAsserted = reader.ReadBoolean();
                parasiteIrqAsserted = reader.ReadBoolean();
                parasiteNmiAsserted = reader.ReadBoolean();
                parasiteResetAsserted = reader.ReadBoolean();
                UpdateInterrupts();
                HostIrqChanged?.Invoke(hostIrqAsserted);
                ParasiteIrqChanged?.Invoke(parasiteIrqAsserted);
                ParasiteNmiChanged?.Invoke(parasiteNmiAsserted);
                ParasiteResetChanged?.Invoke(parasiteResetAsserted);
                Monitor.PulseAll(sync);
            }
        }

        public byte ReadHost(ushort address)
        {
            lock (sync)
            {
                byte value;
                int register = RegisterIndex(address);

                switch (address & 7)
                {
                    case 0:
                        value = (byte)((hostStatus[R1] & (DataAvailable | SpaceAvailable))
                            | (internalStatus & ~(DataAvailable | SpaceAvailable)));
                        return value;
                    case 1:
                        value = HostReadR1();
                        break;
                    case 2:
                    case 4:
                    case 6:
                        value = hostStatus[register];
                        return value;
                    case 3:
                        value = HostReadSingle(R2, parasiteToHost[R2]);
                        break;
                    case 5:
                        value = HostReadR3();
                        break;
                    case 7:
                        value = HostReadSingle(R4, parasiteToHost[R4]);
                        break;
                    default:
                        value = 0xFE;
                        break;
                }

                UpdateInterrupts();
                return value;
            }
        }

        public void WriteHost(ushort address, byte value)
        {
            lock (sync)
            {
                int register = RegisterIndex(address);

                switch (address & 7)
                {
                    case 0:
                        HostWriteStatus(value);
                        break;
                    case 1:
                        HostWriteSingle(R1, value);
                        break;
                    case 3:
                        HostWriteSingle(R2, value);
                        break;
                    case 5:
                        HostWriteR3(value);
                        break;
                    case 7:
                        HostWriteSingle(R4, value);
                        break;
                }

                UpdateInterrupts();
            }

            Thread.Sleep(0);
        }

        public byte ReadParasite(ushort address)
        {
            lock (sync)
            {
                byte value;
                int register = RegisterIndex(address);

                switch (address & 7)
                {
                    case 4:
                        value = parasiteStatus[R3];
                        if (parasiteToHostR3Count == 0)
                            value |= DataAvailable;
                        return value;
                    case 0:
                    case 2:
                    case 6:
                        value = parasiteStatus[register];
                        return value;
                    case 1:
                        value = ParasiteReadSingle(R1, hostToParasite[R1]);
                        break;
                    case 3:
                        value = ParasiteReadSingle(R2, hostToParasite[R2]);
                        break;
                    case 5:
                        value = ParasiteReadR3();
                        break;
                    case 7:
                        value = ParasiteReadSingle(R4, hostToParasite[R4]);
                        break;
                    default:
                        value = 0xFE;
                        break;
                }

                UpdateInterrupts();
                return value;
            }
        }

        public void WriteParasite(ushort address, byte value)
        {
            lock (sync)
            {
                int register = RegisterIndex(address);

                switch (address & 7)
                {
                    case 1:
                        ParasiteWriteR1(value);
                        break;
                    case 3:
                        ParasiteWriteSingle(R2, value);
                        break;
                    case 5:
                        ParasiteWriteR3(value);
                        break;
                    case 7:
                        ParasiteWriteSingle(R4, value);
                        break;
                }

                UpdateInterrupts();
            }

            Thread.Sleep(0);
        }

        private void ResetFifos()
        {
            Array.Fill(hostStatus, SpaceAvailable);
            Array.Fill(parasiteStatus, SpaceAvailable);
            Array.Clear(parasiteToHostR1);
            Array.Clear(parasiteToHost);
            Array.Clear(hostToParasite);
            Array.Clear(parasiteToHostR3);
            Array.Clear(hostToParasiteR3);
            parasiteToHostR1Head = 0;
            parasiteToHostR1Tail = 0;
            parasiteToHostR1Count = 0;
            parasiteToHostR3[0] = 0;
            parasiteToHostR3Head = 0;
            parasiteToHostR3Tail = 1;
            parasiteToHostR3Count = 1;
            hostToParasiteR3Head = 0;
            hostToParasiteR3Tail = 0;
            hostToParasiteR3Count = 0;
            hostStatus[R3] |= DataAvailable;
            Monitor.PulseAll(sync);
        }

        private void HostWriteStatus(byte value)
        {
            byte flags = (byte)(value & ControlMask);
            if ((value & StatusS) != 0)
                internalStatus |= flags;
            else
                internalStatus &= unchecked((byte)~flags);

            if ((value & StatusT) != 0)
                ResetFifos();

            if ((value & StatusP) != 0)
                SetParasiteReset((internalStatus & StatusP) != 0);
        }

        private byte HostReadR1()
        {
            byte value = parasiteToHostR1Count > 0 ? parasiteToHostR1[parasiteToHostR1Head] : parasiteToHost[R1];
            if ((hostStatus[R1] & DataAvailable) != 0 && parasiteToHostR1Count > 0)
            {
                parasiteToHostR1Head = (parasiteToHostR1Head + 1) % R1ParasiteToHostSize;
                parasiteToHostR1Count--;
                parasiteStatus[R1] |= SpaceAvailable;
                if (parasiteToHostR1Count == 0)
                    hostStatus[R1] &= unchecked((byte)~DataAvailable);
                Monitor.PulseAll(sync);
            }

            return value;
        }

        private byte HostReadSingle(int register, byte value)
        {
            parasiteStatus[register] |= SpaceAvailable;
            hostStatus[register] &= unchecked((byte)~DataAvailable);
            Monitor.PulseAll(sync);
            return value;
        }

        private byte HostReadR3()
        {
            byte value = parasiteToHostR3Count > 0 ? parasiteToHostR3[parasiteToHostR3Head] : (byte)0;
            if ((hostStatus[R3] & DataAvailable) != 0 && parasiteToHostR3Count > 0)
            {
                parasiteToHostR3Head = (parasiteToHostR3Head + 1) % parasiteToHostR3.Length;
                parasiteToHostR3Count--;
                if (parasiteToHostR3Count == 0)
                    parasiteStatus[R3] |= SpaceAvailable;
                if (parasiteToHostR3Count == 0)
                    hostStatus[R3] &= unchecked((byte)~DataAvailable);
                Monitor.PulseAll(sync);
            }

            return value;
        }

        private void HostWriteSingle(int register, byte value)
        {
            if ((hostStatus[register] & SpaceAvailable) == 0)
                return;

            hostToParasite[register] = value;
            parasiteStatus[register] |= DataAvailable;
            hostStatus[register] &= unchecked((byte)~SpaceAvailable);
            Monitor.PulseAll(sync);
        }

        private void HostWriteR3(byte value)
        {
            if (!WaitForSpace(hostStatus, R3))
                return;

            hostToParasiteR3[hostToParasiteR3Tail] = value;
            hostToParasiteR3Tail = (hostToParasiteR3Tail + 1) % hostToParasiteR3.Length;
            hostToParasiteR3Count++;
            if (hostToParasiteR3Count >= GetR3TransferSize())
            {
                parasiteStatus[R3] |= DataAvailable;
                hostStatus[R3] &= unchecked((byte)~SpaceAvailable);
            }
            Monitor.PulseAll(sync);
        }

        private byte ParasiteReadSingle(int register, byte value)
        {
            hostStatus[register] |= SpaceAvailable;
            parasiteStatus[register] &= unchecked((byte)~DataAvailable);
            Monitor.PulseAll(sync);
            return value;
        }

        private byte ParasiteReadR3()
        {
            byte value = hostToParasiteR3Count > 0 ? hostToParasiteR3[hostToParasiteR3Head] : (byte)0;
            if ((parasiteStatus[R3] & DataAvailable) != 0 && hostToParasiteR3Count > 0)
            {
                hostToParasiteR3Head = (hostToParasiteR3Head + 1) % hostToParasiteR3.Length;
                hostToParasiteR3Count--;
                if (hostToParasiteR3Count == 0)
                    hostStatus[R3] |= SpaceAvailable;
                if (hostToParasiteR3Count == 0)
                    parasiteStatus[R3] &= unchecked((byte)~DataAvailable);
                Monitor.PulseAll(sync);
            }

            return value;
        }

        private void ParasiteWriteR1(byte value)
        {
            if ((parasiteStatus[R1] & SpaceAvailable) == 0 || parasiteToHostR1Count >= R1ParasiteToHostSize)
                return;

            parasiteToHostR1[parasiteToHostR1Tail] = value;
            parasiteToHostR1Tail = (parasiteToHostR1Tail + 1) % R1ParasiteToHostSize;
            parasiteToHostR1Count++;
            hostStatus[R1] |= DataAvailable;
            if (parasiteToHostR1Count == R1ParasiteToHostSize)
                parasiteStatus[R1] &= unchecked((byte)~SpaceAvailable);
            Monitor.PulseAll(sync);
        }

        private void ParasiteWriteSingle(int register, byte value)
        {
            if ((parasiteStatus[register] & SpaceAvailable) == 0)
                return;

            parasiteToHost[register] = value;
            hostStatus[register] |= DataAvailable;
            parasiteStatus[register] &= unchecked((byte)~SpaceAvailable);
            Monitor.PulseAll(sync);
        }

        private void ParasiteWriteR3(byte value)
        {
            if (!WaitForSpace(parasiteStatus, R3))
                return;

            parasiteToHostR3[parasiteToHostR3Tail] = value;
            parasiteToHostR3Tail = (parasiteToHostR3Tail + 1) % parasiteToHostR3.Length;
            parasiteToHostR3Count++;
            if (parasiteToHostR3Count >= GetR3TransferSize())
            {
                hostStatus[R3] |= DataAvailable;
                parasiteStatus[R3] &= unchecked((byte)~SpaceAvailable);
            }
            Monitor.PulseAll(sync);
        }

        private void UpdateInterrupts()
        {
            SetHostIrq((internalStatus & StatusQ) != 0 && (hostStatus[R4] & DataAvailable) != 0);
            SetParasiteIrq(((internalStatus & StatusI) != 0 && (parasiteStatus[R1] & DataAvailable) != 0)
                || ((internalStatus & StatusJ) != 0 && (parasiteStatus[R4] & DataAvailable) != 0));

            SetParasiteNmi((internalStatus & StatusM) != 0
                && (hostToParasiteR3Count >= GetR3TransferSize() || parasiteToHostR3Count == 0));
        }

        private int GetR3TransferSize() => (internalStatus & StatusV) != 0 ? 2 : 1;

        private bool WaitForSpace(byte[] status, int register)
        {
            long deadline = DateTime.UtcNow.AddSeconds(1).Ticks;
            while ((status[register] & SpaceAvailable) == 0)
            {
                if (DateTime.UtcNow.Ticks >= deadline)
                    return false;

                Monitor.Wait(sync, 1);
            }

            return true;
        }

        private void SetHostIrq(bool asserted)
        {
            if (hostIrqAsserted == asserted)
                return;

            hostIrqAsserted = asserted;
            HostIrqChanged?.Invoke(asserted);
        }

        private void SetParasiteIrq(bool asserted)
        {
            if (parasiteIrqAsserted == asserted)
                return;

            parasiteIrqAsserted = asserted;
            ParasiteIrqChanged?.Invoke(asserted);
        }

        private void SetParasiteNmi(bool asserted)
        {
            if (parasiteNmiAsserted == asserted)
                return;

            parasiteNmiAsserted = asserted;
            ParasiteNmiChanged?.Invoke(asserted);
        }

        private void SetParasiteReset(bool asserted)
        {
            if (parasiteResetAsserted == asserted)
                return;

            parasiteResetAsserted = asserted;
            ParasiteResetChanged?.Invoke(asserted);
        }

        private static int RegisterIndex(ushort address) => (address >> 1) & 0x03;

        private static void WriteByteArray(BinaryWriter writer, byte[] bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static void ReadByteArray(BinaryReader reader, byte[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            bytes.CopyTo(destination, 0);
        }
    }
}
