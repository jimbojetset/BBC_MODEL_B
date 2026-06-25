// ============================================================================
// Project:     BBC
// File:        System6522Via.cs
// Description: BBC system 6522 VIA: keyboard matrix, slow data bus, video
//              address latch, timers, VSYNC IRQ, and ADC EOC signalling.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See COPYING in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{

    /// <summary>
    /// The BBC Micro's system 6522 VIA owns the keyboard scan lines, the slow
    /// data bus used by the SN76489, the video address latch, and several IRQ
    /// sources that MOS expects to behave like real 6522 pins and timers.
    /// </summary>
    public sealed class System6522Via
    {
        private const byte SoundWriteEnableLatchBit = 0;
        private const byte KeyboardWriteEnableLatchBit = 3;
        private const byte ScreenAddressLatchLowBit = 4;
        private const byte ScreenAddressLatchHighBit = 5;
        private const byte InterruptFlagTimer1 = 0x40;
        private const byte InterruptFlagTimer2 = 0x20;
        private const byte InterruptFlagVsync = 0x02;
        private const byte InterruptFlagKeyboard = 0x01;
        private const byte InterruptFlagAdcEoc = 0x10;
        private const int VsyncPeripheralCycles = 20_000;
        private const int Timer1ReloadExtraCycles = 1;
        private const int Timer1LoadExtraCycles = 128;
        private const byte InterruptSummary = 0x80;
        private readonly SN76489_Sound sound;
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
        private bool vsyncLineActive;
        private bool externalVsyncLineEnabled;

        public System6522Via(SN76489_Sound sound)
        {
            this.sound = sound ?? throw new ArgumentNullException(nameof(sound));
        }

        /// <summary>Raised when IC32 bits 4 or 5 select a different screen memory wrap window.</summary>
        public event Action<ScreenMemoryWindow>? ScreenMemoryWindowChanged;

        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE40 and <= 0xFE4F;
        }

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
            vsyncLineActive = false;
            externalVsyncLineEnabled = false;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(registers.Length);
            writer.Write(registers);
            writer.Write(pressedKeys.Length);
            foreach (bool pressed in pressedKeys)
                writer.Write(pressed);

            writer.Write(addressableLatch);
            writer.Write(interruptFlags);
            writer.Write(interruptEnable);
            writer.Write(portA);
            writer.Write(portB);
            writer.Write(dataDirectionA);
            writer.Write(dataDirectionB);
            writer.Write(timer1Counter);
            writer.Write(timer1Latch);
            writer.Write(timer2Counter);
            writer.Write(timer2Latch);
            writer.Write(timer1Running);
            writer.Write(timer2Running);
            writer.Write(timer2HasInterrupted);
            writer.Write(peripheralCycleRemainder);
            writer.Write(vsyncCycleCounter);
            writer.Write(frameCounter);
            writer.Write(vsyncLineActive);
            writer.Write(externalVsyncLineEnabled);
        }

        public void LoadState(BinaryReader reader)
        {
            ReadBytes(reader, registers);
            int pressedKeyCount = reader.ReadInt32();
            if (pressedKeyCount != pressedKeys.Length)
                throw new InvalidDataException("Save state has an incompatible system VIA keyboard matrix.");

            for (int i = 0; i < pressedKeys.Length; i++)
                pressedKeys[i] = reader.ReadBoolean();

            addressableLatch = reader.ReadByte();
            interruptFlags = reader.ReadByte();
            interruptEnable = reader.ReadByte();
            portA = reader.ReadByte();
            portB = reader.ReadByte();
            dataDirectionA = reader.ReadByte();
            dataDirectionB = reader.ReadByte();
            timer1Counter = reader.ReadUInt16();
            timer1Latch = reader.ReadUInt16();
            timer2Counter = reader.ReadUInt16();
            timer2Latch = reader.ReadUInt16();
            timer1Running = reader.ReadBoolean();
            timer2Running = reader.ReadBoolean();
            timer2HasInterrupted = reader.ReadBoolean();
            peripheralCycleRemainder = reader.ReadInt32();
            vsyncCycleCounter = reader.ReadInt32();
            frameCounter = reader.ReadInt32();
            vsyncLineActive = reader.ReadBoolean();
            externalVsyncLineEnabled = reader.ReadBoolean();

            UpdateSoundSlowDataBus();
            ScreenMemoryWindowChanged?.Invoke(CurrentScreenMemoryWindow);
        }

        private static void ReadBytes(BinaryReader reader, byte[] destination)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException("Save state has an incompatible system VIA register block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            bytes.CopyTo(destination, 0);
        }

        public int FrameCounter => Volatile.Read(ref frameCounter);

        public int ScreenMemoryStart => CurrentScreenMemoryWindow.Start;

        public int ScreenMemorySize => CurrentScreenMemoryWindow.Size;

        public ScreenMemoryWindow CurrentScreenMemoryWindow => GetScreenMemoryWindow(addressableLatch);

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

        /// <summary>Updates the VIA CB1 interrupt state from the uPD7002 EOC line.</summary>
        public void SignalAdcEndOfConversion(bool eocActive)
        {
            if (eocActive)
                SetInterrupt(InterruptFlagAdcEoc);
            else
                ClearInterrupt(InterruptFlagAdcEoc);
        }

        public bool IsKeyPressed(byte internalKey)
        {
            return internalKey < pressedKeys.Length && pressedKeys[internalKey];
        }

        public bool IrqAsserted => (interruptFlags & interruptEnable & 0x7F) != 0;

        public bool ExternalVsyncLineEnabled
        {
            get => externalVsyncLineEnabled;
            set => externalVsyncLineEnabled = value;
        }

        public void SetVsyncLine(bool active)
        {
            if (vsyncLineActive == active)
                return;

            vsyncLineActive = active;
            if (active)
            {
                vsyncCycleCounter = 0;
                peripheralCycleRemainder = 0;
                Interlocked.Increment(ref frameCounter);
                SetInterrupt(InterruptFlagVsync);
            }
        }

        /// <summary>The 6522 timers tick at the BBC's 1 MHz peripheral clock, half the 2 MHz CPU rate.</summary>
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

            if (!externalVsyncLineEnabled)
                TickVsync(peripheralCycles);
        }

        public byte Read(ushort address)
        {
            int register = address & 0x0F;

            return register switch
            {
                0x0 => ReadPort(portB, dataDirectionB),
                0x1 => ReadPortAWithHandshake(),
                0xF => ReadPortAWithoutHandshake(),
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
                    WritePortB(value);
                    break;

                case 0x1:
                case 0xF:
                    portA = value;
                    UpdateSoundSlowDataBus();
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

        private void TickVsync(int peripheralCycles)
        {
            vsyncCycleCounter += peripheralCycles;

            int period = VsyncPeripheralCycles;
            while (vsyncCycleCounter >= period)
            {
                vsyncCycleCounter -= period;
                Interlocked.Increment(ref frameCounter);
                SetInterrupt(InterruptFlagVsync);
            }
        }

        private void WritePortB(byte value)
        {
            int latchBit = value & 0x07;
            bool latchValue = (value & 0x08) != 0;
            ScreenMemoryWindow previousWindow = GetScreenMemoryWindow(addressableLatch);

            if (latchValue)
                addressableLatch |= (byte)(1 << latchBit);
            else
                addressableLatch &= unchecked((byte)~(1 << latchBit));

            if (latchBit == SoundWriteEnableLatchBit)
                UpdateSoundSlowDataBus();

            if (latchBit is ScreenAddressLatchLowBit or ScreenAddressLatchHighBit)
            {
                ScreenMemoryWindow currentWindow = GetScreenMemoryWindow(addressableLatch);
                if (currentWindow != previousWindow)
                    ScreenMemoryWindowChanged?.Invoke(currentWindow);
            }
        }

        private void UpdateSoundSlowDataBus()
        {
            sound.UpdateSlowDataBus(portA, (addressableLatch & (1 << SoundWriteEnableLatchBit)) == 0);
        }

        private static ScreenMemoryWindow GetScreenMemoryWindow(byte latch)
        {
            int code = ((latch >> ScreenAddressLatchLowBit) & 0x01)
                | (((latch >> ScreenAddressLatchHighBit) & 0x01) << 1);

            return code switch
            {
                0 => new ScreenMemoryWindow(0x4000, 0x4000, code, 8),
                1 => new ScreenMemoryWindow(0x6000, 0x2000, code, 4),
                2 => new ScreenMemoryWindow(0x3000, 0x5000, code, 10),
                _ => new ScreenMemoryWindow(0x5800, 0x2800, code, 5)
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

        private static ushort AddTimerOffset(ushort value, int offset)
        {
            return (ushort)Math.Clamp(value + offset, 0, 0xFFFF);
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

        private byte ReadPortAWithoutHandshake()
        {
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

    /// <summary>
    /// BBC screen memory wraps through one of four windows selected by IC32,
    /// letting the CRTC's 14-bit address counter scroll without moving RAM.
    /// </summary>
    public readonly record struct ScreenMemoryWindow(int Start, int Size, int HardwareScroll, int AddressSubtract);
}
