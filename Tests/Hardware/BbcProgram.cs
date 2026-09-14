using BBC;
using System.Reflection;

// Shared by the external VIA and timing programs: actual NMOS CPU, BBC bus and VIAs.
internal sealed class BbcProgram : IDisposable
{
    public Emulator Emulator { get; } = new();
    public byte[] Ram => Emulator.Memory.Memory;

    public BbcProgram()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Emulator).GetMethod("InstallMemoryMapHooks", flags)!.Invoke(Emulator, null);
        var system = (System6522Via)typeof(Emulator).GetField("systemVia", flags)!.GetValue(Emulator)!;
        var user = (User6522Via)typeof(Emulator).GetField("userVia", flags)!.GetValue(Emulator)!;
        system.Reset();
        user.Reset();
        system.ExternalVsyncLineEnabled = true;
        Emulator.Cpu.OnBeforeInstruction = null;
        Emulator.Cpu.registers.P = 0x24;
        Emulator.Cpu.registers.S = 0xFF;
    }

    public void Run(byte[] code, int address = 0x2000)
    {
        // These programs finish with RTS. Stop before that final return, since no
        // BASIC CALL frame was installed. Internal JSR/RTS pairs still execute.
        if (code.Length == 0 || code[^1] != 0x60)
            throw new InvalidOperationException("BBC test program must end with RTS");
        code.CopyTo(Ram, address);
        Emulator.Cpu.registers.PC = (ulong)address;
        int end = address + code.Length - 1;
        for (int steps = 0; steps < 100000; steps++)
        {
            if (Emulator.Cpu.registers.PC == (ulong)end) return;
            Emulator.Cpu.StepInstruction();
        }
        throw new InvalidOperationException($"BBC program did not reach final RTS; PC=${Emulator.Cpu.registers.PC:X4}");
    }

    public void Dispose() => Emulator.Dispose();
}
