using BBC;

internal static class SystemTests
{
    public static void Add(Action<string, Action<Via>> add)
    {
        add("Address/Decode", v =>
        {
            Check.True(System6522Via.IsAddress(0xFE40) && System6522Via.IsAddress(0xFE5F), "system VIA address range");
            Check.True(!System6522Via.IsAddress(0xFE3F) && !System6522Via.IsAddress(0xFE60), "adjacent devices excluded");
        });
        add("Vsync/EdgeAndReadHandshake", v =>
        {
            v.Write(12, 1); v.Write(14, 0x82);
            v.System!.SetVsyncLine(true);
            Check.True(v.Irq, "VSYNC asserts enabled CA1 IRQ");
            v.Read(15);
            Check.True(v.Irq, "no-handshake alias preserves CA1");
            v.Read(1);
            Check.True(!v.Irq, "ORA read acknowledges CA1");
            v.System.SetVsyncLine(true);
            Check.True(!v.Irq, "steady VSYNC cannot retrigger");
            v.System.SetVsyncLine(false); v.System.SetVsyncLine(true);
            Check.True(v.Irq, "next VSYNC edge retriggers");
        });
        add("Vsync/WriteHandshake", v =>
        {
            v.Write(12, 1); v.System!.SetVsyncLine(true);
            v.Write(15, 0x55);
            Check.Equal((byte)2, (byte)(v.Read(13) & 2), "no-handshake write preserves CA1");
            v.Write(1, 0xAA);
            Check.Equal((byte)0, (byte)(v.Read(13) & 2), "ORA write acknowledges CA1");
        });
        add("Vsync/ExternalDisablesSynthetic", v =>
        {
            v.Advance(80000);
            Check.Equal((byte)0, (byte)(v.Read(13) & 2), "external VSYNC mode waits for video input");
        });
        add("Keyboard/SelectedKey", v =>
        {
            v.Write(2, 0x0F); v.Write(0, 3); // IC32 keyboard auto-scan disable.
            v.Write(3, 0x7F); v.Write(15, 0x41);
            Check.Equal((byte)0x41, v.Read(15), "unpressed selected key");
            v.System!.SetKeyState(0x41, true);
            Check.Equal((byte)0xC1, v.Read(15), "selected key pulls sense active");
            v.Write(15, 0x42);
            Check.Equal((byte)0x42, v.Read(15), "another key remains unpressed");
            v.System.SetKeyState(0x41, false); v.Write(15, 0x41);
            Check.Equal((byte)0x41, v.Read(15), "key release");
        });
        add("Keyboard/AutoscanInterrupt", v =>
        {
            v.Write(14, 0x81); v.System!.SetKeyState(0x41, true);
            Check.True(v.Irq, "auto-scan keyboard interrupt");
            v.Write(13, 1);
            Check.True(!v.Irq, "keyboard source can be acknowledged");
        });
        add("IC32/ScreenAddressLatch", v =>
        {
            v.Write(2, 0x0F);
            foreach (var (code, start, size) in new[] { (0, 0x4000, 0x4000), (1, 0x6000, 0x2000),
                (2, 0x3000, 0x5000), (3, 0x5800, 0x2800) })
            {
                v.Write(0, (byte)(4 | ((code & 1) << 3)));
                v.Write(0, (byte)(5 | ((code & 2) << 2)));
                Check.Equal(start, v.System!.ScreenMemoryStart, "screen RAM start");
                Check.Equal(size, v.System.ScreenMemorySize, "screen wrap size");
                v.Write(0, 0x0B);
                Check.Equal(start, v.System.ScreenMemoryStart, "other IC32 bit preserves screen latch");
            }
        });
        add("ADC/InterruptLatchesUntilAcknowledged", v =>
        {
            v.Write(14, 0x90);
            v.System!.SignalAdcEndOfConversion(true);
            Check.True(v.Irq, "ADC completion sets CB1 interrupt");
            v.System.SignalAdcEndOfConversion(false);
            Check.True(v.Irq, "returning CB1 to idle does not acknowledge its latched flag");
            v.Write(13, 0x10);
            Check.True(!v.Irq, "IFR write acknowledges ADC interrupt");
        });
    }
}
