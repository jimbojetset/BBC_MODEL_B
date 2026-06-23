// ============================================================================
// Project:     BBC
// File:        User6522Via.cs
// Description: BBC user 6522 VIA: user-port I/O, game-control inputs, AMX-style
//              mouse pulses, and timer IRQ behaviour.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
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
        private const byte InterruptSummary = 0x80;
        private const int FloatingInputPeriodCycles = 50_000;
        private const int FloatingInputPollWindowCycles = 2_048;
        private const int Timer1ReloadExtraCycles = 1;
        private const int Timer1LoadExtraCycles = 128;
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
        private ushort timer1Counter;
        private ushort timer1Latch;
        private ushort timer2Counter;
        private ushort timer2Latch;
        private bool timer1Running;
        private bool timer2Running;
        private bool timer2HasInterrupted;
        private int peripheralCycleRemainder;

        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE60 and <= 0xFE6F;
        }

        public bool IrqAsserted => (interruptFlags & interruptEnable & 0x7F) != 0;

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
            timer1Counter = 0;
            timer1Latch = 0;
            timer2Counter = 0;
            timer2Latch = 0;
            timer1Running = false;
            timer2Running = false;
            timer2HasInterrupted = false;
            peripheralCycleRemainder = 0;
        }

        /// <summary>The user 6522 timers run from the same 1 MHz peripheral clock as the system 6522 VIA.</summary>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            int peripheralCycles = (cycles + peripheralCycleRemainder) / 2;
            peripheralCycleRemainder = (cycles + peripheralCycleRemainder) & 1;

            if (peripheralCycles == 0)
                return;

            peripheralCycleCounter += peripheralCycles;
            TickFloatingInputs(peripheralCycles);
            TickTimer1(peripheralCycles);

            if (timer2Running)
                TickTimer2(peripheralCycles);
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
                0x5 => (byte)(timer1Counter >> 8),
                0x6 => (byte)timer1Latch,
                0x7 => (byte)(timer1Latch >> 8),
                0x8 => ReadTimerLow(timer2Counter, InterruptFlagTimer2),
                0x9 => (byte)(timer2Counter >> 8),
                0xD => GetInterruptFlags(),
                0xE => (byte)(interruptEnable | 0x80),
                _ => registers[register]
            };
        }

        public void Write(ushort address, byte value)
        {
            int register = address & 0x0F;
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
                    timer1Latch = (ushort)((timer1Latch & 0xFF00) | value);
                    registers[0x6] = value;
                    break;

                case 0x5:
                    timer1Latch = (ushort)((value << 8) | (timer1Latch & 0x00FF));
                    LoadTimer1Counter();
                    timer1Running = true;
                    registers[0x7] = value;
                    ClearInterrupt(InterruptFlagTimer1);
                    break;

                case 0x6:
                    timer1Latch = (ushort)((timer1Latch & 0xFF00) | value);
                    break;

                case 0x7:
                    timer1Latch = (ushort)((value << 8) | (timer1Latch & 0x00FF));
                    ClearInterrupt(InterruptFlagTimer1);
                    break;

                case 0x8:
                    timer2Latch = (ushort)((timer2Latch & 0xFF00) | value);
                    break;

                case 0x9:
                    timer2Latch = (ushort)((value << 8) | (timer2Latch & 0x00FF));
                    timer2Counter = timer2Latch;
                    timer2Running = true;
                    timer2HasInterrupted = false;
                    ClearInterrupt(InterruptFlagTimer2);
                    break;

                case 0xD:
                    ClearInterrupt((byte)(value & 0x7F));
                    break;

                case 0xE:
                    if ((value & 0x80) != 0)
                        interruptEnable |= (byte)(value & 0x7F);
                    else
                        interruptEnable &= unchecked((byte)~(value & 0x7F));
                    break;
            }
        }

        private void TickTimer1(int cycles)
        {
            if (!timer1Running)
            {
                timer1Counter -= (ushort)cycles;
                return;
            }

            int remaining = cycles;
            while (remaining > 0 && timer1Running)
            {
                if (remaining <= timer1Counter)
                {
                    timer1Counter -= (ushort)remaining;
                    return;
                }

                remaining -= timer1Counter + 1;
                SetInterrupt(InterruptFlagTimer1);
                if (IsTimer1FreeRunning())
                {
                    ReloadTimer1Counter();
                }
                else
                {
                    timer1Running = false;
                }
            }
        }

        private void ReloadTimer1Counter()
        {
            timer1Counter = AddTimerOffset(timer1Latch, Timer1ReloadExtraCycles);
        }

        private void LoadTimer1Counter()
        {
            timer1Counter = AddTimerOffset(timer1Latch, Timer1LoadExtraCycles);
        }

        private void TickTimer2(int cycles)
        {
            if (!timer2HasInterrupted && cycles > timer2Counter)
            {
                timer2HasInterrupted = true;
                SetInterrupt(InterruptFlagTimer2);
            }

            timer2Counter = unchecked((ushort)(timer2Counter - cycles));
        }

        private byte ReadTimerLow(ushort value, byte interruptFlag)
        {
            ClearInterrupt(interruptFlag);
            return (byte)value;
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

        private static ushort AddTimerOffset(ushort value, int offset)
        {
            return (ushort)Math.Clamp(value + offset, 0, 0xFFFF);
        }

        private bool IsTimer1FreeRunning()
        {
            return (registers[0xB] & 0x40) != 0;
        }

        private byte ReadPortB()
        {
            byte floatingInput = IsRepeatedPortBPoll() ? floatingPortBInput : (byte)0xFF;
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
