using BBC;

// Local interface regressions; no independent BBC timing capture is claimed.
internal static class ResultTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("Unverified/SoftwareRegression/PolledResultAcknowledgement", () =>
        {
            var fdc = new Intel8271_Disk();
            ControllerTests.Send(fdc, 0x2C);
            Check.Equal(false, fdc.NmiLineAsserted, "polled result does not interrupt");
            Check.Equal(0x10, fdc.Read(0xFE80) & 0x18, "result full without interrupt");
            fdc.Read(0xFE81);
            Check.Equal(0, fdc.Read(0xFE80) & 0x18, "reading result acknowledges it");
            ControllerTests.Send(fdc, 0x2C);
            fdc.Reset();
            Check.Equal((byte)0, fdc.Read(0xFE80), "reset discards unread result");
        }));
        tests.Add(("Unverified/SoftwareRegression/SpecifyHasNoResult", () =>
        {
            var fdc = new Intel8271_Disk();
            ControllerTests.Send(fdc, 0x2C);
            ControllerTests.Send(fdc, 0x35, 0x0D, 12, 10, 0xC8);
            Check.Equal((byte)0, fdc.Read(0xFE80), "SPECIFY does not leave a result or interrupt");
            Check.Equal(false, fdc.NmiLineAsserted, "SPECIFY completion");
        }));
        tests.Add(("Unverified/SoftwareRegression/DeferredReadCompletion", () =>
        {
            var fdc = new Intel8271_Disk();
            fdc.MountImage(new byte[40 * 10 * 256], 0, null, "completion.ssd", false);
            ControllerTests.Send(fdc, 0x53, 0, 0, 0x21);
            int count = 0;
            for (int ticks = 0; ticks < 4_000_000; ticks++)
            {
                if ((fdc.Read(0xFE80) & 4) != 0)
                {
                    fdc.Read(0xFE84);
                    if (++count == 256)
                    {
                        Check.Equal(0, fdc.Read(0xFE80) & 0x10, "deferred result is not yet readable");
                        Check.Equal(false, fdc.NmiLineAsserted, "completion interrupt is still deferred");
                        return;
                    }
                }
                fdc.Tick(1);
            }
            throw new TimeoutException("read did not reach deferred completion");
        }));
    }
}
