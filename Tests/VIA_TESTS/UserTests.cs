using BBC;

internal static class UserTests
{
    public static void Add(Action<string, Action<Via>> add)
    {
        add("Address/Decode", v =>
        {
            Check.True(User6522Via.IsAddress(0xFE60) && User6522Via.IsAddress(0xFE7F), "user VIA address range");
            Check.True(!User6522Via.IsAddress(0xFE5F) && !User6522Via.IsAddress(0xFE80), "adjacent devices excluded");
        });
        add("Ports/ExternalInputBits", v =>
        {
            v.User!.SetPortBInputBits(0x03, 0x01);
            Check.Equal((byte)1, (byte)(v.Read(0) & 3), "external PB0 high and PB1 low");
            v.User.SetPortBInputBits(0x03, 0xFE);
            Check.Equal((byte)2, (byte)(v.Read(0) & 3), "external value is masked");
        });
        add("Ports/PortBOutputReadback", v =>
        {
            v.User!.SetPortBInputBits(0x03, 0);
            v.Write(2, 0x03); v.Write(0, 0x03);
            Check.Equal((byte)3, (byte)(v.Read(0) & 3), "ORB output bits read output latch regardless of input level");
        });
        add("Timer2/PulseModeIgnoresElapsedTime", v =>
        {
            v.User!.SetPortBInputBits(0x40, 0x40);
            v.Write(11, 0x20); v.LoadTimer(2, 16);
            int before = v.Counter(2); v.Advance(1024);
            Check.Equal(before, v.Counter(2), "T2 pulse mode waits for PB6 falling edges");
            Check.Equal((byte)0, (byte)(v.Read(13) & 0x20), "no PB6 pulses means no T2 timeout");
        });
        add("Timer2/PB6FallingEdges", v =>
        {
            v.User!.SetPortBInputBits(0x40, 0x40); v.Write(11, 0x20); v.LoadTimer(2, 16);
            // Both instances receive identical PHI2 clocks, but only one sees PB6 pulses.
            using var steady = new Via(false);
            steady.User!.SetPortBInputBits(0x40, 0x40); steady.Write(11, 0x20); steady.LoadTimer(2, 16);
            for (int i = 0; i < 4; i++)
            {
                v.User.SetPortBInputBits(0x40, 0); v.Tick(4); steady.Tick(4);
                v.User.SetPortBInputBits(0x40, 0x40); v.Tick(4); steady.Tick(4);
            }
            Check.Equal(4, (steady.Counter(2) - v.Counter(2)) & 0xFFFF, "four falling edges cause four decrements");
        });
        add("Printer/ManualStrobe", v =>
        {
            var bytes = new List<byte>();
            v.User!.PrinterEnabled = true; v.User.PrinterByteWritten = bytes.Add;
            v.Write(3, 0xFF); v.Write(1, 0x41);
            v.Write(12, 0x0E); v.Write(12, 0x0C); v.Write(12, 0x0C);
            Check.True(bytes.SequenceEqual(new byte[] { 0x41 }), "one falling CA2 strobe sends one byte");
            Check.Equal((byte)2, (byte)(v.Read(13) & 2), "printer acknowledgment raises CA1");
        });
        add("Printer/InputPortDoesNotTransmit", v =>
        {
            int count = 0; v.User!.PrinterEnabled = true; v.User.PrinterByteWritten = _ => count++;
            v.Write(3, 0); v.Write(1, 0x41); v.Write(12, 0x0E); v.Write(12, 0x0C);
            Check.Equal(0, count, "input-configured port must not send printer data");
        });
        add("Handshake/PortAAcknowledgesCA1", v =>
        {
            v.User!.PrinterEnabled = true; v.User.PrinterByteWritten = _ => { };
            v.Write(3, 0xFF); v.Write(12, 0x0E); v.Write(12, 0x0C);
            v.Read(15);
            Check.Equal((byte)2, (byte)(v.Read(13) & 2), "no-handshake alias preserves CA1");
            v.Read(1);
            Check.Equal((byte)0, (byte)(v.Read(13) & 2), "ORA read acknowledges CA1");
        });
    }
}
