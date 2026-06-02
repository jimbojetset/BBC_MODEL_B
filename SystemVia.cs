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
        private readonly Sound sound;
        private readonly byte[] registers = new byte[16];
        private byte addressableLatch = 0xFF;
        private byte portA;

        /// <summary>Initializes a new system VIA shim.</summary>
        /// <param name="sound">The sound generator connected to the VIA slow bus.</param>
        public SystemVia(Sound sound)
        {
            this.sound = sound ?? throw new ArgumentNullException(nameof(sound));
        }

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
            addressableLatch = 0xFF;
            portA = 0;
        }

        /// <summary>Reads a system VIA register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
        public byte Read(ushort address)
        {
            int register = address & 0x0F;

            return register switch
            {
                0x0 => registers[0x0], // ORB/IRB
                0x1 => portA,          // ORA/IRA
                0xD => 0x00,           // IFR: no VIA interrupts yet.
                0xE => 0x00,           // IER.
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
                case 0xF:
                    WritePortB(value);
                    break;

                case 0x1:
                    portA = value;
                    break;
            }
        }

        private void WritePortB(byte value)
        {
            int latchBit = value & 0x07;
            bool latchValue = (value & 0x08) != 0;
            bool previousSoundWriteEnable = (addressableLatch & (1 << SoundWriteEnableLatchBit)) != 0;

            if (latchValue)
                addressableLatch |= (byte)(1 << latchBit);
            else
                addressableLatch &= unchecked((byte)~(1 << latchBit));

            bool currentSoundWriteEnable = (addressableLatch & (1 << SoundWriteEnableLatchBit)) != 0;

            if (latchBit == SoundWriteEnableLatchBit && previousSoundWriteEnable && !currentSoundWriteEnable)
                sound.WriteData(portA);
        }
    }
}
