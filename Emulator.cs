// ============================================================================
// Project:     BBC
// File:        Emulator.cs
// Description: Application entry point and main BBC Model B emulator host,
//              wiring CPU, memory, ROMs, video, sound, filing, and input.
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
    /// <summary>
    /// Coordinates the main BBC Model B emulator components and command-line startup.
    /// </summary>
    public sealed class Emulator : IDisposable
    {

        /// <summary>Runs the command-line BBC Model B emulator host.</summary>
        /// <param name="args">The command-line arguments.</param>
        private static void Main(string[] args)
        {
            using Emulator emulator = new Emulator();
            StartupOptions options = ParseStartupOptions(args);

            if (!string.IsNullOrEmpty(options.PrintAutoLoadPath))
            {
                Intel8271_Disk disc = new Intel8271_Disk();
                disc.Mount(options.PrintAutoLoadPath);
                Console.WriteLine(disc.AutoLoadCommand ?? string.Empty);
                return;
            }

            emulator.Initialise(createDisplay: options.HeadlessMilliseconds == 0);
            emulator.Cpu.SpeedScale = options.SpeedScale;
            emulator.Sound.ThrottleToPlayback = options.SpeedScale <= 1.0;

            Console.WriteLine("BBC Model B emulator initialised.");
            Console.WriteLine($"OS ROM:     ${Emulator.OsRomStart:X4}-${Emulator.OsRomEnd:X4}");
            Console.WriteLine($"BASIC ROM:  ${Emulator.SidewaysRomStart:X4}-${Emulator.SidewaysRomEnd:X4}");
            Console.WriteLine($"DFS ROM:    bank {Emulator.DfsRomBank}");
            if (emulator.AmxMouseRomPath is not null)
                Console.WriteLine($"AMX ROM:    bank {Emulator.AmxMouseRomBank}");
            Console.WriteLine($"Reset PC:   ${emulator.Cpu.registers.PC:X4}");

            foreach (string path in options.MountPaths)
            {
                try
                {
                    emulator.MountHostFile(path, options.AutoRunDisc);
                }
                catch (Exception ex) when (IsUserMountException(ex))
                {
                    string message = emulator.QueueHostMountFailure(path, ex);
                    Console.WriteLine(message);
                }
            }

            if (options.HeadlessMilliseconds > 0)
                emulator.RunHeadless(TimeSpan.FromMilliseconds(options.HeadlessMilliseconds));
            else
                emulator.Run();
        }

        /// <summary>Parses command-line startup options.</summary>
        /// <param name="args">The command-line arguments.</param>
        /// <returns>The resulting value.</returns>
        private static StartupOptions ParseStartupOptions(string[] args)
        {
            int headlessMilliseconds = 0;
            List<string> mountPaths = new List<string>();
            string? printAutoLoadPath = null;
            double speedScale = 1.0;
            bool autoRunDisc = true;

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
                    autoRunDisc = true;
                    continue;
                }

                if (string.Equals(args[i], "--no-boot-disc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--no-autoboot", StringComparison.OrdinalIgnoreCase))
                {
                    autoRunDisc = false;
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

            return new StartupOptions(headlessMilliseconds, mountPaths, printAutoLoadPath, speedScale, autoRunDisc);
        }

        /// <summary>Attempts to parse speed scale.</summary>
        /// <param name="value">The input value.</param>
        /// <param name="speedScale">The speed scale value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Checks whether a mount exception should be reported inside the emulator.</summary>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True when the exception is suitable for user-facing reporting; otherwise, false.</returns>
        private static bool IsUserMountException(Exception exception)
        {
            return exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or InvalidOperationException;
        }

        private readonly record struct StartupOptions(int HeadlessMilliseconds, IReadOnlyList<string> MountPaths, string? PrintAutoLoadPath, double SpeedScale, bool AutoRunDisc);
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
        public const int AmxMouseRomBank = 13;
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
        private const string AmxMouseRomFileName = "AMXMSE331.rom";
        private const string BasicRomMarker = "BASIC\0(C)1982 Acorn";
        private const string DfsRomMarker = "DFS\0" + "0.90";
        private static readonly bool MouseTraceEnabled = Environment.GetEnvironmentVariable("BBC_MOUSE_TRACE") == "1";
        private const string OsRomMarker = "BBC Computer";
        private const string AmxMouseRomMarker = "AMX Mouse Support";
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
        private const int HostDiscActivityLedMilliseconds = 180;
        private const int MaxAmxMouseStepsPerFrame = 16;
        private const int BootScriptInitialDelayMilliseconds = 1000;
        private const int ExecScriptInitialDelayMilliseconds = 100;
        private const int KeyboardScriptLineDelayMilliseconds = 120;
        private const uint DisplayBlack = 0xFF000000;

        private bool initialised;
        private Thread? cpuThread;
        private Exception? cpuException;
        private readonly byte[] inputScratch = new byte[64];
        private readonly HostKeyChange[] keyChangeScratch = new HostKeyChange[64];
        private readonly HostJoystickChange[] joystickChangeScratch = new HostJoystickChange[16];
        private readonly HostAnalogJoystickChange[] analogJoystickChangeScratch = new HostAnalogJoystickChange[16];
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
        private bool breakContinuationQueued;
        private long nextBootScriptLineAtTicks;
        private readonly SystemVia systemVia;
        private readonly UserVia userVia = new UserVia();
        private readonly CassetteInterface cassetteInterface = new CassetteInterface();
        private readonly uPD7002_ADC adc = new uPD7002_ADC();
        private readonly HostFilingSystem hostFilingSystem;
        private readonly Intel8271_Disk discController;
        private JoystickState joystickState;
        private bool mouseEnabled;
        private bool amxMouseRomLoaded;
        private bool mousePositionInitialized;
        private byte lastMouseX;
        private byte lastMouseY;
        private long keyboardInputEnabledAtTicks;
        private long hostDiscActivityLedUntilTicks;
        private int capsLockTapPulseCycles;
        private bool capsLockTapPressed;
        private bool hostCapsLockState;
        private bool bbcCapsLockState = true;

        /// <summary>Gets the 64 KiB CPU-visible memory bus.</summary>
        public FlatMemoryBus Memory { get; } = new FlatMemoryBus();

        /// <summary>Gets the 6502 CPU.</summary>
        public CPU_6502 Cpu { get; }

        /// <summary>Gets the video display controller.</summary>
        public HD6845_Video Video { get; }

        /// <summary>Gets the SN76489 sound generator.</summary>
        public SN76489_Sound Sound { get; }

        /// <summary>Gets the SDL display surface.</summary>
        public Display? Display { get; private set; }

        /// <summary>Gets the loaded OS ROM path.</summary>
        public string OsRomPath { get; private set; } = string.Empty;

        /// <summary>Gets the loaded BASIC ROM path.</summary>
        public string BasicRomPath { get; private set; } = string.Empty;

        /// <summary>Gets the loaded DFS ROM path.</summary>
        public string DfsRomPath { get; private set; } = string.Empty;

        /// <summary>Gets the loaded AMX mouse ROM path when present.</summary>
        public string? AmxMouseRomPath { get; private set; }

        /// <summary>Initializes a new emulator coordinator.</summary>
        public Emulator()
        {
            Sound = new SN76489_Sound();
            systemVia = new SystemVia(Sound);
            hostFilingSystem = new HostFilingSystem(Memory);
            hostFilingSystem.QueueKeyboardText = QueueKeyboardText;
            hostFilingSystem.QueueKeyboardScript = QueueExecScript;
            hostFilingSystem.BreakCommandObserved = QueueBreakContinuation;
            hostFilingSystem.DiscImageLoadActivity = PulseHostDiscActivityLed;
            hostFilingSystem.MouseEnabledChanged = SetMouseEnabled;
            discController = new Intel8271_Disk();
            Video = new HD6845_Video(Memory.Memory);
            systemVia.ExternalVsyncLineEnabled = true;
            Video.VsyncChanged += systemVia.SetVsyncLine;
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
        /// <param name="createDisplay">The create display value.</param>
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
            }

            Sound.Start();

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
                DrainHostAnalogJoystickInput(Display);
                UpdateHostMouseInput(Display);
                DrainHostKeyboardInput(Display);
                QueuePendingBootScriptLine();
                RenderDisplayFrame(Display);
                DrainHostScreenshotRequests(Display);
                DrainHostTraceToggleRequests(Display);
                Display.DiscMounted = discController.HasMountedDisc;
                Display.DiscActivityLedActive = discController.ReadLedActive || Stopwatch.GetTimestamp() < hostDiscActivityLedUntilTicks;
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
        /// <param name="autoRunDisc">Whether mounted disc images should queue their boot script.</param>
        public void MountHostFile(string path, bool autoRunDisc = true)
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
                if (autoRunDisc)
                    QueueMountedDiscAutoRun();
                return;
            }

            hostFilingSystem.Mount(path);

            Console.WriteLine($"Mounted:    {hostFilingSystem.MountedFileName}");
        }

        /// <summary>Queues and returns a host mount failure message.</summary>
        /// <param name="path">The requested host path.</param>
        /// <param name="exception">The mount exception.</param>
        /// <returns>The formatted message.</returns>
        public string QueueHostMountFailure(string path, Exception exception)
        {
            string displayPath = GetMountFailurePath(path);
            string message = exception is FileNotFoundException
                ? $"File does not exist at: {displayPath}"
                : $"Could not load file: {displayPath}";

            Display?.ShowNotification(
                exception is FileNotFoundException ? "File not found" : "Could not load file",
                displayPath);

            return message;
        }

        /// <summary>Queues the mounted disc boot command or boot script.</summary>
        public void QueueMountedDiscAutoRun()
        {
            if (discController.TryGetBootExecScript(out string? bootScript) && bootScript is not null)
            {
                QueueBootScript("*EXEC !BOOT");
                return;
            }

            if (!string.IsNullOrWhiteSpace(discController.AutoLoadCommand))
                QueueBootScript(discController.AutoLoadCommand);
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
            discController.StopTrace();
            Sound.Dispose();
            Display?.Dispose();
        }

        /// <summary>Runs the background CPU execution loop.</summary>
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

        /// <summary>Starts the background CPU thread.</summary>
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

        /// <summary>Stops the background CPU thread.</summary>
        private void StopCpu()
        {
            Cpu.Stop();

            if (cpuThread is not null && cpuThread.IsAlive)
                cpuThread.Join(TimeSpan.FromSeconds(2));

            cpuThread = null;
        }

        /// <summary>Resets video, sound, VIA, disc, ADC, and host input state for a BBC reset.</summary>
        private void ResetDeviceState()
        {
            Video.Reset();
            Sound.Reset();
            systemVia.Reset();
            userVia.Reset();
            systemVia.ExternalVsyncLineEnabled = true;
            Video.SetScreenMemoryWindow(systemVia.CurrentScreenMemoryWindow);
            discController.Reset();
            hostDiscActivityLedUntilTicks = 0;
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
            mouseEnabled = false;
            mousePositionInitialized = false;
            Display?.SetRelativeMouseMode(false);
            UpdateJoystickInputs();
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

        /// <summary>Copies the current video frame into the host display framebuffer.</summary>
        /// <param name="display">The target display.</param>
        private void RenderDisplayFrame(Display display)
        {
            Video.Render(display);
        }

        /// <summary>Extends the host disc activity LED pulse.</summary>
        private void PulseHostDiscActivityLed()
        {
            long pulseTicks = (long)HostDiscActivityLedMilliseconds * Stopwatch.Frequency / 1000;
            Interlocked.Exchange(ref hostDiscActivityLedUntilTicks, Stopwatch.GetTimestamp() + pulseTicks);
        }

        /// <summary>Ticks all clocked emulator devices for the CPU cycles just executed.</summary>
        /// <param name="cycles">The number of emulated CPU cycles.</param>
        private void AdvanceDeviceCycles(int cycles)
        {
            Sound.Tick(cycles);
            systemVia.Tick(cycles);
            Video.Tick(cycles);
            userVia.Tick(cycles);
            discController.Tick(cycles);
            adc.Tick(cycles);
            TickCapsLockTap(cycles);

            UpdateCpuIrqLine();
        }

        /// <summary>Pulses the BBC Caps Lock matrix key to mirror host Caps Lock changes.</summary>
        private void StartCapsLockTap()
        {
            capsLockTapPulseCycles = CapsLockTapPulseCycles;
            capsLockTapPressed = true;
            systemVia.SetKeyState(BbcCapsLockKey, true);
        }

        /// <summary>Counts down and releases the synthetic BBC Caps Lock key press.</summary>
        /// <param name="cycles">The number of emulated CPU cycles.</param>
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

        /// <summary>Refreshes CPU IRQ line after related emulator state changes.</summary>
        private void UpdateCpuIrqLine()
        {
            Cpu.SetIrqLine(systemVia.IrqAsserted || userVia.IrqAsserted);
        }

        /// <summary>Intercepts MOS firmware entry points that the host can service directly.</summary>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private bool HandleHostFirmwareHooks()
        {
            return TryHandleSidewaysRomLanguageCommand()
                || hostFilingSystem.TryHandleOsword(Cpu)
                || hostFilingSystem.TryHandleOsfile(Cpu)
                || hostFilingSystem.TryHandleOscli(Cpu)
                || hostFilingSystem.TryHandleFscv(Cpu)
                || TryHandleOsbyte();
        }

        /// <summary>Attempts to handle sideways ROM language command.</summary>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Checks whether a sideways ROM bank advertises a language entry point.</summary>
        /// <param name="bank">The bank value.</param>
        /// <returns>True when language rom is true; otherwise, false.</returns>
        private bool IsLanguageRom(int bank)
        {
            if (bank < 0 || bank >= SidewaysRomBanks)
                return false;

            int typeOffset = bank * RomSize + 6;
            return (sidewaysRoms[typeOffset] & 0x40) != 0;
        }

        /// <summary>Checks whether the CPU is currently executing an OSCLI entry point.</summary>
        /// <param name="pc">The PC value.</param>
        /// <returns>True when cli entry point is true; otherwise, false.</returns>
        private bool IsCliEntryPoint(ushort pc)
        {
            return pc == OscliEntry || pc == ReadWord(CliVector);
        }

        /// <summary>Intercepts OSBYTE calls for keyboard scanning, Escape, and analogue input shortcuts.</summary>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
        private bool TryHandleOsbyte()
        {
            if ((Cpu.registers.PC & 0xFFFF) != 0xFFF4)
                return false;

            if (Cpu.registers.A == 0x80)
            {
                // Negative ADVAL channels report MOS sound-buffer state. Let the OS handle
                // those; the host shortcut is only for the analogue joystick/fire channels.
                if ((Cpu.registers.X & 0x80) != 0)
                    return false;

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

            if (Cpu.registers.Y != 0xFF)
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

        /// <summary>Scans the BBC keyboard matrix for an INKEY request.</summary>
        /// <param name="scanKey">The scan key value.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Updates the CPU Z and N flags from an intercepted firmware result byte.</summary>
        /// <param name="result">The result value.</param>
        private void SetFirmwareResultFlags(byte result)
        {
            Cpu.registers.Flags.Z = result == 0;
            Cpu.registers.Flags.N = (result & 0x80) != 0;
        }

        /// <summary>Returns the emulated analogue channel value in the MOS ADVAL X/Y byte format.</summary>
        /// <param name="channel">The channel value.</param>
        /// <param name="x">The low result byte value.</param>
        /// <param name="y">The high result byte value.</param>
        private void ReadAdval(byte channel, out byte x, out byte y)
        {
            GetJoystickAxisValues(out ushort xAxis, out ushort yAxis);
            ushort value = channel switch
            {
                0 => joystickState.Fire ? (ushort)0x0001 : (ushort)0x0000,
                1 => xAxis,
                2 => yAxis,
                _ => 0x8000
            };

            x = (byte)value;
            y = (byte)(value >> 8);
        }

        /// <summary>Attempts to map negative inkey code.</summary>
        /// <param name="code">The code value.</param>
        /// <param name="internalKey">The BBC keyboard matrix key.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Restores CPU state for from firmware subroutine.</summary>
        private void ReturnFromFirmwareSubroutine()
        {
            byte lo = Memory.Memory[0x0100 + ((Cpu.registers.S + 1) & 0xFF)];
            byte hi = Memory.Memory[0x0100 + ((Cpu.registers.S + 2) & 0xFF)];
            Cpu.registers.S += 2;
            Cpu.registers.PC = (ushort)(((hi << 8) | lo) + 1);
        }

        /// <summary>Reads OS string from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The string read from emulated memory or host data.</returns>
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

        /// <summary>Reads a little-endian 16-bit word from BBC memory.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private ushort ReadWord(ushort address)
        {
            return (ushort)(Memory.Memory[address & 0xFFFF] | (Memory.Memory[(address + 1) & 0xFFFF] << 8));
        }

        /// <summary>Extracts the first token from a star command line.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>The normalized name.</returns>
        private static string GetCommandName(string command)
        {
            int separator = command.IndexOfAny([' ', '\t', '\r']);
            return separator < 0 ? command : command[..separator];
        }

        /// <summary>Matches a star command token against a sideways ROM title, including abbreviated commands.</summary>
        /// <param name="commandName">The command name value.</param>
        /// <param name="romTitle">The ROM title value.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Reads the title string from a sideways ROM service header.</summary>
        /// <param name="bank">The bank value.</param>
        /// <returns>The string read from emulated memory or host data.</returns>
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

        /// <summary>Consumes disc or file mount requests from the display event queue.</summary>
        /// <param name="display">The target display.</param>
        private void DrainHostDiscLoads(Display display)
        {
            discLoadScratch.Clear();
            display.DrainDiscLoads(discLoadScratch);

            foreach (string path in discLoadScratch)
            {
                try
                {
                    MountHostFile(path);
                }
                catch (Exception ex) when (ex is FileNotFoundException
                    or DirectoryNotFoundException
                    or UnauthorizedAccessException
                    or IOException
                    or InvalidOperationException)
                {
                    string message = QueueHostMountFailure(path, ex);
                    Console.WriteLine(message);
                }
            }
        }

        /// <summary>Consumes screenshot requests and writes display PNG captures.</summary>
        /// <param name="display">The target display.</param>
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

        /// <summary>Consumes trace toggle requests and starts or stops disc controller tracing.</summary>
        /// <param name="display">The target display.</param>
        private void DrainHostTraceToggleRequests(Display display)
        {
            int count = display.DrainTraceToggleRequests();
            for (int i = 0; i < count; i++)
            {
                if (discController.TraceEnabled)
                {
                    string? path = discController.StopTrace();
                    Console.WriteLine($"8271 trace stopped: {path}");
                }
                else
                {
                    string path = Path.Combine(Environment.CurrentDirectory, "bbc-8271-trace.log");
                    discController.StartTrace(path);
                    Console.WriteLine($"8271 trace started: {path}");
                }
            }
        }

        /// <summary>Consumes host text input and feeds it into the BBC keyboard buffer.</summary>
        /// <param name="display">The target display.</param>
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

        /// <summary>Consumes host key up/down events and applies them to the BBC keyboard matrix.</summary>
        /// <param name="display">The target display.</param>
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

        /// <summary>Applies a host key transition to the BBC keyboard matrix while preserving minimum press duration.</summary>
        /// <param name="change">The change value.</param>
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

        /// <summary>Releases matrix keys whose minimum host press duration has expired.</summary>
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

        /// <summary>Consumes host joystick key changes and updates the emulated analogue joystick state.</summary>
        /// <param name="display">The target display.</param>
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

            UpdateJoystickInputs();
        }

        /// <summary>Consumes host analogue joystick changes and updates ADC joystick axes.</summary>
        /// <param name="display">The target display.</param>
        private void DrainHostAnalogJoystickInput(Display display)
        {
            int count = display.DrainAnalogJoystickChanges(analogJoystickChangeScratch);
            if (count == 0)
                return;

            for (int i = 0; i < count; i++)
            {
                HostAnalogJoystickChange change = analogJoystickChangeScratch[i];
                switch (change.Axis)
                {
                    case JoystickAxis.X:
                        joystickState.AnalogX = SdlAxisToAdcValue(change.Value);
                        joystickState.HasAnalogX = true;
                        break;

                    case JoystickAxis.Y:
                        joystickState.AnalogY = SdlAxisToAdcValue(change.Value);
                        joystickState.HasAnalogY = true;
                        break;
                }
            }

            UpdateJoystickInputs();
        }

        /// <summary>Enables or disables the emulated mouse interface.</summary>
        /// <param name="enabled">Whether mouse input should be exposed to BBC software.</param>
        private void SetMouseEnabled(bool enabled)
        {
            mouseEnabled = enabled;
            mousePositionInitialized = false;
            Display?.SetRelativeMouseMode(enabled);
            if (!enabled)
                UpdateJoystickInputs();
        }

        /// <summary>Maps host mouse state into the BBC mouse state used by Repton's editor.</summary>
        /// <param name="display">The target display.</param>
        private void UpdateHostMouseInput(Display display)
        {
            if (!mouseEnabled)
                return;

            HostMouseState mouse = display.GetMouseState();
            byte x = (byte)Math.Clamp(mouse.X * 160 / Math.Max(1, display.Width - 1), 0, 0x9F);
            byte y = (byte)Math.Clamp(mouse.Y * 256 / Math.Max(1, display.Height), 0, 0xFF);
            Memory.Memory[0x009A] = x;
            Memory.Memory[0x009B] = y;

            byte activeLowButtons = (byte)(0x07 ^ (mouse.Buttons & 0x07));
            int deltaX = 0;
            int deltaY = 0;
            if (display.RelativeMouseMode)
            {
                deltaX = Math.Clamp(mouse.DeltaX, -MaxAmxMouseStepsPerFrame, MaxAmxMouseStepsPerFrame);
                deltaY = Math.Clamp(mouse.DeltaY, -MaxAmxMouseStepsPerFrame, MaxAmxMouseStepsPerFrame);
            }
            else if (mousePositionInitialized)
            {
                deltaX = Math.Clamp(x - lastMouseX, -MaxAmxMouseStepsPerFrame, MaxAmxMouseStepsPerFrame);
                deltaY = Math.Clamp(y - lastMouseY, -MaxAmxMouseStepsPerFrame, MaxAmxMouseStepsPerFrame);
            }

            lastMouseX = x;
            lastMouseY = y;
            mousePositionInitialized = true;
            userVia.SetMouseInput(activeLowButtons, -deltaY, deltaX);
            UpdateCpuIrqLine();
            if (MouseTraceEnabled && (deltaX != 0 || deltaY != 0 || mouse.Buttons != 0))
                Console.WriteLine($"MOUSE x={x} y={y} buttons=${mouse.Buttons:X2} dx={deltaX} dy={deltaY}");
        }

        /// <summary>Refreshes joystick hardware inputs after related emulator state changes.</summary>
        private void UpdateJoystickInputs()
        {
            UpdateAdcChannels();
            if (!mouseEnabled)
                userVia.SetSwitchedJoystickInput(
                    joystickState.Left,
                    joystickState.Right,
                    joystickState.Up,
                    joystickState.Down,
                    joystickState.Fire);
        }

        /// <summary>Refreshes ADC channels after related emulator state changes.</summary>
        private void UpdateAdcChannels()
        {
            // µPD7002 channels follow the BBC hardware convention: analogue axes live on
            // channels 0-3. The MOS ADVAL(0) fire shortcut remains handled separately.
            GetJoystickAxisValues(out ushort xAxis, out ushort yAxis);
            adc.SetChannel(0, xAxis);
            adc.SetChannel(1, yAxis);
            adc.SetChannel(2, xAxis);
            adc.SetChannel(3, yAxis);
        }

        /// <summary>Gets the current BBC analogue joystick axis values.</summary>
        /// <param name="xAxis">The X-axis ADC value.</param>
        /// <param name="yAxis">The Y-axis ADC value.</param>
        private void GetJoystickAxisValues(out ushort xAxis, out ushort yAxis)
        {
            xAxis = joystickState.Left ? (ushort)0xFFFF
                  : joystickState.Right ? (ushort)0x0000
                  : joystickState.HasAnalogX ? joystickState.AnalogX
                  : (ushort)0x8000;
            yAxis = joystickState.Up ? (ushort)0xFFFF
                  : joystickState.Down ? (ushort)0x0000
                  : joystickState.HasAnalogY ? joystickState.AnalogY
                  : (ushort)0x8000;
        }

        /// <summary>Converts a signed SDL joystick axis to the BBC ADC range.</summary>
        /// <param name="value">The signed SDL axis value.</param>
        /// <returns>The BBC ADC value.</returns>
        private static ushort SdlAxisToAdcValue(short value)
        {
            int normalized = value == short.MinValue ? -32767 : value;
            int adcValue = 0x8000 - normalized;
            return (ushort)Math.Clamp(adcValue, 0, 0xFFFF);
        }

        /// <summary>Consumes host break-key requests and schedules a BBC BREAK reset.</summary>
        /// <param name="display">The target display.</param>
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

            Array.Fill(display.FrameBuffer, DisplayBlack);
            display.Present();
            Cpu.RequestReset();
        }

        /// <summary>Handles host Escape input as either BREAK cancellation or a BBC Escape condition.</summary>
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

        /// <summary>Sets the MOS Escape state and marks the Escape key as active.</summary>
        private void TriggerEscapeCondition()
        {
            Memory.Memory[EscapeFlag] |= EscapePendingFlag;
        }

        /// <summary>Queues keyboard text.</summary>
        /// <param name="text">The text.</param>
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

        /// <summary>Queues boot script.</summary>
        /// <param name="script">The boot script text.</param>
        private void QueueBootScript(string script)
        {
            breakContinuationQueued = false;
            QueueKeyboardScript(script, BootScriptInitialDelayMilliseconds);
        }

        /// <summary>Queues an EXEC script as paced keyboard input.</summary>
        /// <param name="script">The script text.</param>
        private void QueueExecScript(string script)
        {
            breakContinuationQueued = false;
            QueueKeyboardScript(script, ExecScriptInitialDelayMilliseconds);
        }

        /// <summary>Queues the soft-key continuation after an observed BREAK command.</summary>
        /// <param name="script">The decoded soft-key script.</param>
        private void QueueBreakContinuation(string? script)
        {
            if (breakContinuationQueued || string.IsNullOrWhiteSpace(script))
                return;

            QueueKeyboardScript(script, BootScriptInitialDelayMilliseconds);
            breakContinuationQueued = true;
        }

        /// <summary>Queues keyboard script text.</summary>
        /// <param name="script">The script text.</param>
        /// <param name="initialDelayMilliseconds">Initial delay before typing the first line.</param>
        private void QueueKeyboardScript(string script, int initialDelayMilliseconds)
        {
            pendingBootScriptLines.Clear();
            foreach (string line in script.Replace('\n', '\r').Split('\r'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    pendingBootScriptLines.Enqueue(trimmed);
            }

            nextBootScriptLineAtTicks = Stopwatch.GetTimestamp() + (initialDelayMilliseconds * Stopwatch.Frequency / 1000);
        }

        /// <summary>Queues pending boot script line.</summary>
        private void QueuePendingBootScriptLine()
        {
            if (pendingBootScriptLines.Count == 0 || pendingKeyboardInput.Count > 0)
                return;

            long now = Stopwatch.GetTimestamp();
            if (now < keyboardInputEnabledAtTicks || now < nextBootScriptLineAtTicks || !IsKeyboardBufferEmpty())
                return;

            string line = pendingBootScriptLines.Dequeue();
            QueueKeyboardText(line + "\r");
            if (pendingBootScriptLines.Count == 0)
                breakContinuationQueued = false;

            nextBootScriptLineAtTicks = now + (KeyboardScriptLineDelayMilliseconds * Stopwatch.Frequency / 1000);
        }

        /// <summary>Feeds queued host characters into the MOS keyboard buffer while space is available.</summary>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Checks the MOS keyboard buffer read/write pointers for pending characters.</summary>
        /// <returns>True when keyboard buffer empty is true; otherwise, false.</returns>
        private bool IsKeyboardBufferEmpty()
        {
            return Memory.Memory[KeyboardBufferStartIndex] == Memory.Memory[KeyboardBufferEndIndex];
        }

        /// <summary>Attempts to insert keyboard buffer character.</summary>
        /// <param name="character">The character value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Advances a MOS keyboard buffer pointer with BBC buffer wrapping.</summary>
        /// <param name="offset">The buffer or image offset.</param>
        /// <returns>The resulting value.</returns>
        private static byte NextKeyboardBufferOffset(byte offset)
        {
            return offset >= (KeyboardBufferEnd & 0xFF)
                ? (byte)(KeyboardBufferStart & 0xFF)
                : (byte)(offset + 1);
        }

        /// <summary>Creates a timestamped path for the next host screenshot capture.</summary>
        /// <returns>The resolved host path.</returns>
        private static string CreateScreenshotPath()
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "Screenshots");
            string fileName = $"bbc-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            return Path.Combine(directory, fileName);
        }

        /// <summary>Gets the host path associated with a mount failure.</summary>
        /// <param name="path">The requested host path.</param>
        /// <returns>The path to display.</returns>
        private static string GetMountFailurePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "(empty path)" : path;
        }

        private struct JoystickState
        {
            public bool Left;
            public bool Right;
            public bool Up;
            public bool Down;
            public bool Fire;
            public ushort AnalogX;
            public ushort AnalogY;
            public bool HasAnalogX;
            public bool HasAnalogY;
        }

        /// <summary>Sleeps or spins until the requested stopwatch deadline is reached.</summary>
        /// <param name="deadlineTicks">The stopwatch deadline tick value.</param>
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

        /// <summary>Loads OS, BASIC, and DFS ROM images into the emulator address space.</summary>
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
            AmxMouseRomPath = Path.Combine(romRoot, AmxMouseRomFileName);

            ValidateRom(OsRomPath, OsRomMarker, RomSize);
            ValidateRom(BasicRomPath, BasicRomMarker, RomSize);
            ValidateRom(DfsRomPath, DfsRomMarker, RomSize / 2, RomSize);
            if (File.Exists(AmxMouseRomPath))
                ValidateRom(AmxMouseRomPath, AmxMouseRomMarker, RomSize);
            else
                AmxMouseRomPath = null;

            Memory.Load(OsRomStart, File.ReadAllBytes(OsRomPath));

            Array.Fill(sidewaysRoms, (byte)0xFF);
            LoadSidewaysRomBank(BasicRomPath, BasicRomBank);
            LoadSidewaysRomBank(DfsRomPath, DfsRomBank);
            amxMouseRomLoaded = AmxMouseRomPath is not null;
            hostFilingSystem.MouseCommandFallbackEnabled = !amxMouseRomLoaded;
            if (amxMouseRomLoaded)
                LoadSidewaysRomBank(AmxMouseRomPath!, AmxMouseRomBank);
        }

        /// <summary>Copies a ROM image into the selected sideways ROM bank, mirroring short images.</summary>
        /// <param name="path">The host file path.</param>
        /// <param name="bank">The bank value.</param>
        private void LoadSidewaysRomBank(string path, int bank)
        {
            byte[] rom = File.ReadAllBytes(path);
            int bankOffset = bank * RomSize;

            for (int i = 0; i < RomSize; i++)
                sidewaysRoms[bankOffset + i] = rom[i % rom.Length];
        }

        /// <summary>Validates ROM.</summary>
        /// <param name="path">The host file path.</param>
        /// <param name="marker">The marker value.</param>
        /// <param name="exactSize">The exact size value.</param>
        private static void ValidateRom(string path, string marker, int exactSize)
        {
            ValidateRom(path, marker, exactSize, exactSize);
        }

        /// <summary>Validates ROM.</summary>
        /// <param name="path">The host file path.</param>
        /// <param name="marker">The marker value.</param>
        /// <param name="minimumSize">The minimum size value.</param>
        /// <param name="maximumSize">The maximum size value.</param>
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

        /// <summary>Scans ROM data for an identifying ASCII marker.</summary>
        /// <param name="data">The data byte or buffer.</param>
        /// <param name="marker">The marker value.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private static bool ContainsAscii(ReadOnlySpan<byte> data, string marker)
        {
            ReadOnlySpan<byte> needle = Encoding.ASCII.GetBytes(marker);
            return data.IndexOf(needle) >= 0;
        }

        /// <summary>Installs CPU bus read and write callbacks for ROM, RAM, and SHEILA address ranges.</summary>
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

        /// <summary>Checks whether the selected sideways bank is writable RAM.</summary>
        /// <param name="bank">The bank value.</param>
        /// <returns>True when sideways ram bank is true; otherwise, false.</returns>
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
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The computed value.</returns>
        private int ComputeBusStretchCycles(ulong address)
        {
            ushort addr = (ushort)(address & 0xFFFF);
            if (addr >= IoStart && addr <= IoEnd)
                return 1;
            return 0;
        }

        /// <summary>Reads sideways ROM from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadSidewaysRom(ushort address)
        {
            int bankOffset = selectedSidewaysRom * RomSize;
            int romOffset = address - SidewaysRomStart;
            return sidewaysRoms[bankOffset + romOffset];
        }

        /// <summary>Dispatches a SHEILA read to the emulated device mapped at the supplied address.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadSheila(ushort address)
        {
            if (HD6845_Video.IsSheilaAddress(address))
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

            if (Intel8271_Disk.IsAddress(address))
                return discController.Read(address);

            if (CassetteInterface.IsAddress(address))
                return cassetteInterface.Read(address);

            if (uPD7002_ADC.IsAddress(address))
                return adc.Read(address);

            return address switch
            {
                0xFE30 => (byte)selectedSidewaysRom,
                _ => 0x00
            };
        }

        /// <summary>Dispatches a SHEILA write to the emulated device mapped at the supplied address.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The input value.</param>
        private void WriteSheila(ushort address, byte value)
        {
            if (HD6845_Video.IsSheilaAddress(address))
            {
                Video.WriteSheila(address, value);
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

            if (Intel8271_Disk.IsAddress(address))
            {
                discController.Write(address, value);
                return;
            }

            if (CassetteInterface.IsAddress(address))
            {
                cassetteInterface.Write(address, value);
                return;
            }

            if (uPD7002_ADC.IsAddress(address))
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

        /// <summary>Checks whether a host path looks like a DFS disc image.</summary>
        /// <param name="path">The host file path.</param>
        /// <returns>True when disc image path is true; otherwise, false.</returns>
        private static bool IsDiscImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".ssd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dsd", StringComparison.OrdinalIgnoreCase);
        }
    }
}
