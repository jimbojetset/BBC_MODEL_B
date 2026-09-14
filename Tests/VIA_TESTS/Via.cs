using BBC;

// Exercise both real VIA implementations through the same register-level tests.
// No CPU or 1MHz bus wait states run here: Tick receives 2MHz CPU cycles directly.
internal sealed class Via : IDisposable
{
    private readonly SN76489_Sound? sound;
    public System6522Via? System { get; }
    public User6522Via? User { get; }
    public ushort BaseAddress => System is not null ? (ushort)0xFE40 : (ushort)0xFE60;
    public Via(bool system)
    {
        if (system)
        {
            sound = new SN76489_Sound();
            System = new System6522Via(sound);
            System.Reset();
            System.ExternalVsyncLineEnabled = true;
        }
        else
        {
            User = new User6522Via();
            User.Reset();
        }
    }
    public byte Read(int register) => System is not null
        ? System.Read((ushort)(BaseAddress + register)) : User!.Read((ushort)(BaseAddress + register));
    public void Write(int register, byte value)
    {
        if (System is not null) System.Write((ushort)(BaseAddress + register), value);
        else User!.Write((ushort)(BaseAddress + register), value);
    }
    public void Tick(int cycles)
    {
        if (System is not null) System.Tick(cycles); else User!.Tick(cycles);
    }
    public bool Irq => System?.IrqAsserted ?? User!.IrqAsserted;
    public void Reset() { if (System is not null) System.Reset(); else User!.Reset(); }
    public void LoadTimer(int timer, ushort value)
    {
        int low = timer == 1 ? 4 : 8;
        Write(low, (byte)value);
        Write(low + 1, (byte)(value >> 8));
    }
    public int Counter(int timer)
    {
        int low = timer == 1 ? 4 : 8;
        return Read(low) | (Read(low + 1) << 8);
    }
    public void Advance(int cpuCycles)
    {
        // Two-cycle chunks model successive VIA clocks without skipping underflows.
        for (int i = 0; i < cpuCycles; i += 2) Tick(Math.Min(2, cpuCycles - i));
    }
    public int AwaitFlag(byte mask, int limit = 4096)
    {
        for (int cycles = 0; cycles <= limit; cycles += 2)
        {
            if ((Read(13) & mask) != 0) return cycles;
            Tick(2);
        }
        throw new InvalidOperationException($"IFR ${mask:X2} did not assert within {limit} CPU cycles");
    }
    public void AcknowledgeTimer(int timer)
    {
        // Avoid reading on the same edge as an underflow; that race is a separate test.
        Tick(2);
        Read(timer == 1 ? 4 : 8);
    }
    public byte[] Save()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (System is not null) System.SaveState(writer); else User!.SaveState(writer);
        return stream.ToArray();
    }
    public void Load(byte[] state)
    {
        using var reader = new BinaryReader(new MemoryStream(state));
        if (System is not null) System.LoadState(reader); else User!.LoadState(reader);
    }
    public void Dispose() => sound?.Dispose();
}
internal static class Check
{
    public static void Equal<T>(T expected, T actual, string reason) where T : IEquatable<T>
    {
        if (!expected.Equals(actual)) throw new InvalidOperationException($"{reason}: expected {expected}, actual {actual}");
    }
    public static void True(bool condition, string reason) => Equal(true, condition, reason);
}
