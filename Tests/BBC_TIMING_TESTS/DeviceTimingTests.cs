internal static class DeviceTimingTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        foreach (bool slow in new[]{false,true})
            tests.Add(($"Devices/UserTimerAdvancesDuring{(slow ? "SlowIO" : "RAM")}", () =>
            {
                using var m = new TimingMachine();
                // Warm the bus phase so the measured slow-access run spans whole VIA clocks.
                m.Instruction(0xAD, slow ? (byte)0x63 : (byte)0, slow ? (byte)0xFE : (byte)6);
                m.User.Write(0xFE64, 0); m.User.Write(0xFE65, 0x40);
                int before = m.Counter(m.User); long start = m.Cpu.TotalCycles;
                for (int i = 0; i < 32; i++) m.Instruction(0xAD, slow ? (byte)0x63 : (byte)0, slow ? (byte)0xFE : (byte)6);
                long elapsed = m.Cpu.TotalCycles - start;
                Check.Equal(elapsed / 2, (long)((before - m.Counter(m.User)) & 0xFFFF), "VIA gets normal and stretched CPU cycles");
            }));
        tests.Add(("Devices/SharedVIAClock", () =>
        {
            using var m = new TimingMachine();
            m.User.Write(0xFE64, 0); m.User.Write(0xFE65, 0x40);
            m.System.Write(0xFE44, 0); m.System.Write(0xFE45, 0x40);
            int SystemCounter() => m.System.Read(0xFE44) | (m.System.Read(0xFE45) << 8);
            int user = m.Counter(m.User), system = SystemCounter();
            for (int i = 0; i < 20; i++) m.Instruction(0xEA);
            Check.Equal(20, user - m.Counter(m.User), "User VIA 1MHz clock");
            Check.Equal(20, system - SystemCounter(), "System VIA shares 1MHz clock");
        }));
        tests.Add(("Devices/UserTimerIRQReachesCPU", () =>
        {
            using var m = new TimingMachine();
            m.Instruction(0x58); m.Instruction(0xEA); // CLI latency has elapsed.
            m.Ram[0xFFFE] = 0; m.Ram[0xFFFF] = 4;
            m.User.Write(0xFE6E, 0xA0); m.User.Write(0xFE68, 16); m.User.Write(0xFE69, 0);
            m.Cpu.registers.PC = 0x200;
            for (int i = 0; i < 64 && m.Cpu.registers.PC < 0x400; i++) m.Cpu.StepInstruction();
            Check.Equal(0x401UL, m.Cpu.registers.PC, "VIA timeout enters IRQ handler through production wiring");
        }));
        tests.Add(("Devices/WiredORInterruptAcknowledgement", () =>
        {
            using var m = new TimingMachine();
            m.System.Write(0xFE4E, 0xA0); m.User.Write(0xFE6E, 0xA0);
            m.System.Write(0xFE48, 8); m.System.Write(0xFE49, 0);
            m.User.Write(0xFE68, 8); m.User.Write(0xFE69, 0);
            for(int i=0;i<32;i++) m.Instruction(0xEA);
            Check.True(m.Cpu.IrqLineAsserted, "two pending sources assert IRQ");
            m.Instruction(0xAD, 0x48, 0xFE);
            Check.True(m.Cpu.IrqLineAsserted, "acknowledging System VIA leaves User VIA IRQ");
            m.Instruction(0xAD, 0x68, 0xFE);
            Check.True(!m.Cpu.IrqLineAsserted, "acknowledging final source releases CPU IRQ");
        }));
        tests.Add(("Devices/StallAdvancesVIATimer", () =>
        {
            using var m = new TimingMachine(); m.User.Write(0xFE64, 0); m.User.Write(0xFE65, 0x40);
            int before = m.Counter(m.User);
            m.Cpu.RequestExternalStallCycles(8); m.Instruction(0xEA);
            Check.Equal(5, before - m.Counter(m.User), "8 stall cycles plus 2 NOP cycles reach VIA");
        }));
    }
}
