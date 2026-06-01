// ============================================================================
// Project:     BBC
// File:        Program.cs
// Description: Core BBC Model B emulator host.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using BBC.CPU;
using System.Text;

namespace BBC
{
    internal static class Program
    {
        private static void Main()
        {
            using Emulator emulator = new Emulator();
            emulator.Initialise();

            Console.WriteLine("BBC Model B emulator initialised.");
            Console.WriteLine($"OS ROM:     {emulator.OsRomPath} -> ${Emulator.OsRomStart:X4}-${Emulator.OsRomEnd:X4}");
            Console.WriteLine($"BASIC ROM:  {emulator.BasicRomPath} -> ${Emulator.SidewaysRomStart:X4}-${Emulator.SidewaysRomEnd:X4}");
            Console.WriteLine($"Reset PC:   ${emulator.Cpu.registers.PC:X4}");
        }
    }

    /// <summary>
    /// Coordinates the main BBC Model B emulator components.
    /// </summary>
    public sealed class Emulator : IDisposable
    {
        public const ushort RamStart = 0x0000;
        public const ushort RamEnd = 0x7FFF;
        public const ushort SidewaysRomStart = 0x8000;
        public const ushort SidewaysRomEnd = 0xBFFF;
        public const ushort OsRomStart = 0xC000;
        public const ushort OsRomEnd = 0xFFFF;
        public const int RomSize = 16 * 1024;
        public const int CpuClockHz = 2_000_000;

        private const string RomDirectory = "ROMS";
        private const string BasicRomMarker = "BASIC\0(C)1982 Acorn";
        private const string OsRomMarker = "BBC Computer";

        private bool initialised;

        /// <summary>Gets the 64 KiB CPU-visible memory bus.</summary>
        public FlatMemoryBus Memory { get; } = new FlatMemoryBus();

        /// <summary>Gets the 6502 CPU.</summary>
        public CPU_6502 Cpu { get; }

        /// <summary>Gets the SDL display surface.</summary>
        public Display? Display { get; private set; }

        /// <summary>Gets the loaded OS ROM path.</summary>
        public string OsRomPath { get; private set; } = string.Empty;

        /// <summary>Gets the loaded BASIC ROM path.</summary>
        public string BasicRomPath { get; private set; } = string.Empty;

        /// <summary>Initializes a new emulator coordinator.</summary>
        public Emulator()
        {
            Cpu = new CPU_6502(Memory, CpuClockHz);
        }

        /// <summary>Initializes memory, display, ROMs, and CPU reset state.</summary>
        public void Initialise(bool createDisplay = false)
        {
            if (initialised)
                return;

            Array.Clear(Memory.Memory);
            LoadSystemRoms();
            InstallMemoryMapHooks();

            if (createDisplay)
                Display = new Display();

            Cpu.ResetNow();
            initialised = true;
        }

        /// <summary>Releases emulator-owned resources.</summary>
        public void Dispose()
        {
            Display?.Dispose();
        }

        private void LoadSystemRoms()
        {
            string romRoot = Path.Combine(AppContext.BaseDirectory, RomDirectory);
            if (!Directory.Exists(romRoot))
                romRoot = Path.Combine(Environment.CurrentDirectory, RomDirectory);

            if (!Directory.Exists(romRoot))
                throw new DirectoryNotFoundException($"ROM directory not found: {romRoot}");

            IReadOnlyList<string> romPaths = Directory
                .EnumerateFiles(romRoot)
                .Where(path => new FileInfo(path).Length == RomSize)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (romPaths.Count == 0)
                throw new InvalidOperationException($"No {RomSize} byte ROM images were found in {romRoot}.");

            OsRomPath = FindRomByMarker(romPaths, OsRomMarker);
            BasicRomPath = FindRomByMarker(romPaths, BasicRomMarker);

            Memory.Load(OsRomStart, File.ReadAllBytes(OsRomPath));
            Memory.Load(SidewaysRomStart, File.ReadAllBytes(BasicRomPath));
        }

        private static string FindRomByMarker(IReadOnlyList<string> romPaths, string marker)
        {
            foreach (string path in romPaths)
            {
                byte[] rom = File.ReadAllBytes(path);
                if (ContainsAscii(rom, marker))
                    return path;
            }

            throw new FileNotFoundException($"Could not find a {RomSize} byte ROM containing marker '{marker}'.");
        }

        private static bool ContainsAscii(ReadOnlySpan<byte> data, string marker)
        {
            ReadOnlySpan<byte> needle = Encoding.ASCII.GetBytes(marker);
            return data.IndexOf(needle) >= 0;
        }

        private void InstallMemoryMapHooks()
        {
            Memory.OnWrite = (address, _) =>
            {
                ushort addr = (ushort)(address & 0xFFFF);

                if (addr >= SidewaysRomStart)
                    return true;

                return false;
            };
        }
    }
}
