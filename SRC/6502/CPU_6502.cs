// ============================================================================
// Project:     BBC
// File:        CPU_6502.cs
// Description: NMOS 6502 core for the BBC Micro, including IRQ/NMI stack
//              semantics, page-cross timing, and common undocumented opcodes.
// Author:      James Booth
// Created:     2025
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BBC.CPU
{

    /// <summary>
    /// The BBC Model B uses a 2 MHz NMOS 6502. Exact interrupt stack bits,
    /// branch timing, and undocumented opcodes matter for games, demos, and MOS.
    /// </summary>
    public class CPU_6502
    {

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private const uint TimerResolutionMs = 1;

        public Registers registers = new Registers();

        public FlatMemoryBus memory = new FlatMemoryBus(0x10000);
        private FlatMemoryBus bus = null!;

        private bool running = true;
        private bool paused;
        public bool Paused => Volatile.Read(ref paused);
        private bool jammed;
        private bool jamReported;
        private ulong jamAddress;
        public bool Jammed => jammed;
        public ushort JamAddress => (ushort)(jamAddress & 0xFFFF);

        private readonly int clockFreq = 2000000;

        private readonly ConcurrentQueue<ulong> IRQ_Buffer = new ConcurrentQueue<ulong>();
        private readonly ConcurrentQueue<ulong> NMI_Buffer = new ConcurrentQueue<ulong>();
        private int irqLineAsserted;
        private bool nmiLineWasAsserted;

        public void InitiateIRQ(ulong value)
        {
            IRQ_Buffer.Enqueue(value);
        }

        public void SetIrqLine(bool asserted)
        {
            Volatile.Write(ref irqLineAsserted, asserted ? 1 : 0);
        }

        public bool IrqLineAsserted => Volatile.Read(ref irqLineAsserted) != 0;

        public void SetOverflowInput()
        {
            registers.Flags.V = true;
        }

        public void InitiateNMI(ulong value)
        {
            NMI_Buffer.Enqueue(value);
        }

        private int cyclesThisOperation = 0;
        private int cyclesNotifiedThisInstruction;
        private long totalCycles;
        public long TotalCycles => Interlocked.Read(ref totalCycles);
        public Action<int>? OnCyclesExecuted;
        public Func<bool>? OnBeforeInstruction;
        public Func<bool>? NmiLineAsserted;
        private int externalStallCycles;

        /// <summary>
        /// BBC peripheral pages are slower than main RAM; SHEILA, FRED, and JIM
        /// can stretch accesses onto the 1 MHz bus.
        /// </summary>
        public Func<ulong, int>? OnAccessStretch;

        private bool deferredIFlagPending;
        private bool deferredIFlagValue;
        private bool iFlagBeforeInstruction;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ScheduleIFlag(bool value)
        {
            deferredIFlagPending = true;
            deferredIFlagValue = value;
        }

        public void RequestExternalStallCycles(int cycles)
        {
            if (cycles <= 0) return;
            Interlocked.Add(ref externalStallCycles, cycles);
        }

        public CPU_6502(int freq = 1000000)
        {
            Initialise();
            clockFreq = freq;
        }

        public CPU_6502(FlatMemoryBus bus, int freq = 1000000)
        {
            Initialise();
            Bus = bus;
            clockFreq = freq;
        }

        public FlatMemoryBus Bus
        {
            get => bus;
            set => bus = value ?? throw new ArgumentNullException(nameof(value));
        }

        public void Initialise()
        {
            registers = new Registers();
            registers.Clear();
            registers.S = 0xFF;
            memory = new FlatMemoryBus(0x10000);
            bus = memory;
        }

        public Action? OnReset;
        private int resetPending;

        public void RequestReset() => Interlocked.Exchange(ref resetPending, 1);

        public void Stop() => Volatile.Write(ref running, false);

        public void ResetNow()
        {
            DoReset();
        }

        public void SetPaused(bool value) => Volatile.Write(ref paused, value);

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(registers.PC);
            writer.Write(registers.S);
            writer.Write(registers.P);
            writer.Write(registers.A);
            writer.Write(registers.X);
            writer.Write(registers.Y);
            writer.Write(Interlocked.Read(ref totalCycles));
            writer.Write(jammed);
            writer.Write(jamReported);
            writer.Write(jamAddress);
            writer.Write(nmiLineWasAsserted);
            writer.Write(Volatile.Read(ref irqLineAsserted));
            writer.Write(externalStallCycles);
            writer.Write(deferredIFlagPending);
            writer.Write(deferredIFlagValue);
            writer.Write(iFlagBeforeInstruction);
        }

        public void LoadState(BinaryReader reader)
        {
            registers.PC = reader.ReadUInt64();
            registers.S = reader.ReadByte();
            registers.P = reader.ReadByte();
            registers.A = reader.ReadByte();
            registers.X = reader.ReadByte();
            registers.Y = reader.ReadByte();
            Interlocked.Exchange(ref totalCycles, reader.ReadInt64());
            jammed = reader.ReadBoolean();
            jamReported = reader.ReadBoolean();
            jamAddress = reader.ReadUInt64();
            nmiLineWasAsserted = reader.ReadBoolean();
            Interlocked.Exchange(ref resetPending, 0);
            Volatile.Write(ref irqLineAsserted, reader.ReadInt32());
            externalStallCycles = reader.ReadInt32();
            deferredIFlagPending = reader.ReadBoolean();
            deferredIFlagValue = reader.ReadBoolean();
            iFlagBeforeInstruction = reader.ReadBoolean();

            while (IRQ_Buffer.TryDequeue(out _)) { }
            while (NMI_Buffer.TryDequeue(out _)) { }
        }

        private void DoReset()
        {
            OnReset?.Invoke();
            registers.Clear();
            registers.S = 0xFF;
            registers.Flags.I = true;
            registers.PC = ReadWordFromBus(0xFFFC);
            jammed = false;
            jamReported = false;
            jamAddress = 0;
            nmiLineWasAsserted = false;

            while (IRQ_Buffer.TryDequeue(out _)) { }
            while (NMI_Buffer.TryDequeue(out _)) { }
            Interlocked.Exchange(ref totalCycles, 0);
        }

        // Small slices keep raster IRQ effects from arriving in visible bursts.
        private const int SliceCycles = 64;

        public void Run(ulong startVector = 0xFFFC)
        {
            registers.PC = ReadWordFromBus(startVector);
            running = true;

            int sliceCycles = SliceCycles;
            long ticksPerSlice = Math.Max(1, Stopwatch.Frequency * SliceCycles / Math.Max(1, clockFreq));
            long nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;

            bool timerRaised = TryBeginHighResolutionTimer();
            try
            {
                while (Volatile.Read(ref running))
                {
                    if (Volatile.Read(ref paused))
                    {
                        Thread.Sleep(2);
                        nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;
                        continue;
                    }

                    if (Interlocked.Exchange(ref resetPending, 0) == 1)
                    {
                        DoReset();
                        nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;
                    }

                    if (jammed)
                    {
                        if (!jamReported)
                        {
                            Console.Error.WriteLine($"CPU jammed at ${jamAddress & 0xFFFF:X4}");
                            jamReported = true;
                        }

                        WaitUntil(nextDeadline);
                        nextDeadline += ticksPerSlice;

                        long nowJammed = Stopwatch.GetTimestamp();
                        if (nextDeadline < nowJammed - ticksPerSlice * 4)
                            nextDeadline = nowJammed + ticksPerSlice;
                        continue;
                    }

                    cyclesThisOperation = 0;
                    while (cyclesThisOperation < sliceCycles)
                    {
                        int stall = Interlocked.Exchange(ref externalStallCycles, 0);
                        if (stall > 0)
                        {
                            cyclesThisOperation += stall;
                            Interlocked.Add(ref totalCycles, stall);
                            continue;
                        }

                        bool nmiLineAsserted = NmiLineAsserted?.Invoke() == true;
                        if (nmiLineAsserted && !nmiLineWasAsserted)
                            ProcessNMI();
                        nmiLineWasAsserted = nmiLineAsserted;

                        while (NMI_Buffer.TryDequeue(out ulong nmiValue))
                        {
                            if (nmiValue != 0xFFFA)
                                ProcessNMI(nmiValue);
                            else
                                ProcessNMI();
                        }
                        bool irqGate = !iFlagBeforeInstruction;
                        while (irqGate && IRQ_Buffer.TryDequeue(out ulong irqValue))
                        {
                            if (irqValue != 0xFFFE)
                                ProcessIRQ(irqValue);
                            else
                                ProcessIRQ();
                            irqGate = !registers.Flags.I;
                        }
                        if (irqGate && Volatile.Read(ref irqLineAsserted) != 0)
                            ProcessIRQ();
                        int beforeCycles = cyclesThisOperation;
                        cyclesNotifiedThisInstruction = 0;
                        bool handledByHost = OnBeforeInstruction?.Invoke() == true;

                        if (!handledByHost)
                        {
                            iFlagBeforeInstruction = registers.Flags.I;
                            Execute(GetNextByteInstruction());
                            if (deferredIFlagPending)
                            {
                                registers.Flags.I = deferredIFlagValue;
                                deferredIFlagPending = false;
                            }
                        }
                        else
                        {
                            iFlagBeforeInstruction = registers.Flags.I;
                        }

                        int deltaCycles = cyclesThisOperation - beforeCycles;
                        if (handledByHost && deltaCycles <= 0)
                            deltaCycles = 6;

                        if (deltaCycles > 0)
                        {
                            if (handledByHost)
                                cyclesThisOperation += deltaCycles;

                            int remainingCycles = Math.Max(0, deltaCycles - cyclesNotifiedThisInstruction);
                            if (remainingCycles > 0)
                            {
                                Interlocked.Add(ref totalCycles, remainingCycles);
                                OnCyclesExecuted?.Invoke(remainingCycles);
                            }
                        }
                        if (jammed)
                            break;
                    }

                    WaitUntil(nextDeadline);

                    nextDeadline += ticksPerSlice;

                    long now = Stopwatch.GetTimestamp();
                    if (nextDeadline < now - ticksPerSlice * 4)
                        nextDeadline = now + ticksPerSlice;
                }
            }
            finally
            {
                if (timerRaised) TryEndHighResolutionTimer();
            }
        }

        private static bool TryBeginHighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { return TimeBeginPeriod(TimerResolutionMs) == 0 /* TIMERR_NOERROR */; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static void TryEndHighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows()) return;
            try { TimeEndPeriod(TimerResolutionMs); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static void WaitUntil(long deadlineTicks)
        {
            long remaining = deadlineTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;

            long remainingMs = remaining * 1000 / Stopwatch.Frequency;

            if (remainingMs > 1)
                Thread.Sleep((int)(remainingMs - 1));

            while (Stopwatch.GetTimestamp() < deadlineTicks)
                Thread.SpinWait(64);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Execute(byte opcode)
        {
            switch (opcode)
            {
                #region Documented Opcodes

                #region NOP

                case 0xEA:
                    cyclesThisOperation += 2;
                    break;

                #endregion NOP

                #region LD*

                case 0xA9:
                    LDA_IM();
                    break;

                case 0xAD:
                    LDA_AB();
                    break;

                case 0xBD:
                    LDA_ABX();
                    break;

                case 0xB9:
                    LDA_ABY();
                    break;

                case 0xA5:
                    LDA_ZP();
                    break;

                case 0xB5:
                    LDA_ZPX();
                    break;

                case 0xA1:
                    LDA_ZPIX();
                    break;

                case 0xB1:
                    LDA_ZPIY();
                    break;

                case 0xA2:
                    LDX_IM();
                    break;

                case 0xAE:
                    LDX_AB();
                    break;

                case 0xBE:
                    LDX_ABY();
                    break;

                case 0xA6:
                    LDX_ZP();
                    break;

                case 0xB6:
                    LDX_ZPY();
                    break;

                case 0xA0:
                    LDY_IM();
                    break;

                case 0xAC:
                    LDY_AB();
                    break;

                case 0xBC:
                    LDY_ABX();
                    break;

                case 0xA4:
                    LDY_ZP();
                    break;

                case 0xB4:
                    LDY_ZPX();
                    break;

                #endregion LD*

                #region ST*

                case 0x8E:
                    STX_AB();
                    break;

                case 0x86:
                    STX_ZP();
                    break;

                case 0x96:
                    STX_ZPY();
                    break;

                case 0x8C:
                    STY_AB();
                    break;

                case 0x84:
                    STY_ZP();
                    break;

                case 0x94:
                    STY_ZPX();
                    break;

                case 0x8D:
                    STA_AB();
                    break;

                case 0x9D:
                    STA_ABX();
                    break;

                case 0x99:
                    STA_ABY();
                    break;

                case 0x85:
                    STA_ZP();
                    break;

                case 0x95:
                    STA_ZPX();
                    break;

                case 0x81:
                    STA_ZPIX();
                    break;

                case 0x91:
                    STA_ZPIY();
                    break;

                #endregion ST*

                #region T**

                case 0xAA:
                    TAX();
                    break;

                case 0xA8:
                    TAY();
                    break;

                case 0xBA:
                    TSX();
                    break;

                case 0x8A:
                    TXA();
                    break;

                case 0x9A:
                    TXS();
                    break;

                case 0x98:
                    TYA();
                    break;

                #endregion T**

                #region SE*

                case 0x38:
                    SEC();
                    break;

                case 0xF8:
                    SED();
                    break;

                case 0x78:
                    SEI();
                    break;

                #endregion SE*

                #region PH*

                case 0x48:
                    PHA();
                    break;

                case 0x08:
                    PHP();
                    break;

                #endregion PH*

                #region PL*

                case 0x68:
                    PLA();
                    break;

                case 0x28:
                    PLP();
                    break;

                #endregion PL*

                #region CL*

                case 0x18:
                    CLC();
                    break;

                case 0xD8:
                    CLD();
                    break;

                case 0x58:
                    CLI();
                    break;

                case 0xB8:
                    CLV();
                    break;

                #endregion CL*

                #region DE*

                case 0xCE:
                    DECA();
                    break;

                case 0xDE:
                    DECXA();
                    break;

                case 0xC6:
                    DECZP();
                    break;

                case 0xD6:
                    DECXZP();
                    break;

                case 0xCA:
                    DEX();
                    break;

                case 0x88:
                    DEY();
                    break;

                #endregion DE*

                #region IN*

                case 0xEE:
                    INCA();
                    break;

                case 0xFE:
                    INCXA();
                    break;

                case 0xE6:
                    INCZP();
                    break;

                case 0xF6:
                    INCXZP();
                    break;

                case 0xE8:
                    INX();
                    break;

                case 0xC8:
                    INY();
                    break;

                #endregion IN*

                #region CM*

                case 0xC9:
                    CMPI();
                    break;

                case 0xCD:
                    CMPA();
                    break;

                case 0xDD:
                    CMPXA();
                    break;

                case 0xD9:
                    CMPYA();
                    break;

                case 0xC5:
                    CMPZ();
                    break;

                case 0xD5:
                    CMPXZ();
                    break;

                case 0xC1:
                    CMPXZI();
                    break;

                case 0xD1:
                    CMPYZI();
                    break;

                #endregion CM*

                #region CPX

                case 0xE0:
                    CPXI();
                    break;

                case 0xEC:
                    CPXA();
                    break;

                case 0xE4:
                    CPXZ();
                    break;

                #endregion CPX

                #region CPY

                case 0xC0:
                    CPYI();
                    break;

                case 0xCC:
                    CPYA();
                    break;

                case 0xC4:
                    CPYZ();
                    break;

                #endregion CPY

                #region ADC

                case 0x69:
                    ADCI();
                    break;

                case 0x6D:
                    ADCA();
                    break;

                case 0x7D:
                    ADCXA();
                    break;

                case 0x79:
                    ADCYA();
                    break;

                case 0x65:
                    ADCZ();
                    break;

                case 0x75:
                    ADCXZ();
                    break;

                case 0x61:
                    ADCXZI();
                    break;

                case 0x71:
                    ADCYZI();
                    break;

                #endregion ADC

                #region SBC

                case 0xE9:
                    SBCI();
                    break;

                case 0xED:
                    SBCA();
                    break;

                case 0xFD:
                    SBCXA();
                    break;

                case 0xF9:
                    SBCYA();
                    break;

                case 0xE5:
                    SBCZ();
                    break;

                case 0xF5:
                    SBCXZ();
                    break;

                case 0xE1:
                    SBCXZI();
                    break;

                case 0xF1:
                    SBCYZI();
                    break;

                #endregion SBC

                #region EOR

                case 0x49:
                    EORI();
                    break;

                case 0x4D:
                    EORA();
                    break;

                case 0x5D:
                    EORXA();
                    break;

                case 0x59:
                    EORYA();
                    break;

                case 0x45:
                    EORZ();
                    break;

                case 0x55:
                    EORXZ();
                    break;

                case 0x41:
                    EORXZI();
                    break;

                case 0x51:
                    EORYZI();
                    break;

                #endregion EOR

                #region ORA

                case 0x09:
                    ORAI();
                    break;

                case 0x0D:
                    ORAA();
                    break;

                case 0x1D:
                    ORAXA();
                    break;

                case 0x19:
                    ORAYA();
                    break;

                case 0x05:
                    ORAZ();
                    break;

                case 0x15:
                    ORAXZ();
                    break;

                case 0x01:
                    ORAXZI();
                    break;

                case 0x11:
                    ORAYZI();
                    break;

                #endregion ORA

                #region AND

                case 0x29:
                    ANDI();
                    break;

                case 0x2D:
                    ANDA();
                    break;

                case 0x3D:
                    ANDXA();
                    break;

                case 0x39:
                    ANDYA();
                    break;

                case 0x25:
                    ANDZ();
                    break;

                case 0x35:
                    ANDXZ();
                    break;

                case 0x21:
                    ANDXZI();
                    break;

                case 0x31:
                    ANDYZI();
                    break;

                #endregion AND

                #region BIT

                case 0x2C:
                    BITA();
                    break;

                case 0x24:
                    BITZ();
                    break;

                #endregion BIT

                #region ASL

                case 0x0A:
                    ASLAC();
                    break;

                case 0x0E:
                    ASLA();
                    break;

                case 0x1E:
                    ASLXA();
                    break;

                case 0x06:
                    ASLZ();
                    break;

                case 0x16:
                    ASLXZ();
                    break;

                #endregion ASL

                #region LSR

                case 0x4A:
                    LSRAC();
                    break;

                case 0x4E:
                    LSRA();
                    break;

                case 0x5E:
                    LSRXA();
                    break;

                case 0x46:
                    LSRZ();
                    break;

                case 0x56:
                    LSRXZ();
                    break;

                #endregion LSR

                #region ROL

                case 0x2A:
                    ROLAC();
                    break;

                case 0x2E:
                    ROLA();
                    break;

                case 0x3E:
                    ROLXA();
                    break;

                case 0x26:
                    ROLZ();
                    break;

                case 0x36:
                    ROLXZ();
                    break;

                #endregion ROL

                #region ROR

                case 0x6A:
                    RORAC();
                    break;

                case 0x6E:
                    RORA();
                    break;

                case 0x7E:
                    RORXA();
                    break;

                case 0x66:
                    RORZ();
                    break;

                case 0x76:
                    RORXZ();
                    break;

                #endregion ROR

                #region BRANCH

                case 0x90:
                    BCC();
                    break;

                case 0xB0:
                    BCS();
                    break;

                case 0xF0:
                    BEQ();
                    break;

                case 0x30:
                    BMI();
                    break;

                case 0xd0:
                    BNE();
                    break;

                case 0x10:
                    BPL();
                    break;

                case 0x50:
                    BVC();
                    break;

                case 0x70:
                    BVS();
                    break;

                case 0x00:
                    BRK();
                    break;

                #endregion BRANCH

                #region J**

                case 0x4C:
                    JMPA();
                    break;

                case 0x6C:
                    JMPAI();
                    break;

                case 0x20:
                    JSRA();
                    break;

                #endregion J**

                #region RT*

                case 0x40:
                    RTI();
                    break;

                case 0x60:
                    RTS();
                    break;

                #endregion RT*

                #region Illegal / undocumented opcodes

                // BBC games and copy-protected loaders do use NMOS illegal opcodes;
                // treating them as NOPs breaks real software.

                // LAX: load A and X from memory together.
                case 0xA3: LAX(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 6; break;
                case 0xA7: LAX(Zero_Page()); cyclesThisOperation += 3; break;
                case 0xAF: LAX(Absolute()); cyclesThisOperation += 4; break;
                case 0xB3: LAX(Zero_Page_Indirect_Y_Indexed()); cyclesThisOperation += 5; break;
                case 0xB7: LAX(Y_Indexed_Zero_Page()); cyclesThisOperation += 4; break;
                case 0xBF: LAX(Y_Indexed_Absolute()); cyclesThisOperation += 4; break;

                // SAX: store A AND X; flags are untouched.
                case 0x83: SAX(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 6; break;
                case 0x87: SAX(Zero_Page()); cyclesThisOperation += 3; break;
                case 0x8F: SAX(Absolute()); cyclesThisOperation += 4; break;
                case 0x97: SAX(Y_Indexed_Zero_Page()); cyclesThisOperation += 4; break;

                // DCP: decrement memory, then compare with A.
                case 0xC3: DCP(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0xC7: DCP(Zero_Page()); cyclesThisOperation += 5; break;
                case 0xCF: DCP(Absolute()); cyclesThisOperation += 6; break;
                case 0xD3: DCP(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0xD7: DCP(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0xDB: DCP(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0xDF: DCP(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                // ISC/ISB: increment memory, then subtract with carry.
                case 0xE3: ISC(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0xE7: ISC(Zero_Page()); cyclesThisOperation += 5; break;
                case 0xEF: ISC(Absolute()); cyclesThisOperation += 6; break;
                case 0xF3: ISC(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0xF7: ISC(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0xFB: ISC(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0xFF: ISC(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                // SLO: shift memory left, then OR into A.
                case 0x03: SLO(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x07: SLO(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x0F: SLO(Absolute()); cyclesThisOperation += 6; break;
                case 0x13: SLO(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x17: SLO(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x1B: SLO(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x1F: SLO(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                // SRE: shift memory right, then EOR into A.
                case 0x43: SRE(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x47: SRE(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x4F: SRE(Absolute()); cyclesThisOperation += 6; break;
                case 0x53: SRE(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x57: SRE(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x5B: SRE(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x5F: SRE(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                // RLA: rotate memory left, then AND into A.
                case 0x23: RLA(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x27: RLA(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x2F: RLA(Absolute()); cyclesThisOperation += 6; break;
                case 0x33: RLA(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x37: RLA(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x3B: RLA(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x3F: RLA(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                // RRA: rotate memory right, then add with carry.
                case 0x63: RRA(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x67: RRA(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x6F: RRA(Absolute()); cyclesThisOperation += 6; break;
                case 0x73: RRA(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x77: RRA(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x7B: RRA(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x7F: RRA(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                // Multi-byte NOPs still consume operand bytes on NMOS 6502.
                case 0x1A:
                case 0x3A:
                case 0x5A:
                case 0x7A:
                case 0xDA:
                case 0xFA:
                    cyclesThisOperation += 2; break;
                case 0x80:
                case 0x82:
                case 0x89:
                case 0xC2:
                case 0xE2:
                    Immediate(); cyclesThisOperation += 2; break;
                case 0x04:
                case 0x44:
                case 0x64:
                    Zero_Page(); cyclesThisOperation += 3; break;
                case 0x14:
                case 0x34:
                case 0x54:
                case 0x74:
                case 0xD4:
                case 0xF4:
                    X_Indexed_Zero_Page(); cyclesThisOperation += 4; break;
                case 0x0C:
                    Absolute(); cyclesThisOperation += 4; break;
                case 0x1C:
                case 0x3C:
                case 0x5C:
                case 0x7C:
                case 0xDC:
                case 0xFC:
                    X_Indexed_Absolute(); cyclesThisOperation += 4; break;

                // Immediate-only illegal ALU forms.
                case 0x0B: case 0x2B: ANC_IM(); cyclesThisOperation += 2; break;
                case 0x4B: ALR_IM(); cyclesThisOperation += 2; break;
                case 0x6B: ARR_IM(); cyclesThisOperation += 2; break;
                case 0x8B: XAA_IM(); cyclesThisOperation += 2; break;
                case 0xAB: LAX_IM(); cyclesThisOperation += 2; break;
                case 0xBB: LAS_AY(); cyclesThisOperation += 4; break;
                case 0xCB: AXS_IM(); cyclesThisOperation += 2; break;
                case 0xEB: SBCI(); break; // NMOS $EB behaves like SBC #imm.

                // Store-high variants used by some packed or protected code.
                case 0x93: AHX_IY(); cyclesThisOperation += 6; break;
                case 0x9B: TAS_AY(); cyclesThisOperation += 5; break;
                case 0x9C: SHY_AX(); cyclesThisOperation += 5; break;
                case 0x9E: SHX_AY(); cyclesThisOperation += 5; break;
                case 0x9F: AHX_AY(); cyclesThisOperation += 5; break;

                // JAM/KIL halts a real NMOS 6502 until reset.
                case 0x02:
                case 0x12:
                case 0x22:
                case 0x32:
                case 0x42:
                case 0x52:
                case 0x62:
                case 0x72:
                case 0x92:
                case 0xB2:
                case 0xD2:
                case 0xF2:
                    jamAddress = (registers.PC - 1) & 0xFFFF;
                    jammed = true; cyclesThisOperation += 2; break;

                #endregion Illegal / undocumented opcodes

                default:
                    throw new InvalidOperationException($"Unhandled opcode ${opcode:X2} at ${((registers.PC - 1) & 0xFFFF):X4}");

                    #endregion Documented Opcodes
            }
        }

        public int StepInstruction()
        {
            if (jammed)
            {
                if (!jamReported)
                {
                    Console.Error.WriteLine($"CPU jammed at ${jamAddress & 0xFFFF:X4}");
                    jamReported = true;
                }
                return 0;
            }

            int beforeCycles = cyclesThisOperation;
            cyclesNotifiedThisInstruction = 0;
            bool irqGate = !iFlagBeforeInstruction;
            while (irqGate && IRQ_Buffer.TryDequeue(out ulong irqValue))
            {
                if (irqValue != 0xFFFE)
                    ProcessIRQ(irqValue);
                else
                    ProcessIRQ();
                irqGate = !registers.Flags.I;
            }
            if (irqGate && Volatile.Read(ref irqLineAsserted) != 0)
                ProcessIRQ();

            iFlagBeforeInstruction = registers.Flags.I;
            Execute(GetNextByteInstruction());
            if (deferredIFlagPending)
            {
                registers.Flags.I = deferredIFlagValue;
                deferredIFlagPending = false;
            }
            int elapsed = cyclesThisOperation - beforeCycles;
            if (elapsed > 0)
            {
                int remainingCycles = Math.Max(0, elapsed - cyclesNotifiedThisInstruction);
                if (remainingCycles > 0)
                {
                    Interlocked.Add(ref totalCycles, remainingCycles);
                    OnCyclesExecuted?.Invoke(remainingCycles);
                }
            }

            return elapsed;
        }

        #region Illegal opcode helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAX(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.A = v;
            registers.X = v;
            Set_FlagsNZ(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SAX(ulong addr)
        {
            WriteByteToMemory(addr, (byte)(registers.A & registers.X));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DCP(ulong addr)
        {
            byte v = (byte)(ReadByteFromMemory(addr) - 1);
            WriteByteToMemory(addr, v);
            int diff = registers.A - v;
            registers.Flags.C = (diff & 0x100) == 0;
            Set_FlagsNZ((byte)diff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ISC(ulong addr)
        {
            byte v = (byte)(ReadByteFromMemory(addr) + 1);
            WriteByteToMemory(addr, v);
            SBC(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SLO(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.Flags.C = (v & 0x80) != 0;
            v <<= 1;
            WriteByteToMemory(addr, v);
            registers.A |= v;
            Set_FlagsNZ(registers.A);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SRE(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.Flags.C = (v & 0x01) != 0;
            v >>= 1;
            WriteByteToMemory(addr, v);
            registers.A ^= v;
            Set_FlagsNZ(registers.A);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RLA(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            bool oldC = registers.Flags.C;
            registers.Flags.C = (v & 0x80) != 0;
            v = (byte)((v << 1) | (oldC ? 1 : 0));
            WriteByteToMemory(addr, v);
            registers.A &= v;
            Set_FlagsNZ(registers.A);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RRA(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            bool oldC = registers.Flags.C;
            registers.Flags.C = (v & 0x01) != 0;
            v = (byte)((v >> 1) | (oldC ? 0x80 : 0));
            WriteByteToMemory(addr, v);
            ADC(v);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ANC_IM()
        {
            registers.A &= Immediate();
            Set_FlagsNZ(registers.A);
            registers.Flags.C = registers.Flags.N;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ALR_IM()
        {
            byte v = (byte)(registers.A & Immediate());
            registers.Flags.C = (v & 0x01) != 0;
            registers.A = (byte)(v >> 1);
            Set_FlagsNZ(registers.A);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ARR_IM()
        {
            byte v = (byte)(registers.A & Immediate());
            byte r = (byte)((v >> 1) | (registers.Flags.C ? 0x80 : 0));
            registers.A = r;
            Set_FlagsNZ(r);
            if (registers.Flags.D)
            {
                registers.Flags.V = ((v ^ r) & 0x40) != 0;
                if (((v & 0x0F) + (v & 0x01)) > 0x05)
                    registers.A = (byte)((registers.A & 0xF0) | ((registers.A + 0x06) & 0x0F));

                if (((v & 0xF0) + (v & 0x10)) > 0x50)
                {
                    registers.Flags.C = true;
                    registers.A += 0x60;
                }
                else
                {
                    registers.Flags.C = false;
                }
            }
            else
            {
                registers.Flags.C = (r & 0x40) != 0;
                registers.Flags.V = ((r ^ (r << 1)) & 0x40) != 0;
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AXS_IM()
        {
            int t = (registers.A & registers.X) - Immediate();
            registers.X = (byte)t;
            registers.Flags.C = (t & 0x100) == 0;
            Set_FlagsNZ(registers.X);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void XAA_IM()
        {
            registers.A = (byte)((registers.A | 0xEE) & registers.X & Immediate());
            Set_FlagsNZ(registers.A);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAX_IM()
        {
            byte v = (byte)((registers.A | 0xEE) & Immediate());
            registers.A = v;
            registers.X = v;
            Set_FlagsNZ(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAS_AY()
        {
            ulong addr = Y_Indexed_Absolute();
            byte v = (byte)(ReadByteFromMemory(addr) & registers.S);
            registers.A = v;
            registers.X = v;
            registers.S = v;
            Set_FlagsNZ(v);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AHX_IY()
        {
            (ulong addr, byte value) = StoreHighIndirectY((byte)(registers.A & registers.X));
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AHX_AY()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute((byte)(registers.A & registers.X), registers.Y);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TAS_AY()
        {
            byte s = (byte)(registers.A & registers.X);
            registers.S = s;
            (ulong addr, byte value) = StoreHighIndexedAbsolute(s, registers.Y);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SHY_AX()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute(registers.Y, registers.X);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SHX_AY()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute(registers.X, registers.Y);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (ulong Address, byte Value) StoreHighIndexedAbsolute(byte source, byte index)
        {
            ulong baseAddr = Absolute();
            ulong indexedAddr = baseAddr + index;
            byte value = (byte)(source & (((baseAddr >> 8) + 1) & 0xFF));
            byte writeHigh = CrossBoundary(indexedAddr, baseAddr) ? value : (byte)((indexedAddr >> 8) & 0xFF);
            ulong writeAddr = ((ulong)writeHigh << 8) | (indexedAddr & 0xFF);
            return (writeAddr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (ulong Address, byte Value) StoreHighIndirectY(byte source)
        {
            byte zeroPage = GetNextByteInstruction();
            byte low = ReadByteFromMemory(zeroPage);
            byte high = ReadByteFromMemory((byte)(zeroPage + 1));
            ulong baseAddr = (ulong)((high << 8) | low);
            ulong indexedAddr = baseAddr + registers.Y;
            byte value = (byte)(source & (((baseAddr >> 8) + 1) & 0xFF));
            byte writeHigh = CrossBoundary(indexedAddr, baseAddr) ? value : (byte)((indexedAddr >> 8) & 0xFF);
            ulong writeAddr = ((ulong)writeHigh << 8) | (indexedAddr & 0xFF);
            return (writeAddr, value);
        }

        #endregion Illegal opcode helpers

        #region Addressing Modes

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Immediate()
        {
            byte addr = GetNextByteInstruction();
            return addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Absolute()
        {
            ulong addr = GetNextWordInstruction();
            return addr & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong AbsoluteIndirect()
        {
            ulong addr = Absolute();
            byte lo;
            byte hi;
            if ((addr & 0x00FF) == 0xFF)
            {
                cyclesThisOperation += 2;
                lo = ReadByteFromMemory((addr & 0xFF00) + 0xFF);
                hi = ReadByteFromMemory((addr & 0xFF00));
            }
            else
            {
                lo = ReadByteFromMemory(addr);
                hi = ReadByteFromMemory((addr + 1));
            }
            ulong value = (ulong)((hi << 8) | lo);
            return value & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong X_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong baseAddr = Absolute();
            ulong addr = baseAddr + registers.X;
            if (CrossBoundary(addr, baseAddr) && checkBoundary)
            {
                cyclesThisOperation += 1;
                NotifyCyclesDuringInstruction(1);
            }
            return addr & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Y_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong baseAddr = Absolute();
            ulong addr = baseAddr + registers.Y;
            if (CrossBoundary(addr, baseAddr) && checkBoundary)
            {
                cyclesThisOperation += 1;
                NotifyCyclesDuringInstruction(1);
            }
            return addr & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void WriteIndexedAbsolute(byte index, byte value)
        {
            ulong baseAddr = Absolute();
            ulong addr = (baseAddr + index) & 0xFFFF;
            ulong dummyAddr = (baseAddr & 0xFF00) | ((baseAddr + index) & 0x00FF);
            ReadByteFromMemory(dummyAddr);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Zero_Page()
        {
            byte addr = GetNextByteInstruction();
            return addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte X_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.X) & 0xFF);
            return addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Y_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.Y) & 0xFF);
            return addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong X_Indexed_Zero_Page_Indirect()
        {
            byte value = (byte)(GetNextByteInstruction() + registers.X);
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong addr = (ulong)((value2 << 8) | value1);
            return addr & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Zero_Page_Indirect_Y_Indexed(bool checkBoundary = true)
        {
            byte value = GetNextByteInstruction();
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong value3 = (ulong)((value2 << 8) | value1);
            ulong addr = value3 + registers.Y;
            if (CrossBoundary(addr, value3) && checkBoundary)
            {
                cyclesThisOperation += 1;
                NotifyCyclesDuringInstruction(1);
            }
            return addr & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void WriteIndirectY(byte value)
        {
            byte zeroPage = GetNextByteInstruction();
            byte low = ReadByteFromMemory(zeroPage);
            byte high = ReadByteFromMemory((byte)(zeroPage + 1));
            ulong baseAddr = (ulong)((high << 8) | low);
            ulong addr = (baseAddr + registers.Y) & 0xFFFF;
            ulong dummyAddr = (baseAddr & 0xFF00) | ((baseAddr + registers.Y) & 0x00FF);
            ReadByteFromMemory(dummyAddr);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Set_FlagsNZ(byte value)
        {
            registers.Flags.Z = (value == 0);
            registers.Flags.N = ((value & 0x80) != 0);
        }

        #endregion Addressing Modes

        #region Documented Opcodes

        #region LD*

        private void LDA_IM()
        {
            registers.A = Immediate();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        private void LDA_AB()
        {
            registers.A = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        private void LDA_ABX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        private void LDA_ABY()
        {
            registers.A = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        private void LDA_ZP()
        {
            registers.A = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 3;
        }

        private void LDA_ZPX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        private void LDA_ZPIX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }

        private void LDA_ZPIY()
        {
            registers.A = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }

        private void LDX_IM()
        {
            registers.X = Immediate();
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        private void LDX_AB()
        {
            registers.X = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        private void LDX_ABY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        private void LDX_ZP()
        {
            registers.X = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 3;
        }

        private void LDX_ZPY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        private void LDY_IM()
        {
            registers.Y = Immediate();
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }

        private void LDY_AB()
        {
            registers.Y = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        private void LDY_ABX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        private void LDY_ZP()
        {
            registers.Y = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 3;
        }

        private void LDY_ZPX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        #endregion LD*

        #region ST*

        private void STA_AB()
        {
            WriteByteToMemory(Absolute(), registers.A);
            cyclesThisOperation += 4;
        }

        private void STA_ABX()
        {
            WriteIndexedAbsolute(registers.X, registers.A);
            cyclesThisOperation += 5;
        }

        private void STA_ABY()
        {
            WriteIndexedAbsolute(registers.Y, registers.A);
            cyclesThisOperation += 5;
        }

        private void STA_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.A);
            cyclesThisOperation += 3;
        }

        private void STA_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.A);
            cyclesThisOperation += 4;
        }

        private void STA_ZPIX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page_Indirect(), registers.A);
            cyclesThisOperation += 6;
        }

        private void STA_ZPIY()
        {
            WriteIndirectY(registers.A);
            cyclesThisOperation += 6;
        }

        private void STX_AB()
        {
            WriteByteToMemory(Absolute(), registers.X);
            cyclesThisOperation += 4;
        }

        private void STX_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.X);
            cyclesThisOperation += 3;
        }

        private void STX_ZPY()
        {
            WriteByteToMemory(Y_Indexed_Zero_Page(), registers.X);
            cyclesThisOperation += 4;
        }

        private void STY_AB()
        {
            WriteByteToMemory(Absolute(), registers.Y);
            cyclesThisOperation += 4;
        }

        private void STY_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.Y);
            cyclesThisOperation += 3;
        }

        private void STY_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.Y);
            cyclesThisOperation += 4;
        }

        #endregion ST*

        #region T**

        private void TAX()
        {
            registers.X = registers.A;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        private void TAY()
        {
            registers.Y = registers.A;
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }

        private void TSX()
        {
            registers.X = registers.S;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        private void TXA()
        {
            registers.A = registers.X;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        private void TXS()
        {
            registers.S = registers.X;
            cyclesThisOperation += 2;
        }

        private void TYA()
        {
            registers.A = registers.Y;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        #endregion T**

        #region SE*

        private void SEC()
        {
            registers.Flags.C = true;
            cyclesThisOperation += 2;
        }

        private void SED()
        {
            registers.Flags.D = true;
            cyclesThisOperation += 2;
        }

        private void SEI()
        {
            ScheduleIFlag(true);
            cyclesThisOperation += 2;
        }

        #endregion SE*

        #region PH*

        private void PHA()
        {
            PushByteToStack(registers.A);
            cyclesThisOperation += 3;
        }

        private void PHP()
        {
            byte addr = registers.Flags.GetFlagsAsByte();
            addr = (byte)(addr | (1 << 4));
            addr = (byte)(addr | (1 << 5));
            PushByteToStack(addr);
            cyclesThisOperation += 3;
        }

        #endregion PH*

        #region PL*

        private void PLA()
        {
            registers.A = PopByteFromStack();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        private void PLP()
        {
            byte value = PopByteFromStack();
            bool newI = (value & 0x04) != 0;
            registers.Flags.SetFlagsFromByte((byte)(value & ~0x04), 0xCB);
            ScheduleIFlag(newI);
            cyclesThisOperation += 4;
        }

        #endregion PL*

        #region CL*

        private void CLC()
        {
            registers.Flags.C = false;
            cyclesThisOperation += 2;
        }

        private void CLD()
        {
            registers.Flags.D = false;
            cyclesThisOperation += 2;
        }

        private void CLI()
        {
            ScheduleIFlag(false);
            cyclesThisOperation += 2;
        }

        private void CLV()
        {
            registers.Flags.V = false;
            cyclesThisOperation += 2;
        }

        #endregion CL*

        #region DE*

        private void DECA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        private void DECXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 7;
        }

        private void DECZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }

        private void DECXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        private void DEX()
        {
            byte value2 = (byte)(registers.X - 1);
            registers.X = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        private void DEY()
        {
            byte value2 = (byte)(registers.Y - 1);
            registers.Y = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        #endregion DE*

        #region IN*

        private void INCA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)(value1 + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        private void INCXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)(value1 + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 7;
        }

        private void INCZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)(value1 + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }

        private void INCXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)(value1 + 1);
            WriteReadModifyWrite(addr, value1, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        private void INX()
        {
            byte value1 = (byte)(registers.X + 1);
            registers.X = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }

        private void INY()
        {
            byte value1 = (byte)(registers.Y + 1);
            registers.Y = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }

        #endregion IN*

        #region CM*

        private void CMPI()
        {
            byte addr = Immediate();
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        private void CMPA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        private void CMPXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        private void CMPYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        private void CMPZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 3;
        }

        private void CMPXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        private void CMPXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        private void CMPYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }

        #endregion CM*

        #region CPX

        private void CPXI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        private void CPXA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void CPXZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        #endregion CPX

        #region CPY

        private void CPYI()
        {
            byte addr = Immediate();
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        private void CPYA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void CPYZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        #endregion CPY

        #region ADC

        private void ADCI()
        {
            byte value = Immediate();
            ADC(value);
            cyclesThisOperation += 2;
        }

        private void ADCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        private void ADCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        private void ADCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        private void ADCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            ADC(value);
            cyclesThisOperation += 3;
        }

        private void ADCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            ADC(value);
            cyclesThisOperation += 4;
        }

        private void ADCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            ADC(value);
            cyclesThisOperation += 6;
        }

        private void ADCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            ADC(value);
            cyclesThisOperation += 5;
        }

        private void ADC(byte value)
        {
            int carry = registers.Flags.C ? 1 : 0;

            if (registers.Flags.D)
            {
                int low = (registers.A & 0xF) + (value & 0xF) + (registers.Flags.C ? 0x1 : 0);
                bool halfCarry = (low > 0x9);
                int high = (registers.A & 0xF0) + (value & 0xF0) + (halfCarry ? 0x10 : 0);
                registers.Flags.C = (high > 0x9F);
                byte value2 = (byte)((low & 0xF) + (high & 0xF0));
                if (halfCarry)
                    low += 0x6;
                if (registers.Flags.C)
                    high += 0x60;
                registers.Flags.V = ((registers.A ^ value2) & (value ^ value2) & 0x80) != 0;
                registers.A = (byte)((low & 0xF) + (high & 0xF0));
                Set_FlagsNZ(value2);
            }
            else
            {
                int value2 = registers.A + value + carry;
                registers.Flags.V = (((registers.A ^ value2) & 0x80) != 0) && (((registers.A ^ value) & 0x80) == 0);
                registers.Flags.C = value2 > 0xFF;
                registers.A = (byte)(value2);
                Set_FlagsNZ(registers.A);
            }
        }

        #endregion ADC

        #region SBC

        private void SBCI()
        {
            byte value = Immediate();
            SBC(value);
            cyclesThisOperation += 2;
        }

        private void SBCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        private void SBCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        private void SBCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        private void SBCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            SBC(value);
            cyclesThisOperation += 3;
        }

        private void SBCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            SBC(value);
            cyclesThisOperation += 4;
        }

        private void SBCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            SBC(value);
            cyclesThisOperation += 6;
        }

        private void SBCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            SBC(value);
            cyclesThisOperation += 5;
        }

        private void SBC(byte value)
        {
            int carry = registers.Flags.C ? 1 : 0;
            if (registers.Flags.D)
            {
                int low = 0xF + (registers.A & 0xF) - (value & 0xF) + (registers.Flags.C ? 0x1 : 0);
                bool halfCarry = (low > 0xF);
                int high = 0xF0 + (registers.A & 0xF0) - (value & 0xF0) + (halfCarry ? 0x10 : 0);
                registers.Flags.C = (high > 0xFF);
                byte binary = (byte)((low & 0xF) + (high & 0xF0));
                if (!halfCarry)
                    low -= 0x6;
                if (!registers.Flags.C)
                    high -= 0x60;
                registers.Flags.V = ((registers.A ^ binary) & (~value ^ binary) & 0x80) != 0;
                registers.A = (byte)((low & 0xF) + (high & 0xF0));
                Set_FlagsNZ(binary);
            }
            else
            {
                int value2 = 0xFF + registers.A - value + carry;
                registers.Flags.V = ((registers.A ^ value2) & (~value ^ value2) & 0x80) != 0;
                registers.Flags.C = value2 > 0xFF;
                registers.A = (byte)(value2);
                Set_FlagsNZ(registers.A);
            }
        }

        #endregion SBC

        #region EOR

        private void EORI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        private void EORA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void EORXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void EORYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void EORZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        private void EORXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void EORXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        private void EORYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }

        #endregion EOR

        #region ORA

        private void ORAI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        private void ORAA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ORAXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ORAYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ORAZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        private void ORAXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ORAXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        private void ORAYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }

        #endregion ORA

        #region AND

        private void ANDI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        private void ANDA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ANDXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ANDYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ANDZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        private void ANDXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        private void ANDXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        private void ANDYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }

        #endregion AND

        #region BIT

        private void BITA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 4;
        }

        private void BITZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 3;
        }

        #endregion BIT

        #region ASL

        private void ASLAC()
        {
            byte addr = registers.A;
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            Set_FlagsNZ(value);
            registers.A = value;
            cyclesThisOperation += 2;
        }

        private void ASLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        private void ASLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 7;
        }

        private void ASLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 5;
        }

        private void ASLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        #endregion ASL

        #region LSR

        private void LSRAC()
        {
            byte addr = registers.A;
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            registers.Flags.Z = (value == 0);
            registers.A = value;
            cyclesThisOperation += 2;
        }

        private void LSRA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            registers.Flags.Z = (value2 == 0);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        private void LSRXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            registers.Flags.Z = (value2 == 0);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 7;
        }

        private void LSRZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 5;
        }

        private void LSRXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        #endregion LSR

        #region ROL

        private void ROLAC()
        {
            byte addr = registers.A;
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)((addr << 1) + carry);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            Set_FlagsNZ(value);
            registers.A = (byte)value;
            cyclesThisOperation += 2;
        }

        private void ROLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        private void ROLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 7;
        }

        private void ROLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 5;
        }

        private void ROLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        #endregion ROL

        #region ROR

        private void RORAC()
        {
            byte addr = registers.A;
            byte value = (byte)(addr >> 1);
            if (registers.Flags.C)
                value += 0x80;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            Set_FlagsNZ(value);
            registers.A = (byte)value;
            cyclesThisOperation += 2;
        }

        private void RORA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        private void RORXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 7;
        }

        private void RORZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 5;
        }

        private void RORXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteReadModifyWrite(addr, value, value2);
            cyclesThisOperation += 6;
        }

        #endregion ROR

        #region BRANCH

        private void BCC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.C)
                Branch(value);
        }

        private void BCS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.C)
                Branch(value);
        }

        private void BEQ()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.Z)
                Branch(value);
        }

        private void BMI()
        {
            byte value = ReadByteFromMemory(registers.PC);
            cyclesThisOperation += 2;
            IncrementProgramCounter();
            if (registers.Flags.N)
                Branch(value);
        }

        private void BNE()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.Z)
                Branch(value);
        }

        private void BPL()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.N)
                Branch(value);
        }

        private void BVC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.V)
                Branch(value);
        }

        private void BVS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.V)
                Branch(value);
        }

        private void BRK()
        {
            IncrementProgramCounter();
            // BRK sets B and the reserved bit in the stacked status byte.
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            PushByteToStack((byte)(registers.P | 0x30));
            registers.Flags.I = true;
            registers.PC = (ulong)(ReadByteFromMemory(0xFFFE) + ReadByteFromMemory(0xFFFF) * 0x100);
            cyclesThisOperation += 7;
        }

        private void Branch(ulong value)
        {
            ulong oldPc = registers.PC;
            int offset = (sbyte)(byte)value;
            registers.PC = (ulong)((long)registers.PC + offset) & 0xFFFF;
            // A taken branch costs one extra cycle, and another when it crosses a page.
            cyclesThisOperation += 1;
            if (CrossBoundary(oldPc, registers.PC))
                cyclesThisOperation += 1;
        }

        #endregion BRANCH

        #region J**

        private void JMPA()
        {
            ulong value = Absolute();
            registers.PC = value;
            cyclesThisOperation += 3;
        }

        private void JMPAI()
        {
            ulong addr = AbsoluteIndirect();
            registers.PC = addr;
            cyclesThisOperation += 5;
        }

        private void JSRA()
        {
            byte pclo = ReadByteFromMemory(registers.PC);
            registers.PC++;
            byte hi = (byte)(((registers.PC) >> 8) & 0xFF);
            PushByteToStack(hi);
            byte lo = (byte)((registers.PC) & 0xFF);
            PushByteToStack(lo);
            byte pchi = ReadByteFromMemory(registers.PC);
            registers.PC = (ulong)((pchi << 8) | pclo);
            cyclesThisOperation += 6;
        }

        #endregion J**

        #region RT*

        private void RTI()
        {
            byte flags = PopByteFromStack();
            byte lo = PopByteFromStack();
            byte hi = PopByteFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.Flags.SetFlagsFromByte(flags, 0b11001111);
            cyclesThisOperation += 6;
        }

        private void RTS()
        {
            byte lo = PopByteFromStack();
            byte hi = PopByteFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.PC++;
            cyclesThisOperation += 6;
        }

        #endregion RT*

        #endregion Documented Opcodes

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementProgramCounter(ulong value = 1)
        {
            registers.PC = (registers.PC + value) & 0xFFFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadByteFromMemory(ulong addr)
        {
            ulong masked = addr & 0xFFFF;
            NotifyCyclesDuringInstruction(1);
            byte value = bus.ReadByte(masked);
            var stretch = OnAccessStretch;
            if (stretch != null)
            {
                int extra = stretch(masked);
                if (extra > 0)
                {
                    cyclesThisOperation += extra;
                    NotifyCyclesDuringInstruction(extra);
                }
            }
            return value;
        }

        // Trace peeks must see the same ROM and SHEILA mapping as opcode fetches, without adding 1 MHz bus stretch cycles.
        public byte PeekByte(ulong addr)
        {
            return bus.ReadByte(addr & 0xFFFF);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteByteToMemory(ulong addr, byte value)
        {
            ulong masked = addr & 0xFFFF;
            NotifyCyclesDuringInstruction(1);
            bus.WriteByte(masked, value);
            var stretch = OnAccessStretch;
            if (stretch != null)
            {
                int extra = stretch(masked);
                if (extra > 0)
                {
                    cyclesThisOperation += extra;
                    NotifyCyclesDuringInstruction(extra);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteReadModifyWrite(ulong addr, byte originalValue, byte modifiedValue)
        {
            WriteByteToMemory(addr, originalValue);
            WriteByteToMemory(addr, modifiedValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NotifyCyclesDuringInstruction(int cycles)
        {
            cyclesNotifiedThisInstruction += cycles;
            Interlocked.Add(ref totalCycles, cycles);
            OnCyclesExecuted?.Invoke(cycles);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadWordFromBus(ulong addr)
        {
            byte lo = ReadByteFromMemory(addr);
            byte hi = ReadByteFromMemory((addr + 1) & 0xFFFF);
            return (ulong)(lo | (hi << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetNextByteInstruction()
        {
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong GetNextWordInstruction()
        {
            byte value1 = GetNextByteInstruction();
            byte value2 = GetNextByteInstruction();
            return (ulong)(value1 | (value2 << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushByteToStack(byte value)
        {
            WriteByteToMemory((ulong)(registers.S + 0x100), value);
            registers.S--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte PopByteFromStack()
        {
            registers.S++;
            return ReadByteFromMemory((ulong)(registers.S + 0x100));
        }

        private void ProcessNMI(ulong value = 0xFFFA)
        {
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            // NMI stacks status like IRQ: B clear, reserved bit set.
            PushByteToStack((byte)((registers.P & 0xEF) | 0x20));
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        private void ProcessIRQ(ulong value = 0xFFFE)
        {
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            // IRQ leaves B clear in the stacked status so firmware can tell it from BRK.
            PushByteToStack((byte)((registers.P & 0xEF) | 0x20));
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CrossBoundary(ulong addr1, ulong addr2)
        {
            return (addr1 & 0xff00) != (addr2 & 0xff00);
        }
    }

}
