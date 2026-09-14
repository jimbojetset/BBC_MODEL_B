internal static class BusTimingTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        foreach (var (name, address, slow) in new[] {
            ("RAM",0x600,false), ("ROM",0xC000,false), ("FRED",0xFC00,true), ("JIM",0xFD00,true),
            ("CRTC",0xFE00,true), ("ACIA",0xFE08,true), ("SerialULA",0xFE10,true),
            ("VideoULA",0xFE20,false), ("ROMSEL",0xFE30,false), ("SystemVIA",0xFE43,true),
            ("UserVIA",0xFE63,true), ("FDC",0xFE80,false), ("ADC",0xFEC0,true), ("Tube",0xFEE0,false)
        })
        {
            foreach (bool write in new[]{false,true})
                tests.Add(($"Bus/{name}/{(write ? "Write" : "Read")}", () =>
                {
                    int[] times = new int[2];
                    for (int phase = 0; phase < 2; phase++)
                    {
                        using var m = new TimingMachine();
                        if (phase == 1) m.Instruction(0x24, 0x10); // 3-cycle BIT changes the clock phase.
                        long before = m.Cpu.TotalCycles;
                        times[phase] = m.Instruction(write ? (byte)0x8D : (byte)0xAD, (byte)address, (byte)(address >> 8));
                        Check.Equal((long)times[phase], m.Cpu.TotalCycles - before, "elapsed instruction cycles");
                    }
                    Array.Sort(times);
                    Check.True(times.SequenceEqual(slow ? new[]{5,6} : new[]{4,4}),
                        $"absolute access at both phases: expected {(slow ? "5,6" : "4,4")}, actual {string.Join(',', times)}");
                }));
        }
        tests.Add(("Bus/ReadModifyWriteStretchesAllThreeAccesses", () =>
        {
            var times = new List<int>();
            for (int phase = 0; phase < 2; phase++)
            {
                using var m = new TimingMachine();
                if (phase == 1) m.Instruction(0x24, 0x10);
                times.Add(m.Instruction(0xEE, 0x63, 0xFE)); // INC User VIA DDRA: read, old write, new write.
                Check.Equal((byte)1, m.User.Read(0xFE63), "INC updates VIA register");
            }
            times.Sort();
            Check.True(times.SequenceEqual(new[]{9,10}), $"RMW includes three stretched cycles: actual {string.Join(',',times)}");
        }));
        tests.Add(("Bus/PeekDoesNotAdvanceClock", () =>
        {
            using var m = new TimingMachine(); long before = m.Cpu.TotalCycles;
            m.Cpu.PeekByte(0xFE63);
            Check.Equal(before, m.Cpu.TotalCycles, "debug read cannot stretch CPU clock");
        }));
    }
}
