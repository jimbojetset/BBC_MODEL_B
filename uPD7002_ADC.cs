// ============================================================================
// Project:     BBC_MODEL_B
// File:        uPD7002_ADC.cs
// Description: Minimal NEC uPD7002 4-channel analogue-to-digital converter
//              fitted to the BBC Micro Model B for joystick/paddle input.
//              Mapped at SHEILA &FEC0-&FEC3.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// ============================================================================

namespace BBC
{

    /// <summary>
    /// Emulates the uPD7002 ADC fitted to the BBC Model B at &amp;FEC0-&amp;FEC3.
    /// Provides 8/10/12-bit conversion of four analogue channels with a programmable
    /// completion delay and an EOC line that can drive System VIA CB1 (bit 4 of IFR).
    /// </summary>
    public sealed class uPD7002_ADC
    {
        private const ushort BaseAddress = 0xFEC0;
        private const ushort EndAddress = 0xFEC3;

        private const byte StatusBusyMask = 0x80;
        private const byte StatusNotEocMask = 0x40;
        private const byte StatusChannelMask = 0x03;
        private const byte StatusFlag2Mask = 0x10;
        private const byte StatusFlag3Mask = 0x20;
        private const byte StatusPrecisionMask = 0x08;

        private const int Conversion8BitCycles = 8000;
        private const int Conversion10BitCycles = 20000;

        private byte status = StatusNotEocMask;
        private ushort latchedResult;
        private int conversionCountdown;
        private byte selectedChannel;
        private bool tenBitMode;

        private readonly ushort[] channels = new ushort[4] { 0x8000, 0x8000, 0x8000, 0x8000 };

        /// <summary>Raised when an EOC transition occurs (true = conversion complete).</summary>
        public Action<bool>? EndOfConversionChanged;

        /// <summary>Checks whether address is true for the current emulator state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when address is true; otherwise, false.</returns>
        public static bool IsAddress(ushort address) => address >= BaseAddress && address <= EndAddress;

        /// <summary>Applies channel to the emulated hardware state.</summary>
        /// <param name="channel">The channel value.</param>
        /// <param name="value">The input value.</param>
        public void SetChannel(int channel, ushort value)
        {
            if ((uint)channel < 4) channels[channel] = value;
        }

        /// <summary>Resets the converter to its power-on state.</summary>
        public void Reset()
        {
            status = StatusNotEocMask;
            latchedResult = 0;
            conversionCountdown = 0;
            selectedChannel = 0;
            tenBitMode = false;
        }

        /// <summary>Advances the uPD7002 conversion timer and completes conversions when ready.</summary>
        /// <param name="cycles">The number of emulated CPU cycles.</param>
        public void Tick(int cycles)
        {
            if (conversionCountdown <= 0) return;
            conversionCountdown -= cycles;
            if (conversionCountdown <= 0)
            {
                conversionCountdown = 0;
                CompleteConversion();
            }
        }

        /// <summary>Reads  from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        public byte Read(ushort address)
        {
            switch (address & 0x03)
            {
                case 0:
                    return status;
                case 1:
                    return (byte)((latchedResult >> 8) & 0xFF);
                case 2:
                    return (byte)(latchedResult & 0xF0);
                default:
                    return 0;
            }
        }

        /// <summary>Writes  into emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The input value.</param>
        public void Write(ushort address, byte value)
        {
            if ((address & 0x03) != 0)
                return;

            selectedChannel = (byte)(value & 0x03);
            tenBitMode = (value & 0x08) != 0;

            status = (byte)(StatusBusyMask | StatusNotEocMask | selectedChannel);
            if (tenBitMode) status |= StatusPrecisionMask;
            if ((value & 0x04) != 0) status |= StatusFlag2Mask;
            if ((value & 0x08) != 0) status |= StatusFlag3Mask;

            EndOfConversionChanged?.Invoke(false);
            conversionCountdown = tenBitMode ? Conversion10BitCycles : Conversion8BitCycles;
        }

        /// <summary>Finishes a uPD7002 conversion and latches the selected analogue channel value.</summary>
        private void CompleteConversion()
        {
            ushort sample = channels[selectedChannel & 0x03];

            latchedResult = sample;

            byte newStatus = (byte)(selectedChannel & StatusChannelMask);
            if (tenBitMode) newStatus |= StatusPrecisionMask;
            newStatus |= (byte)(status & (StatusFlag2Mask | StatusFlag3Mask));
            status = newStatus;

            EndOfConversionChanged?.Invoke(true);
        }
    }
}
