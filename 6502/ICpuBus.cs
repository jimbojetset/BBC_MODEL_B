// ============================================================================
// Project:     BBC
// File:        ICpuBus.cs
// Description: Byte-level CPU bus abstraction used by the 6502 core to access
//              RAM, ROM, and memory-mapped BBC hardware.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC.CPU
{

    /// <summary>
    /// Provides byte-level CPU bus access for 6502-compatible processors.
    /// </summary>
    public interface ICpuBus
    {

        /// <summary>Reads one byte from the CPU-visible bus implementation.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value read from the bus.</returns>
        byte ReadByte(ulong addr);

        /// <summary>Writes one byte to the CPU-visible bus implementation.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value to write to the bus.</param>
        void WriteByte(ulong addr, byte value);
    }
}
