// ============================================================================
// Project:     BBC
// File:        SecondProcessor6502.cs
// Description: Acorn Tube 6502 second processor host.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See COPYING in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using BBC.CPU;

namespace BBC
{

    public sealed class SecondProcessor6502 : IDisposable
    {
        public const int ClockHz = 3_000_000;

        private readonly TubeUla tube;
        private readonly FlatMemoryBus memory = new FlatMemoryBus();
        private readonly CPU_65C02 cpu;
        private byte[]? bootRom;
        private bool bootRomEnabled;
        private bool emulationPaused;
        private bool parasiteNmiAsserted;
        private bool parasiteResetAsserted;
        private double pendingCpuCycles;
        private long queuedParasiteNmis;
        private Exception? cpuException;

        public SecondProcessor6502(TubeUla tube)
        {
            this.tube = tube;
            cpu = new CPU_65C02(memory, ClockHz);
            memory.OnRead = ReadMemory;
            memory.OnWrite = WriteMemory;
            tube.ParasiteIrqChanged += cpu.SetIrqLine;
            tube.ParasiteNmiChanged += SetParasiteNmi;
            tube.ParasiteResetChanged += SetParasiteReset;
        }

        public CPU_65C02 Cpu => cpu;

        public byte[] Memory => memory.Memory;

        public Exception? CpuException => cpuException;

        public bool BootRomEnabled => bootRomEnabled;

        public long QueuedParasiteNmis => Interlocked.Read(ref queuedParasiteNmis);

        public long CpuQueuedNmis => cpu.NmiQueuedCount;

        public long CpuServicedNmis => cpu.NmiServicedCount;

        public void SaveState(BinaryWriter writer)
        {
            cpu.SaveState(writer);
            writer.Write(bootRomEnabled);
            writer.Write(parasiteNmiAsserted);
            writer.Write(parasiteResetAsserted);
            WriteByteArray(writer, memory.Memory);
        }

        public void LoadState(BinaryReader reader, int saveStateVersion)
        {
            cpu.LoadState(reader);
            bootRomEnabled = reader.ReadBoolean();
            parasiteNmiAsserted = reader.ReadBoolean();
            parasiteResetAsserted = saveStateVersion >= 3 && reader.ReadBoolean();
            UpdateCpuPause();
            ReadByteArray(reader, memory.Memory, "Tube 6502 RAM");
        }

        public void LoadRom(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"6502 Tube ROM not found: {path}");

            bootRom = File.ReadAllBytes(path);
            if (bootRom.Length <= 0 || bootRom.Length > memory.Memory.Length)
                throw new InvalidOperationException($"6502 Tube ROM '{path}' must be between 1 and 65536 bytes.");
        }

        public void Reset()
        {
            Array.Clear(memory.Memory);
            bootRomEnabled = bootRom is not null;
            parasiteNmiAsserted = false;
            parasiteResetAsserted = false;
            pendingCpuCycles = 0;
            cpu.SetIrqLine(false);
            UpdateCpuPause();
            cpu.ResetNow();
        }

        public void SetPaused(bool paused)
        {
            Volatile.Write(ref emulationPaused, paused);
            UpdateCpuPause();
        }

        public void Start()
        {
            cpuException = null;
        }

        public void Stop()
        {
            cpu.Stop();
        }

        public void AdvanceHostCycles(int hostCycles)
        {
            if (hostCycles <= 0 || Volatile.Read(ref emulationPaused) || Volatile.Read(ref parasiteResetAsserted))
                return;

            pendingCpuCycles += hostCycles * (ClockHz / (double)Emulator.CpuClockHz);
            while (pendingCpuCycles >= 1)
            {
                int elapsed = cpu.StepInstruction();
                if (elapsed <= 0)
                    break;

                pendingCpuCycles -= elapsed;
            }
        }

        public void Dispose()
        {
            Stop();
            tube.ParasiteIrqChanged -= cpu.SetIrqLine;
            tube.ParasiteNmiChanged -= SetParasiteNmi;
            tube.ParasiteResetChanged -= SetParasiteReset;
        }

        private void SetParasiteNmi(bool asserted)
        {
            Volatile.Write(ref parasiteNmiAsserted, asserted);
            if (asserted)
            {
                Interlocked.Increment(ref queuedParasiteNmis);
                cpu.InitiateNMI(0xFFFA);
            }
        }

        private void SetParasiteReset(bool asserted)
        {
            Volatile.Write(ref parasiteResetAsserted, asserted);
            if (asserted)
            {
                Array.Clear(memory.Memory);
                bootRomEnabled = bootRom is not null;
                Volatile.Write(ref parasiteNmiAsserted, false);
                pendingCpuCycles = 0;
                cpu.SetIrqLine(false);
                UpdateCpuPause();
                return;
            }

            cpu.RequestReset();
            UpdateCpuPause();
        }

        private void UpdateCpuPause()
        {
            cpu.SetPaused(Volatile.Read(ref emulationPaused) || Volatile.Read(ref parasiteResetAsserted));
        }

        private byte ReadMemory(ulong address, byte value)
        {
            ushort addr = (ushort)(address & 0xFFFF);
            if (TubeUla.IsParasiteAddress(addr))
            {
                byte tubeValue = tube.ReadParasite(addr);
                bootRomEnabled = false;
                return tubeValue;
            }

            if (bootRomEnabled && bootRom is not null)
            {
                int romStart = memory.Memory.Length - bootRom.Length;
                if (addr >= romStart)
                    return bootRom[addr - romStart];
            }

            return value;
        }

        private bool WriteMemory(ulong address, byte value)
        {
            ushort addr = (ushort)(address & 0xFFFF);
            if (TubeUla.IsParasiteAddress(addr))
            {
                tube.WriteParasite(addr, value);
                bootRomEnabled = false;
                return true;
            }

            return false;
        }

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
