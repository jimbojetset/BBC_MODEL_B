// ============================================================================
// Project:     BBC
// File:        FlatMemoryBus.cs
// Description: Generic flat 6502 memory bus with optional read/write hooks.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.CompilerServices;

namespace BBC.CPU
{
    /// <summary>
    /// Provides a generic 64 KiB 6502 bus with no 6510 processor port or C64 banking behavior.
    /// </summary>
    public class FlatMemoryBus : ICpuBus
    {
        /// <summary>Gets the backing address space.</summary>
        public byte[] Memory { get; }

        /// <summary>Optional whole-address-space write hook. Return true to suppress the backing RAM write.</summary>
        public Func<ulong, byte, bool>? OnWrite;

        /// <summary>Optional whole-address-space read hook. Receives the backing RAM value and returns the CPU-visible value.</summary>
        public Func<ulong, byte, byte>? OnRead;

        /// <summary>Initializes a new FlatMemoryBus instance.</summary>
        /// <param name="size">The bus size in bytes. Defaults to the 6502 64 KiB address space.</param>
        public FlatMemoryBus(int size = 0x10000)
        {
            Memory = new byte[size];
        }

        /// <summary>Reads a byte from the CPU-visible address space.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value read from the bus.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ulong addr)
        {
            addr %= (ulong)Memory.Length;
            byte value = Memory[addr];
            return OnRead is null ? value : OnRead(addr, value);
        }

        /// <summary>Writes a byte to the CPU-visible address space.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value to write to the bus.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            addr %= (ulong)Memory.Length;
            if (OnWrite is not null && OnWrite(addr, value))
                return;

            Memory[addr] = value;
        }

        /// <summary>Reads a little-endian 16-bit word from the bus.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadWord(ulong addr)
        {
            return (ulong)(ReadByte(addr) | (ReadByte((addr + 1) & 0xFFFF) << 8));
        }

        /// <summary>Copies data into the backing memory.</summary>
        /// <param name="startAddr">The first emulated address to fill.</param>
        /// <param name="data">The bytes to copy into memory.</param>
        public void Load(ulong startAddr, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
                Memory[(startAddr + (ulong)i) % (ulong)Memory.Length] = data[i];
        }
    }
}
