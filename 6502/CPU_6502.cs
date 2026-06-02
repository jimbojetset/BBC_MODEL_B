// ============================================================================
// Project:     BBC
// File:        CPU_6502.cs
// Description: MOS 6502-compatible CPU emulator core with opcode dispatch, addressing
//              modes, interrupt handling, stack operations, timing, and memory
//              callbacks.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BBC.CPU
{
    /// <summary>
    /// Emulates an NMOS 6502-compatible CPU core, including opcode dispatch, interrupt processing, reset handling, cycle accounting, and pacing.
    /// Memory access is routed through <see cref="ICpuBus"/> so the core can also run with non-C64 6502 memory maps.
    /// </summary>
    public class CPU_6502
    {
        /// <summary>Requests high-resolution Windows timer scheduling.</summary>
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        /// <summary>Releases high-resolution Windows timer scheduling.</summary>
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private const uint TimerResolutionMs = 1;

        public Registers registers = new Registers();

        /// <summary>
        /// Compatibility C64 memory bus used by the existing emulator host.
        /// Non-C64 hosts should provide their own ICpuBus via the Bus property or constructor.
        /// </summary>
        public FlatMemoryBus memory = new FlatMemoryBus(0x10000);
        private ICpuBus bus = null!;

        private bool running = true;
        private bool paused;
        public bool Paused => Volatile.Read(ref paused);
        private bool jammed;
        private bool jamReported;
        private ulong jamAddress;

        private readonly int clockFreq = 2000000; //1MHz

        private readonly ConcurrentQueue<ulong> IRQ_Buffer = new ConcurrentQueue<ulong>();
        private readonly ConcurrentQueue<ulong> NMI_Buffer = new ConcurrentQueue<ulong>();
        private int irqLineAsserted;

        /// <summary>Queues an IRQ request for the CPU.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        public void InitiateIRQ(ulong value)
        {
            IRQ_Buffer.Enqueue(value);
        }

        /// <summary>Sets the level-sensitive IRQ input line for device cores that hold IRQ active until acknowledged.</summary>
        /// <param name="asserted">Whether the IRQ line is currently asserted.</param>
        public void SetIrqLine(bool asserted)
        {
            Volatile.Write(ref irqLineAsserted, asserted ? 1 : 0);
        }

        /// <summary>Queues an NMI request for the CPU.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        public void InitiateNMI(ulong value)
        {
            NMI_Buffer.Enqueue(value);
        }

        private int cyclesThisOperation = 0;
        private long totalCycles;
        public Action<int>? OnCyclesExecuted;
        public Func<bool>? OnBeforeInstruction;
        private int externalStallCycles;
        public bool PacingEnabled { get; set; } = true;
        public double SpeedScale { get; set; } = 1.0;

        /// <summary>Adds externally requested CPU stall cycles.</summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        public void RequestExternalStallCycles(int cycles)
        {
            if (cycles <= 0) return;
            Interlocked.Add(ref externalStallCycles, cycles);
        }

        /// <summary>Initializes a new CPU_6502 instance.</summary>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6502(int freq = 1000000)
        {
            Initialise();
            clockFreq = freq;
        }

        /// <summary>Initializes a new CPU_6502 instance with an external CPU bus.</summary>
        /// <param name="bus">The CPU-visible bus implementation.</param>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6502(ICpuBus bus, int freq = 1000000)
        {
            Initialise();
            Bus = bus;
            clockFreq = freq;
        }

        /// <summary>Gets or sets the CPU-visible bus used for instruction fetches and data access.</summary>
        public ICpuBus Bus
        {
            get => bus;
            set => bus = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Initializes this component.</summary>
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

        /// <summary>Requests a CPU reset at the next safe point.</summary>
        public void RequestReset() => Interlocked.Exchange(ref resetPending, 1);

        /// <summary>Requests the CPU run loop to stop at the next safe point.</summary>
        public void Stop() => Volatile.Write(ref running, false);

        /// <summary>Immediately resets CPU registers and loads the reset vector on the current thread.</summary>
        public void ResetNow()
        {
            DoReset();
        }

        /// <summary>Sets whether execution is paused.</summary>
        public void SetPaused(bool value) => Volatile.Write(ref paused, value);

        /// <summary>Resets CPU state on the CPU thread.</summary>
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

            while (IRQ_Buffer.TryDequeue(out _)) { }
            while (NMI_Buffer.TryDequeue(out _)) { }
            Interlocked.Exchange(ref totalCycles, 0);
        }

        /// Keep CPU pacing in small cycle chunks so raster IRQ-driven effects
        /// are not serviced in large bursts.
        private const int SliceCycles = 64;

        /// <summary>Runs the main emulator loop.</summary>
        /// <param name="startVector">The start vector value used by the operation.</param>
        public void Run(ulong startVector = 0xFFFC)
        {
            registers.PC = ReadWordFromBus(startVector);
            running = true;

            int sliceCycles = SliceCycles;
            int effectiveClockFreq = Math.Max(1, (int)Math.Round(clockFreq * Math.Clamp(SpeedScale, 0.01, 4.0)));
            long ticksPerSlice = Math.Max(1, Stopwatch.Frequency * SliceCycles / effectiveClockFreq);

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

                        while (NMI_Buffer.TryDequeue(out ulong nmiValue))
                        {
                            if (nmiValue != 0xFFFA)
                                ProcessNMI(nmiValue);
                            else
                                ProcessNMI();
                        }
                        while (!registers.Flags.I && IRQ_Buffer.TryDequeue(out ulong irqValue))
                        {
                            if (irqValue != 0xFFFE)
                                ProcessIRQ(irqValue);
                            else
                                ProcessIRQ();
                        }
                        if (!registers.Flags.I && Volatile.Read(ref irqLineAsserted) != 0)
                            ProcessIRQ();
                        int beforeCycles = cyclesThisOperation;
                        bool handledByHost = OnBeforeInstruction?.Invoke() == true;

                        if (!handledByHost)
                            Execute(GetNextByteInstruction());

                        int deltaCycles = cyclesThisOperation - beforeCycles;
                        if (handledByHost && deltaCycles <= 0)
                            deltaCycles = 6;

                        if (deltaCycles > 0)
                        {
                            if (handledByHost)
                                cyclesThisOperation += deltaCycles;

                            Interlocked.Add(ref totalCycles, deltaCycles);
                            OnCyclesExecuted?.Invoke(deltaCycles);
                        }
                        if (jammed)
                            break;
                    }

                    if (PacingEnabled)
                    {
                        WaitUntil(nextDeadline);

                        nextDeadline += ticksPerSlice;

                        long now = Stopwatch.GetTimestamp();
                        if (nextDeadline < now - ticksPerSlice * 4)
                            nextDeadline = now + ticksPerSlice;
                    }
                    else
                    {
                        nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;
                    }
                }
            }
            finally
            {
                if (timerRaised) TryEndHighResolutionTimer();
            }
        }

        /// <summary>Attempts to begin high resolution timer.</summary>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private static bool TryBeginHighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { return TimeBeginPeriod(TimerResolutionMs) == 0 /* TIMERR_NOERROR */; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>Attempts to end high resolution timer.</summary>
        private static void TryEndHighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows()) return;
            try { TimeEndPeriod(TimerResolutionMs); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        /// <summary>Waits until the specified stopwatch deadline.</summary>
        /// <param name="deadlineTicks">The high-resolution timer deadline to wait for.</param>
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

        /// <summary>Executes one decoded CPU opcode.</summary>
        /// <param name="opcode">The 6510 opcode byte to execute.</param>
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

                /// C64 game code uses these heavily (LAX, DCP, SLO, ISC, SAX,
                /// etc.) for tighter inner loops. Without them we silently
                /// skip the instruction and the game's logic drifts off.

                /// ---- LAX: load A and X from memory together. ----
                case 0xA3: LAX(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 6; break;
                case 0xA7: LAX(Zero_Page()); cyclesThisOperation += 3; break;
                case 0xAF: LAX(Absolute()); cyclesThisOperation += 4; break;
                case 0xB3: LAX(Zero_Page_Indirect_Y_Indexed()); cyclesThisOperation += 5; break;
                case 0xB7: LAX(Y_Indexed_Zero_Page()); cyclesThisOperation += 4; break;
                case 0xBF: LAX(Y_Indexed_Absolute()); cyclesThisOperation += 4; break;

                /// ---- SAX: store (A AND X) - no flags affected. ----
                case 0x83: SAX(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 6; break;
                case 0x87: SAX(Zero_Page()); cyclesThisOperation += 3; break;
                case 0x8F: SAX(Absolute()); cyclesThisOperation += 4; break;
                case 0x97: SAX(Y_Indexed_Zero_Page()); cyclesThisOperation += 4; break;

                /// ---- DCP: DEC memory, CMP result with A. ----
                case 0xC3: DCP(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0xC7: DCP(Zero_Page()); cyclesThisOperation += 5; break;
                case 0xCF: DCP(Absolute()); cyclesThisOperation += 6; break;
                case 0xD3: DCP(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0xD7: DCP(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0xDB: DCP(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0xDF: DCP(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                /// ---- ISC/ISB: INC memory, SBC result from A. ----
                case 0xE3: ISC(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0xE7: ISC(Zero_Page()); cyclesThisOperation += 5; break;
                case 0xEF: ISC(Absolute()); cyclesThisOperation += 6; break;
                case 0xF3: ISC(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0xF7: ISC(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0xFB: ISC(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0xFF: ISC(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                /// ---- SLO: ASL memory, ORA result into A. ----
                case 0x03: SLO(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x07: SLO(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x0F: SLO(Absolute()); cyclesThisOperation += 6; break;
                case 0x13: SLO(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x17: SLO(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x1B: SLO(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x1F: SLO(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                /// ---- SRE: LSR memory, EOR result into A. ----
                case 0x43: SRE(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x47: SRE(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x4F: SRE(Absolute()); cyclesThisOperation += 6; break;
                case 0x53: SRE(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x57: SRE(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x5B: SRE(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x5F: SRE(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                /// ---- RLA: ROL memory, AND result into A. ----
                case 0x23: RLA(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x27: RLA(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x2F: RLA(Absolute()); cyclesThisOperation += 6; break;
                case 0x33: RLA(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x37: RLA(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x3B: RLA(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x3F: RLA(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                /// ---- RRA: ROR memory, ADC result with A. ----
                case 0x63: RRA(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x67: RRA(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x6F: RRA(Absolute()); cyclesThisOperation += 6; break;
                case 0x73: RRA(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x77: RRA(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x7B: RRA(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x7F: RRA(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                /// ---- Multi-byte NOPs. They consume their operand bytes
                /// so PC advances correctly; flags unaffected. ----
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

                /// ---- Immediate-only logic ops. ----
                case 0x0B: case 0x2B: ANC_IM(); cyclesThisOperation += 2; break;
                case 0x4B: ALR_IM(); cyclesThisOperation += 2; break;
                case 0x6B: ARR_IM(); cyclesThisOperation += 2; break;
                case 0x8B: XAA_IM(); cyclesThisOperation += 2; break;
                case 0xAB: LAX_IM(); cyclesThisOperation += 2; break;
                case 0xBB: LAS_AY(); cyclesThisOperation += 4; break;
                case 0xCB: AXS_IM(); cyclesThisOperation += 2; break;
                case 0xEB: SBCI();   /* duplicate of $E9 SBC #imm */ break;

                /// ---- Store-high variants used by some packed/cracked code. ----
                case 0x93: AHX_IY(); cyclesThisOperation += 6; break;
                case 0x9B: TAS_AY(); cyclesThisOperation += 5; break;
                case 0x9C: SHY_AX(); cyclesThisOperation += 5; break;
                case 0x9E: SHX_AY(); cyclesThisOperation += 5; break;
                case 0x9F: AHX_AY(); cyclesThisOperation += 5; break;

                /// ---- JAM / KIL: real CPU halts until reset. Keep the CPU
                /// thread alive so a later reset request can recover.
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

        /// <summary>Executes a single instruction and returns the number of cycles it consumed.</summary>
        /// <returns>The number of CPU cycles consumed by the instruction.</returns>
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
            while (!registers.Flags.I && IRQ_Buffer.TryDequeue(out ulong irqValue))
            {
                if (irqValue != 0xFFFE)
                    ProcessIRQ(irqValue);
                else
                    ProcessIRQ();
            }
            if (!registers.Flags.I && Volatile.Read(ref irqLineAsserted) != 0)
                ProcessIRQ();

            Execute(GetNextByteInstruction());
            int elapsed = cyclesThisOperation - beforeCycles;
            if (elapsed > 0)
            {
                Interlocked.Add(ref totalCycles, elapsed);
                OnCyclesExecuted?.Invoke(elapsed);
            }

            return elapsed;
        }

        #region Illegal opcode helpers

        /// <summary>Executes the LAX CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAX(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.A = v;
            registers.X = v;
            Set_FlagsNZ(v);
        }

        /// <summary>Executes the SAX CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SAX(ulong addr)
        {
            WriteByteToMemory(addr, (byte)(registers.A & registers.X));
        }

        /// <summary>Executes the DCP CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DCP(ulong addr)
        {
            byte v = (byte)(ReadByteFromMemory(addr) - 1);
            WriteByteToMemory(addr, v);
            int diff = registers.A - v;
            registers.Flags.C = (diff & 0x100) == 0;
            Set_FlagsNZ((byte)diff);
        }

        /// <summary>Executes the ISC CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ISC(ulong addr)
        {
            byte v = (byte)(ReadByteFromMemory(addr) + 1);
            WriteByteToMemory(addr, v);
            SBC(v);
        }

        /// <summary>Executes the SLO CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
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

        /// <summary>Executes the SRE CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
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

        /// <summary>Executes the RLA CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
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

        /// <summary>Executes the RRA CPU operation.</summary>
        /// <param name="addr">The emulated address to access.</param>
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

        /// ANC: AND with immediate, then copy bit 7 (N) into C. Used in
        /// some bit-test routines as a faster "AND # / BMI" pair.

        /// <summary>Executes the ANC instruction using immediate addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ANC_IM()
        {
            registers.A &= Immediate();
            Set_FlagsNZ(registers.A);
            registers.Flags.C = registers.Flags.N;
        }

        /// ALR: AND with immediate, then LSR A.

        /// <summary>Executes the ALR instruction using immediate addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ALR_IM()
        {
            byte v = (byte)(registers.A & Immediate());
            registers.Flags.C = (v & 0x01) != 0;
            registers.A = (byte)(v >> 1);
            Set_FlagsNZ(registers.A);
        }

        /// ARR: AND with immediate, then ROR A. Has unusual flag effects:
        /// C = bit 6 of result; V = bit 6 XOR bit 5 of result.

        /// <summary>Executes the ARR instruction using immediate addressing.</summary>
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

        /// AXS: X = (A AND X) - immediate. No borrow input; C set normally.

        /// <summary>Executes the AXS instruction using immediate addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AXS_IM()
        {
            int t = (registers.A & registers.X) - Immediate();
            registers.X = (byte)t;
            registers.Flags.C = (t & 0x100) == 0;
            Set_FlagsNZ(registers.X);
        }

        /// XAA / ANE (unstable on real silicon). Common practical approximation:
        /// A = X AND immediate.

        /// <summary>Executes the XAA instruction using immediate addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void XAA_IM()
        {
            registers.A = (byte)((registers.A | 0xEE) & registers.X & Immediate());
            Set_FlagsNZ(registers.A);
        }

        /// LAX immediate unofficial variant.

        /// <summary>Executes the LAX instruction using immediate addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAX_IM()
        {
            byte v = (byte)((registers.A | 0xEE) & Immediate());
            registers.A = v;
            registers.X = v;
            Set_FlagsNZ(v);
        }

        /// <summary>Executes the LAS instruction using absolute Y-indexed addressing.</summary>
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

        /// AHX stores (A AND X AND (high_byte_of_effective_address + 1)).
        /// With page-boundary crossing addressing modes, the actual address calculation
        /// may wrap differently than expected. The high-byte formula captures this subtlety.

        /// <summary>Executes the AHX instruction using indirect Y-indexed addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AHX_IY()
        {
            (ulong addr, byte value) = StoreHighIndirectY((byte)(registers.A & registers.X));
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes the AHX instruction using absolute Y-indexed addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AHX_AY()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute((byte)(registers.A & registers.X), registers.Y);
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes the TAS instruction using absolute Y-indexed addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TAS_AY()
        {
            byte s = (byte)(registers.A & registers.X);
            registers.S = s;
            (ulong addr, byte value) = StoreHighIndexedAbsolute(s, registers.Y);
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes the SHY instruction using absolute X-indexed addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SHY_AX()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute(registers.Y, registers.X);
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes the SHX instruction using absolute Y-indexed addressing.</summary>
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

        /// <summary>Reads the next immediate operand byte.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Immediate()
        {
            byte addr = GetNextByteInstruction();
            return addr;
        }

        /// <summary>Reads the next absolute operand address.</summary>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Absolute()
        {
            ulong addr = GetNextWordInstruction();
            return addr & 0xFFFF;
        }

        /// <summary>Reads an absolute-indirect jump target.</summary>
        /// <returns>The numeric value produced by the operation.</returns>
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

        /// <summary>Reads an absolute address indexed by X.</summary>
        /// <param name="checkBoundary">The check boundary value used by the operation.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong X_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong baseAddr = Absolute();
            ulong addr = baseAddr + registers.X;
            if (CrossBoundary(addr, baseAddr) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }

        /// <summary>Reads an absolute address indexed by Y.</summary>
        /// <param name="checkBoundary">The check boundary value used by the operation.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Y_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong baseAddr = Absolute();
            ulong addr = baseAddr + registers.Y;
            if (CrossBoundary(addr, baseAddr) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }

        /// <summary>Reads the next zero-page operand address.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Zero_Page()
        {
            byte addr = GetNextByteInstruction();
            return addr;
        }

        /// <summary>Reads a zero-page address indexed by X.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte X_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.X) & 0xFF);
            return addr;
        }

        /// <summary>Reads a zero-page address indexed by Y.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Y_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.Y) & 0xFF);
            return addr;
        }

        /// <summary>Reads an indexed-indirect zero-page address.</summary>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong X_Indexed_Zero_Page_Indirect()
        {
            byte value = (byte)(GetNextByteInstruction() + registers.X);
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong addr = (ulong)((value2 << 8) | value1);
            return addr & 0xFFFF;
        }

        /// <summary>Reads an indirect-indexed zero-page address.</summary>
        /// <param name="checkBoundary">The check boundary value used by the operation.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Zero_Page_Indirect_Y_Indexed(bool checkBoundary = true)
        {
            byte value = GetNextByteInstruction();
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong value3 = (ulong)((value2 << 8) | value1);
            ulong addr = value3 + registers.Y;
            if (CrossBoundary(addr, value3) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }

        /// <summary>Sets flags nz.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Set_FlagsNZ(byte value)
        {
            registers.Flags.Z = (value == 0);
            registers.Flags.N = ((value & 0x80) != 0);
        }

        #endregion Addressing Modes

        #region Documented Opcodes

        #region LD*

        /// <summary>Executes the LDA instruction using immediate addressing.</summary>
        private void LDA_IM()
        {
            registers.A = Immediate();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the LDA instruction using absolute addressing.</summary>
        private void LDA_AB()
        {
            registers.A = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDA instruction using absolute X-indexed addressing.</summary>
        private void LDA_ABX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDA instruction using absolute Y-indexed addressing.</summary>
        private void LDA_ABY()
        {
            registers.A = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDA instruction using zero-page addressing.</summary>
        private void LDA_ZP()
        {
            registers.A = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the LDA instruction using zero-page X-indexed addressing.</summary>
        private void LDA_ZPX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDA instruction using indexed-indirect zero-page addressing.</summary>
        private void LDA_ZPIX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the LDA instruction using indirect-indexed zero-page addressing.</summary>
        private void LDA_ZPIY()
        {
            registers.A = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the LDX instruction using immediate addressing.</summary>
        private void LDX_IM()
        {
            registers.X = Immediate();
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the LDX instruction using absolute addressing.</summary>
        private void LDX_AB()
        {
            registers.X = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDX instruction using absolute Y-indexed addressing.</summary>
        private void LDX_ABY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDX instruction using zero-page addressing.</summary>
        private void LDX_ZP()
        {
            registers.X = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the LDX instruction using zero-page Y-indexed addressing.</summary>
        private void LDX_ZPY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDY instruction using immediate addressing.</summary>
        private void LDY_IM()
        {
            registers.Y = Immediate();
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the LDY instruction using absolute addressing.</summary>
        private void LDY_AB()
        {
            registers.Y = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDY instruction using absolute X-indexed addressing.</summary>
        private void LDY_ABX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the LDY instruction using zero-page addressing.</summary>
        private void LDY_ZP()
        {
            registers.Y = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the LDY instruction using zero-page X-indexed addressing.</summary>
        private void LDY_ZPX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        #endregion LD*

        #region ST*

        /// <summary>Executes the STA instruction using absolute addressing.</summary>
        private void STA_AB()
        {
            WriteByteToMemory(Absolute(), registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the STA instruction using absolute X-indexed addressing.</summary>
        private void STA_ABX()
        {
            WriteByteToMemory(X_Indexed_Absolute(false), registers.A);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the STA instruction using absolute Y-indexed addressing.</summary>
        private void STA_ABY()
        {
            WriteByteToMemory(Y_Indexed_Absolute(false), registers.A);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the STA instruction using zero-page addressing.</summary>
        private void STA_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.A);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the STA instruction using zero-page X-indexed addressing.</summary>
        private void STA_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the STA instruction using indexed-indirect zero-page addressing.</summary>
        private void STA_ZPIX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page_Indirect(), registers.A);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the STA instruction using indirect-indexed zero-page addressing.</summary>
        private void STA_ZPIY()
        {
            WriteByteToMemory(Zero_Page_Indirect_Y_Indexed(false), registers.A);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the STX instruction using absolute addressing.</summary>
        private void STX_AB()
        {
            WriteByteToMemory(Absolute(), registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the STX instruction using zero-page addressing.</summary>
        private void STX_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.X);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the STX instruction using zero-page Y-indexed addressing.</summary>
        private void STX_ZPY()
        {
            WriteByteToMemory(Y_Indexed_Zero_Page(), registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the STY instruction using absolute addressing.</summary>
        private void STY_AB()
        {
            WriteByteToMemory(Absolute(), registers.Y);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the STY instruction using zero-page addressing.</summary>
        private void STY_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.Y);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the STY instruction using zero-page X-indexed addressing.</summary>
        private void STY_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.Y);
            cyclesThisOperation += 4;
        }

        #endregion ST*

        #region T**

        /// <summary>Executes the TAX CPU operation.</summary>
        private void TAX()
        {
            registers.X = registers.A;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the TAY CPU operation.</summary>
        private void TAY()
        {
            registers.Y = registers.A;
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the TSX CPU operation.</summary>
        private void TSX()
        {
            registers.X = registers.S;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the TXA CPU operation.</summary>
        private void TXA()
        {
            registers.A = registers.X;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the TXS CPU operation.</summary>
        private void TXS()
        {
            registers.S = registers.X;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the TYA CPU operation.</summary>
        private void TYA()
        {
            registers.A = registers.Y;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        #endregion T**

        #region SE*

        /// <summary>Executes the SEC CPU operation.</summary>
        private void SEC()
        {
            registers.Flags.C = true;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the SED CPU operation.</summary>
        private void SED()
        {
            registers.Flags.D = true;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the SEI CPU operation.</summary>
        private void SEI()
        {
            registers.Flags.I = true;
            cyclesThisOperation += 2;
        }

        #endregion SE*

        #region PH*

        /// <summary>Executes the PHA CPU operation.</summary>
        private void PHA()
        {
            PushByteToStack(registers.A);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the PHP CPU operation.</summary>
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

        /// <summary>Executes the PLA CPU operation.</summary>
        private void PLA()
        {
            registers.A = PopByteFromStack();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the PLP CPU operation.</summary>
        private void PLP()
        {
            byte value = PopByteFromStack();
            registers.Flags.SetFlagsFromByte(value, 0xCF); //ignore bits 5 & 6
            cyclesThisOperation += 4;
        }

        #endregion PL*

        #region CL*

        /// <summary>Executes the CLC CPU operation.</summary>
        private void CLC()
        {
            registers.Flags.C = false;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the CLD CPU operation.</summary>
        private void CLD()
        {
            registers.Flags.D = false;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the CLI CPU operation.</summary>
        private void CLI()
        {
            registers.Flags.I = false;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the CLV CPU operation.</summary>
        private void CLV()
        {
            registers.Flags.V = false;
            cyclesThisOperation += 2;
        }

        #endregion CL*

        #region DE*

        /// <summary>Executes the DECA CPU operation.</summary>
        private void DECA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the DECXA CPU operation.</summary>
        private void DECXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Executes the DECZP CPU operation.</summary>
        private void DECZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the DECXZP CPU operation.</summary>
        private void DECXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the DEX CPU operation.</summary>
        private void DEX()
        {
            byte value2 = (byte)(registers.X - 1);
            registers.X = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the DEY CPU operation.</summary>
        private void DEY()
        {
            byte value2 = (byte)(registers.Y - 1);
            registers.Y = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        #endregion DE*

        #region IN*

        /// <summary>Executes the INCA CPU operation.</summary>
        private void INCA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the INCXA CPU operation.</summary>
        private void INCXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 7;
        }

        /// <summary>Executes the INCZP CPU operation.</summary>
        private void INCZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the INCXZP CPU operation.</summary>
        private void INCXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the INX CPU operation.</summary>
        private void INX()
        {
            byte value1 = (byte)(registers.X + 1);
            registers.X = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the INY CPU operation.</summary>
        private void INY()
        {
            byte value1 = (byte)(registers.Y + 1);
            registers.Y = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }

        #endregion IN*

        #region CM*

        /// <summary>Executes the CMPI CPU operation.</summary>
        private void CMPI()
        {
            byte addr = Immediate();
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the CMPA CPU operation.</summary>
        private void CMPA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the CMPXA CPU operation.</summary>
        private void CMPXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the CMPYA CPU operation.</summary>
        private void CMPYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the CMPZ CPU operation.</summary>
        private void CMPZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the CMPXZ CPU operation.</summary>
        private void CMPXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the CMPXZI CPU operation.</summary>
        private void CMPXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the CMPYZI CPU operation.</summary>
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

        /// <summary>Executes the CPXI CPU operation.</summary>
        private void CPXI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the CPXA CPU operation.</summary>
        private void CPXA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the CPXZ CPU operation.</summary>
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

        /// <summary>Executes the CPYI CPU operation.</summary>
        private void CPYI()
        {
            byte addr = Immediate();
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the CPYA CPU operation.</summary>
        private void CPYA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the CPYZ CPU operation.</summary>
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

        /// <summary>Executes the ADCI CPU operation.</summary>
        private void ADCI()
        {
            byte value = Immediate();
            ADC(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the ADCA CPU operation.</summary>
        private void ADCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ADCXA CPU operation.</summary>
        private void ADCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ADCYA CPU operation.</summary>
        private void ADCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ADCZ CPU operation.</summary>
        private void ADCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            ADC(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the ADCXZ CPU operation.</summary>
        private void ADCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ADCXZI CPU operation.</summary>
        private void ADCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            ADC(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the ADCYZI CPU operation.</summary>
        private void ADCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            ADC(value);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the ADC CPU operation.</summary>
        /// <param name="value">The value supplied to the operation.</param>
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

        /// <summary>Executes the SBCI CPU operation.</summary>
        private void SBCI()
        {
            byte value = Immediate();
            SBC(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the SBCA CPU operation.</summary>
        private void SBCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the SBCXA CPU operation.</summary>
        private void SBCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the SBCYA CPU operation.</summary>
        private void SBCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the SBCZ CPU operation.</summary>
        private void SBCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            SBC(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the SBCXZ CPU operation.</summary>
        private void SBCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the SBCXZI CPU operation.</summary>
        private void SBCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            SBC(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the SBCYZI CPU operation.</summary>
        private void SBCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            SBC(value);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the SBC CPU operation.</summary>
        /// <param name="value">The value supplied to the operation.</param>
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

        /// <summary>Executes the EORI CPU operation.</summary>
        private void EORI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the EORA CPU operation.</summary>
        private void EORA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the EORXA CPU operation.</summary>
        private void EORXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the EORYA CPU operation.</summary>
        private void EORYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the EORZ CPU operation.</summary>
        private void EORZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the EORXZ CPU operation.</summary>
        private void EORXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the EORXZI CPU operation.</summary>
        private void EORXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the EORYZI CPU operation.</summary>
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

        /// <summary>Executes the ORAI CPU operation.</summary>
        private void ORAI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the ORAA CPU operation.</summary>
        private void ORAA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ORAXA CPU operation.</summary>
        private void ORAXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ORAYA CPU operation.</summary>
        private void ORAYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ORAZ CPU operation.</summary>
        private void ORAZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the ORAXZ CPU operation.</summary>
        private void ORAXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ORAXZI CPU operation.</summary>
        private void ORAXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the ORAYZI CPU operation.</summary>
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

        /// <summary>Executes the ANDI CPU operation.</summary>
        private void ANDI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the ANDA CPU operation.</summary>
        private void ANDA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ANDXA CPU operation.</summary>
        private void ANDXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ANDYA CPU operation.</summary>
        private void ANDYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ANDZ CPU operation.</summary>
        private void ANDZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the ANDXZ CPU operation.</summary>
        private void ANDXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the ANDXZI CPU operation.</summary>
        private void ANDXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the ANDYZI CPU operation.</summary>
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

        /// <summary>Executes the BITA CPU operation.</summary>
        private void BITA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 4;
        }

        /// <summary>Executes the BITZ CPU operation.</summary>
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

        /// <summary>Executes the ASLAC CPU operation.</summary>
        private void ASLAC()
        {
            byte addr = registers.A;
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            registers.Flags.Z = (value == 0);
            registers.A = value;
            cyclesThisOperation += 2;
        }

        /// <summary>Executes the ASLA CPU operation.</summary>
        private void ASLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the ASLXA CPU operation.</summary>
        private void ASLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Executes the ASLZ CPU operation.</summary>
        private void ASLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the ASLXZ CPU operation.</summary>
        private void ASLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        #endregion ASL

        #region LSR

        /// <summary>Executes the LSRAC CPU operation.</summary>
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

        /// <summary>Executes the LSRA CPU operation.</summary>
        private void LSRA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the LSRXA CPU operation.</summary>
        private void LSRXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Executes the LSRZ CPU operation.</summary>
        private void LSRZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the LSRXZ CPU operation.</summary>
        private void LSRXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        #endregion LSR

        #region ROL

        /// <summary>Executes the ROLAC CPU operation.</summary>
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

        /// <summary>Executes the ROLA CPU operation.</summary>
        private void ROLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the ROLXA CPU operation.</summary>
        private void ROLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Executes the ROLZ CPU operation.</summary>
        private void ROLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the ROLXZ CPU operation.</summary>
        private void ROLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        #endregion ROL

        #region ROR

        /// <summary>Executes the RORAC CPU operation.</summary>
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

        /// <summary>Executes the RORA CPU operation.</summary>
        private void RORA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the RORXA CPU operation.</summary>
        private void RORXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Executes the RORZ CPU operation.</summary>
        private void RORZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the RORXZ CPU operation.</summary>
        private void RORXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        #endregion ROR

        #region BRANCH

        /// <summary>Executes the BCC CPU operation.</summary>
        private void BCC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.C)
                Branch(value);
        }

        /// <summary>Executes the BCS CPU operation.</summary>
        private void BCS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.C)
                Branch(value);
        }

        /// <summary>Executes the BEQ CPU operation.</summary>
        private void BEQ()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.Z)
                Branch(value);
        }

        /// <summary>Executes the BMI CPU operation.</summary>
        private void BMI()
        {
            byte value = ReadByteFromMemory(registers.PC);
            cyclesThisOperation += 2;
            IncrementProgramCounter();
            if (registers.Flags.N)
                Branch(value);
        }

        /// <summary>Executes the BNE CPU operation.</summary>
        private void BNE()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.Z)
                Branch(value);
        }

        /// <summary>Executes the BPL CPU operation.</summary>
        private void BPL()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.N)
                Branch(value);
        }

        /// <summary>Executes the BVC CPU operation.</summary>
        private void BVC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.V)
                Branch(value);
        }

        /// <summary>Executes the BVS CPU operation.</summary>
        private void BVS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.V)
                Branch(value);
        }

        /// <summary>Executes the BRK CPU operation.</summary>
        private void BRK()
        {
            IncrementProgramCounter();
            /// BRK pushes the status byte with both B (bit 4) and the
            /// always-1 reserved bit (bit 5) set. RTI/PLP later restore
            /// these unchanged - they exist only on the stack.
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            PushByteToStack((byte)(registers.P | 0x30));
            registers.Flags.I = true;
            registers.PC = (ulong)(ReadByteFromMemory(0xFFFE) + ReadByteFromMemory(0xFFFF) * 0x100);
            cyclesThisOperation += 7;
        }

        /// <summary>Applies a relative branch target and cycle penalty.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        private void Branch(ulong value)
        {
            /// Branch offset is a signed 8-bit value; cast handles both directions.
            ulong oldPc = registers.PC;
            int offset = (sbyte)(byte)value;
            registers.PC = (ulong)((long)registers.PC + offset) & 0xFFFF;
            /// Taken branch costs +1 cycle, plus +1 on page cross.
            cyclesThisOperation += 1;
            if (CrossBoundary(oldPc, registers.PC))
                cyclesThisOperation += 1;
        }

        #endregion BRANCH

        #region J**

        /// <summary>Executes the JMPA CPU operation.</summary>
        private void JMPA()
        {
            ulong value = Absolute();
            registers.PC = value;
            cyclesThisOperation += 3;
        }

        /// <summary>Executes the JMPAI CPU operation.</summary>
        private void JMPAI()
        {
            ulong addr = AbsoluteIndirect();
            registers.PC = addr;
            cyclesThisOperation += 5;
        }

        /// <summary>Executes the JSRA CPU operation.</summary>
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

        /// <summary>Executes the RTI CPU operation.</summary>
        private void RTI()
        {
            byte flags = PopByteFromStack();
            byte lo = PopByteFromStack();
            byte hi = PopByteFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.Flags.SetFlagsFromByte(flags, 0b11001111);
            cyclesThisOperation += 6;
        }

        /// <summary>Executes the RTS CPU operation.</summary>
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

        /// <summary>Increments program counter.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementProgramCounter(ulong value = 1)
        {
            /// Mask is branch-free and equivalent to wrapping the 16-bit PC.
            registers.PC = (registers.PC + value) & 0xFFFF;
        }

        /// <summary>Reads byte from memory.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadByteFromMemory(ulong addr)
        {
            return bus.ReadByte(addr & 0xFFFF);
        }

        /// <summary>Writes byte to memory.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteByteToMemory(ulong addr, byte value)
        {
            bus.WriteByte(addr & 0xFFFF, value);
        }

        /// <summary>Reads a little-endian 16-bit word from the CPU bus.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadWordFromBus(ulong addr)
        {
            byte lo = ReadByteFromMemory(addr);
            byte hi = ReadByteFromMemory((addr + 1) & 0xFFFF);
            return (ulong)(lo | (hi << 8));
        }

        /// <summary>Reads the next instruction byte and advances PC.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetNextByteInstruction()
        {
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            return value;
        }

        /// <summary>Reads the next instruction word and advances PC.</summary>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong GetNextWordInstruction()
        {
            byte value1 = GetNextByteInstruction();
            byte value2 = GetNextByteInstruction();
            return (ulong)(value1 | (value2 << 8));
        }

        /// <summary>Pushes byte to stack.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushByteToStack(byte value)
        {
            WriteByteToMemory((ulong)(registers.S + 0x100), value);
            registers.S--;
        }

        /// <summary>Pops byte from stack.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte PopByteFromStack()
        {
            registers.S++;
            return ReadByteFromMemory((ulong)(registers.S + 0x100));
        }

        /// <summary>Processes nmi.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        private void ProcessNMI(ulong value = 0xFFFA)
        {
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            /// NMI push uses the same bit pattern as IRQ.
            PushByteToStack((byte)((registers.P & 0xEF) | 0x20));
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        /// <summary>Processes irq.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        private void ProcessIRQ(ulong value = 0xFFFE)
        {
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            /// IRQ push: B bit (4) clear, reserved bit (5) set. The KERNAL
            /// IRQ handler tests this exact bit on the stack to decide
            /// whether to dispatch via $0314 (IRQ) or $0316 (BRK).
            PushByteToStack((byte)((registers.P & 0xEF) | 0x20));
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        /// <summary>Determines whether two addresses cross a page boundary.</summary>
        /// <param name="addr1">The first address to compare.</param>
        /// <param name="addr2">The second address to compare.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CrossBoundary(ulong addr1, ulong addr2)
        {
            return (addr1 & 0xff00) != (addr2 & 0xff00);
        }
    }

}
