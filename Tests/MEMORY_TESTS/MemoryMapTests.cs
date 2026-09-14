using BBC.CPU;

internal static class MemoryMapTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("RAM/All32KAddresses", () =>
        {
            using var m = new MemoryMachine();
            foreach (int shift in new[] { 0, 8 })
            {
                for (int a = 0; a < 0x8000; a++) m.Write(a, (byte)(a >> shift));
                for (int a = 0; a < 0x8000; a++) Check.Byte((byte)(a >> shift), m.Read(a), $"RAM ${a:X4}");
            }
        }));
        tests.Add(("RAM/CPULoadStore", () =>
        {
            using var m = new MemoryMachine();
            m.Instruction(0xA9, 0xA5);
            m.Instruction(0x8D, 0xFF, 0x7F);
            Check.Byte(0xA5, m.Read(0x7FFF), "CPU store at RAM boundary");
            m.Instruction(0xAD, 0xFF, 0x7F);
            Check.Byte(0xA5, m.Emulator.Cpu.registers.A, "CPU read at RAM boundary");
        }));
        tests.Add(("OS/ReadAndWriteProtection", () =>
        {
            using var m = new MemoryMachine();
            for (int a = 0xC000; a <= 0xFFFF; a++)
            {
                if (a is >= 0xFC00 and <= 0xFEFF) continue;
                byte expected = (byte)(a ^ (a >> 8));
                m.Ram[a] = expected;
                m.Write(a, (byte)~expected);
                Check.Byte(expected, m.Read(a), $"OS ROM ${a:X4}");
            }
        }));
        tests.Add(("OS/CPUWriteProtection", () =>
        {
            using var m = new MemoryMachine();
            m.Ram[0xC000] = 0x42;
            m.Instruction(0xA9, 0xA5);
            m.Instruction(0x8D, 0, 0xC0);
            Check.Byte(0x42, m.Read(0xC000), "CPU cannot overwrite OS");
        }));
        tests.Add(("OS/ResetVector", () =>
        {
            using var m = new MemoryMachine();
            m.Ram[0xFFFC] = 0x34;
            m.Ram[0xFFFD] = 0xC1;
            m.Write(0xFFFC, 0);
            m.Write(0xFFFD, 0);
            Check.True(m.Emulator.Memory.ReadWord(0xFFFC) == 0xC134, "reset vector remains in fixed ROM");
            m.Emulator.Cpu.ResetNow();
            Check.True(m.Emulator.Cpu.registers.PC == 0xC134, "CPU reset reads mapped OS vector");
        }));
        tests.Add(("Memory/WordCrossesBankBoundary", () =>
        {
            using var m = new MemoryMachine();
            byte[] rom = new byte[0x4000]; rom[^1] = 0x34;
            m.SeedRom(3, rom);
            m.Select(3);
            m.Ram[0xC000] = 0x12;
            Check.True(m.Emulator.Memory.ReadWord(0xBFFF) == 0x1234, "word read spans sideways and OS ROM");
        }));
        tests.Add(("Memory/WordWrapsAtFFFF", () =>
        {
            using var m = new MemoryMachine();
            m.Ram[0xFFFF] = 0x34;
            m.Write(0, 0x12);
            Check.True(m.Emulator.Memory.ReadWord(0xFFFF) == 0x1234, "16-bit bus wraps at top of address space");
        }));
        foreach (var (name, start) in new[] { ("FRED", 0xFC00), ("JIM", 0xFD00), ("SHEILA", 0xFE00) })
            tests.Add(($"IO/{name}/WritesDoNotModifyBackingROM", () =>
            {
                using var m = new MemoryMachine();
                for (int a = start; a < start + 256; a++)
                {
                    m.Ram[a] = 0xA5;
                    m.Write(a, 0x5A);
                    Check.Byte(0xA5, m.Ram[a], $"I/O write ${a:X4} must be decoded, not stored");
                }
            }));
        tests.Add(("IO/UnmappedReadsDoNotExposeOSBytes", () =>
        {
            using var m = new MemoryMachine();
            // Do not impose a fixed value on electrically floating/open bus reads.
            foreach (int a in new[] { 0xFC00, 0xFCFF, 0xFD00, 0xFDFF, 0xFEA0, 0xFEE0 })
            {
                m.Ram[a] = 0xA5;
                byte before = m.Read(a);
                m.Ram[a] = 0x5A;
                Check.Byte(before, m.Read(a), $"I/O hole ${a:X4} must hide underlying ROM");
            }
        }));
        tests.Add(("FlatBus/16BitAddressWrapping", () =>
        {
            var bus = new FlatMemoryBus(); bus.WriteByte(0x10000, 0xA5);
            Check.Byte(0xA5, bus.ReadByte(0), "flat-bus wrap");
            Check.Byte(0xA5, bus.ReadByte(0x20000), "flat-bus read wrap");
        }));
    }
}
