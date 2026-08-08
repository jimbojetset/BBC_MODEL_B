// ============================================================================
// Project:     BBC
// File:        Registers.cs
// Description: 6502 register set used by the BBC CPU core.
// Author:      James Booth
// Created:     2025
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC.CPU
{

    public class Registers
    {

        public ulong PC { get; set; }

        public byte S { get; set; }

        public byte P
        { 
            get { return Flags.GetFlagsAsByte(); } 
            set { Flags.SetFlagsFromByte(value); } 
        }

        public byte A { get; set; }

        public byte X { get; set; }

        public byte Y { get; set; }

        public Flags Flags = new Flags();

        public Registers()
        {
            Clear();
        }

        public void Clear()
        {
            PC = S = A = X = Y = 0;
            Flags.Clear();
            Flags.I = true;
        }
    }
}
