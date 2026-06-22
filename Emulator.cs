// ============================================================================
// Project:     BBC
// File:        Emulator.cs
// Description: BBC Model B host wiring: MOS/BASIC/DFS ROMs, SHEILA devices,
//              keyboard input, disc mounting, video, and sound.
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
    /// Brings the BBC hardware model together around the MOS-visible memory map.
    /// </summary>
    public sealed class Emulator : IDisposable
    {

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
            emulator.ConfigureStartupSpeedScale(options.SpeedScale);

            Console.WriteLine("BBC Model B emulator initialised.");
            Console.WriteLine($"OS ROM:     ${Emulator.OsRomStart:X4}-${Emulator.OsRomEnd:X4}");
            Console.WriteLine($"BASIC ROM:  ${Emulator.SidewaysRomStart:X4}-${Emulator.SidewaysRomEnd:X4}");
            Console.WriteLine($"DFS ROM:    bank {Emulator.DfsRomBank}");
            if (emulator.AmxMouseRomPath is not null)
                Console.WriteLine($"AMX ROM:    bank {Emulator.AmxMouseRomBank}");
            Console.WriteLine($"Reset PC:   ${emulator.Cpu.registers.PC:X4}");

            foreach (MountRequest mount in options.Mounts)
            {
                try
                {
                    emulator.MountHostFile(mount.Path, options.AutoRunDisc, mount.Drive);
                }
                catch (Exception ex) when (IsUserMountException(ex))
                {
                    string message = emulator.QueueHostMountFailure(mount.Path, ex);
                    Console.WriteLine(message);
                }
            }

            if (options.HeadlessMilliseconds > 0)
                emulator.RunHeadless(TimeSpan.FromMilliseconds(options.HeadlessMilliseconds));
            else
                emulator.Run();
        }

        private static StartupOptions ParseStartupOptions(string[] args)
        {
            int headlessMilliseconds = 0;
            List<MountRequest> mounts = new List<MountRequest>();
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

                    mounts.Add(new MountRequest(args[++i], null));
                    continue;
                }

                if (TryParseDriveOption(args[i], "--drive", out int discDrive))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"{args[i]} requires a path.");

                    mounts.Add(new MountRequest(args[++i], discDrive));
                    continue;
                }

                if (TryParseDriveOption(args[i], "--blank-disc", out int blankDiscDrive)
                    || TryParseDriveOption(args[i], "--blank-disk", out blankDiscDrive)
                    || TryParseDriveOption(args[i], "--blank-drive", out blankDiscDrive))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"{args[i]} requires a path.");

                    string blankPath = args[++i];
                    CreateBlankDfsImage(blankPath);
                    mounts.Add(new MountRequest(blankPath, blankDiscDrive));
                    continue;
                }

                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    mounts.Add(new MountRequest(args[i], null));
            }

            return new StartupOptions(headlessMilliseconds, mounts, printAutoLoadPath, speedScale, autoRunDisc);
        }

        private static bool TryParseDriveOption(string option, string prefix, out int drive)
        {
            drive = 0;
            if (!option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = option[prefix.Length..];
            return suffix.Length == 1 && char.IsDigit(suffix[0]) && int.TryParse(suffix, out drive) && drive is >= 0 and <= 3;
        }

        private static void CreateBlankDfsImage(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return;

            byte[] image = new byte[80 * 10 * 256];
            string title = Path.GetFileNameWithoutExtension(fullPath).ToUpperInvariant();
            if (title.Length > 12)
                title = title[..12];

            for (int i = 0; i < title.Length && i < 8; i++)
                image[i] = (byte)title[i];
            for (int i = 8; i < title.Length; i++)
                image[0x100 + i - 8] = (byte)title[i];

            image[0x104] = 1;
            image[0x105] = 0;
            image[0x106] = 0x03;
            image[0x107] = 0x20;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllBytes(fullPath, image);
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

        private static bool IsUserMountException(Exception exception)
        {
            return exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or InvalidOperationException;
        }

        private readonly record struct MountRequest(string Path, int? Drive);

        private readonly record struct StartupOptions(int HeadlessMilliseconds, IReadOnlyList<MountRequest> Mounts, string? PrintAutoLoadPath, double SpeedScale, bool AutoRunDisc);
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
        private readonly System6522Via systemVia;
        private readonly User6522Via userVia = new User6522Via();
        private readonly TapeACIAStub tapeAciaStub = new TapeACIAStub();
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
        private double requestedStartupSpeedScale = 1.0;
        private bool startupSpeedScaleApplied = true;
        private int capsLockTapPulseCycles;
        private bool capsLockTapPressed;
        private bool hostCapsLockState;
        private bool bbcCapsLockState = true;

        public FlatMemoryBus Memory { get; } = new FlatMemoryBus();

        public CPU_6502 Cpu { get; }

        public HD6845_Video Video { get; }

        public SN76489_Sound Sound { get; }

        public Display? Display { get; private set; }

        public string OsRomPath { get; private set; } = string.Empty;

        public string BasicRomPath { get; private set; } = string.Empty;

        public string DfsRomPath { get; private set; } = string.Empty;

        public string? AmxMouseRomPath { get; private set; }

        public Emulator()
        {
            Sound = new SN76489_Sound();
            systemVia = new System6522Via(Sound);
            hostFilingSystem = new HostFilingSystem(Memory);
            hostFilingSystem.QueueKeyboardText = QueueKeyboardText;
            hostFilingSystem.QueueKeyboardScript = QueueExecScript;
            hostFilingSystem.BreakCommandObserved = QueueBreakContinuation;
            hostFilingSystem.MouseEnabledChanged = SetMouseEnabled;
            discController = new Intel8271_Disk();
            Video = new HD6845_Video(Memory.Memory);
            systemVia.ExternalVsyncLineEnabled = true;
            Video.VsyncChanged += systemVia.SetVsyncLine;
            systemVia.ScreenMemoryWindowChanged += Video.SetScreenMemoryWindow;
            Cpu = new CPU_6502(Memory, CpuClockHz);
            Cpu.NmiLineAsserted = () => discController.NmiLineAsserted;
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

        /// <summary>MOS startup remains at real speed so the power-on path sounds and feels like a BBC.</summary>
        public void ConfigureStartupSpeedScale(double speedScale)
        {
            requestedStartupSpeedScale = Math.Clamp(speedScale, 0.01, 4.0);
            startupSpeedScaleApplied = requestedStartupSpeedScale == 1.0;
            Cpu.SpeedScale = 1.0;
            Sound.ThrottleToPlayback = true;
        }

        /// <summary>MOS sees RAM, paged ROMs, and SHEILA devices in their BBC Model B address ranges.</summary>
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

        public void Run()
        {
            if (!initialised)
                Initialise(createDisplay: true);

            if (Display is null)
            {
                Display = new Display();
            }

            Sound.Start();
            Sound.QueuePowerOnBeep();

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
                ApplyStartupSpeedScaleIfReady(now);
                if (nextFrame < now - frameTicks * 4)
                    nextFrame = now + frameTicks;
            }

            StopCpu();
        }

        public void RunHeadless(TimeSpan duration)
        {
            if (!initialised)
                Initialise();

            StartCpu();

            long deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            keyboardInputEnabledAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

            while (Stopwatch.GetTimestamp() < deadline)
            {
                long now = Stopwatch.GetTimestamp();
                ApplyStartupSpeedScaleIfReady(now);
                if (now >= keyboardInputEnabledAtTicks)
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

        public void MountHostFile(string path, bool autoRunDisc = true, int? requestedDrive = null)
        {
            if (IsDiscImagePath(path))
            {
                if (discController.HasMountedDisc && discController.ImageDirty)
                {
                    if (discController.Flush())
                        Console.WriteLine($"Saved disc:   {discController.MountedFileName}");
                }

                discController.Mount(path, requestedDrive.GetValueOrDefault(0));
                hostFilingSystem.Unmount();

                Console.WriteLine($"Mounted DFS: {discController.MountedDriveSummary}");
                if (autoRunDisc && requestedDrive.GetValueOrDefault(0) == 0)
                    QueueMountedDiscAutoRun();
                return;
            }

            hostFilingSystem.Mount(path);

            Console.WriteLine($"Mounted:    {hostFilingSystem.MountedFileName}");
        }

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

        /// <summary>DFS boot option 3 uses EXEC !BOOT; otherwise the host queues the inferred BASIC load command.</summary>
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

        private void ApplyStartupSpeedScaleIfReady(long now)
        {
            if (startupSpeedScaleApplied || now < keyboardInputEnabledAtTicks)
                return;

            Cpu.SpeedScale = requestedStartupSpeedScale;
            Sound.ThrottleToPlayback = requestedStartupSpeedScale <= 1.0;
            startupSpeedScaleApplied = true;
        }

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

        private void RenderDisplayFrame(Display display)
        {
            Video.Render(display);
        }

        private void PulseHostDiscActivityLed()
        {
            long pulseTicks = (long)HostDiscActivityLedMilliseconds * Stopwatch.Frequency / 1000;
            Interlocked.Exchange(ref hostDiscActivityLedUntilTicks, Stopwatch.GetTimestamp() + pulseTicks);
        }

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

        private static bool TryMapNegativeInkeyCode(byte code, out byte internalKey)
        {
            internalKey = code switch
            {
                0xFF => 0x00,
                0xFE => 0x01,
                0xEF => 0x10,
                0xDE => 0x21,
                0xDD => 0x22,
                0xDC => 0x23,
                0xDB => 0x24,
                0xDA => 0x25,
                0xD9 => 0x26,
                0xD8 => 0x27,
                0xE7 => 0x28,
                0xCF => 0x30,
                0xCE => 0x31,
                0xCD => 0x32,
                0xCC => 0x33,
                0xCB => 0x34,
                0xCA => 0x35,
                0xC9 => 0x36,
                0xC8 => 0x37,
                0xC7 => 0x38,
                0xBF => 0x40,
                0xBE => 0x41,
                0xBD => 0x42,
                0xBC => 0x43,
                0xBB => 0x44,
                0xBA => 0x45,
                0xB9 => 0x46,
                0xB8 => 0x47,
                0xB7 => 0x48,
                0xB6 => 0x49,
                0xAE => 0x51,
                0xAD => 0x52,
                0xAC => 0x53,
                0xAB => 0x54,
                0xAA => 0x55,
                0xA9 => 0x56,
                0xA8 => 0x57,
                0xA7 => 0x58,
                0xA6 => 0x59,
                0x9F => 0x60,
                0x9E => 0x61,
                0x9D => 0x62,
                0x9C => 0x63,
                0x9B => 0x64,
                0x9A => 0x65,
                0x99 => 0x66,
                0x98 => 0x67,
                0x97 => 0x68,
                0x8F => 0x70,
                0xDF => 0x20,
                0x8E => 0x71,
                0x8D => 0x72,
                0x8C => 0x73,
                0xEB => 0x14,
                0x8B => 0x74,
                0x8A => 0x75,
                0xE9 => 0x16,
                0x89 => 0x76,
                0x88 => 0x77,
                0xE8 => 0x17,
                0xE6 => 0x18,
                0x87 => 0x78,
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

            UpdateJoystickInputs();
        }

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

        private void SetMouseEnabled(bool enabled)
        {
            mouseEnabled = enabled;
            mousePositionInitialized = false;
            Display?.SetRelativeMouseMode(enabled);
            if (!enabled)
                UpdateJoystickInputs();
        }

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

        private void UpdateAdcChannels()
        {
            GetJoystickAxisValues(out ushort xAxis, out ushort yAxis);
            adc.SetChannel(0, xAxis);
            adc.SetChannel(1, yAxis);
            adc.SetChannel(2, xAxis);
            adc.SetChannel(3, yAxis);
        }

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

        private static ushort SdlAxisToAdcValue(short value)
        {
            int normalized = value == short.MinValue ? -32767 : value;
            int adcValue = 0x8000 - normalized;
            return (ushort)Math.Clamp(adcValue, 0, 0xFFFF);
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

            Array.Fill(display.FrameBuffer, DisplayBlack);
            display.Present();
            Cpu.RequestReset();
        }

        private void HandleEscapeKeyPress()
        {
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
            breakContinuationQueued = false;
            QueueKeyboardScript(script, BootScriptInitialDelayMilliseconds);
        }

        private void QueueExecScript(string script)
        {
            breakContinuationQueued = false;
            QueueKeyboardScript(script, ExecScriptInitialDelayMilliseconds);
        }

        private void QueueBreakContinuation(string? script)
        {
            if (breakContinuationQueued || string.IsNullOrWhiteSpace(script))
                return;

            QueueKeyboardScript(script, BootScriptInitialDelayMilliseconds);
            breakContinuationQueued = true;
        }

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
            if (HD6845_Video.IsSheilaAddress(address))
                return Video.ReadSheila(address);

            if (System6522Via.IsAddress(address))
            {
                byte value = systemVia.Read(address);
                UpdateCpuIrqLine();
                return value;
            }

            if (User6522Via.IsAddress(address))
            {
                byte value = userVia.Read(address);
                UpdateCpuIrqLine();
                return value;
            }

            if (Intel8271_Disk.IsAddress(address))
                return discController.Read(address);

            if (TapeACIAStub.IsAddress(address))
                return tapeAciaStub.Read(address);

            if (uPD7002_ADC.IsAddress(address))
                return adc.Read(address);

            return address switch
            {
                0xFE30 => (byte)selectedSidewaysRom,
                _ => 0x00
            };
        }

        private void WriteSheila(ushort address, byte value)
        {
            if (HD6845_Video.IsSheilaAddress(address))
            {
                Video.WriteSheila(address, value);
                return;
            }

            if (System6522Via.IsAddress(address))
            {
                systemVia.Write(address, value);
                UpdateCpuIrqLine();
                return;
            }

            if (User6522Via.IsAddress(address))
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

            if (TapeACIAStub.IsAddress(address))
            {
                tapeAciaStub.Write(address, value);
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

        private static bool IsDiscImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".ssd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dsd", StringComparison.OrdinalIgnoreCase);
        }
    }
}
