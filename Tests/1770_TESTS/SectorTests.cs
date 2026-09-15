using BBC;

// Local image round trips only: these do not establish WD1770 hardware behaviour.
internal static class SectorTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        foreach (byte track in new byte[] { 0, 39 })
            tests.Add(($"Unverified/SoftwareRegression/SectorRoundTrip/Track{track}", () => RoundTrip(track)));
    }

    private static void RoundTrip(byte track)
    {
        var fdc = new WD1770_Disk();
        byte[] image = new byte[40 * 10 * 256];
        byte[] expected = Enumerable.Range(0, 256).Select(i => (byte)(i ^ 0xA5)).ToArray();
        expected.CopyTo(image, (track * 10 + 9) * 256);
        fdc.MountImage(image, 0, null, "roundtrip.ssd", false);
        fdc.Write(0xFE80, 0x29);
        fdc.Write(0xFE87, track);
        fdc.Write(0xFE84, 0x18);
        int elapsed = 0;
        while ((fdc.Read(0xFE84) & 1) != 0)
        {
            fdc.Tick(16);
            if ((elapsed += 16) >= 4_000_000) throw new TimeoutException("seek did not finish");
        }
        Transfer(fdc, expected, false);
        for (int i = 0; i < expected.Length; i++) expected[i] ^= 0xFF;
        Transfer(fdc, expected, true);
        Transfer(fdc, expected, false);
    }

    private static void Transfer(WD1770_Disk fdc, byte[] expected, bool write)
    {
        fdc.Write(0xFE86, 9);
        fdc.Write(0xFE84, write ? (byte)0xA8 : (byte)0x88);
        int count = 0;
        for (int ticks = 0; ticks < 4_000_000; ticks += 16)
        {
            byte status = fdc.Read(0xFE84);
            if ((status & 2) != 0)
            {
                if (count >= expected.Length) throw new InvalidOperationException("extra sector byte requested");
                if (write) fdc.Write(0xFE87, expected[count]);
                else Check.Equal(expected[count], fdc.Read(0xFE87), $"read byte {count}");
                count++;
            }
            else if ((status & 1) == 0)
            {
                Check.Equal(0, status & 0x7C, "transfer errors");
                Check.Equal(expected.Length, count, "transferred byte count");
                return;
            }
            fdc.Tick(16);
        }
        throw new TimeoutException("1770 sector transfer did not complete within the harness limit");
    }
}
