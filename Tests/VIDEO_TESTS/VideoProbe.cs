using BBC;
using System.Reflection;

internal sealed class VideoProbe
{
    public byte[] Memory { get; } = new byte[0x10000];
    public HD6845_Video Video { get; }
    public VideoProbe()
    {
        Video = new HD6845_Video(Memory);
        Video.Reset();
        // beebjit video_power_on_reset's MODE 7 fixture, not a claim that a
        // physical CRTC powers up with these register values.
        byte[] registers = { 63, 40, 51, 0x24, 30, 2, 25, 28, 0x93, 18, 0x72, 0x13, 0, 0, 0, 0 };
        for (int i = 0; i < registers.Length; i++) Crtc(i, registers[i]);
        Video.WriteSheila(0xFE20, 0x02);
    }

    public void Crtc(int register, byte value)
    {
        Video.WriteSheila(0xFE00, (byte)register);
        Video.WriteSheila(0xFE01, value);
    }

    public T Read<T>(string field) => (T)typeof(HD6845_Video)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Video)!;

    public void Tick(int cycles) => Video.Tick(cycles);

    public void Until(Func<bool> condition)
    {
        for (int cycles = 0; cycles < 160000; cycles++)
        {
            if (condition()) return;
            Tick(1);
        }
        throw new InvalidOperationException("Video fixture did not reach the requested beam position");
    }
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string reason) where T : IEquatable<T>
    {
        if (!expected.Equals(actual)) throw new InvalidOperationException($"{reason}: expected {expected}, actual {actual}");
    }
}
