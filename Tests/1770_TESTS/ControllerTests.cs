using BBC;

// Acorn Model B register layout and WD1770 variant, as in jsbeeb's default fixture.
internal static class ControllerTests
{
    internal static WD1770_Disk Create(bool mounted = false)
    {
        var fdc = new WD1770_Disk();
        if (mounted)
            fdc.MountImage(new byte[40 * 10 * 256], 0, null, "test.ssd", false);
        fdc.Write(0xFE80, 0x21);
        return fdc;
    }

    private static void Seek(WD1770_Disk fdc)
    {
        fdc.Write(0xFE85, 0);
        fdc.Write(0xFE87, 40);
        fdc.Write(0xFE84, 0x18);
        fdc.Tick(1000);
    }

    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("Unverified/jsbeeb/1770/ForceInterrupt/D0", () =>
        {
            var fdc = Create();
            fdc.Write(0xFE84, 0xD0);
            Check.Equal(false, fdc.NmiLineAsserted, "D0 interrupt");
        }));
        tests.Add(("Unverified/jsbeeb/1770/ForceInterrupt/D8", () =>
        {
            var fdc = Create();
            fdc.Write(0xFE84, 0xD8);
            Check.Equal(true, fdc.NmiLineAsserted, "immediate interrupt");
        }));
        tests.Add(("Unverified/jsbeeb/1770/ForceInterrupt/AbortSeek", () =>
        {
            var fdc = Create(true);
            Seek(fdc);
            Check.Equal(1, fdc.Read(0xFE84) & 1, "seek busy before abort");
            fdc.Write(0xFE84, 0xD8);
            Check.Equal(true, fdc.NmiLineAsserted, "abort interrupt");
            fdc.Tick(1000);
            Check.Equal(true, fdc.NmiLineAsserted, "completion retains interrupt");
            Check.Equal(0, fdc.Read(0xFE84) & 1, "busy after abort");
        }));
        tests.Add(("Unverified/jsbeeb/1770/ForceInterrupt/D4Index", () =>
        {
            var fdc = Create();
            fdc.Write(0xFE84, 0xD4);
            Check.Equal(false, fdc.NmiLineAsserted, "before first index");
            fdc.Tick(1000);
            Check.Equal(true, fdc.NmiLineAsserted, "after first index");
        }));
        tests.Add(("Unverified/jsbeeb/1770/ForceInterrupt/DCImmediateAndIndex", () =>
        {
            var fdc = Create();
            fdc.Write(0xFE84, 0xDC);
            Check.Equal(true, fdc.NmiLineAsserted, "immediate interrupt");
            fdc.Read(0xFE84);
            Check.Equal(false, fdc.NmiLineAsserted, "status read acknowledges interrupt");
            fdc.Tick(1000);
            Check.Equal(true, fdc.NmiLineAsserted, "index reasserts interrupt");
        }));
        tests.Add(("Unverified/jsbeeb/1770/ForceInterrupt/DisarmIndex", () =>
        {
            var fdc = Create();
            fdc.Write(0xFE84, 0xD4);
            fdc.Write(0xFE84, 0xD0);
            fdc.Tick(1000);
            Check.Equal(false, fdc.NmiLineAsserted, "disarmed index interrupt");
        }));
        foreach (bool seeking in new[] { false, true })
            tests.Add(($"Unverified/jsbeeb/1770/ForceInterrupt/AllFlags/{(seeking ? "Seeking" : "Idle")}", () =>
            {
                // Upstream checks acceptance only, not the outcome of each flag.
                for (int flags = 0; flags < 16; flags++)
                {
                    var fdc = Create(seeking);
                    if (seeking) Seek(fdc);
                    fdc.Write(0xFE84, (byte)(0xD0 | flags));
                }
            }));
        int[] milliseconds = [6, 12, 20, 30];
        for (int rate = 0; rate < milliseconds.Length; rate++)
        {
            int selectedRate = rate;
            tests.Add(($"Unverified/jsbeeb/1770/StepRate/{rate}", () =>
            {
                var fdc = Create(true);
                fdc.Write(0xFE85, 0);
                fdc.Write(0xFE87, 1);
                fdc.Write(0xFE84, (byte)(0x18 | selectedRate));
                int ticks = 0;
                do
                {
                    if ((fdc.Read(0xFE84) & 2) != 0) fdc.Read(0xFE87);
                    fdc.Tick(16);
                    ticks += 16;
                    if (ticks > 4_000_000) throw new TimeoutException("seek never finished");
                } while ((fdc.Read(0xFE84) & 1) != 0);
                int expected = milliseconds[selectedRate] * 2000 + 64;
                if (ticks < expected || ticks >= expected + 32)
                    throw new InvalidOperationException($"WD1770 rate {selectedRate}: expected [{expected}, {expected + 32}) cycles, got {ticks}");
            }));
        }
    }
}
