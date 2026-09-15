using BBC;

// Local image round trips only: these do not establish 8271 hardware behaviour.
internal static class SectorTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        foreach (byte track in new byte[] { 0, 39 })
            tests.Add(($"Unverified/SoftwareRegression/SectorRoundTrip/Track{track}", () => RoundTrip(track)));
    }

    private static void RoundTrip(byte track)
    {
        var fdc = new Intel8271_Disk();
        byte[] image = new byte[40 * 10 * 256];
        byte[] expected = Enumerable.Range(0, 256).Select(i => (byte)(i ^ 0xA5)).ToArray();
        expected.CopyTo(image, (track * 10 + 9) * 256);
        fdc.MountImage(image, 0, null, "roundtrip.ssd", false);
        Transfer(fdc, track, expected, false);
        for (int i = 0; i < expected.Length; i++) expected[i] ^= 0xFF;
        Transfer(fdc, track, expected, true);
        Transfer(fdc, track, expected, false);
    }

    private static void Transfer(Intel8271_Disk fdc, byte track, byte[] expected, bool write)
    {
        ControllerTests.Send(fdc, write ? (byte)0x4B : (byte)0x53, track, 9, 0x21);
        int count = 0;
        for (int ticks = 0; ticks < 4_000_000; ticks += 16)
        {
            byte status = fdc.Read(0xFE80);
            if ((status & 4) != 0)
            {
                if (count >= expected.Length) throw new InvalidOperationException("extra sector byte requested");
                if (write) fdc.Write(0xFE84, expected[count]);
                else Check.Equal(expected[count], fdc.Read(0xFE84), $"read byte {count}");
                count++;
            }
            else if ((status & 0x10) != 0)
            {
                Check.Equal((byte)0, fdc.Read(0xFE81), "transfer result");
                Check.Equal(expected.Length, count, "transferred byte count");
                return;
            }
            fdc.Tick(16);
        }
        throw new TimeoutException("8271 sector transfer did not complete within the harness limit");
    }
}
