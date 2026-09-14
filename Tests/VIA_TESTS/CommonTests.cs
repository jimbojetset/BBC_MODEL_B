internal static class CommonTests
{
    public static void Add(Action<string, Action<Via>> add)
    {
        add("Reset/Registers", v =>
        {
            Check.Equal((byte)0, v.Read(2), "DDRB inputs");
            Check.Equal((byte)0, v.Read(3), "DDRA inputs");
            Check.Equal((byte)0, v.Read(11), "ACR reset");
            Check.Equal((byte)0, v.Read(12), "PCR reset");
            Check.Equal((byte)0, v.Read(13), "IFR reset");
            Check.Equal((byte)0x80, v.Read(14), "IER reads bit 7 high");
            Check.True(!v.Irq, "IRQ inactive");
        });
        add("Registers/Mirror", v =>
        {
            v.Write(0x12, 0xA5);
            Check.Equal((byte)0xA5, v.Read(2), "16-byte register mirror");
            v.Write(11, 0x40);
            Check.Equal((byte)0x40, v.Read(0x1B), "ACR mirrored read");
        });
        add("Interrupts/EnableSetClear", v =>
        {
            v.Write(14, 0xC0); v.Write(14, 0xA0);
            Check.Equal((byte)0xE0, v.Read(14), "enable writes accumulate");
            v.Write(14, 0x40);
            Check.Equal((byte)0xA0, v.Read(14), "clear selected enable only");
            v.Write(14, 0x80);
            Check.Equal((byte)0xA0, v.Read(14), "empty enable mask");
        });
        add("Interrupts/MaskedPending", v =>
        {
            v.LoadTimer(2, 8); v.AwaitFlag(0x20);
            Check.Equal((byte)0x20, v.Read(13), "masked timer still sets IFR");
            Check.True(!v.Irq, "masked IRQ inactive");
            v.Write(14, 0xA0);
            Check.Equal((byte)0xA0, v.Read(13), "enabling pending source sets summary");
            Check.True(v.Irq, "pending IRQ asserted");
            v.Write(14, 0x20);
            Check.Equal((byte)0x20, v.Read(13), "disabling preserves pending flag");
            Check.True(!v.Irq, "disabled IRQ released");
        });
        add("Interrupts/SelectiveAcknowledge", v =>
        {
            v.Write(14, 0xE0); v.LoadTimer(1, 8); v.LoadTimer(2, 8);
            v.AwaitFlag(0x40); v.AwaitFlag(0x20); v.Tick(2);
            v.Write(13, 0x40);
            Check.Equal((byte)0xA0, v.Read(13), "clearing T1 leaves T2 and summary");
            v.Write(13, 0x80);
            Check.Equal((byte)0xA0, v.Read(13), "IFR7 write cannot clear sources");
            v.Write(13, 0x20);
            Check.Equal((byte)0, v.Read(13), "all sources acknowledged");
            Check.True(!v.Irq, "IRQ released");
        });
        add("Ports/PortADirection", v =>
        {
            v.Write(15, 0xA5); v.Write(3, 0xF0);
            Check.Equal((byte)0xAF, v.Read(15), "output high nibble and undriven input low nibble");
            v.Write(3, 0xFF);
            Check.Equal((byte)0xA5, v.Read(1), "direction change preserves output latch");
        });
        add("Ports/PortBAlias", v =>
        {
            v.Write(2, 0xFF); v.Write(0, 0xA5);
            Check.Equal((byte)0xA5, v.Read(0x10), "port B output latch through mirror");
        });
        add("Timer1/LatchReadback", v =>
        {
            v.Write(6, 0x34); v.Write(7, 0x12);
            Check.Equal((byte)0x34, v.Read(6), "low latch");
            Check.Equal((byte)0x12, v.Read(7), "high latch");
            v.Advance(2048);
            Check.Equal((byte)0, (byte)(v.Read(13) & 0x40), "latch-only write does not start timer");
        });
        add("Timer1/LatchWriteDoesNotReload", v =>
        {
            v.LoadTimer(1, 0x1000); v.Tick(20);
            int before = v.Counter(1);
            v.Write(6, 0x20); v.Write(7, 0);
            Check.Equal(before, v.Counter(1), "latch-only update preserves running counter");
        });
        foreach (int timer in new[] { 1, 2 })
        {
            byte flag = timer == 1 ? (byte)0x40 : (byte)0x20;
            int low = timer == 1 ? 4 : 8;
            add($"Timer{timer}/ClockRate", v =>
            {
                v.LoadTimer(timer, 0x4000);
                int before = v.Counter(timer); v.Tick(20);
                Check.Equal((before - 10) & 0xFFFF, v.Counter(timer), "20 CPU cycles equal 10 VIA clocks");
            });
            add($"Timer{timer}/FirstTimeout", v =>
            {
                const int n = 16;
                v.LoadTimer(timer, n); v.Advance(2 * n);
                Check.Equal((byte)0, (byte)(v.Read(13) & flag), "no premature underflow");
                // Allow one VIA clock of API phase uncertainty beyond the N+2 interval.
                v.Advance(6);
                Check.Equal(flag, (byte)(v.Read(13) & flag), "timeout by N+3 VIA clocks");
            });
            add($"Timer{timer}/OneShotAndRearm", v =>
            {
                v.LoadTimer(timer, 16); v.AwaitFlag(flag); v.AcknowledgeTimer(timer);
                v.Advance(1024);
                Check.Equal((byte)0, (byte)(v.Read(13) & flag), "one-shot must not interrupt again");
                v.LoadTimer(timer, 16); v.AwaitFlag(flag);
            });
            add($"Timer{timer}/HighReadPreservesLowReadClears", v =>
            {
                v.LoadTimer(timer, 16); v.AwaitFlag(flag); v.Tick(2);
                v.Read(low + 1);
                Check.Equal(flag, (byte)(v.Read(13) & flag), "high counter read preserves interrupt");
                v.Read(low);
                Check.Equal((byte)0, (byte)(v.Read(13) & flag), "low counter read acknowledges interrupt");
            });
            add($"Timer{timer}/ReloadClearsInterrupt", v =>
            {
                v.LoadTimer(timer, 16); v.AwaitFlag(flag); v.Tick(2);
                v.LoadTimer(timer, 0x1234);
                Check.Equal((byte)0, (byte)(v.Read(13) & flag), "counter high write acknowledges interrupt");
            });
        }
        add("Timer1/FreeRunPeriod", v =>
        {
            v.Write(11, 0x40); v.LoadTimer(1, 16); v.AwaitFlag(0x40);
            v.AcknowledgeTimer(1);
            Check.Equal(34, v.AwaitFlag(0x40), "N+2 VIA clocks between interrupts, less acknowledgment clock");
        });
        add("Timer1/PB7OneShot", v =>
        {
            v.Write(2, 0x80); v.Write(0, 0x80); v.Write(11, 0x80);
            v.LoadTimer(1, 16);
            Check.Equal((byte)0, (byte)(v.Read(0) & 0x80), "T1 load drives PB7 low");
            v.AwaitFlag(0x40);
            Check.Equal((byte)0x80, (byte)(v.Read(0) & 0x80), "one-shot underflow drives PB7 high");
        });
        add("Timer1/PB7FreeRun", v =>
        {
            v.Write(2, 0x80); v.Write(0, 0); v.Write(11, 0xC0); v.LoadTimer(1, 16);
            v.AwaitFlag(0x40);
            Check.Equal((byte)0x80, (byte)(v.Read(0) & 0x80), "first underflow toggles PB7 high");
            v.AcknowledgeTimer(1); v.AwaitFlag(0x40);
            Check.Equal((byte)0, (byte)(v.Read(0) & 0x80), "second underflow toggles PB7 low");
        });
        add("Timer1/LatchReadPreservesInterrupt", v =>
        {
            v.LoadTimer(1, 16); v.AwaitFlag(0x40); v.Tick(2);
            v.Read(6); v.Read(7);
            Check.Equal((byte)0x40, (byte)(v.Read(13) & 0x40), "latch reads do not acknowledge");
            v.Write(7, 0);
            Check.Equal((byte)0, (byte)(v.Read(13) & 0x40), "high latch write acknowledges");
        });
        add("Reset/ClearsRunningInterrupts", v =>
        {
            v.Write(14, 0xE0); v.LoadTimer(1, 8); v.LoadTimer(2, 8);
            v.AwaitFlag(0x40); v.Reset(); v.Advance(2048);
            Check.Equal((byte)0, v.Read(13), "reset stops timers and clears sources");
            Check.Equal((byte)0x80, v.Read(14), "reset clears enables");
            Check.True(!v.Irq, "reset releases IRQ");
        });
        add("State/DeterministicContinuation", v =>
        {
            v.Write(11, 0x40); v.Write(14, 0xE0); v.LoadTimer(1, 23); v.LoadTimer(2, 31);
            v.Tick(7); byte[] state = v.Save();
            byte[] Trace()
            {
                var trace = new List<byte>();
                for (int i = 0; i < 400; i++) { v.Tick(1); trace.Add(v.Read(13)); if (i % 19 == 0) v.Read(4); }
                return trace.ToArray();
            }
            byte[] expected = Trace(); v.Load(state);
            Check.True(expected.SequenceEqual(Trace()), "restored timer phase and interrupt sequence");
        });
    }
}
