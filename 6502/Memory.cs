// ============================================================================
// Project:     BBC
// File:        FlatMemoryBus.cs
// Description: 64 KiB 6502 bus with hooks for BBC memory-mapped hardware pages.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.CompilerServices;

namespace BBC.CPU
{

    public class FlatMemoryBus
    {

        public byte[] Memory { get; }

        /// <summary>BBC I/O pages can consume writes instead of storing bytes in RAM.</summary>
        public Func<ulong, byte, bool>? OnWrite;

        /// <summary>BBC I/O reads may return live device state rather than the backing RAM byte.</summary>
        public Func<ulong, byte, byte>? OnRead;

        public FlatMemoryBus(int size = 0x10000)
        {
            Memory = new byte[size];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ulong addr)
        {
            addr %= (ulong)Memory.Length;
            byte value = Memory[addr];
            return OnRead is null ? value : OnRead(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            addr %= (ulong)Memory.Length;
            if (OnWrite is not null && OnWrite(addr, value))
                return;

            Memory[addr] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadWord(ulong addr)
        {
            return (ulong)(ReadByte(addr) | (ReadByte((addr + 1) & 0xFFFF) << 8));
        }

        public void Load(ulong startAddr, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
                Memory[(startAddr + (ulong)i) % (ulong)Memory.Length] = data[i];
        }
    }
}
