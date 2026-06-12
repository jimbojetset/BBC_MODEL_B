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
            if (TestRomRunner.TryRunFromArgs(args, out int testRomExitCode))
            {
                Environment.ExitCode = testRomExitCode;
                return;
            }

            using Emulator emulator = new Emulator();
            StartupOptions options = ParseStartupOptions(args);

            if (!string.IsNullOrEmpty(options.PrintAutoLoadPath))
            {
                DiscController8271 disc = new DiscController8271();
                disc.Mount(options.PrintAutoLoadPath);
                Console.WriteLine(disc.AutoLoadCommand ?? string.Empty);
                return;
            }

            emulator.Initialise(createDisplay: options.HeadlessMilliseconds == 0);
            emulator.Cpu.SpeedScale = options.SpeedScale;

            Console.WriteLine("BBC Model B emulator initialised.");
            Console.WriteLine($"OS ROM:     ${Emulator.OsRomStart:X4}-${Emulator.OsRomEnd:X4}");
            Console.WriteLine($"BASIC ROM:  ${Emulator.SidewaysRomStart:X4}-${Emulator.SidewaysRomEnd:X4}");
            Console.WriteLine($"DFS ROM:    bank {Emulator.DfsRomBank}");
            Console.WriteLine($"Reset PC:   ${emulator.Cpu.registers.PC:X4}");

            foreach (string path in options.MountPaths)
                emulator.MountHostFile(path);

            if (options.BootDisc)
                emulator.QueueMountedDiscBoot();

            if (options.HeadlessMilliseconds > 0)
                emulator.RunHeadless(TimeSpan.FromMilliseconds(options.HeadlessMilliseconds));
            else
                emulator.Run();
        }

        private static StartupOptions ParseStartupOptions(string[] args)
        {
            int headlessMilliseconds = 0;
            List<string> mountPaths = new List<string>();
            string? printAutoLoadPath = null;
            double speedScale = 1.0;
            bool bootDisc = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--print-autoload", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--print-autoload requires an SSD path.");

                    printAutoLoadPath = args[++i];
                    continue;
                }

                if (string.Equals(args[i], "--headless-ms", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out headlessMilliseconds) || headlessMilliseconds <= 0)
                        throw new ArgumentException("--headless-ms requires a positive millisecond value.");

                    i++;
                    continue;
                }

                if (string.Equals(args[i], "--speed", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !TryParseSpeedScale(args[i + 1], out speedScale))
                        throw new ArgumentException("--speed requires a value such as 0.25 or 25%.");

                    i++;
                    continue;
                }

                if (string.Equals(args[i], "--boot-disc", StringComparison.OrdinalIgnoreCase))
                {
                    bootDisc = true;
                    continue;
                }

                if (string.Equals(args[i], "--disc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--disk", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--file", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"{args[i]} requires a path.");

                    mountPaths.Add(args[++i]);
                    continue;
                }

                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    mountPaths.Add(args[i]);
            }

            return new StartupOptions(headlessMilliseconds, mountPaths, printAutoLoadPath, speedScale, bootDisc);
        }

        private static bool TryParseSpeedScale(string value, out double speedScale)
        {
            string trimmed = value.Trim();
            bool percent = trimmed.EndsWith('%');
            if (percent)
                trimmed = trimmed[..^1];

            if (!double.TryParse(trimmed, out speedScale))
                return false;

            if (percent)
                speedScale /= 100.0;

            return speedScale is >= 0.01 and <= 4.0;
        }

        private readonly record struct StartupOptions(int HeadlessMilliseconds, IReadOnlyList<string> MountPaths, string? PrintAutoLoadPath, double SpeedScale, bool BootDisc);
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
        public const ushort IoStart = 0xFC00;
        public const ushort IoEnd = 0xFEFF;
        public const ushort OsRomEnd = 0xFFFF;
        public const int RomSize = 16 * 1024;
        public const int SidewaysRomBanks = 16;
        public const int BasicRomBank = 15;
        public const int DfsRomBank = 14;
        // Banks 4..7 are sideways RAM in this configuration. Most enhanced BBC setups
        // (Watford SRAM, Aries B20, Acorn 1.20) populate the upper four banks (12..15)
        // with ROMs and leave the lower banks free for RAM. We mirror that arrangement.
        public const int SidewaysRamFirstBank = 4;
        public const int SidewaysRamLastBank = 7;
        public const int CpuClockHz = 2_000_000;
        public const ushort KeyboardBufferStart = 0x03E0;
        public const ushort KeyboardBufferEnd = 0x03FF;

        private const string RomDirectory = "ROMS";
        private const string OsRomFileName = "OS12.rom";
        private const string BasicRomFileName = "BASIC2.rom";
        private const string DfsRomFileName = "DFS-0.9.rom";
        private const string BasicRomMarker = "BASIC\0(C)1982 Acorn";
        private const string DfsRomMarker = "DFS\0" + "0.90";
        private const string OsRomMarker = "BBC Computer";
        private const int TargetFramesPerSecond = 50;
        private const int FrameMilliseconds = 1000 / TargetFramesPerSecond;
        private const ushort KeyboardBufferBusyFlag = 0x02CF;
        private const ushort KeyboardBufferStartIndex = 0x02D8;
        private const ushort KeyboardBufferEndIndex = 0x02E1;
        private const ushort EscapeFlag = 0x00FF;
        private const ushort EscapeKeyStatus = 0x0275;
        private const ushort CliVector = 0x0208;
        private const ushort OscliEntry = 0xFFF7;
        private const byte KeyboardBufferEmptyFlag = 0x80;
        private const byte EscapePendingFlag = 0x80;
        private const byte BbcCapsLockKey = 0x40;
        private const int CapsLockTapPulseCycles = CpuClockHz / 20;
        private const int MinimumMatrixKeyPressMilliseconds = 40;
        private const uint DisplayBlack = 0xFF000000;

        private bool initialised;
        private Thread? cpuThread;
        private Exception? cpuException;
        private readonly byte[] inputScratch = new byte[64];
        private readonly HostKeyChange[] keyChangeScratch = new HostKeyChange[64];
        private readonly HostJoystickChange[] joystickChangeScratch = new HostJoystickChange[16];
        private readonly BreakKeyPress[] breakScratch = new BreakKeyPress[4];
        private readonly List<string> discLoadScratch = new List<string>();
        private readonly Queue<byte> pendingKeyboardInput = new Queue<byte>();
        private readonly Queue<string> pendingBootScriptLines = new Queue<string>();
        private readonly long[] matrixKeyPressedAtTicks = new long[128];
        private readonly long[] matrixKeyReleaseDueTicks = new long[128];
        private readonly bool[] matrixKeyReleasePending = new bool[128];
        private readonly byte[] sidewaysRoms = new byte[SidewaysRomBanks * RomSize];
        private int selectedSidewaysRom = BasicRomBank;
        private BreakKeyPress pendingBreak;
        private string? pendingBootExecScript;
        private long nextBootScriptLineAtTicks;
        private readonly SystemVia systemVia;
        private readonly UserVia userVia = new UserVia();
        private readonly CassetteInterface cassetteInterface = new CassetteInterface();
        private readonly Adc7002 adc = new Adc7002();
        private readonly HostFilingSystem hostFilingSystem;
        private readonly DiscController8271 discController;
        private JoystickState joystickState;
        private long keyboardInputEnabledAtTicks;
        private int capsLockTapPulseCycles;
        private bool capsLockTapPressed;
        private bool hostCapsLockState;
        private bool bbcCapsLockState = true;
        private bool blankDisplayUntilStableVideo;

        /// <summary>Gets the 64 KiB CPU-visible memory bus.</summary>
        public FlatMemoryBus Memory { get; } = new FlatMemoryBus();

        /// <summary>Gets the 6502 CPU.</summary>
        public CPU_6502 Cpu { get; }

        /// <summary>Gets the video display controller.</summary>
        public Video Video { get; }

        /// <summary>Gets the SN76489 sound generator.</summary>
        public Sound Sound { get; }

        /// <summary>Gets the SDL display surface.</summary>
        public Display? Display { get; private set; }

        /// <summary>Gets the loaded OS ROM path.</summary>
        public string OsRomPath { get; private set; } = string.Empty;

        /// <summary>Gets the loaded BASIC ROM path.</summary>
        public string BasicRomPath { get; private set; } = string.Empty;

        /// <summary>Gets the loaded DFS ROM path.</summary>
        public string DfsRomPath { get; private set; } = string.Empty;

        /// <summary>Initializes a new emulator coordinator.</summary>
        public Emulator()
        {
            Sound = new Sound();
            systemVia = new SystemVia(Sound);
            hostFilingSystem = new HostFilingSystem(Memory);
            hostFilingSystem.QueueKeyboardText = QueueKeyboardText;
            discController = new DiscController8271();
            Video = new Video(Memory.Memory);
            systemVia.ScreenMemoryWindowChanged += Video.SetScreenMemoryWindow;
            Cpu = new CPU_6502(Memory, CpuClockHz);
            discController.NmiRequested += () => Cpu.InitiateNMI(0xFFFA);
            Cpu.OnReset = ResetDeviceState;
            Cpu.OnCyclesExecuted = AdvanceDeviceCycles;
            Cpu.OnBeforeInstruction = HandleHostFirmwareHooks;
            Cpu.OnAccessStretch = ComputeBusStretchCycles;
            adc.EndOfConversionChanged += eocActive =>
            {
                systemVia.SignalAdcEndOfConversion(eocActive);
                UpdateCpuIrqLine();
            };
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
            {
                Display = new Display();
                BlankDisplayUntilStableVideo(Display, presentNow: false);
            }

            pendingBreak = default;
            Cpu.ResetNow();
            initialised = true;
        }

        /// <summary>Runs the CPU and SDL display loop until the window is closed or the CPU faults.</summary>
        public void Run()
        {
            if (!initialised)
                Initialise(createDisplay: true);

            if (Display is null)
            {
                Display = new Display();
                BlankDisplayUntilStableVideo(Display, presentNow: false);
            }

            Sound.Start();
            Thread.Sleep(Sound.PowerOnToneDuration/2);

            StartCpu();

            long frameTicks = Math.Max(1, Stopwatch.Frequency / TargetFramesPerSecond);
            long nextFrame = Stopwatch.GetTimestamp() + frameTicks;
            keyboardInputEnabledAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            while (Display.PumpEvents())
            {
                if (cpuException is not null)
                    throw new InvalidOperationException("CPU execution failed.", cpuException);

                DrainHostBreakInput(Display);
                DrainHostDiscLoads(Display);
                DrainHostKeyMatrixInput(Display);
                DrainHostJoystickInput(Display);
                DrainHostKeyboardInput(Display);
                QueuePendingBootScriptLine();
                RenderDisplayFrame(Display);
                DrainHostScreenshotRequests(Display);
                Display.Present();

                WaitUntil(nextFrame);
                nextFrame += frameTicks;

                long now = Stopwatch.GetTimestamp();
                if (nextFrame < now - frameTicks * 4)
                    nextFrame = now + frameTicks;
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

            long deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            keyboardInputEnabledAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (Stopwatch.GetTimestamp() >= keyboardInputEnabledAtTicks)
                {
                    QueuePendingBootScriptLine();
                    DrainQueuedKeyboardInput();
                }

                Thread.Sleep(FrameMilliseconds);
            }

            StopCpu();

            if (cpuException is not null)
                throw new InvalidOperationException("CPU execution failed.", cpuException);

            Console.WriteLine($"Headless PC: ${Cpu.registers.PC:X4}");
            Console.WriteLine($"Mode 7 non-blank cells: {Video.CountMode7NonBlankCells()}");
            Console.WriteLine($"Tracked video mode: {Video.CurrentMode}");
        }

        /// <summary>Mounts a host file or disc image.</summary>
        /// <param name="path">The host path.</param>
        public void MountHostFile(string path)
        {
            if (IsDiscImagePath(path))
            {
                // Persist any pending writes from the previously mounted image before swapping.
                if (discController.HasMountedDisc && discController.ImageDirty)
                {
                    if (discController.Flush())
                        Console.WriteLine($"Saved disc:   {discController.MountedFileName}");
                }

                discController.Mount(path);
                hostFilingSystem.Mount(path);

                Console.WriteLine($"Mounted DFS: {discController.MountedFileName}");
                return;
            }

            hostFilingSystem.Mount(path);

            Console.WriteLine($"Mounted:    {hostFilingSystem.MountedFileName}");
        }

        public void QueueMountedDiscBoot()
        {
            if (discController.TryGetBootExecScript(out string? bootScript) && bootScript is not null)
                QueueBootScript(bootScript);
        }

        /// <summary>Releases emulator-owned resources.</summary>
        public void Dispose()
        {
            StopCpu();
            if (discController.HasMountedDisc && discController.ImageDirty)
            {
                if (discController.Flush())
                    Console.WriteLine($"Saved disc:   {discController.MountedFileName}");
            }
            Sound.Dispose();
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

        private void ResetDeviceState()
        {
            Video.Reset();
            Sound.Reset();
            systemVia.Reset();
            userVia.Reset();
            Video.SetScreenMemoryWindow(systemVia.CurrentScreenMemoryWindow);
            discController.Reset();
            joystickState = default;
            adc.Reset();
            UpdateAdcChannels();
            Cpu.SetIrqLine(false);
            keyboardInputEnabledAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            selectedSidewaysRom = BasicRomBank;
            if (pendingBreak.Shift)
                systemVia.SetKeyState(0x00, true);
            hostCapsLockState = Display?.HostCapsLockEnabled == true;
            bbcCapsLockState = true;
            capsLockTapPressed = false;
            capsLockTapPulseCycles = 0;
            Array.Clear(matrixKeyPressedAtTicks);
            Array.Clear(matrixKeyReleaseDueTicks);
            Array.Clear(matrixKeyReleasePending);
            Memory.Memory[EscapeFlag] = 0;
            if (!string.IsNullOrEmpty(pendingBootExecScript))
            {
                QueueBootScript(pendingBootExecScript);
                pendingBootExecScript = null;
            }
        }

        private void RenderDisplayFrame(Display display)
        {
            if (blankDisplayUntilStableVideo)
            {
                if (!Video.HasStableDisplaySnapshot)
                {
                    Array.Fill(display.FrameBuffer, DisplayBlack);
                    return;
                }

                blankDisplayUntilStableVideo = false;
            }

            Video.Render(display);
        }

        private void BlankDisplayUntilStableVideo(Display display, bool presentNow)
        {
            blankDisplayUntilStableVideo = true;
            Video.InvalidateFrameSnapshot();
            Array.Fill(display.FrameBuffer, DisplayBlack);
            if (presentNow)
                display.Present();
        }

        private void AdvanceDeviceCycles(int cycles)
        {
            Cpu.PacingEnabled = !discController.TransferActive;
            int previousFrame = systemVia.FrameCounter;
            Sound.Tick(cycles);
            systemVia.Tick(cycles);
            userVia.Tick(cycles);
            discController.Tick(cycles);
            adc.Tick(cycles);
            TickCapsLockTap(cycles);
            if (systemVia.FrameCounter != previousFrame)
                Video.CaptureVisibleFrame();

            UpdateCpuIrqLine();
        }

        private void StartCapsLockTap()
        {
            capsLockTapPulseCycles = CapsLockTapPulseCycles;
            capsLockTapPressed = true;
            systemVia.SetKeyState(BbcCapsLockKey, true);
        }

        private void TickCapsLockTap(int cycles)
        {
            if (!capsLockTapPressed)
                return;

            capsLockTapPulseCycles -= cycles;
            if (capsLockTapPulseCycles > 0)
                return;

            capsLockTapPressed = false;
            systemVia.SetKeyState(BbcCapsLockKey, false);
        }

        private void UpdateCpuIrqLine()
        {
            Cpu.SetIrqLine(systemVia.IrqAsserted || userVia.IrqAsserted);
        }

        private bool HandleHostFirmwareHooks()
        {
            return TryHandleSidewaysRomLanguageCommand()
                || hostFilingSystem.TryHandleOsfile(Cpu)
                || hostFilingSystem.TryHandleOscli(Cpu)
                || hostFilingSystem.TryHandleFscv(Cpu)
                || TryHandleOsbyte();
        }

        private bool TryHandleSidewaysRomLanguageCommand()
        {
            if (!IsCliEntryPoint((ushort)(Cpu.registers.PC & 0xFFFF)))
                return false;

            ushort commandAddress = (ushort)(Cpu.registers.X | (Cpu.registers.Y << 8));
            string command = ReadOsString(commandAddress).Trim();
            if (command.StartsWith('*'))
                command = command[1..].TrimStart();

            string commandName = GetCommandName(command);
            for (int bank = SidewaysRomBanks - 1; bank >= 0; bank--)
            {
                if (!IsLanguageRom(bank))
                    continue;

                string title = ReadSidewaysRomTitle(bank);
                if (title.Length == 0 || !MatchesSidewaysRomCommand(commandName, title))
                    continue;

                selectedSidewaysRom = bank;
                Cpu.registers.A = 1;
                Cpu.registers.X = (byte)bank;
                Cpu.registers.PC = SidewaysRomStart;
                return true;
            }
            return false;
        }

        private bool IsLanguageRom(int bank)
        {
            if (bank < 0 || bank >= SidewaysRomBanks)
                return false;

            int typeOffset = bank * RomSize + 6;
            return (sidewaysRoms[typeOffset] & 0x40) != 0;
        }

        private bool IsCliEntryPoint(ushort pc)
        {
            return pc == OscliEntry || pc == ReadWord(CliVector);
        }

        private bool TryHandleOsbyte()
        {
            if ((Cpu.registers.PC & 0xFFFF) != 0xFFF4)
                return false;

            if (Cpu.registers.A == 0x80)
            {
                ReadAdval(Cpu.registers.X, out byte x, out byte y);
                Cpu.registers.X = x;
                Cpu.registers.Y = y;
                ReturnFromFirmwareSubroutine();
                return true;
            }

            if (Cpu.registers.A == 0x10)
            {
                // OSBYTE 16 selects how many analogue (ADC) channels the MOS samples.
                // The µPD7002 ADC is not emulated; ADVAL is serviced directly via the
                // OSBYTE &80 intercept above, so MOS's background ADC sampling must not
                // be started. Letting this reach the real MOS drives the absent ADC and
                // breaks games such as Frogger. Handle it as a clean no-op (X = previous
                // channel count, reported as 0).
                Cpu.registers.X = 0;
                ReturnFromFirmwareSubroutine();
                return true;
            }

            if (Cpu.registers.A == 0x8B)
            {
                ReturnFromFirmwareSubroutine();
                return true;
            }

            if (Cpu.registers.A == 0x8C)
            {
                // The disc image remains mounted even when tape-oriented loaders
                // issue the cassette-speed OSBYTE.
                ReturnFromFirmwareSubroutine();
                return true;
            }

            if (Cpu.registers.A == 0x79)
            {
                byte scanResult = ScanKeyboard(Cpu.registers.X);
                Cpu.registers.X = scanResult;
                SetFirmwareResultFlags(scanResult);
                ReturnFromFirmwareSubroutine();
                return true;
            }

            if (Cpu.registers.A == 0x7A)
            {
                byte scanResult = ScanKeyboard(0x10);
                Cpu.registers.X = scanResult;
                SetFirmwareResultFlags(scanResult);
                ReturnFromFirmwareSubroutine();
                return true;
            }

            if (Cpu.registers.A != 0x81)
            {
                return false;
            }

            if (!TryMapNegativeInkeyCode(Cpu.registers.X, out byte internalKey))
            {
                return false;
            }

            byte result = systemVia.IsKeyPressed(internalKey) ? (byte)0xFF : (byte)0x00;
            Cpu.registers.X = result;
            Cpu.registers.Y = result;
            SetFirmwareResultFlags(result);
            ReturnFromFirmwareSubroutine();
            return true;
        }

        private byte ScanKeyboard(byte scanKey)
        {
            if ((scanKey & 0x80) != 0)
            {
                byte internalKey = (byte)(scanKey ^ 0x80);
                return internalKey >= 0x10 && systemVia.IsKeyPressed(internalKey)
                    ? scanKey
                    : (byte)0x00;
            }

            int start = Math.Max(0x10, (int)scanKey);
            for (int internalKey = start; internalKey < 0x80; internalKey++)
            {
                if (systemVia.IsKeyPressed((byte)internalKey))
                    return (byte)internalKey;
            }

            return 0xFF;
        }

        private void SetFirmwareResultFlags(byte result)
        {
            Cpu.registers.Flags.Z = result == 0;
            Cpu.registers.Flags.N = (result & 0x80) != 0;
        }

        private void ReadAdval(byte channel, out byte x, out byte y)
        {
            ushort value = channel switch
            {
                0 => joystickState.Fire ? (ushort)0x0001 : (ushort)0x0000,
                1 => joystickState.Left ? (ushort)0xFFFF : joystickState.Right ? (ushort)0x0000 : (ushort)0x8000,
                2 => joystickState.Up ? (ushort)0xFFFF : joystickState.Down ? (ushort)0x0000 : (ushort)0x8000,
                _ => 0x8000
            };

            x = (byte)value;
            y = (byte)(value >> 8);
        }

        private static bool TryMapNegativeInkeyCode(byte code, out byte internalKey)
        {
            internalKey = code switch
            {
                0xFF => 0x00, // SHIFT
                0xFE => 0x01, // CTRL
                0xEF => 0x10, // Q
                0xDE => 0x21, // W
                0xDD => 0x22, // E
                0xDC => 0x23, // T
                0xDB => 0x24, // 7
                0xDA => 0x25, // I
                0xD9 => 0x26, // 9
                0xD8 => 0x27, // 0
                0xE7 => 0x28, // #
                0xCF => 0x30, // 1
                0xCE => 0x31, // 2
                0xCD => 0x32, // D
                0xCC => 0x33, // R
                0xCB => 0x34, // 6
                0xCA => 0x35, // U
                0xC9 => 0x36, // O
                0xC8 => 0x37, // P
                0xC7 => 0x38, // [
                0xBF => 0x40, // CAPS LOCK
                0xBE => 0x41, // A
                0xBD => 0x42, // X
                0xBC => 0x43, // F
                0xBB => 0x44, // Y
                0xBA => 0x45, // J
                0xB9 => 0x46, // K
                0xB8 => 0x47, // @
                0xB7 => 0x48, // :
                0xB6 => 0x49, // RETURN
                0xAE => 0x51, // S
                0xAD => 0x52, // C
                0xAC => 0x53, // G
                0xAB => 0x54, // H
                0xAA => 0x55, // N
                0xA9 => 0x56, // L
                0xA8 => 0x57, // ;
                0xA7 => 0x58, // ]
                0xA6 => 0x59, // DELETE
                0x9F => 0x60, // TAB
                0x9E => 0x61, // Z
                0x9D => 0x62, // SPACE
                0x9C => 0x63, // V
                0x9B => 0x64, // B
                0x9A => 0x65, // M
                0x99 => 0x66, // ,
                0x98 => 0x67, // .
                0x97 => 0x68, // /
                0x8F => 0x70, // ESCAPE
                0xDF => 0x20, // f0
                0x8E => 0x71, // f1
                0x8D => 0x72, // f2
                0x8C => 0x73, // f3
                0xEB => 0x14, // f4
                0x8B => 0x74, // f5
                0x8A => 0x75, // f6
                0xE9 => 0x16, // f7
                0x89 => 0x76, // f8
                0x88 => 0x77, // f9
                0xE8 => 0x17, // -
                0xE6 => 0x18, // ^
                0x87 => 0x78, // backslash
                _ => 0xFF
            };

            return internalKey != 0xFF;
        }

        private void ReturnFromFirmwareSubroutine()
        {
            byte lo = Memory.Memory[0x0100 + ((Cpu.registers.S + 1) & 0xFF)];
            byte hi = Memory.Memory[0x0100 + ((Cpu.registers.S + 2) & 0xFF)];
            Cpu.registers.S += 2;
            Cpu.registers.PC = (ushort)(((hi << 8) | lo) + 1);
        }

        private string ReadOsString(ushort address)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < 255; i++)
            {
                byte value = Memory.Memory[(address + i) & 0xFFFF];
                if (value == 0x0D)
                    break;

                builder.Append((char)value);
            }

            return builder.ToString();
        }

        private ushort ReadWord(ushort address)
        {
            return (ushort)(Memory.Memory[address & 0xFFFF] | (Memory.Memory[(address + 1) & 0xFFFF] << 8));
        }

        private static string GetCommandName(string command)
        {
            int separator = command.IndexOfAny([' ', '\t', '\r']);
            return separator < 0 ? command : command[..separator];
        }

        private static bool MatchesSidewaysRomCommand(string commandName, string romTitle)
        {
            if (string.Equals(commandName, romTitle, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!commandName.EndsWith(".", StringComparison.Ordinal))
                return false;

            string abbreviation = commandName[..^1];
            return abbreviation.Length > 0
                && romTitle.StartsWith(abbreviation, StringComparison.OrdinalIgnoreCase);
        }

        private string ReadSidewaysRomTitle(int bank)
        {
            if (bank < 0 || bank >= SidewaysRomBanks)
                return string.Empty;

            int offset = bank * RomSize + 9;
            if (sidewaysRoms[offset] == 0xFF || sidewaysRoms[offset] == 0)
                return string.Empty;

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < 64 && offset + i < sidewaysRoms.Length; i++)
            {
                byte value = sidewaysRoms[offset + i];
                if (value == 0 || value == 0xFF || value < 32 || value > 126)
                    break;

                builder.Append((char)value);
            }

            return builder.ToString();
        }

        private void DrainHostDiscLoads(Display display)
        {
            discLoadScratch.Clear();
            display.DrainDiscLoads(discLoadScratch);

            foreach (string path in discLoadScratch)
                MountHostFile(path);
        }

        private void DrainHostScreenshotRequests(Display display)
        {
            int count = display.DrainScreenshotRequests();
            for (int i = 0; i < count; i++)
            {
                string path = CreateScreenshotPath();
                display.SavePng(path);
                Console.WriteLine($"Screenshot: {path}");
            }
        }

        private void DrainHostKeyboardInput(Display display)
        {
            if (Stopwatch.GetTimestamp() < keyboardInputEnabledAtTicks)
                return;

            int count = display.DrainInput(inputScratch);
            for (int i = 0; i < count; i++)
            {
                if (inputScratch[i] == 27)
                    HandleEscapeKeyPress();
                else
                    pendingKeyboardInput.Enqueue(inputScratch[i]);
            }

            while (pendingKeyboardInput.Count > 0)
            {
                if (!DrainQueuedKeyboardInput())
                    break;
            }
        }

        private void DrainHostKeyMatrixInput(Display display)
        {
            DrainPendingMatrixKeyReleases();

            int count = display.DrainKeyChanges(keyChangeScratch);

            for (int i = 0; i < count; i++)
            {
                if (keyChangeScratch[i].InternalKey == BbcCapsLockKey)
                {
                    hostCapsLockState = keyChangeScratch[i].Pressed;
                    if (hostCapsLockState != bbcCapsLockState)
                    {
                        StartCapsLockTap();
                        bbcCapsLockState = hostCapsLockState;
                    }

                    continue;
                }

                ApplyHostKeyChange(keyChangeScratch[i]);
            }

            if (count > 0)
                UpdateCpuIrqLine();
        }

        private void ApplyHostKeyChange(HostKeyChange change)
        {
            byte key = change.InternalKey;
            if (key >= matrixKeyPressedAtTicks.Length)
            {
                systemVia.SetKeyState(key, change.Pressed);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (change.Pressed)
            {
                matrixKeyReleasePending[key] = false;
                matrixKeyPressedAtTicks[key] = now;
                systemVia.SetKeyState(key, true);
                return;
            }

            long pressedAt = matrixKeyPressedAtTicks[key];
            long minimumTicks = Stopwatch.Frequency * MinimumMatrixKeyPressMilliseconds / 1000;
            long releaseDue = pressedAt == 0 ? now : pressedAt + minimumTicks;
            if (releaseDue <= now)
            {
                matrixKeyReleasePending[key] = false;
                systemVia.SetKeyState(key, false);
                return;
            }

            matrixKeyReleaseDueTicks[key] = releaseDue;
            matrixKeyReleasePending[key] = true;
        }

        private void DrainPendingMatrixKeyReleases()
        {
            long now = Stopwatch.GetTimestamp();
            bool released = false;

            for (int key = 0; key < matrixKeyReleasePending.Length; key++)
            {
                if (!matrixKeyReleasePending[key] || matrixKeyReleaseDueTicks[key] > now)
                    continue;

                matrixKeyReleasePending[key] = false;
                systemVia.SetKeyState((byte)key, false);
                released = true;
            }

            if (released)
                UpdateCpuIrqLine();
        }

        private void DrainHostJoystickInput(Display display)
        {
            int count = display.DrainJoystickChanges(joystickChangeScratch);
            if (count == 0)
                return;

            for (int i = 0; i < count; i++)
            {
                HostJoystickChange change = joystickChangeScratch[i];
                switch (change.Control)
                {
                    case JoystickControl.Left:
                        joystickState.Left = change.Pressed;
                        break;

                    case JoystickControl.Right:
                        joystickState.Right = change.Pressed;
                        break;

                    case JoystickControl.Up:
                        joystickState.Up = change.Pressed;
                        break;

                    case JoystickControl.Down:
                        joystickState.Down = change.Pressed;
                        break;

                    case JoystickControl.Fire:
                        joystickState.Fire = change.Pressed;
                        break;
                }
            }

            UpdateAdcChannels();
        }

        private void UpdateAdcChannels()
        {
            // µPD7002 channels follow the BBC ADVAL convention: 0x0000 = max one direction, 0xFFFF = max other.
            // Channel 0 returns 0/1 for fire (digital) — we reuse channel 0 for fire (most software polls ADVAL via OSBYTE).
            // Channels 1 (X-axis) and 2 (Y-axis) carry analogue position.
            ushort fire = joystickState.Fire ? (ushort)0xFFFF : (ushort)0x0000;
            ushort xAxis = joystickState.Left ? (ushort)0xFFFF
                         : joystickState.Right ? (ushort)0x0000
                         : (ushort)0x8000;
            ushort yAxis = joystickState.Up ? (ushort)0xFFFF
                         : joystickState.Down ? (ushort)0x0000
                         : (ushort)0x8000;
            adc.SetChannel(0, fire);
            adc.SetChannel(1, xAxis);
            adc.SetChannel(2, yAxis);
            adc.SetChannel(3, 0x8000);
        }

        private void DrainHostBreakInput(Display display)
        {
            int count = display.DrainBreaks(breakScratch);
            if (count == 0)
                return;

            pendingBreak = breakScratch[count - 1];
            if (pendingBreak.Shift && discController.TryGetBootExecScript(out string? bootScript))
            {
                pendingBootExecScript = bootScript;
                pendingBreak = new BreakKeyPress(false, pendingBreak.Control);
            }

            BlankDisplayUntilStableVideo(display, presentNow: true);
            Cpu.RequestReset();
        }

        private void HandleEscapeKeyPress()
        {
            // OSBYTE 229 (OS variable $0275) selects the ESCAPE key behaviour: when it is
            // non-zero the key acts as an ordinary key that generates ASCII 27, otherwise it
            // raises an escape condition. Games such as YieArKungFu issue *FX 229,1 so they
            // can read ESCAPE in-game instead of breaking back into the BASIC loader.
            if (Memory.Memory[EscapeKeyStatus] != 0)
                pendingKeyboardInput.Enqueue(27);
            else
                TriggerEscapeCondition();
        }

        private void TriggerEscapeCondition()
        {
            Memory.Memory[EscapeFlag] |= EscapePendingFlag;
        }

        private void QueueKeyboardText(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n')
                    pendingKeyboardInput.Enqueue(13);
                else if (ch == '#')
                    pendingKeyboardInput.Enqueue((byte)'#');
                else if (ch >= 32 && ch <= 126)
                    pendingKeyboardInput.Enqueue((byte)ch);
            }
        }

        private void QueueBootScript(string script)
        {
            pendingBootScriptLines.Clear();
            foreach (string line in script.Replace('\n', '\r').Split('\r'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    pendingBootScriptLines.Enqueue(trimmed);
            }

            nextBootScriptLineAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        }

        private void QueuePendingBootScriptLine()
        {
            if (pendingBootScriptLines.Count == 0 || pendingKeyboardInput.Count > 0)
                return;

            long now = Stopwatch.GetTimestamp();
            if (now < keyboardInputEnabledAtTicks || now < nextBootScriptLineAtTicks || !IsKeyboardBufferEmpty())
                return;

            string line = pendingBootScriptLines.Dequeue();
            QueueKeyboardText(line + "\r");
            nextBootScriptLineAtTicks = now + (Stopwatch.Frequency / 3);
        }

        private bool DrainQueuedKeyboardInput()
        {
            bool inserted = false;

            while (pendingKeyboardInput.Count > 0)
            {
                if (!TryInsertKeyboardBufferCharacter(pendingKeyboardInput.Peek()))
                    break;

                pendingKeyboardInput.Dequeue();
                inserted = true;
            }

            return inserted;
        }

        private bool IsKeyboardBufferEmpty()
        {
            return Memory.Memory[KeyboardBufferStartIndex] == Memory.Memory[KeyboardBufferEndIndex];
        }

        private bool TryInsertKeyboardBufferCharacter(byte character)
        {
            byte start = Memory.Memory[KeyboardBufferStartIndex];
            byte end = Memory.Memory[KeyboardBufferEndIndex];
            byte nextEnd = NextKeyboardBufferOffset(end);

            if (nextEnd == start)
                return false;

            Memory.Memory[0x0300 + end] = character;
            Memory.Memory[KeyboardBufferEndIndex] = nextEnd;
            Memory.Memory[KeyboardBufferBusyFlag] &= unchecked((byte)~KeyboardBufferEmptyFlag);
            return true;
        }

        private static byte NextKeyboardBufferOffset(byte offset)
        {
            return offset >= (KeyboardBufferEnd & 0xFF)
                ? (byte)(KeyboardBufferStart & 0xFF)
                : (byte)(offset + 1);
        }

        private static string CreateScreenshotPath()
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "Screenshots");
            string fileName = $"bbc-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            return Path.Combine(directory, fileName);
        }

        private struct JoystickState
        {
            public bool Left;
            public bool Right;
            public bool Up;
            public bool Down;
            public bool Fire;
        }

        private static void WaitUntil(long deadlineTicks)
        {
            long remaining = deadlineTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return;

            long remainingMs = remaining * 1000 / Stopwatch.Frequency;
            if (remainingMs > 1)
                Thread.Sleep((int)(remainingMs - 1));

            while (Stopwatch.GetTimestamp() < deadlineTicks)
                Thread.SpinWait(64);
        }

        private void LoadSystemRoms()
        {
            string romRoot = Path.Combine(AppContext.BaseDirectory, RomDirectory);
            if (!Directory.Exists(romRoot))
                romRoot = Path.Combine(Environment.CurrentDirectory, RomDirectory);

            if (!Directory.Exists(romRoot))
                throw new DirectoryNotFoundException($"ROM directory not found: {romRoot}");

            OsRomPath = Path.Combine(romRoot, OsRomFileName);
            BasicRomPath = Path.Combine(romRoot, BasicRomFileName);
            DfsRomPath = Path.Combine(romRoot, DfsRomFileName);

            ValidateRom(OsRomPath, OsRomMarker, RomSize);
            ValidateRom(BasicRomPath, BasicRomMarker, RomSize);
            ValidateRom(DfsRomPath, DfsRomMarker, RomSize / 2, RomSize);

            Memory.Load(OsRomStart, File.ReadAllBytes(OsRomPath));

            Array.Fill(sidewaysRoms, (byte)0xFF);
            LoadSidewaysRomBank(BasicRomPath, BasicRomBank);
            LoadSidewaysRomBank(DfsRomPath, DfsRomBank);
        }

        private void LoadSidewaysRomBank(string path, int bank)
        {
            byte[] rom = File.ReadAllBytes(path);
            int bankOffset = bank * RomSize;

            for (int i = 0; i < RomSize; i++)
                sidewaysRoms[bankOffset + i] = rom[i % rom.Length];
        }

        private static void ValidateRom(string path, string marker, int exactSize)
        {
            ValidateRom(path, marker, exactSize, exactSize);
        }

        private static void ValidateRom(string path, string marker, int minimumSize, int maximumSize)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required ROM not found: {path}");

            byte[] rom = File.ReadAllBytes(path);

            if (rom.Length < minimumSize || rom.Length > maximumSize)
                throw new InvalidOperationException($"ROM '{path}' must be between {minimumSize} and {maximumSize} bytes.");

            if (!ContainsAscii(rom, marker))
                throw new InvalidOperationException($"ROM '{path}' does not contain expected marker '{marker}'.");
        }

        private static bool ContainsAscii(ReadOnlySpan<byte> data, string marker)
        {
            ReadOnlySpan<byte> needle = Encoding.ASCII.GetBytes(marker);
            return data.IndexOf(needle) >= 0;
        }

        private void InstallMemoryMapHooks()
        {
            Memory.OnRead = (address, value) =>
            {
                ushort addr = (ushort)(address & 0xFFFF);

                if (addr >= SidewaysRomStart && addr <= SidewaysRomEnd)
                {
                    byte romValue = ReadSidewaysRom(addr);
                    return romValue;
                }

                if (addr >= IoStart && addr <= IoEnd)
                    return ReadSheila(addr);

                return value;
            };

            Memory.OnWrite = (address, value) =>
            {
                ushort addr = (ushort)(address & 0xFFFF);

                if (addr >= IoStart && addr <= IoEnd)
                {
                    WriteSheila(addr, value);
                    return true;
                }

                if (addr >= SidewaysRomStart && addr <= SidewaysRomEnd)
                {
                    // Sideways RAM: writes are accepted only when the currently paged-in bank is RAM.
                    if (IsSidewaysRamBank(selectedSidewaysRom))
                    {
                        int bankOffset = selectedSidewaysRom * RomSize;
                        int romOffset = addr - SidewaysRomStart;
                        sidewaysRoms[bankOffset + romOffset] = value;
                    }
                    return true;
                }

                if (addr >= SidewaysRomStart)
                    return true;

                return false;
            };
        }

        private static bool IsSidewaysRamBank(int bank)
        {
            return bank >= SidewaysRamFirstBank && bank <= SidewaysRamLastBank;
        }

        /// <summary>
        /// Models BBC 1 MHz bus stretching for FRED/JIM/SHEILA accesses (&amp;FC00-&amp;FEFF).
        /// On a real BBC, accesses to 1 MHz peripherals are synchronised to the 1 MHz clock,
        /// adding a per-access stretch. We approximate this with a constant +1 cycle per access,
        /// which is close enough for software that polls CRTC, VIA, ACIA, ADC, etc.
        /// </summary>
        private int ComputeBusStretchCycles(ulong address)
        {
            ushort addr = (ushort)(address & 0xFFFF);
            if (addr >= IoStart && addr <= IoEnd)
                return 1;
            return 0;
        }

        private byte ReadSidewaysRom(ushort address)
        {
            int bankOffset = selectedSidewaysRom * RomSize;
            int romOffset = address - SidewaysRomStart;
            return sidewaysRoms[bankOffset + romOffset];
        }

        private byte ReadSheila(ushort address)
        {
            if (Video.IsSheilaAddress(address))
                return Video.ReadSheila(address);

            if (SystemVia.IsAddress(address))
            {
                byte value = systemVia.Read(address);
                UpdateCpuIrqLine();
                return value;
            }

            if (UserVia.IsAddress(address))
            {
                byte value = userVia.Read(address);
                UpdateCpuIrqLine();
                return value;
            }

            if (DiscController8271.IsAddress(address))
                return discController.Read(address);

            if (CassetteInterface.IsAddress(address))
                return cassetteInterface.Read(address);

            if (Adc7002.IsAddress(address))
                return adc.Read(address);

            return address switch
            {
                0xFE30 => (byte)selectedSidewaysRom,
                _ => 0x00
            };
        }

        private void WriteSheila(ushort address, byte value)
        {
            if (Video.IsSheilaAddress(address))
            {
                Video.WriteSheila(address, value, systemVia.FrameCpuCycle);
                // CRTC programming or ULA clock-rate changes alter the frame period; push the
                // latest CRTC-derived period so the SystemVia vsync timer tracks reality.
                systemVia.SetVsyncPeriod(Video.GetCrtcFramePeriodPeripheralCycles());
                return;
            }

            if (SystemVia.IsAddress(address))
            {
                systemVia.Write(address, value);
                UpdateCpuIrqLine();
                return;
            }

            if (UserVia.IsAddress(address))
            {
                userVia.Write(address, value);
                UpdateCpuIrqLine();
                return;
            }

            if (DiscController8271.IsAddress(address))
            {
                discController.Write(address, value);
                return;
            }

            if (CassetteInterface.IsAddress(address))
            {
                cassetteInterface.Write(address, value);
                return;
            }

            if (Adc7002.IsAddress(address))
            {
                adc.Write(address, value);
                return;
            }

            switch (address)
            {
                case 0xFE30:
                    selectedSidewaysRom = value & 0x0F;
                    break;
            }
        }

        private static bool IsDiscImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".ssd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dsd", StringComparison.OrdinalIgnoreCase);
        }
    }
}
