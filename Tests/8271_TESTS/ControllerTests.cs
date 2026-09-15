using BBC;

// Adapted from jsbeeb's Intel 8271 status tests. No per-case hardware results.
internal static class ControllerTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("Unverified/jsbeeb/8271/Idle", () =>
            Check.Equal((byte)0, new Intel8271_Disk().Read(0xFE80), "initial status")));
        tests.Add(("Unverified/jsbeeb/8271/ModeWriteStatus", () =>
        {
            var fdc = new Intel8271_Disk();
            Send(fdc, 0x3A, 0x17, 0xC1);
            Check.Equal((byte)0, fdc.Read(0xFE80), "status after mode write");
            Send(fdc, 0x2C);
            Check.Equal((byte)0x10, fdc.Read(0xFE80), "drive status result ready");
        }));
        tests.Add(("Unverified/jsbeeb/8271/ModeReadback", () =>
        {
            var fdc = new Intel8271_Disk();
            Send(fdc, 0x3A, 0x17, 0xC1);
            Send(fdc, 0x3D, 0x17);
            Check.Equal((byte)0x81, fdc.Read(0xFE81), "mode bits excluding command full");
        }));
        tests.Add(("Unverified/jsbeeb/8271/CommandBusyWithoutFull", () =>
        {
            var fdc = new Intel8271_Disk();
            Send(fdc, 0x3A);
            Check.Equal(0x80, fdc.Read(0xFE80) & 0xC0, "busy / command full");
        }));
        tests.Add(("Unverified/jsbeeb/8271/ParametersWithoutFull", () =>
        {
            var fdc = new Intel8271_Disk();
            Send(fdc, 0x3A, 0x17);
            Check.Equal(0x80, fdc.Read(0xFE80) & 0xA0, "busy / parameter full after first parameter");
            Send(fdc, 0x3A, 0x17, 0xC1);
            Check.Equal(0, fdc.Read(0xFE80) & 0xA0, "busy / parameter full after mode write");
        }));
    }

    internal static void Send(Intel8271_Disk fdc, byte command, params byte[] parameters)
    {
        fdc.Write(0xFE80, command);
        foreach (byte value in parameters) fdc.Write(0xFE81, value);
    }
}
