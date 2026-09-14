using BBC.CPU;

internal static class CpuTimingTests
{
    private static CPU_6502 Cpu(byte flags = 0x20)
    {
        var cpu = new CPU_6502();
        Array.Fill(cpu.Bus.Memory, (byte)0xEA);
        cpu.Bus.Memory[0xFFFE] = 0; cpu.Bus.Memory[0xFFFF] = 4;
        cpu.Bus.Memory[0xFFFA] = 0; cpu.Bus.Memory[0xFFFB] = 5;
        cpu.registers.PC = 0x200; cpu.registers.P = flags; cpu.registers.S = 0xFF;
        cpu.StepInstruction(); // Establish the interrupt mask at a completed instruction boundary.
        cpu.registers.PC = 0x200;
        return cpu;
    }
    public static void Add(List<(string Name, Action Body)> tests)
    {
        void Add(string name, Action body) => tests.Add(("CPU/" + name, body));
        foreach (var (name, bytes, expected, x, z, pc) in new[] {
            ("NOP", new byte[]{0xEA}, 2, 0, false, 0x200),
            ("LDAImmediate", new byte[]{0xA9,1}, 2, 0, false, 0x200),
            ("LDAAbsolute", new byte[]{0xAD,0,6}, 4, 0, false, 0x200),
            ("LDAIndexedSamePage", new byte[]{0xBD,0,6}, 4, 1, false, 0x200),
            ("LDAIndexedPageCross", new byte[]{0xBD,0xFF,6}, 5, 1, false, 0x200),
            ("STAIndexedSamePage", new byte[]{0x9D,0,6}, 5, 1, false, 0x200),
            ("STAIndexedPageCross", new byte[]{0x9D,0xFF,6}, 5, 1, false, 0x200),
            ("INCAbsolute", new byte[]{0xEE,0,6}, 6, 0, false, 0x200),
            ("BranchNotTaken", new byte[]{0xD0,2}, 2, 0, true, 0x200),
            ("BranchTaken", new byte[]{0xD0,2}, 3, 0, false, 0x200),
            ("BranchPageCross", new byte[]{0xD0,2}, 4, 0, false, 0x2FD),
            ("JSR", new byte[]{0x20,0,6}, 6, 0, false, 0x200),
            ("BRK", new byte[]{0}, 7, 0, false, 0x200)
        })
        {
            Add("Cycles/" + name, () =>
            {
                var cpu = Cpu(); cpu.registers.PC = (ushort)pc; cpu.registers.X = (byte)x; cpu.registers.Flags.Z = z;
                bytes.CopyTo(cpu.Bus.Memory, pc);
                long before = cpu.TotalCycles; int delivered = 0;
                cpu.OnCyclesExecuted = n => delivered += n;
                Check.Equal(expected, cpu.StepInstruction(), "instruction cycle count");
                Check.Equal((long)expected, cpu.TotalCycles - before, "CPU clock accounting");
                Check.Equal(expected, delivered, "device callback receives every cycle exactly once");
            });
        }
        Add("Interrupts/CLIHasOneInstructionDelay", () =>
        {
            var c = Cpu(0x24); c.Bus.Memory[0x200] = 0x58; c.SetIrqLine(true);
            c.StepInstruction(); Check.Equal(0x201UL, c.registers.PC, "CLI executes while masked");
            c.StepInstruction(); Check.Equal(0x202UL, c.registers.PC, "one instruction follows CLI before IRQ");
            c.StepInstruction(); Check.Equal(0x401UL, c.registers.PC, "IRQ entry and handler NOP");
        });
        Add("Interrupts/PLPHasOneInstructionDelay", () =>
        {
            var c = Cpu(0x24); c.Bus.Memory[0x200] = 0x28; c.registers.S = 0xFE; c.Bus.Memory[0x1FF] = 0x20;
            c.SetIrqLine(true); c.StepInstruction(); c.StepInstruction();
            Check.Equal(0x202UL, c.registers.PC, "PLP unmask delayed by one instruction");
            c.StepInstruction(); Check.Equal(0x401UL, c.registers.PC, "IRQ after PLP delay");
        });
        Add("Interrupts/SEIUsesPreviousMask", () =>
        {
            var c = Cpu(); c.Bus.Memory[0x200] = 0x78;
            c.OnCyclesExecuted = _ => c.SetIrqLine(true);
            c.StepInstruction(); c.OnCyclesExecuted = null; c.StepInstruction();
            Check.Equal(0x401UL, c.registers.PC, "IRQ sampled during SEI still accepted");
        });
        Add("Interrupts/RTIUnmasksImmediately", () =>
        {
            var c = Cpu(0x24); c.Bus.Memory[0x200] = 0x40; c.registers.S = 0xFC;
            c.Bus.Memory[0x1FD] = 0x20; c.Bus.Memory[0x1FE] = 0; c.Bus.Memory[0x1FF] = 6;
            c.SetIrqLine(true); c.StepInstruction(); c.StepInstruction();
            Check.Equal(0x401UL, c.registers.PC, "pending IRQ immediately after RTI");
        });
        Add("Interrupts/PolledIRQSurvivesDeassertion", () =>
        {
            var c = Cpu(); int elapsed = 0;
            // Two-cycle NOP polls at its first cycle; release IRQ at the final cycle.
            c.OnCyclesExecuted = n => { elapsed += n; c.SetIrqLine(elapsed == 1); };
            c.StepInstruction(); c.OnCyclesExecuted = null; c.StepInstruction();
            Check.Equal(0x401UL, c.registers.PC, "accepted IRQ retained until instruction boundary");
        });
        foreach (bool nmi in new[]{false,true})
            Add("Interrupts/" + (nmi ? "NMI" : "IRQ") + "CycleDelivery", () =>
            {
                var c = Cpu(); int delivered = 0; long before = c.TotalCycles;
                c.OnCyclesExecuted = n => delivered += n;
                if (nmi) c.NmiLineAsserted = () => true; else c.SetIrqLine(true);
                Check.Equal(9, c.StepInstruction(), "7 entry cycles plus 2-cycle handler NOP");
                Check.Equal(9, delivered, "interrupt cycles delivered to devices");
                Check.Equal(9L, c.TotalCycles - before, "interrupt cycles included in CPU clock");
            });
        Add("Interrupts/NMIHeldLineDoesNotRetrigger", () =>
        {
            var c = Cpu(0x24); bool line = true; c.NmiLineAsserted = () => line;
            c.StepInstruction(); Check.Equal(0x501UL, c.registers.PC, "NMI bypasses I mask");
            c.StepInstruction(); Check.Equal(0x502UL, c.registers.PC, "held line has no second edge");
            line = false; c.StepInstruction(); line = true; c.StepInstruction();
            Check.Equal(0x501UL, c.registers.PC, "new edge retriggers NMI");
        });
        Add("Interrupts/NMIPriorityOverIRQ", () =>
        {
            var c = Cpu(); c.SetIrqLine(true); c.NmiLineAsserted = () => true; c.StepInstruction();
            Check.Equal(0x501UL, c.registers.PC, "NMI selected when both lines assert");
            Check.Equal((byte)0xFC, c.registers.S, "only one interrupt stack frame");
        });
        Add("Cycles/ExternalStallReachesDevices", () =>
        {
            var c = Cpu(); long before = c.TotalCycles; int delivered = 0;
            c.OnCyclesExecuted = n => delivered += n; c.RequestExternalStallCycles(4);
            Check.Equal(6, c.StepInstruction(), "stall plus NOP");
            Check.Equal(6L, c.TotalCycles - before, "stalled CPU clock");
            Check.Equal(6, delivered, "devices continue during CPU stall");
        });
    }
}
