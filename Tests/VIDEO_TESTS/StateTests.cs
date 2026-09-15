// Local save-state regressions, not physical BBC measurements.
internal static class StateTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("Unverified/SaveState/BetweenInterlaceVsyncEdges", () =>
        {
            var p = new VideoProbe();
            p.Tick(28 * 10 * 128 + 4);
            RoundTrip(p, restored =>
            {
                restored.Crtc(8, 0);
                restored.Tick(2);
                restored.Crtc(8, 3);
                restored.Tick(64);
            });
        }));
        tests.Add(("Unverified/SaveState/BeforeSixteenthVsyncLine", () =>
        {
            var p = new VideoProbe();
            p.Crtc(8, 0); p.Crtc(9, 9); p.Crtc(7, 0); p.Crtc(3, 4);
            p.Tick(327 * 128);
            RoundTrip(p, restored => restored.Tick(128));
        }));
        tests.Add(("Unverified/SaveState/BetweenUlaHalfCells", () =>
        {
            var p = new VideoProbe();
            p.Crtc(8, 0);
            p.Crtc(12, 0x28);
            p.Crtc(10, 0x20);
            Array.Fill(p.Memory, (byte)0xFF, 0x7C00, 0x400);
            p.Video.WriteSheila(0xFE20, 0x88);
            p.Tick(80000);
            p.Until(() => p.Read<int>("beamVerticalCounter") == 10
                && p.Read<int>("beamHorizontalCounter") == 4 && p.Read<bool>("beamOddClock"));
            RoundTrip(p, restored =>
            {
                for (int i = 0; i < 6; i++)
                {
                    restored.Video.WriteSheila(0xFE20, (byte)(i % 2 == 0 ? 0x8A : 0x88));
                    restored.Tick(11);
                }
            });
        }));
    }

    private static byte[] Save(VideoProbe p)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        p.Video.SaveState(writer);
        return stream.ToArray();
    }

    private static void RoundTrip(VideoProbe original, Action<VideoProbe> advance)
    {
        var restored = new VideoProbe();
        original.Memory.CopyTo(restored.Memory, 0);
        using var stream = new MemoryStream(Save(original));
        using var reader = new BinaryReader(stream);
        restored.Video.LoadState(reader);
        Check.Equal(stream.Length, stream.Position, "video state consumed");
        var originalEdges = new List<bool>();
        var restoredEdges = new List<bool>();
        original.Video.VsyncChanged += originalEdges.Add;
        restored.Video.VsyncChanged += restoredEdges.Add;
        advance(original);
        advance(restored);
        if (!originalEdges.SequenceEqual(restoredEdges))
            throw new InvalidOperationException("VSYNC edges differ after restore");
        if (!Save(original).SequenceEqual(Save(restored)))
            throw new InvalidOperationException("Video state/framebuffer differs after restore");
    }
}
