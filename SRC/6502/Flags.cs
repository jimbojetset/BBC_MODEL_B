// ============================================================================
// Project:     BBC
// File:        Flags.cs
// Description: 6502 processor status byte, including the reserved stack bit
//              behaviour that BBC MOS interrupt handlers rely on.
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

    public class Flags
    {
        // 6502 status byte on the stack: N V 1 B D I Z C.
        // Bit 5 is not a real flag, but interrupt frames preserve it as set.
        private const byte FLAG_C = 0x01;

        private const byte FLAG_Z = 0x02;
        private const byte FLAG_I = 0x04;
        private const byte FLAG_D = 0x08;
        private const byte FLAG_V = 0x40;
        private const byte FLAG_N = 0x80;

        private byte p;

        public bool C { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_C) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_C) : (byte)(p & ~FLAG_C); }
        public bool Z { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_Z) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_Z) : (byte)(p & ~FLAG_Z); }
        public bool I { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_I) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_I) : (byte)(p & ~FLAG_I); }
        public bool D { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_D) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_D) : (byte)(p & ~FLAG_D); }
        public bool V { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_V) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_V) : (byte)(p & ~FLAG_V); }
        public bool N { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_N) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_N) : (byte)(p & ~FLAG_N); }

        public Flags()
        {
        }

        public void Clear()
        {
            p = (byte)(p & 0x20);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFlagsFromByte(byte flags, byte bits = 0xFF)
        {
            p = (byte)((p & (byte)~bits) | (flags & bits));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetFlagsAsByte() => p;
    }
}
