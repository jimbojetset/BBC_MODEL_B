internal static class SeddonTimingTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        // IDs, instruction sequences and NMOS costs from pinned TIMINGS lines
        // 630–1120. T2 uses the upstream ten-copy measurement and +16 overhead.
        Add("10", 630, "B9FF10", 0, 0, 4);
        Add("13", 630, "B9FF10", 0, 255, 5);
        Add("60", 640, "BDFF10", 0, 0, 4);
        Add("63", 640, "BDFF10", 255, 0, 5);
        Add("F2", 670, "AD6FFE", 0, 0, 6);
        Add("11", 680, "B96FFE", 0, 0, 6);
        Add("61", 690, "BD6FFE", 0, 0, 6);
        Add("F3", 720, "2400AD6FFE", 0, 0, 8);
        Add("12", 730, "2400B96FFE", 0, 0, 8);
        Add("62", 740, "2400BD6FFE", 0, 0, 8);
        Add("14", 770, "B9FFFB", 0, 255, 6);
        Add("64", 780, "BDFFFB", 255, 0, 6);
        Add("15", 810, "2400B9FFFB", 0, 255, 10);
        Add("65", 820, "2400BDFFFB", 255, 0, 10);
        Add("16", 850, "B96FFE2400", 0, 255, 10);
        Add("66", 860, "BD6FFE2400", 255, 0, 10);
        Add("17", 890, "2400B96FFE2400", 0, 255, 12);
        Add("67", 900, "2400BD6FFE2400", 255, 0, 12);
        Add("18", 930, "B9FFFC", 0, 255, 8);
        Add("68", 940, "BDFFFC", 255, 0, 8);
        Add("19", 970, "2400B9FFFC", 0, 255, 10);
        Add("69", 980, "2400BDFFFC", 255, 0, 10);
        Add("20", 1030, "99FFF0", 0, 0, 5);
        Add("29", 1030, "99FFF0", 0, 255, 5);
        Add("70", 1040, "9DFFF0", 0, 0, 5);
        Add("79", 1040, "9DFFF0", 255, 0, 5);
        Add("23", 1070, "99FFFB", 0, 255, 6);
        Add("73", 1080, "9DFFFB", 255, 0, 6);
        Add("24", 1110, "240099FFFB", 0, 255, 10);
        Add("74", 1120, "24009DFFFB", 255, 0, 10);

        void Add(string id, int line, string hex, byte x, byte y, int cycles)
        {
            tests.Add(($"SourcedBBC/Seddon/{id}", () =>
            {
                using var m = new BbcProgram();
                byte[] sequence = Convert.FromHexString(hex);
                for (int copy = 0; copy < 10; copy++) sequence.CopyTo(m.Ram, 0x4000 + copy * sequence.Length);
                m.Ram[0x4000 + 10 * sequence.Length] = 0x60;
                // Upstream PROCT: (ten copies + JSR/RTS 12 + timer overhead 4) / 2.
                m.Ram[0x74] = (byte)((cycles * 10 + 16) / 2);
                m.Ram[0x75] = 0;
                m.Run(new byte[] {
                    0x08, 0x78,                         // PHP, SEI
                    0xA9, 0x20, 0x8D, 0x6E, 0xFE,       // disable T2 IRQ
                    0xAD, 0x6B, 0xFE, 0x29, 0xDF, 0x8D, 0x6B, 0xFE,
                    0xA2, x, 0xA0, y,
                    0xA5, 0x74, 0x8D, 0x68, 0xFE,
                    0xA5, 0x75, 0x8D, 0x69, 0xFE,
                    0x20, 0x00, 0x40,
                    0xAE, 0x68, 0xFE, 0xAC, 0x69, 0xFE,
                    0x86, 0x76, 0x84, 0x77, 0x28, 0x60
                });
                // Upstream compares only T2END's low byte; do not strengthen or
                // adjust that criterion based on our emulator's observed result.
                Check.Equal((byte)0, m.Ram[0x76],
                    $"TIMINGS line {line}, case &{id}, {cycles} cycles/copy: expected T2END low byte 0");
            }));
        }
    }
}
