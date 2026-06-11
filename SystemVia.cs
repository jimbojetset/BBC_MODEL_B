// ============================================================================
// Project:     BBC
// File:        SystemVia.cs
// Description: Minimal BBC system VIA model for slow-bus sound writes.
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
    /// Models the subset of the BBC system VIA needed by the sound slow bus.
    /// </summary>
    public sealed class SystemVia
    {
        private const byte SoundWriteEnableLatchBit = 0;
        private const byte KeyboardWriteEnableLatchBit = 3;
        private const byte ScreenAddressLatchLowBit = 4;
        private const byte ScreenAddressLatchHighBit = 5;
        private const byte InterruptFlagTimer1 = 0x40;
        private const byte InterruptFlagTimer2 = 0x20;
        private const byte InterruptFlagVsync = 0x02;
        private const byte InterruptFlagKeyboard = 0x01;
        // CB1 input on the system VIA carries µPD7002 EOC (negative edge = conversion complete).
        private const byte InterruptFlagAdcEoc = 0x10;
        private const int VsyncPeripheralCycles = 20_000;
        private const byte InterruptSummary = 0x80;
        private readonly Sound sound;
        private readonly byte[] registers = new byte[16];
        private readonly bool[] pressedKeys = new bool[128];
        private byte addressableLatch = 0xFF;
        private byte interruptFlags;
        private byte interruptEnable;
        private byte portA;
        private byte portB;
        private byte dataDirectionA;
        private byte dataDirectionB;
        private ushort timer1Counter;
        private ushort timer1Latch;
        private ushort timer2Counter;
        private ushort timer2Latch;
        private bool timer1Running;
        private bool timer2Running;
        private bool timer2HasInterrupted;
        private int peripheralCycleRemainder;
        private int vsyncCycleCounter;
        private int frameCounter;
        private int vsyncPeriodOverride;

        /// <summary>Initializes a new system VIA shim.</summary>
        /// <param name="sound">The sound generator connected to the VIA slow bus.</param>
        public SystemVia(Sound sound)
        {
            this.sound = sound ?? throw new ArgumentNullException(nameof(sound));
        }

        /// <summary>Raised when the addressable latch changes the video RAM wrap window.</summary>
        public event Action<ScreenMemoryWindow>? ScreenMemoryWindowChanged;

        /// <summary>Returns whether an address belongs to the system VIA.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when the address is within &amp;FE40-&amp;FE4F.</returns>
        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE40 and <= 0xFE4F;
        }

        /// <summary>Resets the modelled VIA state.</summary>
        public void Reset()
        {
            Array.Clear(registers);
            Array.Clear(pressedKeys);
            addressableLatch = 0xFF;
            interruptFlags = 0;
            interruptEnable = 0;
            portA = 0;
            portB = 0;
            dataDirectionA = 0;
            dataDirectionB = 0;
            timer1Counter = 0;
            timer1Latch = 0;
            timer2Counter = 0;
            timer2Latch = 0;
            timer1Running = false;
            timer2Running = false;
            timer2HasInterrupted = false;
            peripheralCycleRemainder = 0;
            vsyncCycleCounter = 0;
            frameCounter = 0;
            vsyncPeriodOverride = 0;
        }

        /// <summary>Sets a CRTC-derived vsync period in 1 MHz peripheral cycles.</summary>
        /// <param name="peripheralCycles">The frame period in peripheral cycles, or 0 to use the default 50 Hz timing.</param>
        public void SetVsyncPeriod(int peripheralCycles)
        {
            // Clamp to a sane range so a half-programmed CRTC can't stall vsync entirely.
            // 30 Hz to 100 Hz covers every realistic BBC mode while rejecting nonsense values.
            if (peripheralCycles >= 10_000 && peripheralCycles <= 33_333)
                vsyncPeriodOverride = peripheralCycles;
            else
                vsyncPeriodOverride = 0;
        }

        private int CurrentVsyncPeriod => vsyncPeriodOverride > 0 ? vsyncPeriodOverride : VsyncPeripheralCycles;

        /// <summary>Gets the number of emulated 50 Hz video frames since reset.</summary>
        public int FrameCounter => Volatile.Read(ref frameCounter);

        /// <summary>Gets the approximate CPU cycles elapsed since the current 50 Hz frame started.</summary>
        public int FrameCpuCycle => Math.Clamp((vsyncCycleCounter * 2) + peripheralCycleRemainder, 0, (CurrentVsyncPeriod * 2) - 1);

        /// <summary>Gets the currently selected video RAM start address.</summary>
        public int ScreenMemoryStart => CurrentScreenMemoryWindow.Start;

        /// <summary>Gets the currently selected video RAM window size.</summary>
        public int ScreenMemorySize => CurrentScreenMemoryWindow.Size;

        /// <summary>Gets the currently selected video RAM window and BBC hardware scroll mapping.</summary>
        public ScreenMemoryWindow CurrentScreenMemoryWindow => GetScreenMemoryWindow(addressableLatch);

        /// <summary>Updates one BBC keyboard matrix key state.</summary>
        /// <param name="internalKey">The BBC internal key number.</param>
        /// <param name="pressed">Whether the key is currently pressed.</param>
        public void SetKeyState(byte internalKey, bool pressed)
        {
            if (internalKey >= pressedKeys.Length)
                return;

            pressedKeys[internalKey] = pressed;

            if (pressed && IsKeyboardAutoScanEnabled())
                SetInterrupt(InterruptFlagKeyboard);

            if (dataDirectionA == 0x7F && !IsKeyboardAutoScanEnabled())
                UpdateKeyboardColumnInterrupt();
        }

        /// <summary>Signals an ADC end-of-conversion edge into the system VIA CB1 input.</summary>
        /// <param name="eocActive">True when the ADC has finished a conversion (active-low EOC line going low).</param>
        public void SignalAdcEndOfConversion(bool eocActive)
        {
            if (eocActive)
                SetInterrupt(InterruptFlagAdcEoc);
            else
                ClearInterrupt(InterruptFlagAdcEoc);
        }

        /// <summary>Returns whether one BBC keyboard matrix key is currently held.</summary>
        /// <param name="internalKey">The BBC internal key number.</param>
        /// <returns>True when the key is pressed.</returns>
        public bool IsKeyPressed(byte internalKey)
        {
            return internalKey < pressedKeys.Length && pressedKeys[internalKey];
        }

        /// <summary>Gets whether the VIA IRQ output is currently asserted.</summary>
        public bool IrqAsserted => (interruptFlags & interruptEnable & 0x7F) != 0;

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

            if (timer1Running)
                TickTimer1(peripheralCycles);

            if (timer2Running)
                TickTimer2(peripheralCycles);

            TickVsync(peripheralCycles);
        }

        /// <summary>Reads a system VIA register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
        public byte Read(ushort address)
        {
            int register = address & 0x0F;

            return register switch
            {
                0x0 => ReadPort(portB, dataDirectionB),
                0x1 or 0xF => ReadPortAWithHandshake(),
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

        /// <summary>Writes a system VIA register.</summary>
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
                    WritePortB(value);
                    break;

                case 0x1:
                case 0xF:
                    portA = value;
                    ClearInterrupt(InterruptFlagVsync);
                    if (dataDirectionA == 0x7F && !IsKeyboardAutoScanEnabled())
                        UpdateKeyboardColumnInterrupt();
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
                    timer1Counter = timer1Latch;
                }
                else
                {
                    timer1Running = false;
                }
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

        private void TickVsync(int peripheralCycles)
        {
            vsyncCycleCounter += peripheralCycles;

            int period = CurrentVsyncPeriod;
            while (vsyncCycleCounter >= period)
            {
                vsyncCycleCounter -= period;
                Interlocked.Increment(ref frameCounter);
                SetInterrupt(InterruptFlagVsync);
                period = CurrentVsyncPeriod;
            }
        }

        private void WritePortB(byte value)
        {
            int latchBit = value & 0x07;
            bool latchValue = (value & 0x08) != 0;
            bool previousSoundWriteEnable = (addressableLatch & (1 << SoundWriteEnableLatchBit)) != 0;
            ScreenMemoryWindow previousWindow = GetScreenMemoryWindow(addressableLatch);

            if (latchValue)
                addressableLatch |= (byte)(1 << latchBit);
            else
                addressableLatch &= unchecked((byte)~(1 << latchBit));

            bool currentSoundWriteEnable = (addressableLatch & (1 << SoundWriteEnableLatchBit)) != 0;

            if (latchBit == SoundWriteEnableLatchBit && previousSoundWriteEnable && !currentSoundWriteEnable)
                sound.WriteData(portA);

            if (latchBit is ScreenAddressLatchLowBit or ScreenAddressLatchHighBit)
            {
                ScreenMemoryWindow currentWindow = GetScreenMemoryWindow(addressableLatch);
                if (currentWindow != previousWindow)
                    ScreenMemoryWindowChanged?.Invoke(currentWindow);
            }
        }

        private static ScreenMemoryWindow GetScreenMemoryWindow(byte latch)
        {
            int code = ((latch >> ScreenAddressLatchLowBit) & 0x01)
                | (((latch >> ScreenAddressLatchHighBit) & 0x01) << 1);

            return code switch
            {
                0 => new ScreenMemoryWindow(0x4000, 0x4000, code, 8),  // Mode 3: 16K.
                1 => new ScreenMemoryWindow(0x6000, 0x2000, code, 4),  // Mode 6: 8K.
                2 => new ScreenMemoryWindow(0x3000, 0x5000, code, 10), // Modes 0, 1, 2: 20K.
                _ => new ScreenMemoryWindow(0x5800, 0x2800, code, 5)   // Modes 4, 5: 10K.
            };
        }

        private byte ReadTimerLow(ushort counter, byte interruptFlag)
        {
            ClearInterrupt(interruptFlag);
            return (byte)counter;
        }

        private byte GetInterruptFlags()
        {
            byte flags = interruptFlags;
            if ((flags & interruptEnable & 0x7F) != 0)
                flags |= InterruptSummary;

            return flags;
        }

        private void SetInterrupt(byte flag)
        {
            interruptFlags |= flag;
        }

        private void ClearInterrupt(byte flags)
        {
            interruptFlags &= unchecked((byte)~flags);
        }

        private bool IsTimer1FreeRunning()
        {
            return (registers[0xB] & 0x40) != 0;
        }

        private static byte ReadPort(byte output, byte direction)
        {
            return (byte)((output & direction) | (0xFF & ~direction));
        }

        private byte ReadPortAWithHandshake()
        {
            ClearInterrupt(InterruptFlagVsync);

            if (dataDirectionA == 0x7F)
                return ReadKeyboardPortA();

            return ReadPort(portA, dataDirectionA);
        }

        private byte ReadKeyboardPortA()
        {
            byte selectedKey = (byte)(portA & 0x7F);

            if (IsKeyboardAutoScanEnabled())
                return AnyNonModifierKeyPressed() ? (byte)0x80 : (byte)0x00;

            bool pressed = selectedKey < pressedKeys.Length && pressedKeys[selectedKey];
            return (byte)(selectedKey | (pressed ? 0x80 : 0x00));
        }

        private bool AnyNonModifierKeyPressed()
        {
            for (int i = 0x10; i < pressedKeys.Length; i++)
            {
                if (pressedKeys[i])
                    return true;
            }

            return false;
        }

        private bool IsKeyboardAutoScanEnabled()
        {
            return (addressableLatch & (1 << KeyboardWriteEnableLatchBit)) != 0;
        }

        private void UpdateKeyboardColumnInterrupt()
        {
            int column = portA & 0x0F;
            bool keyInColumnPressed = false;

            for (int key = 0x10 + column; key < pressedKeys.Length; key += 0x10)
            {
                if (pressedKeys[key])
                {
                    keyInColumnPressed = true;
                    break;
                }
            }

            if (keyInColumnPressed)
                SetInterrupt(InterruptFlagKeyboard);
            else
                ClearInterrupt(InterruptFlagKeyboard);
        }
    }
}
