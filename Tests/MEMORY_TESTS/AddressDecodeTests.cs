using BBC;

internal static class AddressDecodeTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        AddBlock("Video", HD6845_Video.IsSheilaAddress, (0xFE00, 0xFE07), (0xFE20, 0xFE2F));
        AddBlock("Serial", SerialACIA.IsAddress, (0xFE08, 0xFE1F));
        AddBlock("SystemVIA", System6522Via.IsAddress, (0xFE40, 0xFE5F));
        AddBlock("UserVIA", User6522Via.IsAddress, (0xFE60, 0xFE7F));
        AddBlock("8271", Intel8271_Disk.IsAddress, (0xFE80, 0xFE9F));
        AddBlock("1770", WD1770_Disk.IsAddress, (0xFE80, 0xFE87));
        AddBlock("ADC", uPD7002_ADC.IsAddress, (0xFEC0, 0xFEDF));
        AddBlock("Tube", TubeUla.IsHostAddress, (0xFEE0, 0xFEFF));

        tests.Add(("Decode/VIAMirrorsAndIsolation", () =>
        {
            using var m = new MemoryMachine();
            m.Write(0xFE52, 0xA5);
            m.Write(0xFE72, 0x5A);
            Check.Byte(0xA5, m.Read(0xFE42), "System VIA DDRB");
            Check.Byte(0x5A, m.Read(0xFE62), "User VIA DDRB");
        }));
        tests.Add(("Decode/ADCMirrors", () =>
        {
            using var m = new MemoryMachine();
            m.Write(0xFEDC, 2);
            Check.Byte(2, (byte)(m.Read(0xFEC0) & 3), "ADC channel");
        }));
        // These preserve the emulator's diagnostic readback; physical ULA/CRTC
        // write-only registers need not return the last value written.
        tests.Add(("Decode/CRTCReadbackRegression", () =>
        {
            using var m = new MemoryMachine();
            m.Write(0xFE06, 1);
            m.Write(0xFE07, 40);
            Check.Byte(1, m.Read(0xFE02), "CRTC register selection");
            Check.Byte(40, m.Read(0xFE05), "CRTC R1");
        }));
        tests.Add(("Decode/ULAReadbackRegression", () =>
        {
            using var m = new MemoryMachine();
            m.Write(0xFE2E, 0x9C);
            m.Write(0xFE2F, 0x47);
            Check.Byte(0x9C, m.Read(0xFE24), "ULA control");
            Check.Byte(0x47, m.Read(0xFE2B), "ULA palette");
        }));

        void AddBlock(string name, Func<ushort, bool> predicate, params (int Start, int End)[] blocks)
        {
            tests.Add(($"Decode/{name}/AddressBlock", () =>
            {
                for (int address = 0; address <= 0xFFFF; address++)
                {
                    bool expected = blocks.Any(b => address >= b.Start && address <= b.End);
                    Check.True(predicate((ushort)address) == expected,
                        $"${address:X4}: expected chip select {expected}");
                }
            }));
        }
    }
}
