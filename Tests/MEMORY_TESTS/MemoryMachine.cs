using BBC;
using System.Reflection;

internal sealed class MemoryMachine : IDisposable
{
    public Emulator Emulator { get; } = new();
    public byte[] Ram => Emulator.Memory.Memory;
    private readonly byte[] banks;
    private string? temporaryDirectory;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public MemoryMachine()
    {
        // Use the production map with synthetic ROM bytes; no MOS boot or external ROMs.
        Invoke("InstallMemoryMapHooks");
        banks = (byte[])typeof(Emulator).GetField("sidewaysRoms", Private)!.GetValue(Emulator)!;
        for (int i = 0; i < 16; i++) Invoke("ClearSidewaysRomBank", i);
        Emulator.Cpu.OnBeforeInstruction = null;
        Emulator.Cpu.registers.P = 0x24;
    }
    public void Invoke(string method, params object[] args)
    {
        try { typeof(Emulator).GetMethod(method, Private)!.Invoke(Emulator, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }
    public void SeedRom(int bank, byte[] data) => data.CopyTo(banks, bank * 0x4000);
    public void LoadRom(int bank, byte[] data)
    {
        temporaryDirectory ??= Path.Combine(Path.GetTempPath(), "bbc-memory-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string file = Path.Combine(temporaryDirectory, $"bank-{bank:X}.rom");
        File.WriteAllBytes(file, data);
        Invoke("SetSidewaysRomBank", bank, file);
    }
    public byte Read(int address) => Emulator.Memory.ReadByte((ushort)address);
    public void Write(int address, byte value) => Emulator.Memory.WriteByte((ushort)address, value);
    public void Select(int bank) => Write(0xFE30, (byte)bank);
    public void Instruction(params byte[] bytes)
    {
        bytes.CopyTo(Ram, 0x200);
        Emulator.Cpu.registers.PC = 0x200;
        Emulator.Cpu.StepInstruction();
    }
    public void Dispose()
    {
        Emulator.Dispose();
        if (temporaryDirectory is not null) Directory.Delete(temporaryDirectory, true);
    }
}
internal static class Check
{
    public static void Byte(byte expected, byte actual, string location)
    {
        if (expected != actual) throw new InvalidOperationException($"{location}: expected ${expected:X2}, actual ${actual:X2}");
    }
    public static void True(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
