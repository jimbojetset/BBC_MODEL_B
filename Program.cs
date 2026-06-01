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
using System.Diagnostics;
using System.Text;

namespace BBC
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            using Emulator emulator = new Emulator();
            int headlessMilliseconds = ParseHeadlessMilliseconds(args);
            emulator.Initialise(createDisplay: headlessMilliseconds == 0);

            Console.WriteLine("BBC Model B emulator initialised.");
            Console.WriteLine($"OS ROM:     {emulator.OsRomPath} -> ${Emulator.OsRomStart:X4}-${Emulator.OsRomEnd:X4}");
            Console.WriteLine($"BASIC ROM:  {emulator.BasicRomPath} -> ${Emulator.SidewaysRomStart:X4}-${Emulator.SidewaysRomEnd:X4}");
            Console.WriteLine($"Reset PC:   ${emulator.Cpu.registers.PC:X4}");

            if (headlessMilliseconds > 0)
                emulator.RunHeadless(TimeSpan.FromMilliseconds(headlessMilliseconds));
            else
                emulator.Run();
        }

        private static int ParseHeadlessMilliseconds(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--headless-ms", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int milliseconds) || milliseconds <= 0)
                    throw new ArgumentException("--headless-ms requires a positive millisecond value.");

                return milliseconds;
            }

            return 0;
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
        public const ushort Mode7ScreenStart = 0x7C00;
        public const int Mode7Columns = 40;
        public const int Mode7Rows = 25;

        private const string RomDirectory = "ROMS";
        private const string BasicRomMarker = "BASIC\0(C)1982 Acorn";
        private const string OsRomMarker = "BBC Computer";
        private const int TargetFramesPerSecond = 50;
        private const int FrameMilliseconds = 1000 / TargetFramesPerSecond;

        private bool initialised;
        private Thread? cpuThread;
        private Exception? cpuException;

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

        /// <summary>Runs the CPU and SDL display loop until the window is closed or the CPU faults.</summary>
        public void Run()
        {
            if (!initialised)
                Initialise(createDisplay: true);

            Display ??= new Display();

            StartCpu();

            Stopwatch frameTimer = Stopwatch.StartNew();
            while (Display.PumpEvents())
            {
                if (cpuException is not null)
                    throw new InvalidOperationException("CPU execution failed.", cpuException);

                RenderMode7TextScreen(Display);
                Display.Present();

                int remaining = FrameMilliseconds - (int)frameTimer.ElapsedMilliseconds;
                if (remaining > 0)
                    Thread.Sleep(remaining);

                frameTimer.Restart();
            }

            StopCpu();
        }

        /// <summary>Runs the CPU without creating a display, primarily for smoke tests.</summary>
        /// <param name="duration">The amount of wall-clock time to run.</param>
        public void RunHeadless(TimeSpan duration)
        {
            if (!initialised)
                Initialise();

            StartCpu();
            Thread.Sleep(duration);
            StopCpu();

            if (cpuException is not null)
                throw new InvalidOperationException("CPU execution failed.", cpuException);
        }

        /// <summary>Releases emulator-owned resources.</summary>
        public void Dispose()
        {
            StopCpu();
            Display?.Dispose();
        }

        private void RunCpu()
        {
            try
            {
                Cpu.Run();
            }
            catch (Exception ex)
            {
                cpuException = ex;
            }
        }

        private void StartCpu()
        {
            if (cpuThread is not null && cpuThread.IsAlive)
                return;

            cpuException = null;
            cpuThread = new Thread(RunCpu)
            {
                IsBackground = true,
                Name = "BBC 6502"
            };
            cpuThread.Start();
        }

        private void StopCpu()
        {
            Cpu.Stop();

            if (cpuThread is not null && cpuThread.IsAlive)
                cpuThread.Join(TimeSpan.FromSeconds(2));

            cpuThread = null;
        }

        private void RenderMode7TextScreen(Display display)
        {
            const uint background = 0xFF000000;
            const uint foreground = 0xFFFFFFFF;
            const int glyphWidth = 8;
            const int glyphHeight = 8;
            const int cellWidth = Display.DefaultWidth / Mode7Columns;
            const int cellHeight = Display.DefaultHeight / Mode7Rows;
            const int xScale = 2;
            const int yScale = 2;
            const int glyphXOffset = 0;
            const int glyphYOffset = 2;

            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, background);

            for (int row = 0; row < Mode7Rows; row++)
            {
                int cellY = row * cellHeight;

                for (int column = 0; column < Mode7Columns; column++)
                {
                    byte character = Memory.Memory[Mode7ScreenStart + (row * Mode7Columns) + column];

                    if (character < 32 || character > 127)
                        character = 32;

                    int glyphAddress = OsRomStart + ((character - 32) * glyphHeight);
                    int cellX = column * cellWidth;

                    for (int glyphY = 0; glyphY < glyphHeight; glyphY++)
                    {
                        byte bits = Memory.Memory[glyphAddress + glyphY];

                        for (int glyphX = 0; glyphX < glyphWidth; glyphX++)
                        {
                            if ((bits & (0x80 >> glyphX)) == 0)
                                continue;

                            int pixelX = cellX + glyphXOffset + (glyphX * xScale);
                            int pixelY = cellY + glyphYOffset + (glyphY * yScale);

                            for (int yy = 0; yy < yScale; yy++)
                            {
                                int y = pixelY + yy;
                                if ((uint)y >= (uint)display.Height)
                                    continue;

                                int offset = y * display.Width;
                                for (int xx = 0; xx < xScale; xx++)
                                {
                                    int x = pixelX + xx;
                                    if ((uint)x < (uint)display.Width)
                                        pixels[offset + x] = foreground;
                                }
                            }
                        }
                    }
                }
            }
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
