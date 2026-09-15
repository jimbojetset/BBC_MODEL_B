// Adapted from jsbeeb hardware teletext T8; measured on one Master, not a Model B.
internal static class UlaTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        foreach (bool phase in new[] { false, true })
            tests.Add(($"SourcedBBC/MasterMeasurement/ULA/T8HalfCellSwitching/Phase{(phase ? 1 : 0)}", () => RunT8(phase)));
    }

    private static void RunT8(bool phase)
    {
        var p = new VideoProbe();
        // MODE 4 setup from beebjit's fixture, with T8's direct register overrides.
        p.Crtc(4, 38);
        p.Crtc(5, 0);
        p.Crtc(6, 26);
        p.Crtc(7, 35);
        p.Crtc(8, 0);
        p.Crtc(9, 7);
        p.Crtc(10, 0x20);
        p.Crtc(12, 0x28);
        p.Crtc(13, 0);
        p.Video.WriteSheila(0xFE20, 0x88);
        // In the solid part of T8 the bitmap is red; teletext output is white/black.
        p.Video.WriteSheila(0xFE21, 0xF6);
        Array.Fill(p.Memory, (byte)0xFF, 0x7C00, 0x400);
        p.Tick(80000);
        p.Until(() => p.Read<int>("beamVerticalCounter") == 10
            && p.Read<int>("beamScanlineCounter") == 3
            && p.Read<int>("beamHorizontalCounter") == 4
            && p.Read<bool>("beamOddClock") == phase);
        int startX = p.Read<int>("beamBitmapX");
        int y = p.Read<int>("beamBitmapY");
        for (int flip = 0; flip < 6; flip++)
        {
            p.Video.WriteSheila(0xFE20, flip % 2 == 0 ? (byte)0x8A : (byte)0x88);
            p.Tick(11);
        }
        p.Tick(8);
        int width = (int)typeof(BBC.HD6845_Video)
            .GetField("BeamFramebufferWidth", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        uint[] pixels = p.Read<uint[]>("beamRenderFrame");
        int from = startX - 16;
        int to = startX + 6 * 11 * 8 + 16;
        if (from < 0 || to >= width || y < 0 || (y + 1) * width > pixels.Length)
            throw new InvalidOperationException("T8 measurement region is outside the framebuffer");
        var boxes = new List<(int Start, int Width)>();
        for (int x = from; x < to;)
        {
            if (pixels[y * width + x] == 0xFFFF0000) { x++; continue; }
            int start = x;
            while (x < to && pixels[y * width + x] != 0xFFFF0000) x++;
            boxes.Add((start, x - start));
        }
        string observed = string.Join(", ", boxes.Select(b => b.Width));
        Check.Equal(3, boxes.Count, $"T8 teletext boxes; widths [{observed}]");
        foreach (var box in boxes)
            Check.Equal(88, box.Width, $"T8 measured width 5.5 cells (16 pixels/cell); widths [{observed}]");
        for (int i = 1; i < boxes.Count; i++)
            Check.Equal(88, boxes[i].Start - boxes[i - 1].Start - boxes[i - 1].Width,
                "T8 red gap width 5.5 cells");
    }
}
