// ============================================================================
// Project:     BBC
// File:        UserVia.cs
// Description: BBC user VIA timer and interrupt model used by games and
//              user-port polling code.
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
    /// Models the subset of the BBC user VIA needed by game timer IRQs.
    /// </summary>
    public sealed class UserVia
    {
        private const byte InterruptFlagTimer1 = 0x40;
        private const byte InterruptFlagTimer2 = 0x20;
        private const byte InterruptSummary = 0x80;
        private const int FloatingInputPollWindowCycles = 512;
        private const int FloatingInputChangeCycles = 40_000;
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
        private byte mouseButtonBits = 0xE0;
        private int pendingMouseX;
        private int pendingMouseY;
        private bool mouseInputActive;
        private int peripheralCycleCounter;
        private int lastPortBReadCycle = int.MinValue / 2;
        private int portBPollStartCycle;
        private int portBPollReadCount;
        private ushort timer1Counter;
        private ushort timer1Latch;
        private ushort timer2Counter;
        private ushort timer2Latch;
        private bool timer1Running;
        private bool timer2Running;
        private bool timer2HasInterrupted;
        private int peripheralCycleRemainder;

        /// <summary>Checks whether address is true for the current emulator state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when the address is within &amp;FE60-&amp;FE6F.</returns>
        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE60 and <= 0xFE6F;
        }

        /// <summary>Gets whether the VIA IRQ output is currently asserted.</summary>
        public bool IrqAsserted => (interruptFlags & interruptEnable & 0x7F) != 0;

        /// <summary>Sets externally-driven user-port B input bits.</summary>
        /// <param name="mask">The bits controlled by the external device.</param>
        /// <param name="value">The bit values exposed on the user port.</param>
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

        /// <summary>Sets active-low switched-joystick inputs on the user port.</summary>
        /// <param name="left">Whether left is pressed.</param>
        /// <param name="right">Whether right is pressed.</param>
        /// <param name="up">Whether up is pressed.</param>
        /// <param name="down">Whether down is pressed.</param>
        /// <param name="fire">Whether fire is pressed.</param>
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

        /// <summary>Sets mouse-style user-port inputs and raises edge interrupt flags for movement.</summary>
        /// <param name="activeLowButtons">The active-low button bits exposed on PB0-PB2.</param>
        /// <param name="deltaX">The host mouse X movement steps.</param>
        /// <param name="deltaY">The host mouse Y movement steps.</param>
        public void SetMouseInput(byte activeLowButtons, int deltaX, int deltaY)
        {
            mouseInputActive = true;
            mouseButtonBits = MapAmxButtonBits(activeLowButtons);
            pendingMouseX += deltaX;
            pendingMouseY += deltaY;
            RefreshMouseInputBits();
        }

        /// <summary>Resets the modelled VIA state.</summary>
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
            mouseButtonBits = 0xE0;
            pendingMouseX = 0;
            pendingMouseY = 0;
            mouseInputActive = false;
            peripheralCycleCounter = 0;
            lastPortBReadCycle = int.MinValue / 2;
            portBPollStartCycle = 0;
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

        /// <summary>Advances user VIA timers for the supplied CPU cycle count.</summary>
        /// <param name="cycles">The elapsed 6502 cycles.</param>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            int peripheralCycles = (cycles + peripheralCycleRemainder) / 2;
            peripheralCycleRemainder = (cycles + peripheralCycleRemainder) & 1;

            if (peripheralCycles == 0)
                return;

            peripheralCycleCounter += peripheralCycles;
            TickTimer1(peripheralCycles);

            if (timer2Running)
                TickTimer2(peripheralCycles);
        }

        /// <summary>Reads  from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
        public byte Read(ushort address)
        {
            int register = address & 0x0F;

            return register switch
            {
                0x0 => ReadPortB(),
                0x1 or 0xF => ReadPort(portA, dataDirectionA),
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

        /// <summary>Writes  into emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The value written by the CPU.</param>
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

        /// <summary>Advances user VIA timer 1 and raises its interrupt on underflow.</summary>
        /// <param name="cycles">The number of emulated CPU cycles.</param>
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

        /// <summary>Reloads user VIA timer 1 from its latch after underflow.</summary>
        private void ReloadTimer1Counter()
        {
            timer1Counter = AddTimerOffset(timer1Latch, Timer1ReloadExtraCycles);
        }

        /// <summary>Loads user VIA timer 1 from its latch and clears the timer interrupt.</summary>
        private void LoadTimer1Counter()
        {
            timer1Counter = AddTimerOffset(timer1Latch, Timer1LoadExtraCycles);
        }

        /// <summary>Advances user VIA timer 2 and raises its interrupt on underflow.</summary>
        /// <param name="cycles">The number of emulated CPU cycles.</param>
        private void TickTimer2(int cycles)
        {
            if (timer2HasInterrupted)
                return;

            if (cycles <= timer2Counter)
            {
                timer2Counter -= (ushort)cycles;
                return;
            }

            timer2Counter = 0xFFFF;
            timer2Running = false;
            timer2HasInterrupted = true;
            SetInterrupt(InterruptFlagTimer2);
        }

        /// <summary>Reads a user VIA timer low byte and clears the associated interrupt flag.</summary>
        /// <param name="value">The input value.</param>
        /// <param name="interruptFlag">The interrupt flag value.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadTimerLow(ushort value, byte interruptFlag)
        {
            ClearInterrupt(interruptFlag);
            return (byte)value;
        }

        /// <summary>Returns the user VIA interrupt flag register with bit 7 reflecting enabled active interrupts.</summary>
        /// <returns>The computed value.</returns>
        private byte GetInterruptFlags()
        {
            byte flags = interruptFlags;
            if ((interruptFlags & interruptEnable & 0x7F) != 0)
                flags |= InterruptSummary;

            return flags;
        }

        /// <summary>Sets a user VIA interrupt flag and refreshes derived IRQ state.</summary>
        /// <param name="flag">The flag value.</param>
        private void SetInterrupt(byte flag)
        {
            interruptFlags |= flag;
        }

        /// <summary>Clears selected user VIA interrupt flags and updates derived IRQ state.</summary>
        /// <param name="mask">The bit mask.</param>
        private void ClearInterrupt(byte mask)
        {
            if ((mask & 0x08) != 0 && (interruptFlags & 0x08) != 0)
                pendingMouseX -= Math.Sign(pendingMouseX);

            if ((mask & 0x10) != 0 && (interruptFlags & 0x10) != 0)
                pendingMouseY -= Math.Sign(pendingMouseY);

            interruptFlags &= unchecked((byte)~mask);
            RefreshMouseInputBits();
        }

        /// <summary>Maps host active-low buttons to the AMX mouse user-port button lines.</summary>
        /// <param name="activeLowButtons">The host active-low button bits.</param>
        /// <returns>The AMX PB5-PB7 button bits.</returns>
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

        /// <summary>Refreshes AMX mouse user-port direction, button, and interrupt state.</summary>
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

        /// <summary>Applies the VIA timer reload offset used by the 6522 counter pipeline.</summary>
        /// <param name="value">The input value.</param>
        /// <param name="offset">The buffer or image offset.</param>
        /// <returns>The resulting value.</returns>
        private static ushort AddTimerOffset(ushort value, int offset)
        {
            return (ushort)Math.Clamp(value + offset, 0, 0xFFFF);
        }

        /// <summary>Checks whether user VIA timer 1 is configured for free-running reloads.</summary>
        /// <returns>True when timer1 free running is true; otherwise, false.</returns>
        private bool IsTimer1FreeRunning()
        {
            return (registers[0xB] & 0x40) != 0;
        }

        /// <summary>Reads user VIA port B, including the printer acknowledge input bit.</summary>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadPortB()
        {
            byte value = ReadPort(portB, dataDirectionB);

            if (dataDirectionB == 0x00)
            {
                int cyclesSinceLastRead = peripheralCycleCounter - lastPortBReadCycle;
                if (cyclesSinceLastRead > FloatingInputPollWindowCycles)
                {
                    portBPollStartCycle = peripheralCycleCounter;
                    portBPollReadCount = 1;
                }
                else
                {
                    portBPollReadCount++;
                }

                if (portBPollReadCount > 1 && peripheralCycleCounter - portBPollStartCycle >= FloatingInputChangeCycles)
                {
                    value = 0xFE;
                    portBPollStartCycle = peripheralCycleCounter;
                    portBPollReadCount = 0;
                }
            }

            lastPortBReadCycle = peripheralCycleCounter;
            value = (byte)((value & ~externalPortBMask) | externalPortBValue);
            if ((externalPortBMask & 0x18) != 0)
                ClearInterrupt(0x18);
            return value;
        }

        /// <summary>Combines a user VIA output latch and data-direction register into the visible port value.</summary>
        /// <param name="output">The port output latch value.</param>
        /// <param name="direction">The I/O direction register value.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private static byte ReadPort(byte output, byte direction)
        {
            return (byte)((output & direction) | (0xFF & ~direction));
        }
    }
}
