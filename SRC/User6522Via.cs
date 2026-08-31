// ============================================================================
// Project:     BBC
// File:        User6522Via.cs
// Description: BBC user 6522 VIA: user-port I/O, game-control inputs, AMX-style
//              mouse pulses, and timer IRQ behaviour.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{

    /// <summary>
    /// The user 6522 VIA exposes the BBC's user port. Games and add-ons often depend
    /// on 6522 timer IRQs and active-low input lines rather than MOS services.
    /// </summary>
    public sealed class User6522Via
    {
        private const byte InterruptFlagTimer1 = 0x40;
        private const byte InterruptFlagTimer2 = 0x20;
        private const byte InterruptFlagCa1 = 0x02;
        private const byte InterruptSummary = 0x80;
        private const int FloatingInputPeriodCycles = 50_000;
        private const int FloatingInputPollWindowCycles = 2_048;
        private const int TimerExpiredThreshold = -2;
        private static readonly int Timer1ReloadExtraCycles = ReadTimerOffset("BBC_USER_VIA_T1_RELOAD_EXTRA", ReadTimerOffset("BBC_USER_VIA_TIMER_RELOAD_EXTRA", 4));
        private static readonly int Timer1LoadExtraCycles = ReadTimerOffset("BBC_USER_VIA_T1_LOAD_EXTRA", ReadTimerOffset("BBC_USER_VIA_TIMER_LOAD_EXTRA", 1));
        private static readonly int Timer2LoadExtraCycles = ReadTimerOffset("BBC_USER_VIA_T2_LOAD_EXTRA", ReadTimerOffset("BBC_USER_VIA_TIMER_LOAD_EXTRA", 1));
        private readonly byte[] registers = new byte[16];
        private byte interruptFlags;
        private byte interruptEnable;
        private byte portA;
        private byte portB;
        private byte dataDirectionA;
        private byte dataDirectionB;
        private byte externalPortBMask;
        private byte externalPortBValue;
        private byte floatingPortBInput = 0xA5;
        private byte mouseButtonBits = 0xE0;
        private int pendingMouseX;
        private int pendingMouseY;
        private bool mouseInputActive;
        private int floatingInputCycleCounter;
        private int peripheralCycleCounter;
        private int lastPortBReadCycle = int.MinValue;
        private int portBPollReadCount;
        private int timer1Counter;
        private int timer1Latch;
        private int timer2Counter;
        private int timer2Latch;
        private bool timer1Running;
        private bool timer1HasInterrupted;
        private bool timer2Running;
        private bool timer2HasInterrupted;
        private int justHit;
        private int peripheralCycleRemainder;

        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE60 and <= 0xFE7F;
        }

        public bool IrqAsserted => (interruptFlags & interruptEnable & 0x7F) != 0;

        public bool PrinterEnabled { get; set; }

        public Action<byte>? PrinterByteWritten { get; set; }

        public string TraceState =>
            $"t1c={timer1Counter} t1l={timer1Latch} t2c={timer2Counter} t2l={timer2Latch} justHit={justHit} ifr=${interruptFlags:X2} ier=${interruptEnable:X2}";

        public string[] GetDebuggerState() =>
        [
            $"ORA ${portA:X2}   DDRA ${dataDirectionA:X2}",
            $"ORB ${portB:X2}   DDRB ${dataDirectionB:X2}",
            $"T1C ${timer1Counter & 0xFFFF:X4}  T1L ${timer1Latch & 0xFFFF:X4}",
            $"T2C ${timer2Counter & 0xFFFF:X4}  T2L ${timer2Latch & 0xFFFF:X4}",
            $"ACR ${registers[11]:X2}   PCR ${registers[12]:X2}",
            $"IFR ${interruptFlags:X2}   IER ${interruptEnable:X2}",
            $"IRQ {(IrqAsserted ? "asserted" : "clear")}",
            $"Printer {(PrinterEnabled ? "enabled" : "disabled")}",
            $"PB input ${externalPortBValue:X2}",
            $"PB mask  ${externalPortBMask:X2}"
        ];

        /// <summary>External user-port devices drive PB lines only where their mask owns the pin.</summary>
        public void SetPortBInputBits(byte mask, byte value)
        {
            mouseInputActive = false;
            mouseButtonBits = 0xE0;
            pendingMouseX = 0;
            pendingMouseY = 0;
            interruptFlags &= 0xE7;
            externalPortBMask = mask;
            externalPortBValue = (byte)(value & mask);
        }

        /// <summary>Switched joysticks hold unpressed user-port lines high and pull pressed controls low.</summary>
        public void SetSwitchedJoystickInput(bool left, bool right, bool up, bool down, bool fire)
        {
            byte value = 0x1F;

            if (up)
                value &= 0xFE;

            if (down)
                value &= 0xFD;

            if (left)
                value &= 0xFB;

            if (right)
                value &= 0xF7;

            if (fire)
                value &= 0xEF;

            SetPortBInputBits(0x1F, value);
        }

        /// <summary>AMX-style mice report movement as user-port pulses, with buttons held active-low.</summary>
        public void SetMouseInput(byte activeLowButtons, int deltaX, int deltaY)
        {
            mouseInputActive = true;
            mouseButtonBits = MapAmxButtonBits(activeLowButtons);
            pendingMouseX += deltaX;
            pendingMouseY += deltaY;
            RefreshMouseInputBits();
        }

        public void Reset()
        {
            Array.Clear(registers);
            interruptFlags = 0;
            interruptEnable = 0;
            portA = 0;
            portB = 0;
            dataDirectionA = 0;
            dataDirectionB = 0;
            externalPortBMask = 0;
            externalPortBValue = 0;
            floatingPortBInput = 0xA5;
            mouseButtonBits = 0xE0;
            pendingMouseX = 0;
            pendingMouseY = 0;
            mouseInputActive = false;
            floatingInputCycleCounter = 0;
            peripheralCycleCounter = 0;
            lastPortBReadCycle = int.MinValue;
            portBPollReadCount = 0;
            timer1Counter = 0x1FFFE;
            timer1Latch = 0x1FFFE;
            timer2Counter = 0x1FFFE;
            timer2Latch = 0x1FFFE;
            timer1Running = true;
            timer1HasInterrupted = true;
            timer2Running = false;
            timer2HasInterrupted = false;
            justHit = 0;
            peripheralCycleRemainder = 0;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(registers.Length);
            writer.Write(registers);
            writer.Write(interruptFlags);
            writer.Write(interruptEnable);
            writer.Write(portA);
            writer.Write(portB);
            writer.Write(dataDirectionA);
            writer.Write(dataDirectionB);
            writer.Write(externalPortBMask);
            writer.Write(externalPortBValue);
            writer.Write(floatingPortBInput);
            writer.Write(mouseButtonBits);
            writer.Write(pendingMouseX);
            writer.Write(pendingMouseY);
            writer.Write(mouseInputActive);
            writer.Write(floatingInputCycleCounter);
            writer.Write(peripheralCycleCounter);
            writer.Write(lastPortBReadCycle);
            writer.Write(portBPollReadCount);
            writer.Write(timer1Counter);
            writer.Write(timer1Latch);
            writer.Write(timer2Counter);
            writer.Write(timer2Latch);
            writer.Write(timer1Running);
            writer.Write(timer1HasInterrupted);
            writer.Write(timer2Running);
            writer.Write(timer2HasInterrupted);
            writer.Write(justHit);
            writer.Write(peripheralCycleRemainder);
        }

        public void LoadState(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length != registers.Length)
                throw new InvalidDataException("Save state has an incompatible user VIA register block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            bytes.CopyTo(registers, 0);
            interruptFlags = reader.ReadByte();
            interruptEnable = reader.ReadByte();
            portA = reader.ReadByte();
            portB = reader.ReadByte();
            dataDirectionA = reader.ReadByte();
            dataDirectionB = reader.ReadByte();
            externalPortBMask = reader.ReadByte();
            externalPortBValue = reader.ReadByte();
            floatingPortBInput = reader.ReadByte();
            mouseButtonBits = reader.ReadByte();
            pendingMouseX = reader.ReadInt32();
            pendingMouseY = reader.ReadInt32();
            mouseInputActive = reader.ReadBoolean();
            floatingInputCycleCounter = reader.ReadInt32();
            peripheralCycleCounter = reader.ReadInt32();
            lastPortBReadCycle = reader.ReadInt32();
            portBPollReadCount = reader.ReadInt32();
            timer1Counter = reader.ReadInt32();
            timer1Latch = reader.ReadInt32();
            timer2Counter = reader.ReadInt32();
            timer2Latch = reader.ReadInt32();
            timer1Running = reader.ReadBoolean();
            timer1HasInterrupted = reader.ReadBoolean();
            timer2Running = reader.ReadBoolean();
            timer2HasInterrupted = reader.ReadBoolean();
            justHit = reader.ReadInt32();
            peripheralCycleRemainder = reader.ReadInt32();
        }

        /// <summary>The 6522 timer counters are observed by the 2 MHz CPU bus, so keep their half-cycle phase.</summary>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            peripheralCycleCounter += cycles / 2;
            TickFloatingInputs(cycles / 2);
            justHit = 0;

            if (timer1Running)
                TickTimer1(cycles);

            if (timer2Running)
                TickTimer2(cycles);
        }

        public byte Read(ushort address)
        {
            int register = address & 0x0F;

            return register switch
            {
                0x0 => ReadPortB(),
                0x1 or 0xF => ReadPort(portA, dataDirectionA, 0xFF),
                0x2 => dataDirectionB,
                0x3 => dataDirectionA,
                0x4 => ReadTimerLow(timer1Counter, InterruptFlagTimer1),
                0x5 => ReadTimerHigh(timer1Counter),
                0x6 => ReadTimerLowLatch(timer1Latch),
                0x7 => ReadTimerHighLatch(timer1Latch),
                0x8 => ReadTimerLow(timer2Counter, InterruptFlagTimer2),
                0x9 => ReadTimerHigh(timer2Counter),
                0xD => GetInterruptFlags(),
                0xE => (byte)(interruptEnable | 0x80),
                _ => registers[register]
            };
        }

        public void Write(ushort address, byte value)
        {
            int register = address & 0x0F;
            byte previousPcr = registers[0xC];
            registers[register] = value;

            switch (register)
            {
                case 0x0:
                    portB = value;
                    break;

                case 0x1:
                case 0xF:
                    portA = value;
                    break;

                case 0x2:
                    dataDirectionB = value;
                    break;

                case 0x3:
                    dataDirectionA = value;
                    break;

                case 0x4:
                    timer1Latch = (timer1Latch & 0x1FE00) | (value << 1);
                    registers[0x6] = value;
                    break;

                case 0x5:
                    timer1Latch = (timer1Latch & 0x1FE) | (value << 9);
                    LoadTimer1Counter();
                    timer1Running = true;
                    timer1HasInterrupted = false;
                    registers[0x7] = value;
                    ClearTimerInterrupt(InterruptFlagTimer1, 0x01);
                    break;

                case 0x6:
                    timer1Latch = (timer1Latch & 0x1FE00) | (value << 1);
                    break;

                case 0x7:
                    timer1Latch = (timer1Latch & 0x1FE) | (value << 9);
                    ClearTimerInterrupt(InterruptFlagTimer1, 0x01);
                    break;

                case 0x8:
                    timer2Latch = (timer2Latch & 0x1FE00) | (value << 1);
                    break;

                case 0x9:
                    timer2Latch = (timer2Latch & 0x1FE) | (value << 9);
                    timer2Counter = timer2Latch;
                    LoadTimer2Counter();
                    timer2Running = true;
                    timer2HasInterrupted = false;
                    ClearTimerInterrupt(InterruptFlagTimer2, 0x02);
                    break;

                case 0xB:
                    if ((justHit & 0x01) != 0 && (value & 0x40) == 0)
                        timer1HasInterrupted = true;
                    break;

                case 0xC:
                    MaybeStrobePrinter(previousPcr, value);
                    break;

                case 0xD:
                    ClearInterrupt((byte)(value & 0x7F));
                    if ((justHit & 0x01) != 0)
                        SetInterrupt(InterruptFlagTimer1);
                    if ((justHit & 0x02) != 0)
                        SetInterrupt(InterruptFlagTimer2);
                    break;

                case 0xE:
                    if ((value & 0x80) != 0)
                        interruptEnable |= (byte)(value & 0x7F);
                    else
                        interruptEnable &= unchecked((byte)~(value & 0x7F));
                    break;
            }
        }

        private void MaybeStrobePrinter(byte previousPcr, byte value)
        {
            if (!PrinterEnabled || PrinterByteWritten is null)
                return;

            int previousCa2Mode = (previousPcr >> 1) & 0x07;
            int ca2Mode = (value >> 1) & 0x07;
            if (dataDirectionA == 0xFF && previousCa2Mode != ca2Mode && ca2Mode == 0x06)
            {
                PrinterByteWritten(portA);
                SetInterrupt(InterruptFlagCa1);
            }
        }

        private void TickTimer1(int cycles)
        {
            int oldCounter = timer1Counter;
            timer1Counter -= cycles;
            if (timer1Counter >= TimerExpiredThreshold)
                return;

            if (oldCounter > -3 && !timer1HasInterrupted)
            {
                SetInterrupt(InterruptFlagTimer1);
                if (timer1Counter == -3)
                    justHit |= 0x01;
                if (!IsTimer1FreeRunning())
                    timer1HasInterrupted = true;
            }

            if (oldCounter > -3 && !IsTimer1FreeRunning())
                timer1HasInterrupted = true;

            while (timer1Counter < -3)
                ReloadTimer1Counter();
        }

        private void ReloadTimer1Counter()
        {
            timer1Counter += timer1Latch + Timer1ReloadExtraCycles;
        }

        private void LoadTimer1Counter()
        {
            timer1Counter = timer1Latch + Timer1LoadExtraCycles;
        }

        private void TickTimer2(int cycles)
        {
            int oldCounter = timer2Counter;
            timer2Counter -= cycles;
            if (timer2Counter >= TimerExpiredThreshold)
                return;

            if (oldCounter > -3 && !timer2HasInterrupted)
            {
                timer2HasInterrupted = true;
                SetInterrupt(InterruptFlagTimer2);
                if (timer2Counter == -3)
                    justHit |= 0x02;
            }

            timer2Counter += 0x20000;
        }

        private void LoadTimer2Counter()
        {
            timer2Counter = timer2Latch + Timer2LoadExtraCycles;
            if ((registers[0xB] & 0x20) != 0)
                timer2Counter -= 2;
        }

        private byte ReadTimerLow(int value, byte interruptFlag)
        {
            ClearTimerInterrupt(interruptFlag, interruptFlag == InterruptFlagTimer1 ? 0x01 : 0x02);
            return (byte)(((value + 1) >> 1) & 0xFF);
        }

        private void ClearTimerInterrupt(byte interruptFlag, int justHitMask)
        {
            if ((justHit & justHitMask) == 0)
                ClearInterrupt(interruptFlag);
        }

        private static byte ReadTimerHigh(int value)
        {
            return (byte)(((value + 1) >> 9) & 0xFF);
        }

        private static byte ReadTimerLowLatch(int value)
        {
            return (byte)((value >> 1) & 0xFF);
        }

        private static byte ReadTimerHighLatch(int value)
        {
            return (byte)((value >> 9) & 0xFF);
        }

        private byte GetInterruptFlags()
        {
            byte flags = interruptFlags;
            if ((interruptFlags & interruptEnable & 0x7F) != 0)
                flags |= InterruptSummary;

            return flags;
        }

        private void SetInterrupt(byte flag)
        {
            interruptFlags |= flag;
        }

        private void ClearInterrupt(byte mask)
        {
            if ((mask & 0x08) != 0 && (interruptFlags & 0x08) != 0)
                pendingMouseX -= Math.Sign(pendingMouseX);

            if ((mask & 0x10) != 0 && (interruptFlags & 0x10) != 0)
                pendingMouseY -= Math.Sign(pendingMouseY);

            interruptFlags &= unchecked((byte)~mask);
            RefreshMouseInputBits();
        }

        private static byte MapAmxButtonBits(byte activeLowButtons)
        {
            byte value = 0xE0;

            if ((activeLowButtons & 0x01) == 0)
                value &= 0xDF;

            if ((activeLowButtons & 0x04) == 0)
                value &= 0xBF;

            if ((activeLowButtons & 0x02) == 0)
                value &= 0x7F;

            return value;
        }

        private void RefreshMouseInputBits()
        {
            if (!mouseInputActive)
                return;

            byte value = mouseButtonBits;

            if (pendingMouseX > 0)
                value |= 0x04;

            if (pendingMouseY > 0)
                value |= 0x01;

            externalPortBMask = 0xE5;
            externalPortBValue = value;

            if (pendingMouseX != 0)
                SetInterrupt(0x08);

            if (pendingMouseY != 0)
                SetInterrupt(0x10);
        }

        private bool IsTimer1FreeRunning()
        {
            return (registers[0xB] & 0x40) != 0;
        }

        private static int ReadTimerOffset(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
        }

        private byte ReadPortB()
        {
            byte floatingInput = IsRepeatedPortBPoll() ? floatingPortBInput : (byte)0xFF;
            // Unconnected user-port inputs idle high on the BBC. Defender polls PB7
            // with BIT $FE60 and visibly stalls if the emulator lets that line drift low.
            floatingInput |= 0x80;
            byte value = ReadPort(portB, dataDirectionB, floatingInput);
            value = (byte)((value & ~externalPortBMask) | externalPortBValue);
            if ((externalPortBMask & 0x18) != 0)
                ClearInterrupt(0x18);
            return value;
        }

        private bool IsRepeatedPortBPoll()
        {
            bool allInput = dataDirectionB == 0;
            int cyclesSinceLastRead = peripheralCycleCounter - lastPortBReadCycle;

            if (allInput && cyclesSinceLastRead <= FloatingInputPollWindowCycles)
                portBPollReadCount++;
            else
                portBPollReadCount = 1;

            lastPortBReadCycle = peripheralCycleCounter;
            return portBPollReadCount >= 3;
        }

        private void TickFloatingInputs(int cycles)
        {
            floatingInputCycleCounter += cycles;
            while (floatingInputCycleCounter >= FloatingInputPeriodCycles)
            {
                floatingInputCycleCounter -= FloatingInputPeriodCycles;
                floatingPortBInput = NextFloatingInput(floatingPortBInput);
            }
        }

        private static byte ReadPort(byte output, byte direction, byte floatingInput)
        {
            return (byte)((output & direction) | (floatingInput & ~direction));
        }

        private static byte NextFloatingInput(byte value)
        {
            return (byte)((value * 73) + 0x41);
        }
    }
}
