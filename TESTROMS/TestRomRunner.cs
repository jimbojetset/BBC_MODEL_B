// ============================================================================
// Project:     BBC
// File:        TestRomRunner.cs
// Description: Headless harness for the beeb_test_os ROM images.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace BBC
{
    /// <summary>
    /// Runs the bundled beeb_test_os BBC Model B ROMs against the emulator.
    /// </summary>
    internal static class TestRomRunner
    {
        private const int CyclesPerSecond = Emulator.CpuClockHz;
        private const int DefaultDurationSeconds = 15;
        private const int DefaultStartupDelayMilliseconds = 1200;
        private const int KeyPressCycles = CyclesPerSecond / 20;
        private const int KeyGapCycles = CyclesPerSecond / 10;
        private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly IReadOnlyDictionary<char, byte> BbcKeys = new Dictionary<char, byte>
        {
            ['0'] = 0x27,
            ['1'] = 0x30,
            ['2'] = 0x31,
            ['3'] = 0x11,
            ['4'] = 0x12,
            ['5'] = 0x13,
            ['6'] = 0x34,
            ['7'] = 0x24,
            ['8'] = 0x15,
            ['9'] = 0x26,
            ['A'] = 0x41,
            ['E'] = 0x22,
            ['F'] = 0x43,
            ['L'] = 0x56,
            ['M'] = 0x65,
            ['N'] = 0x55,
            ['O'] = 0x36,
            ['S'] = 0x51,
            ['U'] = 0x35,
            ['V'] = 0x63,
            ['X'] = 0x42,
            ['Y'] = 0x44,
        };

        public static bool TryRunFromArgs(string[] args, out int exitCode)
        {
            exitCode = 0;
            int commandIndex = Array.FindIndex(args, IsTestRomCommand);
            if (commandIndex < 0)
                return false;

            try
            {
                TestRomOptions options = Parse(args[(commandIndex + 1)..]);
                exitCode = Run(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Test ROM runner failed: {ex.Message}");
                exitCode = 1;
            }

            return true;
        }

        private static bool IsTestRomCommand(string value)
        {
            return string.Equals(value, "--test-rom", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "test-rom", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "--test-roms", StringComparison.OrdinalIgnoreCase);
        }

        private static int Run(TestRomOptions options)
        {
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            if (options.ListOnly)
            {
                foreach (string path in DiscoverModelBRoms(options.TestRomRoot))
                    Console.WriteLine(path);
                return 0;
            }

            IReadOnlyList<TestRomCase> cases = BuildCases(options);
            int failures = 0;

            foreach (TestRomCase testCase in cases)
            {
                TestRomResult result = RunCase(testCase, options);
                PrintResult(result);
                if (!result.Success)
                    failures++;
            }

            return failures == 0 ? 0 : 1;
        }

        private static TestRomResult RunCase(TestRomCase testCase, TestRomOptions options)
        {
            using Emulator emulator = new Emulator();
            emulator.Initialise(createDisplay: false);
            emulator.Cpu.PacingEnabled = false;

            LoadTestOsRom(emulator, testCase.RomPath);
            emulator.Cpu.ResetNow();

            SystemVia systemVia = GetSystemVia(emulator);
            long totalCycles = 0;
            long startupCycles = MillisecondsToCycles(options.StartupDelayMilliseconds);
            long deadlineCycles = startupCycles + (long)options.DurationSeconds * CyclesPerSecond;
            string? failure = null;

            bool started = false;
            Queue<KeyEvent> keyEvents = CreateKeyEvents(testCase.Keys, startupCycles);

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (totalCycles < deadlineCycles)
            {
                while (keyEvents.Count > 0 && keyEvents.Peek().Cycle <= totalCycles)
                {
                    KeyEvent keyEvent = keyEvents.Dequeue();
                    systemVia.SetKeyState(keyEvent.InternalKey, keyEvent.Pressed);
                    UpdateCpuIrqLine(emulator);
                    if (keyEvent.Pressed)
                        started = true;
                }

                ushort pc = (ushort)(emulator.Cpu.registers.PC & 0xFFFF);
                int cycles = emulator.Cpu.StepInstruction();
                if (cycles <= 0)
                {
                    failure = $"CPU stopped making progress at ${pc:X4}.";
                    break;
                }

                totalCycles += cycles;
            }

            stopwatch.Stop();

            int nonBlankMode7Cells = emulator.Video.CountMode7NonBlankCells();
            bool success = failure is null && started && (!testCase.RequireVisibleOutput || nonBlankMode7Cells > 0);
            if (failure is null && !started)
                failure = "No test key was injected.";
            else if (failure is null && testCase.RequireVisibleOutput && nonBlankMode7Cells == 0)
                failure = "Mode 7 screen remained blank.";

            return new TestRomResult(
                testCase.Name,
                testCase.RomPath,
                testCase.Keys,
                success,
                failure,
                emulator.Cpu.registers.PC,
                emulator.Cpu.TotalCycles,
                emulator.Video.CurrentMode,
                nonBlankMode7Cells,
                stopwatch.Elapsed);
        }

        private static IReadOnlyList<TestRomCase> BuildCases(TestRomOptions options)
        {
            string romPath = options.RomPath ?? GetDefaultRomPath(options.TestRomRoot, options.SizeKb);

            if (options.RunAll)
            {
                return
                [
                    new TestRomCase("BBC B standard memory test", romPath, "M", true),
                    new TestRomCase("BBC B lower RAM memory test", romPath, "OYL00", false),
                    new TestRomCase("BBC B upper RAM memory test", romPath, "OYU00", true),
                ];
            }

            if (options.OptionsTest)
            {
                string keys = $"O{options.Refresh}{options.Region}{options.Mask}";
                bool requireVisibleOutput = options.Region is 'A' or 'U';
                return [new TestRomCase($"BBC B options memory test ({keys})", romPath, keys, requireVisibleOutput)];
            }

            return [new TestRomCase("BBC B standard memory test", romPath, "M", true)];
        }

        private static Queue<KeyEvent> CreateKeyEvents(string keys, long firstCycle)
        {
            Queue<KeyEvent> events = new Queue<KeyEvent>();
            long cycle = firstCycle;

            foreach (char key in keys.ToUpperInvariant())
            {
                if (!BbcKeys.TryGetValue(key, out byte internalKey))
                    throw new ArgumentException($"Unsupported test key '{key}'.");

                events.Enqueue(new KeyEvent(cycle, internalKey, true));
                events.Enqueue(new KeyEvent(cycle + KeyPressCycles, internalKey, false));
                cycle += KeyPressCycles + KeyGapCycles;
            }

            return events;
        }

        private static void LoadTestOsRom(Emulator emulator, string romPath)
        {
            if (!File.Exists(romPath))
                throw new FileNotFoundException($"Test ROM not found: {romPath}", romPath);

            byte[] rom = File.ReadAllBytes(romPath);
            if (rom.Length < Emulator.RomSize || rom.Length % Emulator.RomSize != 0)
                throw new InvalidOperationException($"Test ROM must be a whole number of 16 KB pages: {romPath}");

            // Larger PROM images contain repeated 16 KB OS images for physical socket wiring.
            // The CPU-visible BBC B OS window is always the final 16 KB at $C000-$FFFF.
            ReadOnlySpan<byte> osWindow = rom.AsSpan(rom.Length - Emulator.RomSize, Emulator.RomSize);
            emulator.Memory.Load(Emulator.OsRomStart, osWindow);
        }

        private static string GetDefaultRomPath(string testRomRoot, int sizeKb)
        {
            string path = Path.Combine(testRomRoot, sizeKb.ToString(CultureInfo.InvariantCulture), "beeb_test_os.b.bin");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Default BBC B test ROM not found: {path}", path);

            return path;
        }

        private static IEnumerable<string> DiscoverModelBRoms(string testRomRoot)
        {
            if (!Directory.Exists(testRomRoot))
                yield break;

            foreach (string path in Directory.EnumerateFiles(testRomRoot, "beeb_test_os.b.bin", SearchOption.AllDirectories).Order())
                yield return path;
        }

        private static TestRomOptions Parse(string[] args)
        {
            TestRomOptions options = new TestRomOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
                {
                    options.ShowHelp = true;
                    continue;
                }

                if (string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase))
                {
                    options.ListOnly = true;
                    continue;
                }

                if (string.Equals(arg, "--all", StringComparison.OrdinalIgnoreCase))
                {
                    options.RunAll = true;
                    continue;
                }

                if (string.Equals(arg, "--options", StringComparison.OrdinalIgnoreCase))
                {
                    options.OptionsTest = true;
                    continue;
                }

                if (string.Equals(arg, "--rom", StringComparison.OrdinalIgnoreCase))
                {
                    options.RomPath = RequireValue(args, ref i, arg);
                    continue;
                }

                if (string.Equals(arg, "--root", StringComparison.OrdinalIgnoreCase))
                {
                    options.TestRomRoot = RequireValue(args, ref i, arg);
                    continue;
                }

                if (string.Equals(arg, "--seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(RequireValue(args, ref i, arg), out int seconds) || seconds <= 0)
                        throw new ArgumentException("--seconds requires a positive integer.");

                    options.DurationSeconds = seconds;
                    continue;
                }

                if (string.Equals(arg, "--startup-ms", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(RequireValue(args, ref i, arg), out int startupMs) || startupMs < 0)
                        throw new ArgumentException("--startup-ms requires a non-negative integer.");

                    options.StartupDelayMilliseconds = startupMs;
                    continue;
                }

                if (string.Equals(arg, "--size", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(RequireValue(args, ref i, arg), out int sizeKb) || sizeKb is not (16 or 32 or 64))
                        throw new ArgumentException("--size must be 16, 32, or 64 for the BBC B test ROMs.");

                    options.SizeKb = sizeKb;
                    continue;
                }

                if (string.Equals(arg, "--refresh", StringComparison.OrdinalIgnoreCase))
                {
                    options.Refresh = ParseSingleKey(RequireValue(args, ref i, arg), "refresh", ['Y', 'L', 'N']);
                    options.OptionsTest = true;
                    continue;
                }

                if (string.Equals(arg, "--region", StringComparison.OrdinalIgnoreCase))
                {
                    options.Region = ParseSingleKey(RequireValue(args, ref i, arg), "region", ['A', 'L', 'U']);
                    options.OptionsTest = true;
                    continue;
                }

                if (string.Equals(arg, "--mask", StringComparison.OrdinalIgnoreCase))
                {
                    options.Mask = ParseMask(RequireValue(args, ref i, arg));
                    options.OptionsTest = true;
                    continue;
                }

                throw new ArgumentException($"Unknown test ROM option: {arg}");
            }

            return options;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{option} requires a value.");

            return args[++index];
        }

        private static char ParseSingleKey(string value, string optionName, char[] allowed)
        {
            if (value.Length != 1)
                throw new ArgumentException($"--{optionName} requires one of: {string.Join(", ", allowed)}.");

            char key = char.ToUpperInvariant(value[0]);
            if (!allowed.Contains(key))
                throw new ArgumentException($"--{optionName} requires one of: {string.Join(", ", allowed)}.");

            return key;
        }

        private static string ParseMask(string value)
        {
            string mask = value.Trim().ToUpperInvariant();
            if (mask.Length != 2 || !byte.TryParse(mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("--mask requires two hexadecimal digits.");

            return mask;
        }

        private static long MillisecondsToCycles(int milliseconds)
        {
            return (long)milliseconds * CyclesPerSecond / 1000;
        }

        private static SystemVia GetSystemVia(Emulator emulator)
        {
            FieldInfo? field = typeof(Emulator).GetField("systemVia", InstanceNonPublic);
            return (SystemVia?)field?.GetValue(emulator)
                ?? throw new InvalidOperationException("Could not access the emulator system VIA.");
        }

        private static void UpdateCpuIrqLine(Emulator emulator)
        {
            MethodInfo? method = typeof(Emulator).GetMethod("UpdateCpuIrqLine", InstanceNonPublic);
            method?.Invoke(emulator, null);
        }

        private static void PrintResult(TestRomResult result)
        {
            Console.WriteLine(result.Success ? "PASS" : "FAIL");
            Console.WriteLine($"  Test:          {result.Name}");
            Console.WriteLine($"  ROM:           {result.RomPath}");
            Console.WriteLine($"  Keys:          {result.Keys}");
            Console.WriteLine($"  PC:            ${result.ProgramCounter & 0xFFFF:X4}");
            Console.WriteLine($"  Cycles:        {result.Cycles}");
            Console.WriteLine($"  Video mode:    {result.VideoMode}");
            Console.WriteLine($"  Mode 7 cells:  {result.NonBlankMode7Cells}");
            Console.WriteLine($"  Wall time:     {result.Elapsed.TotalSeconds:F2}s");
            if (result.Failure is not null)
                Console.WriteLine($"  Reason:        {result.Failure}");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run -- --test-rom [--seconds N] [--size 16|32|64]");
            Console.WriteLine("  dotnet run -- --test-rom --options --refresh Y --region A --mask 00");
            Console.WriteLine("  dotnet run -- --test-rom --all");
            Console.WriteLine();
            Console.WriteLine("This runner targets the BBC B beeb_test_os images. B+ and Master images need");
            Console.WriteLine("different memory maps, so they are deliberately not selected by default.");
        }

        private sealed class TestRomOptions
        {
            public string TestRomRoot { get; set; } = "TESTROMS";
            public string? RomPath { get; set; }
            public int SizeKb { get; set; } = 16;
            public int DurationSeconds { get; set; } = DefaultDurationSeconds;
            public int StartupDelayMilliseconds { get; set; } = DefaultStartupDelayMilliseconds;
            public bool OptionsTest { get; set; }
            public bool RunAll { get; set; }
            public bool ListOnly { get; set; }
            public bool ShowHelp { get; set; }
            public char Refresh { get; set; } = 'Y';
            public char Region { get; set; } = 'A';
            public string Mask { get; set; } = "00";
        }

        private readonly record struct TestRomCase(string Name, string RomPath, string Keys, bool RequireVisibleOutput);

        private readonly record struct KeyEvent(long Cycle, byte InternalKey, bool Pressed);

        private readonly record struct TestRomResult(
            string Name,
            string RomPath,
            string Keys,
            bool Success,
            string? Failure,
            ulong ProgramCounter,
            long Cycles,
            BbcScreenMode VideoMode,
            int NonBlankMode7Cells,
            TimeSpan Elapsed);
    }
}
