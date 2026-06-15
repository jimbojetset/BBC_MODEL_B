// ============================================================================
// Project:     BBC
// File:        UserVia.cs
// Description: Minimal BBC user VIA timer model.
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
        private const int FloatingInputPeriodCycles = 50_000;
        private readonly byte[] registers = new byte[16];
        private byte interruptFlags;
        private byte interruptEnable;
        private byte portA;
        private byte portB;
        private byte dataDirectionA;
        private byte dataDirectionB;
        private byte floatingPortAInput = 0x5A;
        private byte floatingPortBInput = 0xA5;
        private int floatingInputCycleCounter;
        private ushort timer1Counter;
        private ushort timer1Latch;
        private ushort timer2Counter;
        private ushort timer2Latch;
        private bool timer1Running;
        private bool timer2Running;
        private bool timer2HasInterrupted;
        private int peripheralCycleRemainder;

        /// <summary>Returns whether an address belongs to the user VIA.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when the address is within &amp;FE60-&amp;FE6F.</returns>
        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE60 and <= 0xFE6F;
        }

        /// <summary>Gets whether the VIA IRQ output is currently asserted.</summary>
        public bool IrqAsserted => (interruptFlags & interruptEnable & 0x7F) != 0;

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
            floatingPortAInput = 0x5A;
            floatingPortBInput = 0xA5;
            floatingInputCycleCounter = 0;
            timer1Counter = 0;
            timer1Latch = 0;
            timer2Counter = 0;
            timer2Latch = 0;
            timer1Running = false;
            timer2Running = false;
            timer2HasInterrupted = false;
            peripheralCycleRemainder = 0;
        }

        /// <summary>Advances VIA timers by the supplied number of CPU cycles.</summary>
        /// <param name="cycles">The elapsed 6502 cycles.</param>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            int peripheralCycles = (cycles + peripheralCycleRemainder) / 2;
            peripheralCycleRemainder = (cycles + peripheralCycleRemainder) & 1;

            if (peripheralCycles == 0)
                return;

            TickFloatingInputs(peripheralCycles);
            TickTimer1(peripheralCycles);

            if (timer2Running)
                TickTimer2(peripheralCycles);
        }

        /// <summary>Reads a user VIA register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
        public byte Read(ushort address)
        {
            int register = address & 0x0F;

            return register switch
            {
                0x0 => ReadPort(portB, dataDirectionB, floatingPortBInput),
                0x1 or 0xF => ReadPort(portA, dataDirectionA, floatingPortAInput),
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

        /// <summary>Writes a user VIA register.</summary>
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
                    timer1Counter = timer1Latch;
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
                timer1Counter = timer1Latch;
            }
        }

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
            interruptFlags &= unchecked((byte)~mask);
        }

        private void TickFloatingInputs(int cycles)
        {
            floatingInputCycleCounter += cycles;
            while (floatingInputCycleCounter >= FloatingInputPeriodCycles)
            {
                floatingInputCycleCounter -= FloatingInputPeriodCycles;
                floatingPortAInput = NextFloatingInput(floatingPortAInput);
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
