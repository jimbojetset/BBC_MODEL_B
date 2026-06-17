// ============================================================================
// Project:     BBC_MODEL_B
// File:        Adc7002.cs
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
    public sealed class Adc7002
    {
        private const ushort BaseAddress = 0xFEC0;
        private const ushort EndAddress = 0xFEC3;

        // Status register bits (read at &FEC0).
        private const byte StatusBusyMask = 0x80;
        private const byte StatusNotEocMask = 0x40; // 0 = conversion complete
        private const byte StatusChannelMask = 0x03;
        private const byte StatusFlag2Mask = 0x10;  // tracks bit-2 of last latched data write
        private const byte StatusFlag3Mask = 0x20;  // tracks bit-3 of last latched data write
        private const byte StatusPrecisionMask = 0x08; // 0 = 10-bit, 1 = 12-bit (per WD bit 3)

        // Timing: 10ms conversion in 10-bit mode, 4ms in 8-bit mode at 1MHz.
        // Approximate to CPU cycles (2 MHz host clock).
        private const int Conversion8BitCycles = 8000;     // ~4ms @ 2MHz
        private const int Conversion10BitCycles = 20000;   // ~10ms @ 2MHz

        private byte status = StatusNotEocMask; // idle, no conversion in progress
        private ushort latchedResult; // 12-bit latched value; high byte at &FEC1, low byte at &FEC2
        private int conversionCountdown;
        private byte selectedChannel;
        private bool tenBitMode;

        // Channel inputs are 16-bit unsigned values (0x0000..0xFFFF) where 0x8000 is centre,
        // matching the convention used by ADVAL.
        private readonly ushort[] channels = new ushort[4] { 0x8000, 0x8000, 0x8000, 0x8000 };

        /// <summary>Raised when an EOC transition occurs (true = conversion complete).</summary>
        public Action<bool>? EndOfConversionChanged;

        /// <summary>Returns true when the supplied SHEILA address targets the ADC.</summary>
        public static bool IsAddress(ushort address) => address >= BaseAddress && address <= EndAddress;

        /// <summary>Sets the latest sample for a channel (0..3). Value uses the 16-bit ADVAL convention (0x8000 centre).</summary>
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

        /// <summary>Advances the conversion timer by the given number of CPU cycles.</summary>
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

        /// <summary>Reads a byte from a SHEILA address inside the ADC range.</summary>
        public byte Read(ushort address)
        {
            switch (address & 0x03)
            {
                case 0:
                    return status;
                case 1:
                    return (byte)((latchedResult >> 8) & 0xFF);
                case 2:
                    return (byte)(latchedResult & 0xF0); // low nibble of 12-bit field is zero
                default:
                    return 0;
            }
        }

        /// <summary>Writes a byte to a SHEILA address inside the ADC range.</summary>
        public void Write(ushort address, byte value)
        {
            if ((address & 0x03) != 0)
                return;

            // Data latch (write at &FEC0): bit 0/1 = channel, bit 3 = 10/8 bit, bits 2/3 also tracked in status.
            selectedChannel = (byte)(value & 0x03);
            tenBitMode = (value & 0x08) != 0;

            // Begin a new conversion: BUSY=1, NOT-EOC=1.
            status = (byte)(StatusBusyMask | StatusNotEocMask | selectedChannel);
            if (tenBitMode) status |= StatusPrecisionMask;
            if ((value & 0x04) != 0) status |= StatusFlag2Mask;
            if ((value & 0x08) != 0) status |= StatusFlag3Mask;

            EndOfConversionChanged?.Invoke(false);
            conversionCountdown = tenBitMode ? Conversion10BitCycles : Conversion8BitCycles;
        }

        private void CompleteConversion()
        {
            ushort sample = channels[selectedChannel & 0x03];

            // µPD7002 produces a result where the MSB is the sign-corrected value.
            // ADVAL convention has 0x8000 as centre; the chip itself outputs 0x0000..0xFFFF
            // such that the MSB byte at &FEC1 is the most-significant 8 bits. In 8-bit mode
            // only the top 8 bits are valid; in 10-bit mode the low two bits of &FEC2 are valid.
            latchedResult = sample;

            // Conversion complete: clear BUSY and NOT-EOC, latch channel into status.
            byte newStatus = (byte)(selectedChannel & StatusChannelMask);
            if (tenBitMode) newStatus |= StatusPrecisionMask;
            // Carry the previous flag bits (bits 4,5) which reflect the last data-latch write.
            newStatus |= (byte)(status & (StatusFlag2Mask | StatusFlag3Mask));
            status = newStatus;

            EndOfConversionChanged?.Invoke(true);
        }
    }
}
