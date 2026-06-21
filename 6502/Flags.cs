// ============================================================================
// Project:     BBC
// File:        Flags.cs
// Description: 6502 processor status register model and helpers for flag
//              packing, unpacking, and instruction flag updates.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.CompilerServices;

namespace BBC.CPU
{

    /// <summary>
    /// Stores the 6502 processor status register and exposes named flag accessors used by instruction implementations.
    /// </summary>
    public class Flags
    {
        /// Bit layout of the 6502 status register:
        /// 7 6 5 4 3 2 1 0
        /// N V T B D I Z C   (T = unused-by-CPU "Test" flag in bit 5)
        private const byte FLAG_C = 0x01;

        private const byte FLAG_Z = 0x02;
        private const byte FLAG_I = 0x04;
        private const byte FLAG_D = 0x08;
        private const byte FLAG_V = 0x40;
        private const byte FLAG_N = 0x80;

        private byte p;

        public bool C { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_C) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_C) : (byte)(p & ~FLAG_C); } /// Carry
        public bool Z { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_Z) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_Z) : (byte)(p & ~FLAG_Z); } /// Zero
        public bool I { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_I) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_I) : (byte)(p & ~FLAG_I); } /// Interrupt Disable
        public bool D { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_D) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_D) : (byte)(p & ~FLAG_D); } /// Decimal
        public bool V { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_V) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_V) : (byte)(p & ~FLAG_V); } /// Overflow
        public bool N { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_N) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_N) : (byte)(p & ~FLAG_N); } /// Negative

        /// <summary>Initializes a new Flags instance.</summary>
        public Flags()
        {
        }

        /// <summary>Clears this instance to its reset state.</summary>
        public void Clear()
        {
            p = (byte)(p & 0x20);
        }

        /// <summary>Updates selected processor status flags from a packed status byte.</summary>
        /// <param name="flags">The packed processor status bits to apply.</param>
        /// <param name="bits">The status bits that are allowed to change.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFlagsFromByte(byte flags, byte bits = 0xFF)
        {
            p = (byte)((p & (byte)~bits) | (flags & bits));
        }

        /// <summary>Computes flags as byte from the current emulated hardware state.</summary>
        /// <returns>The computed value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetFlagsAsByte() => p;
    }
}
