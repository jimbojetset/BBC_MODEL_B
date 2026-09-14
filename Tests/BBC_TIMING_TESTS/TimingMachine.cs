using BBC;
using BBC.CPU;
using System.Reflection;

internal sealed class TimingMachine : IDisposable
{
    public Emulator Emulator { get; } = new();
    public CPU_6502 Cpu => Emulator.Cpu;
    public byte[] Ram => Emulator.Memory.Memory;
    public User6522Via User { get; }
    public System6522Via System { get; }
    public TimingMachine()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Install the production memory map without loading ROMs or starting threads.
        // Reflection is confined to setup; measured accesses use the real CPU/bus callbacks.
        typeof(Emulator).GetMethod("InstallMemoryMapHooks", flags)!.Invoke(Emulator, null);
        User = (User6522Via)typeof(Emulator).GetField("userVia", flags)!.GetValue(Emulator)!;
        System = (System6522Via)typeof(Emulator).GetField("systemVia", flags)!.GetValue(Emulator)!;
        User.Reset(); System.Reset(); System.ExternalVsyncLineEnabled = true;
        Cpu.OnBeforeInstruction = null;
        Array.Fill(Ram, (byte)0xEA, 0x200, 0x600);
        Cpu.registers.PC = 0x200; Cpu.registers.P = 0x24;
        Cpu.StepInstruction();
        Cpu.registers.PC = 0x200;
    }
    public int Instruction(params byte[] bytes)
    {
        bytes.CopyTo(Ram, 0x200); Cpu.registers.PC = 0x200;
        return Cpu.StepInstruction();
    }
    public int Counter(User6522Via via) => via.Read(0xFE64) | (via.Read(0xFE65) << 8);
    public void Dispose() => Emulator.Dispose();
}
internal static class Check
{
    public static void Equal<T>(T expected, T actual, string reason) where T : IEquatable<T>
    {
        if (!expected.Equals(actual)) throw new InvalidOperationException($"{reason}: expected {expected}, actual {actual}");
    }
    public static void True(bool condition, string reason) => Equal(true, condition, reason);
}
