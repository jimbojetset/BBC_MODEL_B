// ============================================================================
// Project:     BBC_MODEL_B
// File:        uPD7002_ADC.cs
// Description: NEC uPD7002 analogue input converter at SHEILA &FEC0-&FEC3,
//              including conversion delay and the EOC line into system 6522 VIA CB1.
// Author:      James Booth
// Created:     2025
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// ============================================================================

namespace BBC
{

    /// <summary>
    /// The Model B's uPD7002 converts four analogue channels for joysticks and
    /// paddles. Its EOC output is visible through system 6522 VIA CB1.
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

        /// <summary>Raised when the ADC EOC line changes state before it is passed to system 6522 VIA CB1.</summary>
        public Action<bool>? EndOfConversionChanged;

        public static bool IsAddress(ushort address) => address >= BaseAddress && address <= EndAddress;

        public bool ConversionActive => conversionCountdown > 0;

        public void SetChannel(int channel, ushort value)
        {
            if ((uint)channel < 4) channels[channel] = value;
        }

        public void Reset()
        {
            status = StatusNotEocMask;
            latchedResult = 0;
            conversionCountdown = 0;
            selectedChannel = 0;
            tenBitMode = false;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(status);
            writer.Write(latchedResult);
            writer.Write(conversionCountdown);
            writer.Write(selectedChannel);
            writer.Write(tenBitMode);
            writer.Write(channels.Length);
            foreach (ushort channel in channels)
                writer.Write(channel);
        }

        public void LoadState(BinaryReader reader)
        {
            status = reader.ReadByte();
            latchedResult = reader.ReadUInt16();
            conversionCountdown = reader.ReadInt32();
            selectedChannel = reader.ReadByte();
            tenBitMode = reader.ReadBoolean();

            int channelCount = reader.ReadInt32();
            if (channelCount != channels.Length)
                throw new InvalidDataException("Save state has an incompatible ADC channel block.");

            for (int i = 0; i < channels.Length; i++)
                channels[i] = reader.ReadUInt16();

            EndOfConversionChanged?.Invoke((status & StatusNotEocMask) == 0);
        }

        /// <summary>ADC results are not instant; software can observe the EOC delay after starting a conversion.</summary>
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
