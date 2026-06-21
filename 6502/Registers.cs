// ============================================================================
// Project:     BBC
// File:        Registers.cs
// Description: 6502 register container exposing program counter, stack pointer,
//              accumulator, index registers, and status flags.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC.CPU
{

    /// <summary>
    /// Holds the CPU register set and maps the processor status byte through the structured flag model.
    /// </summary>
    public class Registers
    {

        /// <summary>Gets or sets the CPU program counter.</summary>
        public ulong PC { get; set; } /// Program Counter

        /// <summary>Gets or sets the CPU stack pointer.</summary>
        public byte S { get; set; } /// Stack Pointer

        public byte P
        { get { return Flags.GetFlagsAsByte(); } set { Flags.SetFlagsFromByte(value); } }

        /// <summary>Gets or sets the CPU accumulator.</summary>
        public byte A { get; set; } /// Accumulator

        /// <summary>Gets or sets the CPU X index register.</summary>
        public byte X { get; set; } /// X Index Register

        /// <summary>Gets or sets the CPU Y index register.</summary>
        public byte Y { get; set; } /// Y Index Register

        public Flags Flags = new Flags();

        /// <summary>Initializes a new Registers instance.</summary>
        public Registers()
        {
            Clear();
        }

        /// <summary>Clears this instance to its reset state.</summary>
        public void Clear()
        {
            PC = S = A = X = Y = 0;
            Flags.Clear();
            Flags.I = true;
        }
    }
}
