// ============================================================================
// Project:     BBC
// File:        Emulator.cs
// Description: BBC Model B host wiring: MOS/BASIC/DFS ROMs, SHEILA devices,
//              keyboard input, disc mounting, video, and sound.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using BBC.CPU;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
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

            if (options.Tube6502)
                emulator.ConfigureTube6502(options.TubeHostRomPath, options.Tube6502RomPath);

            emulator.Initialise(createDisplay: options.HeadlessMilliseconds == 0);
            emulator.ConfigureStartupSpeedScale(options.SpeedScale);

            if (options.HayesModem)
                emulator.SetHayesModemEnabled(true, notify: false);

            foreach (MountRequest mount in options.Mounts)
            {
                if (mount.Drive.HasValue)
                    emulator.SetDiscDriveEnabled(mount.Drive.Value & 1, true);
                else if (IsTapeImagePath(mount.Path))
                    emulator.SetTapePlayerEnabled(true);
            }

            if (!string.IsNullOrWhiteSpace(options.TapePath))
                emulator.SetTapePlayerEnabled(true);

            emulator.startupLoadStatePath = options.LoadStatePath;

            Console.WriteLine("BBC Model B emulator initialised.");
            Console.WriteLine($"OS ROM:     ${Emulator.OsRomStart:X4}-${Emulator.OsRomEnd:X4}");
            Console.WriteLine($"BASIC ROM:  bank {Emulator.BasicRomBank} ({Path.GetFileName(emulator.BasicRomPath)})");
            Console.WriteLine($"DFS ROM:    bank {Emulator.DfsRomBank} ({Path.GetFileName(emulator.DfsRomPath)})");
            if (emulator.AmxMouseRomPath is not null)
                Console.WriteLine($"AMX ROM:    available ({Path.GetFileName(emulator.AmxMouseRomPath)})");
            Console.WriteLine($"Reset PC:   ${emulator.Cpu.registers.PC:X4}");
            if (emulator.tube6502 is not null)
                Console.WriteLine($"Tube 6502:  reset PC ${emulator.tube6502.Cpu.registers.PC:X4} ({Path.GetFileName(emulator.Tube6502RomPath)})");

            if (!string.IsNullOrWhiteSpace(options.LoadStatePath))
                Console.WriteLine($"Startup state: {options.LoadStatePath}");

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

            if (!string.IsNullOrWhiteSpace(options.TapePath))
            {
                try
                {
                    emulator.MountTapeFile(options.TapePath);
                }
                catch (Exception ex) when (IsUserMountException(ex))
                {
                    string message = emulator.QueueHostMountFailure(options.TapePath, ex);
                    Console.WriteLine(message);
                }
            }

            if (options.StartupCommands.Count > 0)
                emulator.QueueBootScript(string.Join('\r', options.StartupCommands));

            if (options.HeadlessMilliseconds > 0)
                emulator.RunHeadless(TimeSpan.FromMilliseconds(options.HeadlessMilliseconds));
            else
                emulator.Run();
        }

        private static StartupOptions ParseStartupOptions(string[] args)
        {
            int headlessMilliseconds = 0;
            List<MountRequest> mounts = new List<MountRequest>();
            List<string> createdBlankImages = new List<string>();
            List<string> startupCommands = new List<string>();
            string? printAutoLoadPath = null;
            string? loadStatePath = null;
            string? tapePath = null;
            double speedScale = 1.0;
            bool autoRunDisc = true;
            bool tube6502 = false;
            bool hayesModem = false;
            string? tubeHostRomPath = null;
            string? tube6502RomPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--tube-6502", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--tube-enable", StringComparison.OrdinalIgnoreCase))
                {
                    tube6502 = true;
                    continue;
                }

                if (string.Equals(args[i], "--modem", StringComparison.OrdinalIgnoreCase))
                {
                    hayesModem = true;
                    continue;
                }

                if (string.Equals(args[i], "--tube-host-rom", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--tube-host-rom requires a ROM path.");

                    tubeHostRomPath = args[++i];
                    continue;
                }

                if (string.Equals(args[i], "--tube-6502-rom", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--tube-6502-rom requires a ROM path.");

                    tube6502RomPath = args[++i];
                    continue;
                }

                if (string.Equals(args[i], "--print-autoload", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--print-autoload requires an SSD path.");

                    printAutoLoadPath = args[++i];
                    continue;
                }

                if (string.Equals(args[i], "--load-state", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--load-state requires a .sav path.");

                    loadStatePath = args[++i];
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

                if (string.Equals(args[i], "--type", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--type requires text to type into the BBC keyboard buffer.");

                    startupCommands.Add(args[++i]);
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

                if (string.Equals(args[i], "--tape", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--Tape requires a UEF path.");

                    tapePath = args[++i];
                    continue;
                }

                if (TryParseDriveOption(args[i], "--drive", out int discDrive))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"{args[i]} requires a path.");

                    mounts.Add(new MountRequest(args[++i], discDrive));
                    continue;
                }

                if (string.Equals(args[i], "--blank-ssd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--blank-dsd", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"{args[i]} requires a path.");

                    string blankPath = args[++i];
                    if (args[i - 1].EndsWith("dsd", StringComparison.OrdinalIgnoreCase))
                        CreateBlankDsdImage(blankPath);
                    else
                        CreateBlankSsdImage(blankPath);

                    createdBlankImages.Add(blankPath);
                    continue;
                }

                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    mounts.Add(new MountRequest(args[i], null));
            }

            foreach (string blankImage in createdBlankImages)
            {
                if (!MountsContainPath(mounts, blankImage))
                    mounts.Add(new MountRequest(blankImage, null));
            }

            return new StartupOptions(headlessMilliseconds, mounts, tapePath, printAutoLoadPath, loadStatePath, speedScale, autoRunDisc, tube6502, hayesModem, tubeHostRomPath, tube6502RomPath, startupCommands);
        }

        private static bool MountsContainPath(IEnumerable<MountRequest> mounts, string path)
        {
            string fullPath = Path.GetFullPath(path);
            return mounts.Any(mount => string.Equals(Path.GetFullPath(mount.Path), fullPath, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParseDriveOption(string option, string prefix, out int drive)
        {
            drive = 0;
            if (!option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = option[prefix.Length..];
            return suffix.Length == 1 && char.IsDigit(suffix[0]) && int.TryParse(suffix, out drive) && drive is >= 0 and <= 3;
        }

        private static void CreateBlankSsdImage(string path, bool overwrite = false)
        {
            string fullPath = Path.GetFullPath(path);
            if (!overwrite && File.Exists(fullPath))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllBytes(fullPath, CreateBlankDfsSide(path));
        }

        private static void CreateBlankDsdImage(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return;

            const int sectorsPerTrack = 10;
            const int sectorSize = 256;
            const int trackBytes = sectorsPerTrack * sectorSize;
            const int tracks = 80;

            byte[] side0 = CreateBlankDfsSide(path);
            byte[] side1 = CreateBlankDfsSide(path);
            byte[] image = new byte[side0.Length + side1.Length];
            for (int track = 0; track < tracks; track++)
            {
                int sideOffset = track * trackBytes;
                int imageOffset = track * trackBytes * 2;
                Array.Copy(side0, sideOffset, image, imageOffset, trackBytes);
                Array.Copy(side1, sideOffset, image, imageOffset + trackBytes, trackBytes);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllBytes(fullPath, image);
        }

        private static byte[] CreateBlankDfsSide(string path)
        {
            byte[] image = new byte[80 * 10 * 256];
            string title = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
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
            return image;
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
                or InvalidDataException
                or InvalidOperationException;
        }

        private static bool IsUserStateException(Exception exception)
        {
            return exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or InvalidDataException
                or EndOfStreamException
                or InvalidOperationException;
        }

        private readonly record struct MountRequest(string Path, int? Drive);

        private readonly record struct StartupOptions(
            int HeadlessMilliseconds,
            IReadOnlyList<MountRequest> Mounts,
            string? TapePath,
            string? PrintAutoLoadPath,
            string? LoadStatePath,
            double SpeedScale,
            bool AutoRunDisc,
            bool Tube6502,
            bool HayesModem,
            string? TubeHostRomPath,
            string? Tube6502RomPath,
            IReadOnlyList<string> StartupCommands);

        private sealed class RuntimeTracePcSample
        {
            public RuntimeTracePcSample(byte opcode)
            {
                Opcode = opcode;
            }

            public byte Opcode { get; }

            public int Count { get; set; }
        }

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
        private const string HiBasicRomFileName = "HiBASIC.rom";
        private const string DfsRomFileName = "DFS-0.9.rom";
        private const string TubeHostRomFileName = "DNFS302.rom";
        private const string Tube6502RomFileName = "6502tube_120.rom";
        private const string AmxMouseRomFileName = "AMXMSE331.rom";
        private const string BasicRomMarker = "BASIC\0(C)1982 Acorn";
        private const string HiBasicRomMarker = "BASIC\0(C)1983 Acorn";
        private const string DfsRomMarker = "DFS\0" + "0.90";
        private const string DnfsRomMarker = "DFS,NET";
        private const string Tube6502RomMarker = "6502 TUBE";
        private static readonly bool MouseTraceEnabled = Environment.GetEnvironmentVariable("BBC_MOUSE_TRACE") == "1";
        private static readonly bool TubeDebugEnabled = Environment.GetEnvironmentVariable("BBC_TUBE_DEBUG") == "1";
        private static readonly bool SerialPcTraceEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_PC_TRACE") == "1";
        private static readonly bool SerialPcTraceAutoEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE") == "1";
        private static readonly string? SerialPcTracePath = Environment.GetEnvironmentVariable("BBC_SERIAL_PC_TRACE_FILE")
            ?? (SerialPcTraceAutoEnabled ? SerialACIA.TracePath : null);
        private static readonly bool RuntimeTraceStartupEnabled = Environment.GetEnvironmentVariable("BBC_RUNTIME_TRACE") == "1";
        private static readonly string RuntimeTraceStartupPath = Environment.GetEnvironmentVariable("BBC_RUNTIME_TRACE_FILE") ?? "bbc-runtime-trace.log";
        private static readonly bool TapeAutoplayEnabled = Environment.GetEnvironmentVariable("BBC_TAPE_AUTOPLAY") == "1";
        private static readonly bool UserViaTraceEnabled = Environment.GetEnvironmentVariable("BBC_USER_VIA_TRACE") == "1";
        private static readonly string UserViaTracePath = Environment.GetEnvironmentVariable("BBC_USER_VIA_TRACE_FILE") ?? "bbc-user-via-trace.log";
        private static readonly bool ExileRamTraceEnabled = Environment.GetEnvironmentVariable("BBC_EXILE_RAM_TRACE") == "1";
        private static readonly string ExileRamTracePath = Environment.GetEnvironmentVariable("BBC_EXILE_RAM_TRACE_FILE") ?? "bbc-exile-ram-trace.log";
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
        private const int PausedFrameAdvanceCount = 10;
        private const int RuntimeTraceFirstInstructions = 4096;
        private const int RuntimeTraceHotPcInterval = 512;
        private const int RuntimeTraceTopPcCount = 8;
        private const uint DisplayBlack = 0xFF000000;

        private bool initialised;
        private Thread? cpuThread;
        private Exception? cpuException;
        private readonly byte[] inputScratch = new byte[64];
        private readonly HostKeyChange[] keyChangeScratch = new HostKeyChange[64];
        private readonly HostJoystickChange[] joystickChangeScratch = new HostJoystickChange[16];
        private readonly HostAnalogJoystickChange[] analogJoystickChangeScratch = new HostAnalogJoystickChange[16];
        private readonly BreakKeyPress[] breakScratch = new BreakKeyPress[4];
        private readonly List<HostDiscAction> discActionScratch = new List<HostDiscAction>();
        private readonly List<HostTapeAction> tapeActionScratch = new List<HostTapeAction>();
        private readonly List<HostStateAction> stateActionScratch = new List<HostStateAction>();
        private readonly List<HostRomAction> romActionScratch = new List<HostRomAction>();
        private readonly Queue<byte> pendingKeyboardInput = new Queue<byte>();
        private readonly Queue<string> pendingBootScriptLines = new Queue<string>();
        private readonly long[] matrixKeyPressedAtTicks = new long[128];
        private readonly long[] matrixKeyReleaseDueTicks = new long[128];
        private readonly bool[] matrixKeyReleasePending = new bool[128];
        private readonly byte[] sidewaysRoms = new byte[SidewaysRomBanks * RomSize];
        private readonly string?[] sidewaysRomPaths = new string?[SidewaysRomBanks];
        private readonly SidewaysRomSlot[] sidewaysRomSlots = new SidewaysRomSlot[SidewaysRomBanks];
        private int selectedSidewaysRom = BasicRomBank;
        private BreakKeyPress pendingBreak;
        private string? pendingBootExecScript;
        private bool breakContinuationQueued;
        private long nextBootScriptLineAtTicks;
        private readonly System6522Via systemVia;
        private readonly User6522Via userVia = new User6522Via();
        private readonly SerialACIA serialAcia = new SerialACIA();
        private readonly UefTape tape;
        private readonly uPD7002_ADC adc = new uPD7002_ADC();
        private readonly HostFilingSystem hostFilingSystem;
        private readonly Intel8271_Disk discController;
        private readonly TubeUla tubeUla = new TubeUla();
        private CoProcessor65C02? tube6502;
        private HayesModem? hayesModem;
        private bool tapePlayerEnabled;
        private bool drive0Enabled = true;
        private bool drive1Enabled;
        private string? configuredTubeHostRomPath;
        private string? configuredTube6502RomPath;
        private bool tube6502Configured;
        private bool tubeHostIrqAsserted;
        private readonly DiscDriveSound? discDriveSound;
        private JoystickState joystickState;
        private bool mouseEnabled;
        private bool amxMouseRomLoaded;
        private bool mousePositionInitialized;
        private bool emulationPaused;
        private byte lastMouseX;
        private byte lastMouseY;
        private long keyboardInputEnabledAtTicks;
        private long hostDiscActivityLedUntilTicks;
        private double requestedStartupSpeedScale = 1.0;
        private bool startupSpeedScaleApplied = true;
        private string? startupLoadStatePath;
        private int capsLockTapPulseCycles;
        private bool capsLockTapPressed;
        private bool hostCapsLockState;
        private bool bbcCapsLockState = true;
        private readonly object runtimeTraceLock = new object();
        private readonly Dictionary<ushort, RuntimeTracePcSample> runtimeTraceFramePcSamples = new Dictionary<ushort, RuntimeTracePcSample>();
        private StreamWriter? runtimeTraceWriter;
        private string? runtimeTracePath;
        private long runtimeTraceInstructionCount;
        private int runtimeTraceFrame;
        private StreamWriter? serialPcTraceWriter;
        private StreamWriter? userViaTraceWriter;
        private StreamWriter? exileRamTraceWriter;
        private string? lastSerialPcTraceLine;
        private const uint SaveStateMagic = 0x31535642; // BVS1
        private const int SaveStateVersion = 15;
        private int runtimeTraceActive;
        private long nextTubeDebugDumpTicks;
        private bool romManagerPauseActive;
        private bool romManagerPreviousPaused;
        private bool inputMapperPauseActive;
        private bool inputMapperPreviousPaused;
        private bool romPatternChangedWhileManagerOpen;

        public FlatMemoryBus Memory { get; } = new FlatMemoryBus();

        public CPU_6502 Cpu { get; }

        public HD6845_Video Video { get; }

        public SN76489_Sound Sound { get; }

        public Display? Display { get; private set; }

        public string OsRomPath { get; private set; } = string.Empty;

        public string BasicRomPath { get; private set; } = string.Empty;

        public string DfsRomPath { get; private set; } = string.Empty;

        public string? AmxMouseRomPath { get; private set; }

        public string? Tube6502RomPath { get; private set; }

        public Emulator()
        {
            Sound = new SN76489_Sound();
            discDriveSound = DiscDriveSound.TryLoadDefault();
            Sound.DiscDriveSound = discDriveSound;
            systemVia = new System6522Via(Sound);
            tape = new UefTape(serialAcia, Sound);
            hostFilingSystem = new HostFilingSystem(Memory);
            hostFilingSystem.QueueKeyboardText = QueueKeyboardText;
            hostFilingSystem.QueueKeyboardScript = QueueExecScript;
            hostFilingSystem.BreakCommandObserved = QueueBreakContinuation;
            hostFilingSystem.MouseEnabledChanged = SetMouseEnabled;
            discController = new Intel8271_Disk();
            discController.DriveMotorStarted += _ => discDriveSound?.MotorStarted();
            discController.DriveMotorStopped += _ => discDriveSound?.MotorStopped();
            discController.DriveSeek += (_, trackDelta) => discDriveSound?.Seek(trackDelta);

            tubeUla.HostIrqChanged += asserted =>
            {
                tubeHostIrqAsserted = asserted;
                UpdateCpuIrqLine();
            };
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
            serialAcia.ByteReceived += Cpu.SetOverflowInput;
            serialAcia.IrqChanged += _ => UpdateCpuIrqLine();
            adc.EndOfConversionChanged += eocActive =>
            {
                systemVia.SignalAdcEndOfConversion(eocActive);
                UpdateCpuIrqLine();
            };
        }

        public void ConfigureTube6502(string? hostRomPath = null, string? parasiteRomPath = null)
        {
            if (initialised)
                throw new InvalidOperationException("Tube configuration must be applied before the emulator is initialised.");

            tube6502Configured = true;
            configuredTubeHostRomPath = hostRomPath;
            configuredTube6502RomPath = parasiteRomPath;
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

            QueueStartupSerialText();
            pendingBreak = default;
            Cpu.ResetNow();
            initialised = true;
        }

        private void QueueStartupSerialText()
        {
            string? text = Environment.GetEnvironmentVariable("BBC_SERIAL_RX_TEXT");
            if (!string.IsNullOrEmpty(text))
                serialAcia.QueueReceivedText(text);
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

            StartStartupRuntimeTrace();
            StartCpu();

            long frameTicks = Math.Max(1, Stopwatch.Frequency / TargetFramesPerSecond);
            long nextFrame = Stopwatch.GetTimestamp() + frameTicks;
            keyboardInputEnabledAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            Display.DefaultSaveStateFileName = CreateSaveStateFileName();
            Display.SetRomSlots(sidewaysRomSlots);
            if (!LoadStartupStateIfRequested(Display))
            {
                StopCpu();
                return;
            }
            while (Display.PumpEvents())
            {
                if (cpuException is not null)
                    throw new InvalidOperationException("CPU execution failed.", cpuException);
                if (tube6502?.CpuException is not null)
                    throw new InvalidOperationException("Tube 6502 execution failed.", tube6502.CpuException);

                SynchronizeRomManagerPause(Display);
                SynchronizeInputMapperPause(Display);
                DrainHostPauseRequests(Display);
                DrainHostSoundToggleRequests(Display);
                DrainHostTapePauseRequests(Display);
                DrainHostTapePlayerToggleRequests(Display);
                DrainHostDriveToggleRequests(Display);
                DrainHostTube6502ToggleRequests(Display);
                DrainHostHayesModemToggleRequests(Display);
                DrainHostHayesLoopbackToggleRequests(Display);
                DrainHostHayesResetRequests(Display);
                DrainHostPowerResetRequests(Display);
                DrainHostBreakInput(Display);
                DrainHostDiscLoads(Display);
                DrainHostTapeActions(Display);
                DrainHostStateActions(Display);
                DrainHostRomActions(Display);
                DrainHostKeyMatrixInput(Display);
                DrainHostJoystickInput(Display);
                DrainHostAnalogJoystickInput(Display);
                UpdateHostMouseInput(Display);
                DrainHostKeyboardInput(Display);
                if (!emulationPaused)
                    QueuePendingBootScriptLine();

                DrainHostFrameAdvanceRequests(Display);
                RenderDisplayFrame(Display);
                DrainHostScreenshotRequests(Display);
                DrainHostTraceToggleRequests(Display);
                Display.Drive0Enabled = drive0Enabled;
                Display.Drive1Enabled = drive1Enabled;
                Display.Drive0Mounted = drive0Enabled && discController.IsPhysicalDriveMounted(0);
                Display.Drive1Mounted = drive1Enabled && discController.IsPhysicalDriveMounted(1);
                Display.Drive0DoubleSided = drive0Enabled && discController.IsPhysicalDriveDoubleSided(0);
                Display.Drive1DoubleSided = drive1Enabled && discController.IsPhysicalDriveDoubleSided(1);
                Display.Drive0Label = drive0Enabled ? discController.GetPhysicalDriveLabel(0) : null;
                Display.Drive1Label = drive1Enabled ? discController.GetPhysicalDriveLabel(1) : null;
                Display.Drive0ActivityLedActive = drive0Enabled && (discController.IsPhysicalDriveActivityLedActive(0)
                    || Stopwatch.GetTimestamp() < hostDiscActivityLedUntilTicks);
                Display.Drive1ActivityLedActive = drive1Enabled && discController.IsPhysicalDriveActivityLedActive(1);
                Display.CassetteMotorLedActive = !tape.Paused && (serialAcia.MotorRunning || serialAcia.TapePlaying);
                Display.CapsLockLedActive = bbcCapsLockState;
                Display.EmulationPaused = emulationPaused;
                Display.SoundOutputEnabled = !Sound.HostOutputMuted;
                Display.TapePaused = tape.Paused;
                Display.TapeMounted = tape.HasTape;
                Display.TapePlaying = tape.Playing;
                Display.TapeLabel = tape.MountedFileName;
                Display.TapePlayerEnabled = tapePlayerEnabled;
                Display.Tube6502Enabled = tube6502 is not null;
                Display.HayesModemEnabled = hayesModem is not null;
                Display.HayesLoopbackEnabled = hayesModem?.LoopbackEnabled == true;
                UpdateHayesModemLeds(Display);
                Display.DefaultSaveStateFileName = CreateSaveStateFileName();
                Display.SetRomSlots(sidewaysRomSlots);
                Display.Present();

                DumpTubeDebugStateIfDue();

                WaitUntil(nextFrame);
                nextFrame += frameTicks;

                long now = Stopwatch.GetTimestamp();
                ApplyStartupSpeedScaleIfReady(now);
                if (nextFrame < now - frameTicks * 4)
                    nextFrame = now + frameTicks;
            }

            StopCpu();
            StopStartupRuntimeTrace();
        }

        private void DumpTubeDebugStateIfDue()
        {
            if (tube6502 is null || !TubeDebugEnabled)
                return;

            long now = Stopwatch.GetTimestamp();
            if (now < nextTubeDebugDumpTicks)
                return;

            nextTubeDebugDumpTicks = now + Stopwatch.Frequency;
            Console.WriteLine($"GUI Tube debug: host PC ${Cpu.registers.PC & 0xFFFF:X4}, tube PC ${tube6502.Cpu.registers.PC & 0xFFFF:X4}, overlay {(tube6502.BootRomEnabled ? "on" : "off")}, requested NMI {tube6502.QueuedParasiteNmis}, queued {tube6502.CpuQueuedNmis}, serviced {tube6502.CpuServicedNmis}");
            Console.WriteLine(tubeUla.DebugStatus());
            Console.WriteLine($"Host registers: {FormatRegisters(Cpu.registers)}");
            Console.WriteLine($"Tube registers: {FormatRegisters(tube6502.Cpu.registers)}");
            Console.WriteLine($"Host bytes: {FormatMemoryBytes(Memory.Memory, (ushort)Cpu.registers.PC, 12)}");
            Console.WriteLine($"Tube bytes: {FormatMemoryBytes(tube6502.Memory, (ushort)tube6502.Cpu.registers.PC, 12)}");
            Console.WriteLine("Recent Tube ops:");
            foreach (string line in tubeUla.RecentTrace())
                Console.WriteLine(line);
        }

        public void RunHeadless(TimeSpan duration)
        {
            if (!initialised)
                Initialise();

            StartStartupRuntimeTrace();
            StartCpu();

            long deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            keyboardInputEnabledAtTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            if (!LoadStartupStateIfRequested(null))
            {
                StopCpu();
                return;
            }

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
            StopStartupRuntimeTrace();

            string? headlessPngPath = Environment.GetEnvironmentVariable("BBC_HEADLESS_PNG");
            if (!string.IsNullOrWhiteSpace(headlessPngPath))
            {
                using Display capture = new Display(scanlines: false);
                Video.Render(capture);
                capture.SavePng(headlessPngPath);
                Console.WriteLine($"Headless PNG: {headlessPngPath}");
            }

            if (cpuException is not null)
                throw new InvalidOperationException("CPU execution failed.", cpuException);
            if (tube6502?.CpuException is not null)
                throw new InvalidOperationException("Tube 6502 execution failed.", tube6502.CpuException);

            Console.WriteLine($"Headless PC: ${Cpu.registers.PC:X4}");
            if (tube6502 is not null)
            {
                Console.WriteLine($"Tube 6502 PC: ${tube6502.Cpu.registers.PC:X4}");
                Console.WriteLine($"Tube 6502 boot ROM overlay: {(tube6502.BootRomEnabled ? "enabled" : "disabled")}");
                if (TubeDebugEnabled)
                {
                    Console.WriteLine(tubeUla.DebugStatus());
                    Console.WriteLine($"Host registers: {FormatRegisters(Cpu.registers)}");
                    Console.WriteLine($"Tube registers: {FormatRegisters(tube6502.Cpu.registers)}");
                    Console.WriteLine($"Host bytes: {FormatMemoryBytes(Memory.Memory, (ushort)Cpu.registers.PC, 12)}");
                    Console.WriteLine($"Tube bytes: {FormatMemoryBytes(tube6502.Memory, (ushort)tube6502.Cpu.registers.PC, 12)}");
                    Console.WriteLine($"Host stack: {FormatMemoryBytes(Memory.Memory, (ushort)(0x0100 + Cpu.registers.S), 16)}");
                    Console.WriteLine($"Tube stack: {FormatMemoryBytes(tube6502.Memory, (ushort)(0x0100 + tube6502.Cpu.registers.S), 16)}");
                    Console.WriteLine("Recent Tube ops:");
                    foreach (string line in tubeUla.RecentTrace())
                        Console.WriteLine(line);
                }
                if (tube6502.Cpu.Jammed)
                    Console.WriteLine($"Tube 6502 jam bytes: {FormatMemoryBytes(tube6502.Memory, tube6502.Cpu.JamAddress, 16)}");
            }
            Console.WriteLine($"Mode 7 non-blank cells: {Video.CountMode7NonBlankCells()}");
            Console.WriteLine($"Tracked video mode: {Video.CurrentMode}");
            Console.WriteLine("Mode 7 text:");
            foreach (string row in Video.ReadMode7TextRows())
                Console.WriteLine(row);
        }

        public bool MountHostFile(string path, bool autoRunDisc = true, int? requestedDrive = null)
        {
            int physicalDrive = requestedDrive.GetValueOrDefault(0) & 1;
            if (!IsDiscDriveEnabled(physicalDrive))
                throw new InvalidOperationException($"Disc Drive {physicalDrive} is disabled.");

            if (requestedDrive.HasValue && !IsDriveLoadPath(path))
                throw new InvalidDataException("Drives can only load SSD, DSD, or ZIP files.");

            if (IsZipArchivePath(path))
                return MountZipArchive(path, autoRunDisc, requestedDrive);

            if (IsTapeImagePath(path))
            {
                MountTapeFile(path);
                return false;
            }

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
                return true;
            }

            hostFilingSystem.Mount(path);

            Console.WriteLine($"Mounted:    {hostFilingSystem.MountedFileName}");
            return false;
        }

        private void MountTapeFile(string path)
        {
            if (!tapePlayerEnabled)
                throw new InvalidOperationException("Tape Player is disabled.");

            if (!IsTapeImagePath(path))
                throw new InvalidDataException("Only UEF tape images can be loaded into the cassette player.");

            tape.Mount(path);
            hostFilingSystem.Unmount();

            Console.WriteLine($"Mounted UEF: {tape.MountedFileName}");
            if (TapeAutoplayEnabled && tape.Play())
                Console.WriteLine("Tape playing");
            Display?.ShowNotification($"{tape.MountedFileName} loaded", "Use *TAPE then CHAIN \"\"", 4000);
        }

        private void EjectTape()
        {
            if (!tapePlayerEnabled)
                return;

            string? fileName = tape.MountedFileName;
            tape.Unmount();
            Console.WriteLine(fileName is null ? "Tape ejected" : $"Tape ejected: {fileName}");
            Display?.ShowNotification("Tape ejected", string.Empty, 1500);
        }

        private bool MountZipArchive(string path, bool autoRunDisc, int? requestedDrive)
        {
            List<ArchiveDiscEntry> entries = GetArchiveDiscEntries(path);
            if (entries.Count == 0)
                throw new InvalidOperationException($"'{Path.GetFileName(path)}' does not contain any SSD or DSD images.");

            int drive = requestedDrive.GetValueOrDefault(0);
            if (entries.Count == 1 || Display is null)
            {
                MountArchiveDisc(path, entries[0].EntryPath, drive, autoRunDisc);
                return true;
            }

            Display.ShowDiscArchive(path, entries, drive);
            return false;
        }

        private void MountArchiveDisc(string archivePath, string entryPath, int drive, bool autoRunDisc)
        {
            byte[] image = ReadArchiveEntry(archivePath, entryPath);
            string displayName = $"{Path.GetFileName(archivePath)}:{Path.GetFileName(entryPath)}";

            if (discController.HasMountedDisc && discController.ImageDirty)
            {
                if (discController.Flush())
                    Console.WriteLine($"Saved disc:   {discController.MountedFileName}");
            }

            discController.MountImage(image, drive, null, displayName, readOnly: true);
            hostFilingSystem.Unmount();

            Console.WriteLine($"Mounted DFS: {discController.MountedDriveSummary}");
            if (autoRunDisc && drive == 0)
                QueueMountedDiscAutoRun();
        }

        public void EjectDisc(int drive)
        {
            if (!IsDiscDriveEnabled(drive & 1))
                return;

            discController.EjectPhysicalDrive(drive);
            Console.WriteLine($"Ejected DFS drive {drive}");
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

        /// <summary>DFS boot option 3 uses EXEC !BOOT; discs without that boot path are left at BASIC.</summary>
        public void QueueMountedDiscAutoRun()
        {
            if (discController.TryGetBootExecScript(out string? bootScript) && bootScript is not null)
            {
                QueueBootScript("*EXEC !BOOT");
            }
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
            StopRuntimeTrace();
            StopUserViaTrace();
            StopExileRamTrace();
            DisposeHayesModem();
            tube6502?.Dispose();
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
            tube6502?.SetPaused(emulationPaused);
            tube6502?.Start();
            Cpu.SetPaused(emulationPaused);
            cpuThread = new Thread(RunCpu)
            {
                IsBackground = true,
                Name = "BBC 6502"
            };
            cpuThread.Start();
        }

        private void StopCpu()
        {
            tube6502?.Stop();
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
            tubeUla.Reset();
            tube6502?.Reset();
            UpdateAdcChannels();
            Cpu.SetIrqLine(false);
            SetEmulationPaused(false);
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
            if (Volatile.Read(ref runtimeTraceActive) != 0)
                TraceRuntimeFrame();
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
            if (tapePlayerEnabled)
                tape.Tick(cycles);
            adc.Tick(cycles);
            TickCapsLockTap(cycles);
            tube6502?.AdvanceHostCycles(cycles);

            UpdateCpuIrqLine();
        }

        private void DrainHostPauseRequests(Display display)
        {
            int count = display.DrainPauseToggleRequests();
            for (int i = 0; i < count; i++)
                SetEmulationPaused(!emulationPaused);
        }

        private void DrainHostSoundToggleRequests(Display display)
        {
            int count = display.DrainSoundToggleRequests();
            for (int i = 0; i < count; i++)
            {
                bool muted = !Sound.HostOutputMuted;
                Sound.SetHostOutputMuted(muted);
                display.ShowNotification(muted ? "Sound off" : "Sound on", string.Empty, 1500);
            }
        }

        private void DrainHostTapePauseRequests(Display display)
        {
            int count = display.DrainTapePauseToggleRequests();
            for (int i = 0; i < count; i++)
                ToggleTapePause(display);
        }

        private void DrainHostTapePlayerToggleRequests(Display display)
        {
            int count = display.DrainTapePlayerToggleRequests();
            for (int i = 0; i < count; i++)
                SetTapePlayerEnabled(!tapePlayerEnabled, display);
        }

        private void DrainHostDriveToggleRequests(Display display)
        {
            int drive0Toggles = display.DrainDrive0ToggleRequests();
            for (int i = 0; i < drive0Toggles; i++)
                SetDiscDriveEnabled(0, !drive0Enabled, display);

            int drive1Toggles = display.DrainDrive1ToggleRequests();
            for (int i = 0; i < drive1Toggles; i++)
                SetDiscDriveEnabled(1, !drive1Enabled, display);
        }

        private void SetDiscDriveEnabled(int drive, bool enabled, Display? display = null)
        {
            if (drive is < 0 or > 1)
                return;

            if (!enabled)
                discController.EjectPhysicalDrive(drive);

            if (drive == 0)
                drive0Enabled = enabled;
            else
                drive1Enabled = enabled;

            display?.ShowNotification(
                enabled ? $"Disc Drive {drive} enabled" : $"Disc Drive {drive} disabled",
                enabled ? "Drive controls visible" : string.Empty,
                3000);
        }

        private bool IsDiscDriveEnabled(int drive)
        {
            return drive == 0 ? drive0Enabled : drive1Enabled;
        }

        private void SetTapePlayerEnabled(bool enabled, Display? display = null)
        {
            if (!enabled)
                tape.Unmount();

            tapePlayerEnabled = enabled;
            display?.ShowNotification(
                enabled ? "Tape Player enabled" : "Tape Player disabled",
                enabled ? "Cassette controls visible" : string.Empty,
                3000);
        }

        private void ToggleTapePause(Display display)
        {
            if (!tapePlayerEnabled)
            {
                display.ShowNotification("Tape Player disabled", "Enable it from Machine", 3000);
                return;
            }

            if (!tape.CanPause)
            {
                display.ShowNotification("No tape mounted", string.Empty, 1500);
                return;
            }

            bool paused = tape.TogglePaused();
            display.ShowNotification(
                paused ? "Tape paused" : "Tape running",
                paused ? "BBC continues running" : string.Empty,
                paused ? 4000 : 1500);
        }

        private void PlayTape(Display display)
        {
            if (!tapePlayerEnabled)
            {
                display.ShowNotification("Tape Player disabled", "Enable it from Machine", 3000);
                return;
            }

            if (!tape.Play())
            {
                display.ShowNotification("No tape mounted", string.Empty, 1500);
                return;
            }

            display.ShowNotification("Tape playing", "Waiting for BBC motor", 1500);
        }

        private void StopTape(Display display)
        {
            if (!tapePlayerEnabled)
                return;

            if (!tape.Stop())
            {
                display.ShowNotification("No tape mounted", string.Empty, 1500);
                return;
            }

            display.ShowNotification("Tape stopped", string.Empty, 1500);
        }

        private void RewindTape(Display display)
        {
            if (!tapePlayerEnabled)
                return;

            if (!tape.HasTape)
            {
                display.ShowNotification("No tape mounted", string.Empty, 1500);
                return;
            }

            tape.ResetPlayback();
            display.ShowNotification("Tape rewound", "Ready from start", 1500);
        }

        private void DrainHostTube6502ToggleRequests(Display display)
        {
            int count = display.DrainTube6502ToggleRequests();
            for (int i = 0; i < count; i++)
                SetTube6502Enabled(tube6502 is null);
        }

        private void DrainHostHayesModemToggleRequests(Display display)
        {
            int count = display.DrainHayesModemToggleRequests();
            for (int i = 0; i < count; i++)
                SetHayesModemEnabled(hayesModem is null);
        }

        private void DrainHostHayesLoopbackToggleRequests(Display display)
        {
            int count = display.DrainHayesLoopbackToggleRequests();
            for (int i = 0; i < count; i++)
                SetHayesLoopbackEnabled(hayesModem?.LoopbackEnabled != true);
        }

        private void DrainHostHayesResetRequests(Display display)
        {
            if (display.DrainHayesResetRequests() == 0)
                return;

            hayesModem?.Reset();
            Display?.ShowNotification("Hayes Modem reset", "Power cycle reset", 3000);
        }

        private void UpdateHayesModemLeds(Display display)
        {
            HayesModem? modem = hayesModem;
            if (modem is null)
            {
                display.HayesHighSpeedLedActive = false;
                display.HayesAutoAnswerLedActive = false;
                display.HayesCarrierDetectLedActive = false;
                display.HayesOffHookLedActive = false;
                display.HayesReceiveDataLedActive = false;
                display.HayesSendDataLedActive = false;
                display.HayesTerminalReadyLedActive = false;
                display.HayesModemReadyLedActive = false;
                display.HayesLoopbackEnabled = false;
                return;
            }

            HayesModem.HayesModemLedState ledState = modem.GetLedState(Stopwatch.GetTimestamp());
            display.HayesHighSpeedLedActive = ledState.HighSpeed;
            display.HayesAutoAnswerLedActive = ledState.AutoAnswer;
            display.HayesCarrierDetectLedActive = ledState.CarrierDetect;
            display.HayesOffHookLedActive = ledState.OffHook;
            display.HayesReceiveDataLedActive = ledState.ReceiveData;
            display.HayesSendDataLedActive = ledState.SendData;
            display.HayesTerminalReadyLedActive = ledState.TerminalReady;
            display.HayesModemReadyLedActive = ledState.ModemReady;
        }

        private void DrainHostPowerResetRequests(Display display)
        {
            if (display.DrainPowerResetRequests() == 0)
                return;

            PowerReset(display);
        }

        private void SynchronizeRomManagerPause(Display display)
        {
            if (display.RomManagerOpen)
            {
                if (romManagerPauseActive)
                    return;

                romManagerPauseActive = true;
                romManagerPreviousPaused = emulationPaused;
                Cpu.SetPaused(true);
                tube6502?.SetPaused(true);
                Sound.SetHostOutputPaused(true);
                return;
            }

            if (!romManagerPauseActive)
                return;

            romManagerPauseActive = false;
            bool resetRequired = romPatternChangedWhileManagerOpen;
            romPatternChangedWhileManagerOpen = false;

            if (resetRequired)
            {
                PowerOnResetAfterRomChange(display);
                return;
            }

            Cpu.SetPaused(romManagerPreviousPaused);
            tube6502?.SetPaused(romManagerPreviousPaused);
            Sound.SetHostOutputPaused(romManagerPreviousPaused);
        }

        private void SynchronizeInputMapperPause(Display display)
        {
            if (display.InputMapperOpen)
            {
                if (inputMapperPauseActive)
                    return;

                inputMapperPauseActive = true;
                inputMapperPreviousPaused = emulationPaused;
                Cpu.SetPaused(true);
                tube6502?.SetPaused(true);
                Sound.SetHostOutputPaused(true);
                return;
            }

            if (!inputMapperPauseActive)
                return;

            inputMapperPauseActive = false;
            Cpu.SetPaused(inputMapperPreviousPaused);
            tube6502?.SetPaused(inputMapperPreviousPaused);
            Sound.SetHostOutputPaused(inputMapperPreviousPaused);
        }

        private void DrainHostRomActions(Display display)
        {
            romActionScratch.Clear();
            display.DrainRomActions(romActionScratch);

            foreach (HostRomAction action in romActionScratch)
            {
                try
                {
                    switch (action.Kind)
                    {
                        case HostRomActionKind.Add:
                            SetSidewaysRomBank(action.Bank, action.Path);
                            romPatternChangedWhileManagerOpen = true;
                            Console.WriteLine($"ROM bank {action.Bank}: {action.Path}");
                            break;
                        case HostRomActionKind.Remove:
                            ClearSidewaysRomBank(action.Bank);
                            romPatternChangedWhileManagerOpen = true;
                            Console.WriteLine($"ROM bank {action.Bank}: empty");
                            break;
                        case HostRomActionKind.Move:
                            MoveSidewaysRomBank(action.Bank, action.TargetBank);
                            romPatternChangedWhileManagerOpen = true;
                            Console.WriteLine($"ROM bank {action.Bank} moved to bank {action.TargetBank}");
                            break;
                        case HostRomActionKind.ImportLayout:
                            ImportSidewaysRomLayout(action.Path);
                            romPatternChangedWhileManagerOpen = true;
                            display.ShowNotification("ROM layout imported", Path.GetFileName(action.Path), 2000);
                            Console.WriteLine($"ROM layout imported: {action.Path}");
                            break;
                        case HostRomActionKind.ExportLayout:
                            ExportSidewaysRomLayout(action.Path);
                            display.ShowNotification("ROM layout exported", Path.GetFileName(action.Path), 2000);
                            Console.WriteLine($"ROM layout exported: {action.Path}");
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException)
                {
                    display.ShowNotification("ROM manager", ex.Message, 4000);
                    Console.WriteLine($"ROM manager failed: {ex.Message}");
                }
            }
        }

        private void DrainHostFrameAdvanceRequests(Display display)
        {
            int count = display.DrainFrameAdvanceRequests();
            if (!emulationPaused)
                return;

            for (int i = 0; i < count; i++)
                AdvancePausedFrames(PausedFrameAdvanceCount);
        }

        private void SetEmulationPaused(bool paused)
        {
            if (emulationPaused == paused)
                return;

            emulationPaused = paused;
            Cpu.SetPaused(paused);
            tube6502?.SetPaused(paused);
            Sound.SetHostOutputPaused(paused);

            if (Display is not null)
            {
                Display.EmulationPaused = paused;
                Display.ShowNotification(
                    paused ? "Paused" : "Running",
                    paused ? $"Space advances {PausedFrameAdvanceCount} frames" : string.Empty,
                    paused ? 15000 : 1500);
            }
        }

        private void AdvancePausedFrames(int frames)
        {
            int startFrame = systemVia.FrameCounter;
            int targetFrame = startFrame + Math.Max(1, frames);
            long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 4 * Math.Max(1, frames));

            Cpu.SetPaused(false);
            tube6502?.SetPaused(false);
            try
            {
                while (systemVia.FrameCounter < targetFrame && Stopwatch.GetTimestamp() < deadline)
                {
                    if (cpuException is not null)
                        throw new InvalidOperationException("CPU execution failed.", cpuException);

                    Thread.Sleep(1);
                }
            }
            finally
            {
                Cpu.SetPaused(true);
                tube6502?.SetPaused(true);
            }
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
            Cpu.SetIrqLine(systemVia.IrqAsserted || userVia.IrqAsserted || serialAcia.IrqAsserted || tubeHostIrqAsserted);
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
            if (tube6502 is not null)
                return false;

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

                if (TryEnterSidewaysRomServiceCommand(bank, commandAddress))
                    return true;

                selectedSidewaysRom = bank;
                Cpu.registers.A = 1;
                Cpu.registers.X = (byte)bank;
                Cpu.registers.PC = SidewaysRomStart;
                return true;
            }
            return false;
        }

        private bool TryEnterSidewaysRomServiceCommand(int bank, ushort commandAddress)
        {
            if (!TryGetSidewaysRomServiceEntry(bank, out ushort serviceEntry))
                return false;

            ushort commandPointer = SkipStarCommandPrefix(commandAddress);
            Memory.Memory[0x00F2] = (byte)commandPointer;
            Memory.Memory[0x00F3] = (byte)(commandPointer >> 8);
            SetMosSelectedRomBank(bank);
            selectedSidewaysRom = bank;
            Cpu.registers.A = 4;
            Cpu.registers.X = (byte)bank;
            Cpu.registers.Y = 0;
            Cpu.registers.PC = serviceEntry;
            return true;
        }

        private bool TryGetSidewaysRomServiceEntry(int bank, out ushort serviceEntry)
        {
            serviceEntry = 0;
            if (bank < 0 || bank >= SidewaysRomBanks)
                return false;

            int bankOffset = bank * RomSize;
            byte type = sidewaysRoms[bankOffset + 6];
            bool serviceRom = (type & 0x80) != 0;
            if (!serviceRom || sidewaysRoms[bankOffset + 3] != 0x4C)
                return false;

            serviceEntry = (ushort)(sidewaysRoms[bankOffset + 4] | (sidewaysRoms[bankOffset + 5] << 8));
            return serviceEntry >= SidewaysRomStart && serviceEntry <= SidewaysRomEnd;
        }

        private void SetMosSelectedRomBank(int bank)
        {
            byte bankByte = (byte)bank;
            Memory.Memory[0x00F4] = bankByte;
            Memory.Memory[0x028C] = bankByte;
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

            if (Cpu.registers.A == 0x8E)
            {
                selectedSidewaysRom = Cpu.registers.X & 0x0F;
                Cpu.registers.A = 1;
                Cpu.registers.X = (byte)selectedSidewaysRom;
                Cpu.registers.PC = SidewaysRomStart;
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

        private ushort SkipStarCommandPrefix(ushort address)
        {
            ushort pointer = address;

            if (Memory.Memory[pointer] == (byte)'*')
                pointer++;

            while (Memory.Memory[pointer] is (byte)' ' or (byte)'\t')
                pointer++;

            return pointer;
        }

        private void DrainHostDiscLoads(Display display)
        {
            discActionScratch.Clear();
            display.DrainDiscActions(discActionScratch);

            foreach (HostDiscAction action in discActionScratch)
            {
                try
                {
                    switch (action.Kind)
                    {
                        case HostDiscActionKind.Mount:
                            if (MountHostFile(action.Path, autoRunDisc: false, requestedDrive: action.Drive))
                                ShowDiscLoadedNotice(display, action.Drive);
                            break;
                        case HostDiscActionKind.MountArchiveEntry:
                            MountArchiveDisc(action.Path, action.ArchiveEntryPath, action.Drive, autoRunDisc: false);
                            ShowDiscLoadedNotice(display, action.Drive);
                            break;
                        case HostDiscActionKind.CreateBlankSsd:
                            CreateBlankSsdImage(action.Path, overwrite: true);
                            if (MountHostFile(action.Path, autoRunDisc: false, requestedDrive: action.Drive))
                                ShowDiscLoadedNotice(display, action.Drive);
                            break;
                        case HostDiscActionKind.Eject:
                            EjectDisc(action.Drive);
                            break;
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException
                    or DirectoryNotFoundException
                    or UnauthorizedAccessException
                    or IOException
                    or InvalidDataException
                    or InvalidOperationException)
                {
                    string message = string.IsNullOrWhiteSpace(action.Path)
                        ? $"Disc action failed: {ex.Message}"
                        : QueueHostMountFailure(action.Path, ex);
                    Console.WriteLine(message);
                }
            }
        }

        private void DrainHostTapeActions(Display display)
        {
            tapeActionScratch.Clear();
            display.DrainTapeActions(tapeActionScratch);

            foreach (HostTapeAction action in tapeActionScratch)
            {
                try
                {
                    switch (action.Kind)
                    {
                        case HostTapeActionKind.Mount:
                            MountTapeFile(action.Path);
                            break;
                        case HostTapeActionKind.Play:
                            PlayTape(display);
                            break;
                        case HostTapeActionKind.TogglePause:
                            ToggleTapePause(display);
                            break;
                        case HostTapeActionKind.Stop:
                            StopTape(display);
                            break;
                        case HostTapeActionKind.Rewind:
                            RewindTape(display);
                            break;
                        case HostTapeActionKind.Eject:
                            EjectTape();
                            break;
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException
                    or DirectoryNotFoundException
                    or UnauthorizedAccessException
                    or IOException
                    or InvalidDataException
                    or InvalidOperationException)
                {
                    string message = string.IsNullOrWhiteSpace(action.Path)
                        ? $"Tape action failed: {ex.Message}"
                        : $"Tape action failed for '{Path.GetFileName(action.Path)}': {ex.Message}";
                    Console.WriteLine(message);
                    display.ShowNotification("Tape action failed", ex.Message, 5000);
                }
            }
        }

        private void ShowDiscLoadedNotice(Display display, int drive)
        {
            int physicalDrive = drive & 1;
            string label = discController.GetPhysicalDriveLabel(physicalDrive) ?? "Disc";
            display.ShowNotification($"{label} loaded", "Press SHIFT+BREAK to boot", 4000);
        }

        private void DrainHostStateActions(Display display)
        {
            stateActionScratch.Clear();
            display.DrainStateActions(stateActionScratch);

            foreach (HostStateAction action in stateActionScratch)
            {
                try
                {
                    switch (action.Kind)
                    {
                        case HostStateActionKind.Save:
                            SaveStateFile(action.Path);
                            display.AddRecentState(action.Path);
                            display.ShowNotification("State saved", Path.GetFileName(action.Path), 2000);
                            Console.WriteLine($"Saved state: {action.Path}");
                            break;
                        case HostStateActionKind.Load:
                            LoadStateFile(action.Path);
                            display.AddRecentState(action.Path);
                            display.ShowNotification("State loaded", Path.GetFileName(action.Path), 2000);
                            Console.WriteLine($"Loaded state: {action.Path}");
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or EndOfStreamException
                    or InvalidOperationException)
                {
                    display.ShowNotification("State failed", ex.Message, 4000);
                    Console.WriteLine($"State action failed: {ex.Message}");
                }
            }
        }

        private bool LoadStartupStateIfRequested(Display? display)
        {
            string? path = startupLoadStatePath;
            if (string.IsNullOrWhiteSpace(path))
                return true;

            startupLoadStatePath = null;
            try
            {
                LoadStateFile(path);
                display?.AddRecentState(path);
                display?.ShowNotification("State loaded", Path.GetFileName(path), 2000);
                Console.WriteLine($"Loaded state: {path}");
                return true;
            }
            catch (Exception ex) when (IsUserStateException(ex))
            {
                display?.ShowNotification("State failed", ex.Message, 4000);
                Console.WriteLine($"Could not load state: {ex.Message}");
                Environment.ExitCode = 1;
                return false;
            }
        }

        private void SaveStateFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            WithCpuStoppedForStateFile(() =>
            {
                using FileStream stream = File.Create(path);
                using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

                writer.Write(SaveStateMagic);
                writer.Write(SaveStateVersion);
                Cpu.SaveState(writer);
                WriteByteArray(writer, Memory.Memory);
                WriteByteArray(writer, sidewaysRoms);
                writer.Write(selectedSidewaysRom);
                SaveRomState(writer);
                writer.Write(breakContinuationQueued);
                WriteString(writer, pendingBootExecScript);
                WriteString(writer, pendingBootScriptLines.Count == 0 ? null : string.Join("\n", pendingBootScriptLines));
                writer.Write(nextBootScriptLineAtTicks);
                writer.Write(hostDiscActivityLedUntilTicks);
                writer.Write(capsLockTapPulseCycles);
                writer.Write(capsLockTapPressed);
                writer.Write(hostCapsLockState);
                writer.Write(bbcCapsLockState);
                writer.Write(mouseEnabled);
                writer.Write(mousePositionInitialized);
                writer.Write(lastMouseX);
                writer.Write(lastMouseY);
                SaveJoystickState(writer);
                systemVia.SaveState(writer);
                userVia.SaveState(writer);
                serialAcia.SaveState(writer);
                writer.Write(tapePlayerEnabled);
                tape.SaveState(writer);
                writer.Write(hayesModem is not null);
                if (hayesModem is not null)
                    WriteStateBlock(writer, hayesModem.SaveState);

                adc.SaveState(writer);
                discController.SaveState(writer);
                Sound.SaveState(writer);
                Video.SaveState(writer);
                writer.Write(tube6502 is not null);
                if (tube6502 is not null)
                {
                    WriteStateBlock(writer, tube6502.SaveState);
                    WriteStateBlock(writer, tubeUla.SaveState);
                }
            });
        }

        private void LoadStateFile(string path)
        {
            if (discController.HasMountedDisc && discController.ImageDirty)
                discController.Flush();

            WithCpuStoppedForStateFile(() =>
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

                if (reader.ReadUInt32() != SaveStateMagic)
                    throw new InvalidDataException("Not a BBC Model B save state.");

                int version = reader.ReadInt32();
                if (version != SaveStateVersion)
                    throw new InvalidDataException($"Unsupported BBC save state version {version}.");

                Cpu.LoadState(reader);
                ReadByteArray(reader, Memory.Memory, "main memory");
                ReadByteArray(reader, sidewaysRoms, "sideways ROM/RAM");
                selectedSidewaysRom = reader.ReadInt32();
                LoadRomState(reader);
                breakContinuationQueued = reader.ReadBoolean();
                pendingBootExecScript = ReadString(reader);
                pendingBootScriptLines.Clear();
                string? pendingLines = ReadString(reader);
                if (!string.IsNullOrEmpty(pendingLines))
                {
                    foreach (string line in pendingLines.Split('\n'))
                        pendingBootScriptLines.Enqueue(line);
                }

                nextBootScriptLineAtTicks = reader.ReadInt64();
                hostDiscActivityLedUntilTicks = reader.ReadInt64();
                capsLockTapPulseCycles = reader.ReadInt32();
                capsLockTapPressed = reader.ReadBoolean();
                hostCapsLockState = reader.ReadBoolean();
                bbcCapsLockState = reader.ReadBoolean();
                mouseEnabled = reader.ReadBoolean();
                mousePositionInitialized = reader.ReadBoolean();
                lastMouseX = reader.ReadByte();
                lastMouseY = reader.ReadByte();
                LoadJoystickState(reader);
                systemVia.LoadState(reader);
                userVia.LoadState(reader);
                serialAcia.LoadState(reader);
                tapePlayerEnabled = reader.ReadBoolean();
                tape.LoadState(reader);
                bool saveHasHayesModem = reader.ReadBoolean();
                if (saveHasHayesModem)
                {
                    SetHayesModemEnabled(true, notify: false);
                    byte[] hayesState = ReadStateBlock(reader, "Hayes modem state");
                    using BinaryReader hayesReader = new BinaryReader(new MemoryStream(hayesState), Encoding.UTF8);
                    hayesModem?.LoadState(hayesReader);
                }
                else
                {
                    SetHayesModemEnabled(false, notify: false);
                }

                adc.LoadState(reader);
                discController.LoadState(reader);
                Sound.LoadState(reader);
                Video.LoadState(reader);
                bool saveHasTube = reader.ReadBoolean();
                if (saveHasTube)
                {
                    EnsureTube6502ForLoadedState();
                    CoProcessor65C02 loadedTube = tube6502
                        ?? throw new InvalidDataException("This save state requires the 6502 Tube co-processor.");
                    LoadTubeState(reader, loadedTube);
                }
                else
                {
                    tubeUla.Reset();
                    if (tube6502 is not null)
                    {
                        tube6502.Dispose();
                        tube6502 = null;
                    }
                    tube6502Configured = false;
                }
                UpdateAmxMouseRomState();
                Video.SetScreenMemoryWindow(systemVia.CurrentScreenMemoryWindow);
                UpdateCpuIrqLine();
                UpdateAdcChannels();
                UpdateJoystickInputs();
                Display?.SetRelativeMouseMode(mouseEnabled);
            });
        }

        private void WithCpuStoppedForStateFile(Action action)
        {
            bool wasPaused = emulationPaused;
            Cpu.SetPaused(true);
            tube6502?.SetPaused(true);
            Sound.SetHostOutputPaused(true);
            Thread.Sleep(5);
            try
            {
                action();
            }
            finally
            {
                Cpu.SetPaused(wasPaused);
                tube6502?.SetPaused(wasPaused);
                Sound.SetHostOutputPaused(wasPaused);
            }
        }

        private void SaveJoystickState(BinaryWriter writer)
        {
            writer.Write(joystickState.Left);
            writer.Write(joystickState.Right);
            writer.Write(joystickState.Up);
            writer.Write(joystickState.Down);
            writer.Write(joystickState.Fire);
            writer.Write(joystickState.AnalogX);
            writer.Write(joystickState.AnalogY);
            writer.Write(joystickState.HasAnalogX);
            writer.Write(joystickState.HasAnalogY);
        }

        private void LoadJoystickState(BinaryReader reader)
        {
            joystickState.Left = reader.ReadBoolean();
            joystickState.Right = reader.ReadBoolean();
            joystickState.Up = reader.ReadBoolean();
            joystickState.Down = reader.ReadBoolean();
            joystickState.Fire = reader.ReadBoolean();
            joystickState.AnalogX = reader.ReadUInt16();
            joystickState.AnalogY = reader.ReadUInt16();
            joystickState.HasAnalogX = reader.ReadBoolean();
            joystickState.HasAnalogY = reader.ReadBoolean();
        }

        private static void WriteByteArray(BinaryWriter writer, byte[] bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static void ReadByteArray(BinaryReader reader, byte[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            bytes.CopyTo(destination, 0);
        }

        private static void WriteString(BinaryWriter writer, string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
                writer.Write(value);
        }

        private static string? ReadString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadString() : null;
        }

        private void SaveRomState(BinaryWriter writer)
        {
            writer.Write(tube6502Configured);
            WriteString(writer, OsRomPath);
            WriteString(writer, BasicRomPath);
            WriteString(writer, DfsRomPath);
            WriteString(writer, AmxMouseRomPath);
            WriteString(writer, Tube6502RomPath);

            writer.Write(sidewaysRomPaths.Length);
            for (int bank = 0; bank < sidewaysRomPaths.Length; bank++)
                WriteString(writer, sidewaysRomPaths[bank]);
        }

        private void LoadRomState(BinaryReader reader)
        {
            tube6502Configured = reader.ReadBoolean();
            OsRomPath = ReadString(reader) ?? OsRomPath;
            BasicRomPath = ReadString(reader) ?? BasicRomPath;
            DfsRomPath = ReadString(reader) ?? DfsRomPath;
            AmxMouseRomPath = ReadString(reader);
            Tube6502RomPath = ReadString(reader);

            int bankCount = reader.ReadInt32();
            if (bankCount != sidewaysRomPaths.Length)
                throw new InvalidDataException("Save state has an incompatible sideways ROM path block.");

            for (int bank = 0; bank < sidewaysRomPaths.Length; bank++)
                sidewaysRomPaths[bank] = ReadString(reader);

            RefreshSidewaysRomSlotsFromSavedBytes();
        }

        private void LoadTubeState(BinaryReader reader, CoProcessor65C02 loadedTube)
        {
            byte[] parasiteState = ReadStateBlock(reader, "Tube 6502 state");
            byte[] ulaState = ReadStateBlock(reader, "Tube ULA state");
            using BinaryReader parasiteReader = new BinaryReader(new MemoryStream(parasiteState), Encoding.UTF8);
            using BinaryReader ulaReader = new BinaryReader(new MemoryStream(ulaState), Encoding.UTF8);
            tubeUla.LoadState(ulaReader);
            loadedTube.LoadState(parasiteReader);
        }

        private static void WriteStateBlock(BinaryWriter writer, Action<BinaryWriter> write)
        {
            using MemoryStream stream = new MemoryStream();
            using (BinaryWriter blockWriter = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                write(blockWriter);

            byte[] bytes = stream.ToArray();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static byte[] ReadStateBlock(BinaryReader reader, string name)
        {
            int length = reader.ReadInt32();
            if (length < 0)
                throw new InvalidDataException($"Save state has an invalid {name} block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            return bytes;
        }

        private static string FormatRegisters(BBC.CPU.Registers registers)
        {
            return $"PC=${registers.PC & 0xFFFF:X4} A=${registers.A:X2} X=${registers.X:X2} Y=${registers.Y:X2} S=${registers.S:X2} P=${registers.P:X2}";
        }

        private static string FormatMemoryBytes(byte[] memory, ushort centerAddress, int radius)
        {
            StringBuilder builder = new StringBuilder();
            int start = Math.Max(0, centerAddress - radius);
            int end = Math.Min(memory.Length - 1, centerAddress + radius);

            for (int address = start; address <= end; address++)
            {
                if (builder.Length > 0)
                    builder.Append(' ');

                builder.Append('$');
                builder.Append(address.ToString("X4", CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(memory[address].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
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
                if (discController.TraceEnabled || Volatile.Read(ref runtimeTraceActive) != 0)
                {
                    string? path = discController.StopTrace();
                    Console.WriteLine($"8271 trace stopped: {path}");
                    path = StopRuntimeTrace();
                    Console.WriteLine($"runtime trace stopped: {path}");
                }
                else
                {
                    string discTracePath = Path.Combine(Environment.CurrentDirectory, "bbc-8271-trace.log");
                    discController.StartTrace(discTracePath);
                    Console.WriteLine($"8271 trace started: {discTracePath}");

                    string runtimePath = Path.Combine(Environment.CurrentDirectory, "bbc-runtime-trace.log");
                    StartRuntimeTrace(runtimePath);
                    Console.WriteLine($"runtime trace started: {runtimePath}");
                }
            }
        }

        private void StartRuntimeTrace(string path)
        {
            StopRuntimeTrace();

            lock (runtimeTraceLock)
            {
                runtimeTracePath = Path.GetFullPath(path);
                runtimeTraceWriter = new StreamWriter(runtimeTracePath, append: false, Encoding.UTF8);
                runtimeTraceInstructionCount = 0;
                runtimeTraceFrame = 0;
                runtimeTraceFramePcSamples.Clear();
                runtimeTraceWriter.WriteLine($"TRACE START {DateTimeOffset.Now:O}");
                runtimeTraceWriter.WriteLine($"MOUNTED {discController.MountedDriveSummary}");
                runtimeTraceWriter.WriteLine("Instruction lines are compressed after the first few thousand opcodes; HOT counts show repeated PCs within the current frame.");
                Cpu.OnInstructionExecuted = TraceRuntimeInstruction;
                Volatile.Write(ref runtimeTraceActive, 1);
            }
        }

        private void StartStartupRuntimeTrace()
        {
            if (RuntimeTraceStartupEnabled)
                StartRuntimeTrace(RuntimeTraceStartupPath);
        }

        private void StopStartupRuntimeTrace()
        {
            if (RuntimeTraceStartupEnabled)
                StopRuntimeTrace();
        }

        private string? StopRuntimeTrace()
        {
            lock (runtimeTraceLock)
            {
                Volatile.Write(ref runtimeTraceActive, 0);
                Cpu.OnInstructionExecuted = null;
                if (runtimeTraceWriter is null)
                    return runtimeTracePath;

                WriteRuntimeTraceFrameSummary();
                runtimeTraceWriter.WriteLine($"TRACE STOP {DateTimeOffset.Now:O}");
                runtimeTraceWriter.Dispose();
                runtimeTraceWriter = null;
                runtimeTraceFramePcSamples.Clear();
                return runtimeTracePath;
            }
        }

        private void TraceUserVia(string operation, ushort address, byte value)
        {
            if (!UserViaTraceEnabled)
                return;

            userViaTraceWriter ??= new StreamWriter(UserViaTracePath, append: false, Encoding.UTF8);
            userViaTraceWriter.WriteLine(
                $"via {operation} pc=${Cpu.registers.PC:X4} cycles={Cpu.TotalCycles} addr=${address:X4} value=${value:X2} {userVia.TraceState}");
        }

        private void StopUserViaTrace()
        {
            userViaTraceWriter?.Dispose();
            userViaTraceWriter = null;
        }

        private void TraceExileRamWrite(ushort address, byte value)
        {
            if (!ExileRamTraceEnabled || address < 0x0B00 || address > 0x0BFF)
                return;

            exileRamTraceWriter ??= new StreamWriter(ExileRamTracePath, append: false, Encoding.UTF8);
            exileRamTraceWriter.WriteLine($"ram write pc=${Cpu.registers.PC:X4} cycles={Cpu.TotalCycles} addr=${address:X4} value=${value:X2}");
        }

        private void StopExileRamTrace()
        {
            exileRamTraceWriter?.Dispose();
            exileRamTraceWriter = null;
        }

        private void TraceRuntimeInstruction(ushort pc, byte opcode, int cycles, bool handledByHost)
        {
            if (Volatile.Read(ref runtimeTraceActive) == 0)
                return;

            lock (runtimeTraceLock)
            {
                if (runtimeTraceWriter is null)
                    return;

                runtimeTraceInstructionCount++;
                if (!runtimeTraceFramePcSamples.TryGetValue(pc, out RuntimeTracePcSample? sample))
                {
                    sample = new RuntimeTracePcSample(opcode);
                    runtimeTraceFramePcSamples.Add(pc, sample);
                }

                sample.Count++;

                bool logInstruction = runtimeTraceInstructionCount <= RuntimeTraceFirstInstructions
                    || sample.Count <= 4
                    || sample.Count % RuntimeTraceHotPcInterval == 0;

                if (!logInstruction)
                    return;

                string kind = sample.Count > 4 ? "HOT" : "CPU";
                runtimeTraceWriter.WriteLine(
                    $"{kind} i={runtimeTraceInstructionCount} frame={runtimeTraceFrame} pc=${pc:X4} op=${opcode:X2} " +
                    $"a=${Cpu.registers.A:X2} x=${Cpu.registers.X:X2} y=${Cpu.registers.Y:X2} s=${Cpu.registers.S:X2} p=${Cpu.registers.P:X2} " +
                    $"cycles={cycles} hit={sample.Count} host={(handledByHost ? 1 : 0)} " +
                    $"irq={(Cpu.IrqLineAsserted ? 1 : 0)} nmi={(discController.NmiLineAsserted ? 1 : 0)} disc={(discController.TransferActive ? 1 : 0)}");
            }
        }

        private void TraceRuntimeFrame()
        {
            if (Volatile.Read(ref runtimeTraceActive) == 0)
                return;

            lock (runtimeTraceLock)
            {
                if (runtimeTraceWriter is null)
                    return;

                WriteRuntimeTraceFrameSummary();
                runtimeTraceFramePcSamples.Clear();
                runtimeTraceFrame++;
                runtimeTraceWriter.Flush();
            }
        }

        private void WriteRuntimeTraceFrameSummary()
        {
            if (runtimeTraceWriter is null || runtimeTraceFramePcSamples.Count == 0)
                return;

            string hotPcs = string.Join(" ",
                runtimeTraceFramePcSamples
                    .OrderByDescending(pair => pair.Value.Count)
                    .ThenBy(pair => pair.Key)
                    .Take(RuntimeTraceTopPcCount)
                    .Select(pair => $"${pair.Key:X4}/${pair.Value.Opcode:X2}:{pair.Value.Count}"));

            runtimeTraceWriter.WriteLine(
                $"FRAME {runtimeTraceFrame} totalCycles={Cpu.TotalCycles} pc=${Cpu.registers.PC & 0xFFFF:X4} " +
                $"a=${Cpu.registers.A:X2} x=${Cpu.registers.X:X2} y=${Cpu.registers.Y:X2} s=${Cpu.registers.S:X2} p=${Cpu.registers.P:X2} " +
                $"irq={(Cpu.IrqLineAsserted ? 1 : 0)} nmi={(discController.NmiLineAsserted ? 1 : 0)} disc={(discController.TransferActive ? 1 : 0)} " +
                $"cli=${ReadRamWord(CliVector):X4} v0204=${ReadRamWord(0x0204):X4} v0206=${ReadRamWord(0x0206):X4} hot={hotPcs}");
        }

        private ushort ReadRamWord(ushort address)
        {
            return (ushort)(Memory.Memory[address] | (Memory.Memory[(address + 1) & 0xFFFF] << 8));
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
            tubeUla.Reset();
            tube6502?.Reset();
            Cpu.RequestReset();
        }

        private void PowerOnResetAfterRomChange(Display display)
        {
            Array.Clear(Memory.Memory);
            Memory.Load(OsRomStart, File.ReadAllBytes(OsRomPath));
            pendingBreak = default;
            pendingBootExecScript = null;
            pendingBootScriptLines.Clear();
            selectedSidewaysRom = BasicRomBank;
            serialAcia.Reset();
            tubeUla.Reset();
            tube6502?.Reset();
            Cpu.RequestReset();
            Cpu.SetPaused(romManagerPreviousPaused);
            tube6502?.SetPaused(romManagerPreviousPaused);
            Sound.SetHostOutputPaused(romManagerPreviousPaused);
            display.ShowNotification("ROM pattern changed", "BBC reset", 2000);
        }

        private void PowerReset(Display display)
        {
            StopCpu();
            try
            {
                Array.Clear(Memory.Memory);
                Memory.Load(OsRomStart, File.ReadAllBytes(OsRomPath));
                pendingBreak = default;
                pendingBootExecScript = null;
                pendingBootScriptLines.Clear();
                pendingKeyboardInput.Clear();
                breakContinuationQueued = false;
                selectedSidewaysRom = BasicRomBank;
                Cpu.SpeedScale = 1.0;
                Sound.ThrottleToPlayback = true;
                startupSpeedScaleApplied = requestedStartupSpeedScale == 1.0;
                serialAcia.Reset();
                discController.PowerOff();
                Array.Fill(display.FrameBuffer, DisplayBlack);
                display.Present();
                Cpu.ResetNow();
                Sound.QueuePowerOnBeep();
                display.ShowNotification("Power reset", "BBC power cycled", 2000);
            }
            finally
            {
                StartCpu();
            }
        }

        private void SetTube6502Enabled(bool enabled)
        {
            if (enabled == (tube6502 is not null))
                return;

            bool wasPaused = emulationPaused;
            Cpu.SetPaused(true);
            tube6502?.SetPaused(true);
            Sound.SetHostOutputPaused(true);

            try
            {
                if (enabled)
                {
                    string romRoot = GetRomRoot();
                    string hiBasicRomPath = Path.Combine(romRoot, HiBasicRomFileName);
                    DfsRomPath = configuredTubeHostRomPath ?? Path.Combine(romRoot, TubeHostRomFileName);
                    Tube6502RomPath = configuredTube6502RomPath ?? Path.Combine(romRoot, Tube6502RomFileName);
                    if (File.Exists(hiBasicRomPath))
                    {
                        BasicRomPath = hiBasicRomPath;
                        ValidateRom(BasicRomPath, HiBasicRomMarker, RomSize);
                        SetSidewaysRomBank(BasicRomBank, BasicRomPath);
                    }
                    ValidateRom(DfsRomPath, DnfsRomMarker, RomSize / 2, RomSize);
                    ValidateRom(Tube6502RomPath, Tube6502RomMarker, 1, RomSize);
                    SetSidewaysRomBank(DfsRomBank, DfsRomPath);

                    tubeUla.Reset();
                    tube6502 = new CoProcessor65C02(tubeUla);
                    tube6502.LoadRom(Tube6502RomPath);
                    tube6502.SetPaused(emulationPaused);
                    tube6502.Reset();
                    tube6502.Start();
                }
                else
                {
                    string romRoot = GetRomRoot();
                    BasicRomPath = Path.Combine(romRoot, BasicRomFileName);
                    DfsRomPath = Path.Combine(romRoot, DfsRomFileName);
                    Tube6502RomPath = null;
                    ValidateRom(BasicRomPath, BasicRomMarker, RomSize);
                    ValidateRom(DfsRomPath, DfsRomMarker, RomSize / 2, RomSize);
                    SetSidewaysRomBank(BasicRomBank, BasicRomPath);
                    SetSidewaysRomBank(DfsRomBank, DfsRomPath);

                    tube6502?.Dispose();
                    tube6502 = null;
                    tubeUla.Reset();
                }

                tube6502Configured = enabled;
                UpdateAmxMouseRomState();
                UpdateCpuIrqLine();

                Display?.ShowNotification(
                    enabled ? "6502 Co-Processor enabled" : "6502 Co-Processor disabled",
                    "Press Ctrl-BREAK for the BBC to recognise the change",
                    6000);
            }
            finally
            {
                Cpu.SetPaused(wasPaused);
                tube6502?.SetPaused(wasPaused);
                Sound.SetHostOutputPaused(wasPaused);
            }
        }

        private void SetHayesModemEnabled(bool enabled, bool notify = true)
        {
            if (enabled == (hayesModem is not null))
                return;

            if (enabled)
            {
                HayesModem modem = new HayesModem(serialAcia, Sound);
                serialAcia.ByteTransmitted += modem.Receive;
                hayesModem = modem;
            }
            else
            {
                DisposeHayesModem();
            }

            if (notify)
            {
                Display?.ShowNotification(
                    enabled ? "Hayes Modem enabled" : "Hayes Modem disabled",
                    "BBC RS423 serial port",
                    3000);
            }
        }

        private void SetHayesLoopbackEnabled(bool enabled)
        {
            HayesModem? modem = hayesModem;
            if (modem is null)
                return;

            modem.LoopbackEnabled = enabled;
            Display?.ShowNotification(
                enabled ? "Hayes loopback enabled" : "Hayes loopback disabled",
                "BBC serial data is echoed by the modem",
                3000);
        }

        private void DisposeHayesModem()
        {
            HayesModem? modem = hayesModem;
            if (modem is null)
                return;

            hayesModem = null;
            serialAcia.ByteTransmitted -= modem.Receive;
            modem.Dispose();
        }

        private void EnsureTube6502ForLoadedState()
        {
            string romRoot = GetRomRoot();
            Tube6502RomPath ??= configuredTube6502RomPath ?? Path.Combine(romRoot, Tube6502RomFileName);
            ValidateRom(Tube6502RomPath, Tube6502RomMarker, 1, RomSize);

            if (tube6502 is null)
                tube6502 = new CoProcessor65C02(tubeUla);

            tube6502.LoadRom(Tube6502RomPath);
            tube6502.SetPaused(true);
            tube6502Configured = true;
            tube6502.Start();
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

        private string CreateScreenshotPath()
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "Screenshots");
            string title = GetScreenshotTitle();
            string fileName = $"bbc-{title}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            return Path.Combine(directory, fileName);
        }

        private string CreateSaveStateFileName()
        {
            string title = GetScreenshotTitle();
            return $"bbc-{title}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.sav";
        }

        private string GetScreenshotTitle()
        {
            string? mountedName = discController.MountedFileName ?? hostFilingSystem.MountedFileName;
            if (string.IsNullOrWhiteSpace(mountedName))
                return "untitled";

            string title = Path.GetFileNameWithoutExtension(mountedName);
            return SanitizeScreenshotTitle(string.IsNullOrWhiteSpace(title) ? mountedName : title);
        }

        private static string SanitizeScreenshotTitle(string title)
        {
            StringBuilder builder = new StringBuilder(title.Length);
            bool previousSeparator = false;

            foreach (char ch in title.Trim())
            {
                bool valid = char.IsLetterOrDigit(ch);
                if (valid)
                {
                    builder.Append(ch);
                    previousSeparator = false;
                    continue;
                }

                if (!previousSeparator)
                {
                    builder.Append('-');
                    previousSeparator = true;
                }
            }

            string sanitized = builder.ToString().Trim('-');
            return sanitized.Length == 0 ? "untitled" : sanitized;
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
            string romRoot = GetRomRoot();

            OsRomPath = Path.Combine(romRoot, OsRomFileName);
            bool tubeEnabled = tube6502Configured;
            string basicRomMarker = BasicRomMarker;
            BasicRomPath = Path.Combine(romRoot, BasicRomFileName);
            if (tubeEnabled)
            {
                string hiBasicRomPath = Path.Combine(romRoot, HiBasicRomFileName);
                if (File.Exists(hiBasicRomPath))
                {
                    BasicRomPath = hiBasicRomPath;
                    basicRomMarker = HiBasicRomMarker;
                }
            }
            DfsRomPath = configuredTubeHostRomPath ?? Path.Combine(romRoot, tubeEnabled ? TubeHostRomFileName : DfsRomFileName);
            AmxMouseRomPath = Path.Combine(romRoot, AmxMouseRomFileName);
            Tube6502RomPath = configuredTube6502RomPath ?? (tubeEnabled ? Path.Combine(romRoot, Tube6502RomFileName) : null);

            ValidateRom(OsRomPath, OsRomMarker, RomSize);
            ValidateRom(BasicRomPath, basicRomMarker, RomSize);
            if (tubeEnabled)
                ValidateRom(DfsRomPath, DnfsRomMarker, RomSize / 2, RomSize);
            else
                ValidateRom(DfsRomPath, DfsRomMarker, RomSize / 2, RomSize);
            if (File.Exists(AmxMouseRomPath))
                ValidateRom(AmxMouseRomPath, AmxMouseRomMarker, RomSize);
            else
                AmxMouseRomPath = null;
            if (Tube6502RomPath is not null)
                ValidateRom(Tube6502RomPath, Tube6502RomMarker, 1, RomSize);

            Memory.Load(OsRomStart, File.ReadAllBytes(OsRomPath));

            Array.Fill(sidewaysRoms, (byte)0xFF);
            Array.Clear(sidewaysRomPaths);
            LoadDefaultSidewaysRoms();
            if (tubeEnabled)
                ApplyTubeHostSidewaysRoms();

            UpdateAmxMouseRomState();

            if (Tube6502RomPath is not null)
            {
                tube6502 = new CoProcessor65C02(tubeUla);
                tube6502.LoadRom(Tube6502RomPath);
                tubeUla.Reset();
                tube6502.Reset();
            }
        }

        private void LoadDefaultSidewaysRoms()
        {
            SetSidewaysRomBank(BasicRomBank, BasicRomPath);
            SetSidewaysRomBank(DfsRomBank, DfsRomPath);
            RefreshSidewaysRomSlots();
        }

        private void ApplyTubeHostSidewaysRoms()
        {
            SetSidewaysRomBank(BasicRomBank, BasicRomPath);
            SetSidewaysRomBank(DfsRomBank, DfsRomPath);
        }

        private void UpdateAmxMouseRomState()
        {
            amxMouseRomLoaded = AmxMouseRomPath is not null
                && string.Equals(sidewaysRomPaths[AmxMouseRomBank], Path.GetFullPath(AmxMouseRomPath), StringComparison.OrdinalIgnoreCase);
            hostFilingSystem.MouseCommandFallbackEnabled = !amxMouseRomLoaded;
        }

        private static string GetRomRoot()
        {
            string romRoot = Path.Combine(AppContext.BaseDirectory, RomDirectory);
            if (!Directory.Exists(romRoot))
                romRoot = Path.Combine(Environment.CurrentDirectory, RomDirectory);

            if (!Directory.Exists(romRoot))
                throw new DirectoryNotFoundException($"ROM directory not found: {romRoot}");

            return romRoot;
        }

        private void LoadSidewaysRomBank(string path, int bank)
        {
            byte[] rom = ReadRomFileForBank(path);
            int bankOffset = bank * RomSize;

            for (int i = 0; i < RomSize; i++)
                sidewaysRoms[bankOffset + i] = rom[i % rom.Length];
        }

        private void SetSidewaysRomBank(int bank, string path)
        {
            if (bank < 0 || bank >= SidewaysRomBanks)
                throw new ArgumentOutOfRangeException(nameof(bank));

            string fullPath = Path.GetFullPath(path);
            LoadSidewaysRomBank(fullPath, bank);
            sidewaysRomPaths[bank] = fullPath;
            RefreshSidewaysRomSlot(bank);
        }

        private void ClearSidewaysRomBank(int bank)
        {
            if (bank < 0 || bank >= SidewaysRomBanks)
                throw new ArgumentOutOfRangeException(nameof(bank));

            int bankOffset = bank * RomSize;
            Array.Fill(sidewaysRoms, (byte)0xFF, bankOffset, RomSize);
            sidewaysRomPaths[bank] = null;
            RefreshSidewaysRomSlot(bank);
        }

        private void MoveSidewaysRomBank(int bank, int targetBank)
        {
            if (bank < 0 || bank >= SidewaysRomBanks)
                throw new ArgumentOutOfRangeException(nameof(bank));
            if (targetBank < 0 || targetBank >= SidewaysRomBanks)
                throw new ArgumentOutOfRangeException(nameof(targetBank));
            if (sidewaysRomPaths[bank] is null)
                throw new InvalidOperationException($"ROM bank {bank} is empty.");
            if (sidewaysRomPaths[targetBank] is not null)
                throw new InvalidOperationException($"ROM bank {targetBank} is not empty.");

            int sourceOffset = bank * RomSize;
            int targetOffset = targetBank * RomSize;
            Array.Copy(sidewaysRoms, sourceOffset, sidewaysRoms, targetOffset, RomSize);
            Array.Fill(sidewaysRoms, (byte)0xFF, sourceOffset, RomSize);
            sidewaysRomPaths[targetBank] = sidewaysRomPaths[bank];
            sidewaysRomPaths[bank] = null;
            RefreshSidewaysRomSlot(bank);
            RefreshSidewaysRomSlot(targetBank);
        }

        private void ExportSidewaysRomLayout(string path)
        {
            SidewaysRomLayoutFile.FromPaths(sidewaysRomPaths).Save(path);
        }

        private void ImportSidewaysRomLayout(string path)
        {
            SidewaysRomLayoutFile layout = SidewaysRomLayoutFile.Load(path);
            string?[] importedPaths = new string?[SidewaysRomBanks];

            foreach (SidewaysRomLayoutBank entry in layout.Banks)
            {
                if (entry.Bank < 0 || entry.Bank >= SidewaysRomBanks)
                    throw new InvalidDataException($"ROM layout bank {entry.Bank} is outside 0-15.");
                if (importedPaths[entry.Bank] is not null)
                    throw new InvalidDataException($"ROM layout contains bank {entry.Bank} more than once.");
                if (string.IsNullOrWhiteSpace(entry.Path))
                    throw new InvalidDataException($"ROM layout bank {entry.Bank} has no ROM path.");

                string fullPath = Path.GetFullPath(entry.Path);
                _ = ReadRomFileForBank(fullPath);
                importedPaths[entry.Bank] = fullPath;
            }

            for (int bank = 0; bank < SidewaysRomBanks; bank++)
            {
                if (importedPaths[bank] is null)
                    ClearSidewaysRomBank(bank);
                else
                    SetSidewaysRomBank(bank, importedPaths[bank]!);
            }

            selectedSidewaysRom = BasicRomBank;
            UpdateAmxMouseRomState();
            RefreshSidewaysRomSlots();
        }

        private void RefreshSidewaysRomSlots()
        {
            for (int bank = 0; bank < SidewaysRomBanks; bank++)
                RefreshSidewaysRomSlot(bank);
        }

        private void RefreshSidewaysRomSlot(int bank)
        {
            string? path = sidewaysRomPaths[bank];
            byte[]? rom = null;
            if (path is not null && File.Exists(path))
                rom = ReadRomFileForBank(path);

            sidewaysRomSlots[bank] = SidewaysRomHeader.Inspect(bank, path, rom);
        }

        private void RefreshSidewaysRomSlotsFromSavedBytes()
        {
            for (int bank = 0; bank < SidewaysRomBanks; bank++)
            {
                string? path = sidewaysRomPaths[bank];
                byte[] rom = new byte[RomSize];
                Array.Copy(sidewaysRoms, bank * RomSize, rom, 0, RomSize);
                sidewaysRomSlots[bank] = SidewaysRomHeader.Inspect(bank, path, IsEmptyRomBank(rom) ? null : rom);
            }
        }

        private static bool IsEmptyRomBank(byte[] rom)
        {
            for (int i = 0; i < rom.Length; i++)
            {
                if (rom[i] != 0xFF)
                    return false;
            }

            return true;
        }

        private static byte[] ReadRomFileForBank(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"ROM not found: {path}");

            byte[] rom = File.ReadAllBytes(path);
            if (rom.Length <= 0 || rom.Length > RomSize)
                throw new InvalidOperationException($"ROM '{path}' must be between 1 and {RomSize} bytes.");

            return rom;
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

                TraceExileRamWrite(addr, value);

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
            if (!IsOneMHzBusAddress(addr))
                return 0;

            return 1 + (int)(Cpu.TotalCycles & 1);
        }

        private static bool IsOneMHzBusAddress(ushort address)
        {
            if (address < IoStart || address > IoEnd)
                return false;

            if (address < 0xFE00)
                return true;

            return ((address >> 5) & 0x07) is 0 or 2 or 3 or 6;
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
                TraceUserVia("read", address, value);
                UpdateCpuIrqLine();
                return value;
            }

            if (Intel8271_Disk.IsAddress(address))
                return discController.Read(address);

            if (SerialACIA.IsAddress(address))
            {
                byte value = serialAcia.Read(address);
                TraceSerialPc("read", address, value);
                return value;
            }

            if (uPD7002_ADC.IsAddress(address))
                return adc.Read(address);

            if (TubeUla.IsHostAddress(address) && tube6502 is not null)
                return tubeUla.ReadHost(address);

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
                TraceUserVia("write", address, value);
                userVia.Write(address, value);
                UpdateCpuIrqLine();
                return;
            }

            if (Intel8271_Disk.IsAddress(address))
            {
                discController.Write(address, value);
                return;
            }

            if (SerialACIA.IsAddress(address))
            {
                TraceSerialPc("write", address, value);
                serialAcia.Write(address, value);
                return;
            }

            if (uPD7002_ADC.IsAddress(address))
            {
                adc.Write(address, value);
                return;
            }

            if (TubeUla.IsHostAddress(address) && tube6502 is not null)
            {
                tubeUla.WriteHost(address, value);
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

        private static bool IsDriveLoadPath(string path)
        {
            return IsDiscImagePath(path) || IsZipArchivePath(path);
        }

        private static bool IsTapeImagePath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".uef", StringComparison.OrdinalIgnoreCase);
        }

        private void TraceSerialPc(string operation, ushort address, byte value)
        {
            bool highSignal = address == 0xFE09
                || (address == 0xFE08 && operation == "read" && (value & 0x81) != 0);

            if (!SerialPcTraceEnabled && !(SerialPcTraceAutoEnabled && highSignal))
                return;

            string line = $"[serial-pc] PC ${Cpu.registers.PC & 0xFFFF:X4} {operation} ${address:X4} = ${value:X2} I={(Cpu.registers.Flags.I ? 1 : 0)} V={(Cpu.registers.Flags.V ? 1 : 0)}{FormatTapeStateTrace()}";
            if (line == lastSerialPcTraceLine)
                return;

            lastSerialPcTraceLine = line;
            if (string.IsNullOrWhiteSpace(SerialPcTracePath))
            {
                Console.WriteLine(line);
                return;
            }

            if (SerialPcTracePath == SerialACIA.TracePath)
            {
                SerialACIA.WriteTraceLine(line);
                return;
            }

            serialPcTraceWriter ??= new StreamWriter(SerialPcTracePath, append: false) { AutoFlush = true };
            serialPcTraceWriter.WriteLine(line);
        }

        private string FormatTapeStateTrace()
        {
            if (!SerialPcTraceAutoEnabled && !SerialPcTraceEnabled)
                return string.Empty;

            byte[] memory = Memory.Memory;
            return $" C2=${memory[0x00C2]:X2} BC=${memory[0x00BC]:X2} BD=${memory[0x00BD]:X2} C0=${memory[0x00C0]:X2} C8=${memory[0x03C8]:X2} C9=${memory[0x03C9]:X2}";
        }

        private void TraceSerialPcMessage(string line)
        {
            if (!SerialPcTraceEnabled && !SerialPcTraceAutoEnabled)
                return;

            if (line == lastSerialPcTraceLine)
                return;

            lastSerialPcTraceLine = line;
            if (string.IsNullOrWhiteSpace(SerialPcTracePath))
            {
                Console.WriteLine(line);
                return;
            }

            serialPcTraceWriter ??= new StreamWriter(SerialPcTracePath, append: false) { AutoFlush = true };
            serialPcTraceWriter.WriteLine(line);
        }

        private static bool IsZipArchivePath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ArchiveDiscEntry> GetArchiveDiscEntries(string path)
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            List<ArchiveDiscEntry> entries = new List<ArchiveDiscEntry>();

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length <= 0 || !IsDiscImagePath(entry.FullName))
                    continue;

                string folder = Path.GetDirectoryName(entry.FullName.Replace('\\', Path.DirectorySeparatorChar)) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(folder))
                    folder = "(root)";

                entries.Add(new ArchiveDiscEntry(folder, Path.GetFileName(entry.FullName), entry.FullName));
            }

            entries.Sort((left, right) =>
            {
                int folderCompare = string.Compare(left.Folder, right.Folder, StringComparison.OrdinalIgnoreCase);
                return folderCompare != 0
                    ? folderCompare
                    : string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
            });

            return entries;
        }

        private static byte[] ReadArchiveEntry(string archivePath, string entryPath)
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry entry = archive.GetEntry(entryPath)
                ?? throw new FileNotFoundException($"Archive entry not found: {entryPath}", entryPath);

            using Stream entryStream = entry.Open();
            using MemoryStream image = new MemoryStream();
            entryStream.CopyTo(image);
            return image.ToArray();
        }
    }
}
