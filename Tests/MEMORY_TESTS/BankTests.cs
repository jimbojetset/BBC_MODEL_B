internal static class BankTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        for (int bank = 0; bank < 16; bank++)
        {
            int selected = bank;
            tests.Add(($"Banks/{bank:X}/ROMWindowAndProtection", () =>
            {
                using var m = new MemoryMachine();
                for (int b = 0; b < 16; b++)
                    m.SeedRom(b, Enumerable.Range(0, 0x4000).Select(a => (byte)(a ^ (a >> 8) ^ (b * 17))).ToArray());
                m.Select(selected);
                for (int a = 0; a < 0x4000; a++)
                {
                    byte expected = (byte)(a ^ (a >> 8) ^ (selected * 17));
                    Check.Byte(expected, m.Read(0x8000 + a), $"bank {selected:X} offset ${a:X4}");
                    m.Write(0x8000 + a, (byte)~expected);
                    Check.Byte(expected, m.Read(0x8000 + a), "ROM protection");
                }
            }));
            tests.Add(($"Banks/{bank:X}/RAMIsolation", () =>
            {
                using var m = new MemoryMachine();
                m.Invoke("SetSidewaysRamBank", selected);
                foreach (int a in new[] { 0x8000, 0x9FFF, 0xA000, 0xBFFF })
                {
                    m.Select(selected);
                    m.Write(a, 0xA5);
                    m.Select((selected + 1) & 15);
                    Check.Byte(0xFF, m.Read(a), "neighbour bank unchanged");
                    m.Select(selected);
                    Check.Byte(0xA5, m.Read(a), "RAM survives bank switching");
                }
                m.Write(0x7FFF, 0x12);
                m.Ram[0xC000] = 0x34;
                m.Write(0xC000, 0x56);
                Check.Byte(0x12, m.Read(0x7FFF), "main RAM boundary");
                Check.Byte(0x34, m.Read(0xC000), "OS remains protected alongside sideways RAM");
            }));
        }
        tests.Add(("Banks/ROMSELAllAliasesAndValues", () =>
        {
            using var m = new MemoryMachine();
            for (int b = 0; b < 16; b++) m.SeedRom(b, Enumerable.Repeat((byte)b, 0x4000).ToArray());
            for (int a = 0xFE30; a <= 0xFE3F; a++)
                for (int value = 0; value < 256; value++)
                {
                    m.Write(a, (byte)value);
                    Check.Byte((byte)(value & 15), m.Read(0x8000), "ROMSEL selects low four bits through every alias");
                }
        }));
        tests.Add(("Banks/ROMSELPreservesMOSWorkspace", () =>
        {
            using var m = new MemoryMachine();
            m.Write(0xF4, 0xA5);
            m.Select(3);
            Check.Byte(0xA5, m.Read(0xF4), "MOS updates its software copy; hardware must not do so");
        }));
        tests.Add(("Banks/CPUFetchesSelectedROM", () =>
        {
            using var m = new MemoryMachine();
            for (int b = 0; b < 16; b++)
            {
                m.SeedRom(b, new byte[] { 0xA9, (byte)b });
                m.Instruction(0xA9, (byte)b);
                m.Instruction(0x8D, 0x30, 0xFE);
                m.Emulator.Cpu.registers.PC = 0x8000;
                m.Emulator.Cpu.StepInstruction();
                Check.Byte((byte)b, m.Emulator.Cpu.registers.A, "opcode/operand fetched from selected bank");
            }
        }));
        tests.Add(("Banks/CPUSidewaysRAMWrite", () =>
        {
            using var m = new MemoryMachine();
            m.Invoke("SetSidewaysRamBank", 4);
            m.Select(4);
            m.Instruction(0xA9, 0x6B);
            m.Instruction(0x8D, 0xFF, 0xBF);
            Check.Byte(0x6B, m.Read(0xBFFF), "CPU write reaches sideways RAM");
        }));
        tests.Add(("Banks/EmptySocketRejectsWrites", () =>
        {
            using var m = new MemoryMachine();
            m.Select(5);
            m.Write(0x8000, 0);
            Check.Byte(0xFF, m.Read(0x8000), "empty socket returns configured FF fill");
        }));
        foreach (int size in new[] { 0x2000, 0x4000 })
            tests.Add(($"Banks/Load{size / 1024}KROM", () =>
            {
                using var m = new MemoryMachine();
                byte[] data = Enumerable.Range(0, size).Select(i => (byte)(i ^ (i >> 8))).ToArray();
                m.LoadRom(5, data);
                m.Select(5);
                for (int a = 0; a < 0x4000; a++) Check.Byte(data[a % size], m.Read(0x8000 + a), $"loaded ROM offset ${a:X4}");
            }));
        tests.Add(("Banks/MoveRAMRetainsDataAndWriteability", () =>
        {
            using var m = new MemoryMachine();
            m.Invoke("SetSidewaysRamBank", 2);
            m.Select(2);
            m.Write(0x8123, 0xA5);
            m.Invoke("MoveSidewaysRomBank", 2, 7);
            m.Select(7);
            Check.Byte(0xA5, m.Read(0x8123), "moved RAM data");
            m.Write(0x8123, 0x5A);
            Check.Byte(0x5A, m.Read(0x8123), "moved RAM writable");
            m.Select(2);
            m.Write(0x8123, 0);
            Check.Byte(0xFF, m.Read(0x8123), "old socket empty and not writable");
        }));
        tests.Add(("Banks/MoveRejectsOccupiedTarget", () =>
        {
            using var m = new MemoryMachine();
            m.Invoke("SetSidewaysRamBank", 2);
            m.Invoke("SetSidewaysRamBank", 7);
            m.Select(2);
            m.Write(0x8000, 0xA5);
            m.Select(7);
            m.Write(0x8000, 0x5A);
            Check.Throws<InvalidOperationException>(() => m.Invoke("MoveSidewaysRomBank", 2, 7));
            Check.Byte(0x5A, m.Read(0x8000), "occupied target preserved");
            m.Select(2);
            Check.Byte(0xA5, m.Read(0x8000), "source preserved");
        }));
        tests.Add(("Banks/ClearRAMContentsPreservesROM", () =>
        {
            using var m = new MemoryMachine();
            m.Invoke("SetSidewaysRamBank", 2);
            m.Select(2);
            m.Write(0x8000, 0xA5);
            m.SeedRom(3, new byte[] { 0x5A });
            m.Invoke("ClearSidewaysRamContents");
            Check.Byte(0, m.Read(0x8000), "RAM contents cleared");
            m.Select(3);
            Check.Byte(0x5A, m.Read(0x8000), "ROM contents preserved");
        }));
    }
}
