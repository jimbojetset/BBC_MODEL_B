// Adapted from Chris Evans' beebjit test-video.c; see PROVENANCE.md and retained COPYING.
internal static class CrtcTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("SourcedBBC/Beebjit/CRTC/NoDummyRasterAfterInterlaceDisabled", () =>
        {
            var p = new VideoProbe();
            bool sawVsync = false;
            p.Video.VsyncChanged += level => sawVsync |= level;
            // video_test_no_dummy_raster: 40000 - 128, then disable R8,
            // then half a line. Upstream explicitly reports hardware confirmation.
            p.Tick(39872);
            if (!sawVsync) throw new InvalidOperationException("CRTC fixture did not advance through VSYNC");
            Check.Equal(false, p.Read<bool>("beamInDummyRaster"), "before R8 write");
            p.Crtc(8, 0);
            p.Tick(64);
            Check.Equal(false, p.Read<bool>("beamInDummyRaster"), "dummy raster suppressed");
        }));
        tests.Add(("Unverified/Beebjit/CRTC/ZeroVsyncWidthMeans16Lines", () =>
        {
            var p = new VideoProbe();
            p.Crtc(8, 0);
            p.Crtc(9, 9);
            p.Crtc(7, 0);
            p.Crtc(3, 0x04);
            p.Tick(312 * 128);
            Check.Equal(true, p.Read<bool>("beamInVSync"), "R7=0 starts VSYNC");
            p.Tick(15 * 128);
            Check.Equal(true, p.Read<bool>("beamInVSync"), "VSYNC after 15 lines");
            p.Tick(128);
            Check.Equal(false, p.Read<bool>("beamInVSync"), "VSYNC after 16 lines");
        }));
        tests.Add(("Unverified/Beebjit/CRTC/VsyncDuringVerticalAdjust", () =>
        {
            var p = new VideoProbe();
            p.Crtc(8, 0);
            p.Crtc(9, 9);
            p.Crtc(7, 0);
            p.Crtc(3, 0x04);
            p.Tick((312 + 16) * 128);
            p.Crtc(7, 31);
            p.Tick((310 - 16) * 128);
            Check.Equal(31, p.Read<int>("beamVerticalCounter"), "vertical adjust character row");
            Check.Equal(true, p.Read<bool>("beamInVertAdjust"), "vertical adjust active");
            Check.Equal(true, p.Read<bool>("beamInVSync"), "R7=R4+1 starts a second VSYNC");
        }));
        tests.Add(("Unverified/Beebjit/CRTC/InterlaceVsyncMux", () =>
        {
            var p = new VideoProbe();
            p.Tick(28 * 10 * 128);
            Check.Equal(false, p.Read<bool>("beamInVSync"), "even-field VSYNC waits half a line");
            p.Tick(4);
            p.Crtc(8, 0);
            Check.Equal(true, p.Read<bool>("beamInVSync"), "disabling interlace selects odd VSYNC");
            p.Tick(2);
            p.Crtc(8, 3);
            Check.Equal(false, p.Read<bool>("beamInVSync"), "restoring interlace selects even VSYNC");
            p.Tick(64);
            Check.Equal(true, p.Read<bool>("beamInVSync"), "even VSYNC begins after half a line");
        }));
    }
}
