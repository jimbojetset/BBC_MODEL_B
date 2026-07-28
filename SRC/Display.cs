// ============================================================================
// Project:     BBC
// File:        Display.cs
// Description: Host display and input boundary for BBC video frames, keyboard
//              matrix events, BREAK, disc drops, and joystick inputs.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace BBC
{

    public sealed class Display : IDisposable
    {
        public const int DefaultWidth = 768;
        public const int DefaultHeight = 576;
        private const int HorizontalBorderPercent = 5;
        private const int TopMenuHeight = 24;
        private const int MenuPaddingX = 10;
        private const int MenuTextCellWidth = 7;
        private const int MenuTextCellHeight = 11;
        private const int MenuItemHeight = 20;
        private const int MenuSeparatorHeight = 9;
        private const int MenuDropDownPadding = 5;
        private const int MenuShortcutGap = 18;
        private const int TubeMenuStatusGap = 6;
        private const string TubeMenuStatusLabel = "SECOND PROCESSOR";
        private const int TubeCoProcessorImageWidth = 93;
        private const int TubeCoProcessorImageHeight = 61;
        private const int TubeCoProcessorImageRightInset = 12;
        private const int TubeCoProcessorImageTopInset = 4;
        private const string TubeCoProcessorImageResourceName = "BBC.TubeCoProcessor.png";
        private const int BbcLogoLeftInset = 8;
        private const int BbcLogoTopInset = 10;
        private const byte BbcLogoAlpha = 128;
        private const string BbcLogoImageResourceName = "BBC.BBC_Logo.png";
        private const int CassetteLoadedScalePercent = 130;
        private const int CassetteLoadedOffsetX = 10;
        private const int CassetteLoadedOffsetY = 12;
        private const int CassetteLedOffsetX = 14;
        private const int CassetteLedOffsetY = 4;
        private const int CassetteLedDiameter = DriveLedDiameter - 2;
        private const string CassetteImageResourceName = "BBC.Cassette.png";
        private const string CassetteLoadedImageResourceName = "BBC.CassetteLoaded.png";
        private const int RomSlotColumns = 8;
        private const int RomSlotRows = 2;
        private const int RomSlotWidth = 58;
        private const int RomSlotHeight = 126;
        private const int RomSlotGapX = 11;
        private const int RomSlotGapY = 24;
        private const int RomPanelPadding = 16;
        private const int RomPanelTitleHeight = 24;
        private const int RomBankNumberHeight = 10;
        private const int RomLabelMaxCharacters = 14;
        private const int RomLayoutButtonWidth = InputActionButtonWidth;
        private const int RomLayoutButtonHeight = InputActionButtonHeight;
        private const int RomLayoutButtonBottomInset = 11;
        private const int DfsRomBank = 14;
        private const int BasicRomBank = 15;
        private const int RomActionWidth = 82;
        private const int RomActionRowHeight = 20;
        private const int ArchivePanelTopGap = 42;
        private const int ArchiveSearchHeight = 18;
        private const int InputPanelWidth = 780;
        private const int InputPanelHeight = 390;
        private const bool InputKeyEllipsisEnabled = false;
        private const int InputActionButtonWidth = 56;
        private const int InputShiftLockButtonWidth = 82;
        private const int InputActionButtonHeight = 20;
        private const int InputActionButtonGap = 6;
        private const int MaxRecentStateFiles = 5;
        private const byte BbcShiftKey = 0x00;
        private const byte BbcCapsLockKey = 0x40;
        private const uint Black = 0xFF000000;
        private const uint ScanlineColour = 0x40000000;
        private const int DriveLedDiameter = 6;
        private const int DriveGlyphWidth = 91;
        private const int DriveGlyphHeight = 22;
        private const int DriveGlyphMargin = 8;
        private const int DriveGlyphGap = 5;
        private const byte DriveGlyphBodyRed = 0xd7;
        private const byte DriveGlyphBodyGreen = 0xc7;
        private const byte DriveGlyphBodyBlue = 0x9a;
        private const int DriveNumberWidth = 3;
        private const int DriveNumberHeight = 5;
        private const int DriveNumberGap = 1;
        private const int StatusLedDiameter = 8;
        private const int StatusLedLeftMargin = 24;
        private const int StatusLedGap = 42;
        private const int StatusLabelGlyphWidth = 3;
        private const int StatusLabelGlyphHeight = 5;
        private const int StatusLabelGlyphGap = 1;
        private const int StatusLabelLineGap = 1;
        private const int StatusLabelLedGap = 2;
        private const int HayesPanelPaddingX = 8;
        private const int HayesPanelPaddingY = 5;
        private const int HayesPanelBrandGap = 10;
        private const int HayesPanelLedGap = 21;
        private const int HayesPanelLedCount = 8;
        private const int HayesMenuIndex = -2;
        private const int Drive0MenuIndex = -3;
        private const int Drive1MenuIndex = -4;
        private const int CassetteMenuIndex = -5;
        private const string HayesMenuTitle = "MODEM";
        private const int BottomOverlayPadding = 4;
        private const int BottomOverlayExtraHeight = 20;
        private const int BottomOverlayContentOffsetY = 20;
        private const byte OverlayTextGrey = 80;
        private const int NotificationDurationMilliseconds = 15000;
        private const int NotificationMargin = 28;
        private const int NotificationPadding = 18;
        private const int NotificationGap = 12;
        private const int NotificationTitleCellWidth = 16;
        private const int NotificationTitleCellHeight = 20;
        private const int NotificationBodyCellWidth = 12;
        private const int NotificationBodyCellHeight = 15;
        private const int NotificationGlyphWidth = 5;
        private const int NotificationGlyphHeight = 7;
        private const uint NotificationShadow = 0xFF000000;
        private const uint NotificationBackground = 0xFF101010;
        private const uint NotificationBorder = 0xFFE2E2E2;
        private const uint NotificationAccent = 0xFFFFD75E;
        private const uint NotificationTitleColour = 0xFFFFFFFF;
        private const uint NotificationBodyColour = 0xFFEAEAEA;
        private const short JoystickAxisThreshold = 12000;

        private readonly uint[] frameBuffer;
        private readonly Queue<byte> pendingInput = new Queue<byte>();
        private readonly Queue<BreakKeyPress> pendingBreaks = new Queue<BreakKeyPress>();
        private readonly Queue<HostKeyChange> pendingKeyChanges = new Queue<HostKeyChange>();
        private readonly Queue<HostJoystickChange> pendingJoystickChanges = new Queue<HostJoystickChange>();
        private readonly Queue<HostAnalogJoystickChange> pendingAnalogJoystickChanges = new Queue<HostAnalogJoystickChange>();
        private readonly Queue<HostDiscAction> pendingDiscActions = new Queue<HostDiscAction>();
        private readonly Queue<HostTapeAction> pendingTapeActions = new Queue<HostTapeAction>();
        private readonly Queue<HostStateAction> pendingStateActions = new Queue<HostStateAction>();
        private readonly Queue<HostRomAction> pendingRomActions = new Queue<HostRomAction>();
        private readonly Queue<string> pendingPrinterScreenshotPaths = new Queue<string>();
        private readonly HostJoystickSource[] joystickSources = new HostJoystickSource[Enum.GetValues<JoystickControl>().Length];
        private InputProfile inputProfile = InputProfile.CreateEmulatorDefault();
        private MenuDefinition[] menus = [];
        private readonly SidewaysRomSlot[] romSlots = new SidewaysRomSlot[16];
        private readonly List<string> recentStatePaths = new List<string>();
        private readonly List<ArchiveDiscEntry> archiveEntries = new List<ArchiveDiscEntry>();
        private string[] archiveFolders = [];
        private int pendingScreenshotRequests;
        private int pendingPrintScreenRequests;
        private int suppressedTextInputCharacters;
        private int pendingTraceToggleRequests;
        private int pendingSoundToggleRequests;
        private int pendingPauseToggleRequests;
        private int pendingTapePauseToggleRequests;
        private int pendingTapePlayerToggleRequests;
        private int pendingFrameAdvanceRequests;
        private int pendingDrive0ToggleRequests;
        private int pendingDrive1ToggleRequests;
        private int pendingTube6502ToggleRequests;
        private int pendingHayesModemToggleRequests;
        private int pendingPrinterToggleRequests;
        private int pendingHayesLoopbackToggleRequests;
        private int pendingHayesResetRequests;
        private int pendingPowerResetRequests;
        private HostMouseState mouseState;
        private bool relativeMouseMode;
        private bool printerEnabled;
        private readonly Dictionary<int, ActiveHostKey> activeHostKeys = new Dictionary<int, ActiveHostKey>();
        private readonly HashSet<int> textInputHostKeys = new HashSet<int>();
        private readonly Dictionary<byte, int> activeMatrixKeys = new Dictionary<byte, int>();
        private readonly Dictionary<CachedTextKey, CachedTextTexture> rendererTextCache = new Dictionary<CachedTextKey, CachedTextTexture>();
        private readonly Dictionary<CachedTextKey, CachedTextTexture> tinyTextCache = new Dictionary<CachedTextKey, CachedTextTexture>();
        private readonly int pitchBytes;
        private DotMatrixPrinter? printer;

        private IntPtr window;
        private uint windowId;
        private IntPtr renderer;
        private IntPtr texture;
        private IntPtr scanlineTexture;
        private IntPtr tubeCoProcessorTexture;
        private IntPtr bbcLogoTexture;
        private int bbcLogoTextureWidth;
        private int bbcLogoTextureHeight;
        private IntPtr cassetteTexture;
        private int cassetteTextureWidth;
        private int cassetteTextureHeight;
        private IntPtr cassetteLoadedTexture;
        private int cassetteLoadedTextureWidth;
        private int cassetteLoadedTextureHeight;
        private IntPtr emptyDriveGlyphTexture;
        private IntPtr mountedDriveGlyphTexture;
        private IntPtr emptyRomSocketTexture;
        private IntPtr occupiedRomSocketTexture;
        private IntPtr gameController;
        private IntPtr joystick;
        private int activeJoystickInstanceId = -1;
        private bool scanlinesEnabled;
        private bool showBbcLogo = true;
        private bool disposed;
        private bool hostCapsLockEnabled;
        private bool bbcShiftLockEnabled;
        private bool fullScreenEnabled;
        private int activeMenuIndex = -1;
        private int hoveredMenuIndex = -1;
        private int hoveredMenuItemIndex = -1;
        private int hoveredRomSlot = -1;
        private int activeRomSlot = -1;
        private int movingRomSlot = -1;
        private int infoRomSlot = -1;
        private int hoveredInputKey = -1;
        private int selectedInputKey = -1;
        private BbcShiftAdjustment selectedInputShiftAdjustment = BbcShiftAdjustment.Preserve;
        private string selectedInputLabel = string.Empty;
        private bool inputMapperOpen;
        private bool romManagerOpen;
        private bool inputProfileDirty;
        private string activeInputProfileName = "Default";
        private int logicalWidth;
        private int logicalHeight;
        private int uiMouseX = -1;
        private int uiMouseY = -1;
        private string archivePath = string.Empty;
        private int archiveDrive;
        private int activeArchiveFolder = -1;
        private int hoveredArchiveFolder = -1;
        private int hoveredArchiveEntry = -1;
        private int activeArchiveEntry = -1;
        private int archiveFolderScroll;
        private int archiveEntryScroll;
        private bool archiveEntryFocus;
        private string archiveSearchText = string.Empty;
        private SdlRect viewportRect;
        private string notificationTitle = string.Empty;
        private string notificationBody = string.Empty;
        private long notificationVisibleUntilTicks;
        private bool frameTextureDirty = true;
        private SdlRect frameTextureDirtyRect;

        static Display()
        {
            NativeLibrary.SetDllImportResolver(typeof(Display).Assembly, ResolveNativeLibrary);
        }

        public int Width { get; }

        public int Height { get; }

        public uint[] FrameBuffer => frameBuffer;

        public bool QuitRequested { get; private set; }

        public bool HostCapsLockEnabled => hostCapsLockEnabled;

        public bool Drive0ActivityLedActive { get; set; }

        public bool Drive1ActivityLedActive { get; set; }

        public bool Drive0Enabled { get; set; } = true;

        public bool Drive1Enabled { get; set; }

        public bool Drive0Mounted { get; set; }

        public bool Drive1Mounted { get; set; }

        public bool Drive0DoubleSided { get; set; }

        public bool Drive1DoubleSided { get; set; }

        public string? Drive0Label { get; set; }

        public string? Drive1Label { get; set; }

        public bool CassetteMotorLedActive { get; set; }

        public bool CapsLockLedActive { get; set; }

        public bool ShiftLockLedActive { get; set; }

        public bool EmulationPaused { get; set; }

        public bool SoundOutputEnabled { get; set; } = true;

        public bool TapePaused { get; set; }

        public bool TapeMounted { get; set; }

        public bool TapeRecordable { get; set; }

        public bool TapeRecording { get; set; }

        public bool TapePlaying { get; set; }

        public bool TapeFastTransportActive { get; set; }

        public int TapeCounter { get; set; }

        public string? TapeLabel { get; set; }

        public bool TapePlayerEnabled { get; set; }

        public bool Tube6502Enabled { get; set; }

        public bool HayesModemEnabled { get; set; }

        public bool PrinterEnabled
        {
            get => printerEnabled;
            set
            {
                if (printerEnabled == value)
                    return;

                printerEnabled = value;
                activeMenuIndex = -1;
                menus = CreateMenus();
            }
        }

        public bool HayesLoopbackEnabled { get; set; }

        public bool HayesHighSpeedLedActive { get; set; }

        public bool HayesAutoAnswerLedActive { get; set; }

        public bool HayesCarrierDetectLedActive { get; set; }

        public bool HayesOffHookLedActive { get; set; }

        public bool HayesReceiveDataLedActive { get; set; }

        public bool HayesSendDataLedActive { get; set; }

        public bool HayesTerminalReadyLedActive { get; set; }

        public bool HayesModemReadyLedActive { get; set; }

        public string DefaultSaveStateFileName { get; set; } = "bbc-untitled.sav";
        public Func<string>? DefaultSaveStateFileNameProvider { get; set; }

        public bool RomManagerOpen => romManagerOpen;

        public bool InputMapperOpen => inputMapperOpen;

        public void AttachPrinter(DotMatrixPrinter dotMatrixPrinter)
        {
            printer = dotMatrixPrinter;
        }

        public void ShowNotification(string title, string body, int durationMilliseconds = NotificationDurationMilliseconds)
        {
            notificationTitle = title.Trim();
            notificationBody = body.Trim();
            notificationVisibleUntilTicks = Stopwatch.GetTimestamp()
                + (Math.Max(1, durationMilliseconds) * Stopwatch.Frequency / 1000);
        }

        public Display(string title = "BBC Model B", int width = DefaultWidth, int height = DefaultHeight, bool scanlines = true)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            scanlinesEnabled = scanlines;
            activeInputProfileName = inputProfile.Name;
            pitchBytes = width * sizeof(uint);
            frameBuffer = new uint[width * height];
            Array.Fill(frameBuffer, Black);
            frameTextureDirtyRect = new SdlRect(0, 0, width, height);
            LoadRecentStatePaths();
            menus = CreateMenus();

            ThrowIfSdlFailed(SDL_InitSubSystem(SDL_INIT_VIDEO | SDL_INIT_GAMECONTROLLER | SDL_INIT_JOYSTICK), "SDL_InitSubSystem");
            int horizontalBorder = (int)Math.Round(width * HorizontalBorderPercent / 100.0);
            logicalWidth = width + (horizontalBorder * 2);
            logicalHeight = height + TopMenuHeight + GetBottomOverlayHeight();
            viewportRect = new SdlRect(horizontalBorder, TopMenuHeight, width, height);

            window = SDL_CreateWindow(
                title,
                SDL_WINDOWPOS_CENTERED,
                SDL_WINDOWPOS_CENTERED,
                logicalWidth,
                logicalHeight,
                SDL_WINDOW_SHOWN | SDL_WINDOW_RESIZABLE | SDL_WINDOW_ALLOW_HIGHDPI);
            ThrowIfNull(window, "SDL_CreateWindow");
            windowId = SDL_GetWindowID(window);

            renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
            if (renderer == IntPtr.Zero)
                renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_SOFTWARE | SDL_RENDERER_PRESENTVSYNC);
            ThrowIfNull(renderer, "SDL_CreateRenderer");

            ThrowIfSdlFailed(SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255), "SDL_SetRenderDrawColor");
            ThrowIfSdlFailed(SDL_SetRenderDrawBlendMode(renderer, SDL_BLENDMODE_BLEND), "SDL_SetRenderDrawBlendMode");
            ThrowIfSdlFailed(SDL_RenderSetLogicalSize(renderer, logicalWidth, logicalHeight), "SDL_RenderSetLogicalSize");
            _ = SDL_RenderSetIntegerScale(renderer, SDL_FALSE);

            texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STREAMING, width, height);
            ThrowIfNull(texture, "SDL_CreateTexture");

            scanlineTexture = CreateScanlineTexture(width, height);
            tubeCoProcessorTexture = CreateTubeCoProcessorTexture();
            bbcLogoTexture = CreateBbcLogoTexture();
            cassetteTexture = CreateCassetteTexture();
            cassetteLoadedTexture = CreateCassetteLoadedTexture();
            emptyDriveGlyphTexture = CreateDriveGlyphTexture(0xFF404040);
            mountedDriveGlyphTexture = CreateDriveGlyphTexture(0xFF005020);
            emptyRomSocketTexture = CreateRomSocketTexture(false);
            occupiedRomSocketTexture = CreateRomSocketTexture(true);
            for (int bank = 0; bank < romSlots.Length; bank++)
                romSlots[bank] = SidewaysRomHeader.Inspect(bank, null, null);

            SDL_StartTextInput();
            _ = SDL_GameControllerEventState(SDL_ENABLE);
            _ = SDL_JoystickEventState(SDL_ENABLE);
            OpenFirstGameInput();
            hostCapsLockEnabled = IsHostCapsLockEnabled();
            Present();
        }

        public bool PumpEvents()
        {
            while (SDL_PollEvent(out SdlEvent ev) != 0)
            {
                if (ev.Type == SDL_QUIT)
                {
                    QuitRequested = true;
                    continue;
                }

                if (ev.Type == SDL_WINDOWEVENT && ev.WindowId == windowId && ev.WindowEvent == SDL_WINDOWEVENT_CLOSE)
                {
                    QuitRequested = true;
                    continue;
                }

                if (ev.Type == SDL_DROPFILE)
                {
                    EnqueueDroppedFile(ev.DropFile);
                    continue;
                }

                if (printer?.HandleEvent(ev.Type, ev.WindowId, ev.WindowEvent, ev.MouseButton, ev.MouseX, ev.MouseY, ev.MouseWheelY) == true)
                    continue;

                if (ev.Type == SDL_TEXTINPUT)
                {
                    if (HandleArchiveTextInput(ev.Text))
                        continue;

                    HandleBbcTextInput(ev.Text);
                    continue;
                }

                if (ev.Type == SDL_KEYDOWN && ev.KeyRepeat == 0)
                    EnqueueKeyDown(ev.KeySym);

                if (ev.Type == SDL_KEYUP)
                    EnqueueKeyUp(ev.KeySym);

                if (ev.Type == SDL_MOUSEMOTION)
                {
                    if (HandleRomManagerMouseMotion(ev.MouseX, ev.MouseY))
                        continue;

                    if (HandleInputMapperMouseMotion(ev.MouseX, ev.MouseY))
                        continue;

                    if (HandleArchiveMouseMotion(ev.MouseX, ev.MouseY))
                        continue;

                    if (HandleMenuMouseMotion(ev.MouseX, ev.MouseY))
                        continue;

                    UpdateMouseState(ev.MouseX, ev.MouseY, ev.MouseRelativeX, ev.MouseRelativeY, mouseState.Buttons);
                }

                if (ev.Type is SDL_MOUSEBUTTONDOWN or SDL_MOUSEBUTTONUP)
                {
                    if (HandleRomManagerMouseButton(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY))
                        continue;

                    if (HandleInputMapperMouseButton(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY))
                        continue;

                    if (HandleArchiveMouseButton(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY))
                        continue;

                    if (HandleMenuMouseButton(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY))
                        continue;

                    UpdateMouseButtonState(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY);
                }

                if (ev.Type == SDL_MOUSEWHEEL && HandleArchiveMouseWheel(ev.MouseWheelY))
                    continue;

                if (ev.Type == SDL_CONTROLLERAXISMOTION)
                    UpdateControllerAxis(ev.ControllerAxis, ev.ControllerAxisValue);

                if (ev.Type is SDL_CONTROLLERBUTTONDOWN or SDL_CONTROLLERBUTTONUP)
                    UpdateControllerButton(ev.ControllerButton, ev.Type == SDL_CONTROLLERBUTTONDOWN);

                if (ev.Type == SDL_CONTROLLERDEVICEADDED || ev.Type == SDL_JOYDEVICEADDED)
                    OpenFirstGameInput();

                if (ev.Type == SDL_CONTROLLERDEVICEREMOVED || ev.Type == SDL_JOYDEVICEREMOVED)
                    HandleGameInputRemoved(ev.JoystickDeviceInstanceId);

                if (ev.Type == SDL_JOYAXISMOTION && gameController == IntPtr.Zero)
                    UpdateJoystickAxis(ev.JoystickAxis, ev.JoystickAxisValue);

                if (ev.Type == SDL_JOYHATMOTION && gameController == IntPtr.Zero)
                    UpdateJoystickHat(ev.JoystickHatValue);

                if (ev.Type is SDL_JOYBUTTONDOWN or SDL_JOYBUTTONUP && gameController == IntPtr.Zero)
                    UpdateJoystickButton(ev.JoystickButton, ev.Type == SDL_JOYBUTTONDOWN);
            }

            SyncHostCapsLockState();
            return !QuitRequested;
        }

        public int DrainInput(Span<byte> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingInput.Count > 0)
                destination[count++] = pendingInput.Dequeue();

            return count;
        }

        public int DrainBreaks(Span<BreakKeyPress> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingBreaks.Count > 0)
                destination[count++] = pendingBreaks.Dequeue();

            return count;
        }

        public int DrainKeyChanges(Span<HostKeyChange> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingKeyChanges.Count > 0)
                destination[count++] = pendingKeyChanges.Dequeue();

            return count;
        }

        public int DrainJoystickChanges(Span<HostJoystickChange> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingJoystickChanges.Count > 0)
                destination[count++] = pendingJoystickChanges.Dequeue();

            return count;
        }

        public int DrainAnalogJoystickChanges(Span<HostAnalogJoystickChange> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingAnalogJoystickChanges.Count > 0)
                destination[count++] = pendingAnalogJoystickChanges.Dequeue();

            return count;
        }

        public HostMouseState GetMouseState()
        {
            HostMouseState state = mouseState;
            mouseState = new HostMouseState(state.X, state.Y, state.Buttons, 0, 0);
            return state;
        }

        public bool RelativeMouseMode => relativeMouseMode;

        public void SetRelativeMouseMode(bool enabled)
        {
            if (relativeMouseMode == enabled)
                return;

            ThrowIfSdlFailed(SDL_SetRelativeMouseMode(enabled ? SDL_TRUE : SDL_FALSE), "SDL_SetRelativeMouseMode");
            relativeMouseMode = enabled;
            mouseState = new HostMouseState(mouseState.X, mouseState.Y, mouseState.Buttons, 0, 0);
        }

        public void DrainDiscActions(ICollection<HostDiscAction> destination)
        {
            while (pendingDiscActions.Count > 0)
                destination.Add(pendingDiscActions.Dequeue());
        }

        public void DrainTapeActions(ICollection<HostTapeAction> destination)
        {
            while (pendingTapeActions.Count > 0)
                destination.Add(pendingTapeActions.Dequeue());
        }

        public void DrainStateActions(ICollection<HostStateAction> destination)
        {
            while (pendingStateActions.Count > 0)
                destination.Add(pendingStateActions.Dequeue());
        }

        public void AddRecentState(string path)
        {
            string fullPath = Path.GetFullPath(path);
            recentStatePaths.RemoveAll(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase));
            recentStatePaths.Insert(0, fullPath);
            if (recentStatePaths.Count > MaxRecentStateFiles)
                recentStatePaths.RemoveRange(MaxRecentStateFiles, recentStatePaths.Count - MaxRecentStateFiles);

            SaveRecentStatePaths();
            menus = CreateMenus();
        }

        public void DrainRomActions(ICollection<HostRomAction> destination)
        {
            while (pendingRomActions.Count > 0)
                destination.Add(pendingRomActions.Dequeue());
        }

        public void SetRomSlots(IReadOnlyList<SidewaysRomSlot> slots)
        {
            int count = Math.Min(slots.Count, romSlots.Length);
            for (int i = 0; i < count; i++)
                romSlots[i] = slots[i];
        }

        public void ShowDiscArchive(string path, IReadOnlyList<ArchiveDiscEntry> entries, int drive)
        {
            archivePath = path;
            archiveDrive = drive;
            archiveEntries.Clear();
            archiveEntries.AddRange(entries);
            archiveSearchText = string.Empty;
            archiveFolders = GetFilteredArchiveFolders();
            activeArchiveFolder = archiveFolders.Length == 0 ? -1 : 0;
            activeArchiveEntry = 0;
            hoveredArchiveFolder = -1;
            hoveredArchiveEntry = -1;
            archiveFolderScroll = 0;
            archiveEntryScroll = 0;
            archiveEntryFocus = false;
            hoveredArchiveEntry = -1;
            archiveFolderScroll = 0;
            archiveEntryScroll = 0;
            activeMenuIndex = -1;
        }

        public int DrainScreenshotRequests()
        {
            int count = pendingScreenshotRequests;
            pendingScreenshotRequests = 0;
            return count;
        }

        public int DrainPrintScreenRequests()
        {
            int count = pendingPrintScreenRequests;
            pendingPrintScreenRequests = 0;
            return count;
        }

        public bool TryDrainPrinterScreenshot(out string path)
        {
            if (pendingPrinterScreenshotPaths.Count == 0)
            {
                path = string.Empty;
                return false;
            }

            path = pendingPrinterScreenshotPaths.Dequeue();
            return true;
        }

        public int DrainTraceToggleRequests()
        {
            int count = pendingTraceToggleRequests;
            pendingTraceToggleRequests = 0;
            return count;
        }

        public int DrainSoundToggleRequests()
        {
            int count = pendingSoundToggleRequests;
            pendingSoundToggleRequests = 0;
            return count;
        }

        public int DrainPauseToggleRequests()
        {
            int count = pendingPauseToggleRequests;
            pendingPauseToggleRequests = 0;
            return count;
        }

        public int DrainTapePauseToggleRequests()
        {
            int count = pendingTapePauseToggleRequests;
            pendingTapePauseToggleRequests = 0;
            return count;
        }

        public int DrainTapePlayerToggleRequests()
        {
            int count = pendingTapePlayerToggleRequests;
            pendingTapePlayerToggleRequests = 0;
            return count;
        }

        public int DrainFrameAdvanceRequests()
        {
            int count = pendingFrameAdvanceRequests;
            pendingFrameAdvanceRequests = 0;
            return count;
        }

        public int DrainDrive0ToggleRequests()
        {
            int count = pendingDrive0ToggleRequests;
            pendingDrive0ToggleRequests = 0;
            return count;
        }

        public int DrainDrive1ToggleRequests()
        {
            int count = pendingDrive1ToggleRequests;
            pendingDrive1ToggleRequests = 0;
            return count;
        }

        public int DrainTube6502ToggleRequests()
        {
            int count = pendingTube6502ToggleRequests;
            pendingTube6502ToggleRequests = 0;
            return count;
        }

        public int DrainHayesModemToggleRequests()
        {
            int count = pendingHayesModemToggleRequests;
            pendingHayesModemToggleRequests = 0;
            return count;
        }

        public int DrainPrinterToggleRequests()
        {
            int count = pendingPrinterToggleRequests;
            pendingPrinterToggleRequests = 0;
            return count;
        }

        public int DrainHayesLoopbackToggleRequests()
        {
            int count = pendingHayesLoopbackToggleRequests;
            pendingHayesLoopbackToggleRequests = 0;
            return count;
        }

        public int DrainHayesResetRequests()
        {
            int count = pendingHayesResetRequests;
            pendingHayesResetRequests = 0;
            return count;
        }

        public int DrainPowerResetRequests()
        {
            int count = pendingPowerResetRequests;
            pendingPowerResetRequests = 0;
            return count;
        }

        public void CopyFrame(ReadOnlySpan<uint> pixels)
        {
            if (pixels.Length != frameBuffer.Length)
                throw new ArgumentException("Frame length must match display dimensions.", nameof(pixels));

            pixels.CopyTo(frameBuffer);
            MarkFrameDirty();
        }

        public void MarkFrameDirty()
        {
            MarkFrameDirty(0, 0, Width, Height);
        }

        public void MarkFrameDirty(int x, int y, int width, int height)
        {
            int left = Math.Clamp(x, 0, Width);
            int top = Math.Clamp(y, 0, Height);
            int right = Math.Clamp(x + width, 0, Width);
            int bottom = Math.Clamp(y + height, 0, Height);
            if (right <= left || bottom <= top)
                return;

            SdlRect rect = new SdlRect(left, top, right - left, bottom - top);
            if (!frameTextureDirty)
            {
                frameTextureDirtyRect = rect;
                frameTextureDirty = true;
                return;
            }

            int unionLeft = Math.Min(frameTextureDirtyRect.X, rect.X);
            int unionTop = Math.Min(frameTextureDirtyRect.Y, rect.Y);
            int unionRight = Math.Max(frameTextureDirtyRect.X + frameTextureDirtyRect.W, rect.X + rect.W);
            int unionBottom = Math.Max(frameTextureDirtyRect.Y + frameTextureDirtyRect.H, rect.Y + rect.H);
            frameTextureDirtyRect = new SdlRect(unionLeft, unionTop, unionRight - unionLeft, unionBottom - unionTop);
        }

        public void Present()
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            UpdateFrameTextureIfDirty();

            ThrowIfSdlFailed(SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255), "SDL_SetRenderDrawColor");
            ThrowIfSdlFailed(SDL_RenderClear(renderer), "SDL_RenderClear");
            ThrowIfSdlFailed(SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref viewportRect), "SDL_RenderCopy");

            if (scanlinesEnabled && scanlineTexture != IntPtr.Zero)
                _ = SDL_RenderCopy(renderer, scanlineTexture, IntPtr.Zero, ref viewportRect);

            DrawTopBorderStatusMessage();
            DrawBbcLogo();
            DrawTubeCoProcessorImage();
            DrawDriveGlyphs();
            DrawMenuBar();
            if (IsBottomOverlayMenu(activeMenuIndex))
                DrawOpenMenu(activeMenuIndex);
            DrawRomManager();
            DrawArchiveBrowser();
            DrawInputMapper();

            SDL_RenderPresent(renderer);
            printer?.Render();
        }

        private void UpdateFrameTextureIfDirty()
        {
            if (!frameTextureDirty)
                return;

            GCHandle handle = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);
            try
            {
                if (frameTextureDirtyRect.X == 0
                    && frameTextureDirtyRect.Y == 0
                    && frameTextureDirtyRect.W == Width
                    && frameTextureDirtyRect.H == Height)
                {
                    ThrowIfSdlFailed(SDL_UpdateTexture(texture, IntPtr.Zero, handle.AddrOfPinnedObject(), pitchBytes), "SDL_UpdateTexture");
                }
                else
                {
                    IntPtr pixels = IntPtr.Add(handle.AddrOfPinnedObject(), ((frameTextureDirtyRect.Y * Width) + frameTextureDirtyRect.X) * sizeof(uint));
                    ThrowIfSdlFailed(SDL_UpdateTexture(texture, ref frameTextureDirtyRect, pixels, pitchBytes), "SDL_UpdateTexture");
                }
            }
            finally
            {
                handle.Free();
            }

            frameTextureDirty = false;
        }

        private void DrawTopBorderStatusMessage()
        {
            if (notificationVisibleUntilTicks <= Stopwatch.GetTimestamp()
                || (notificationTitle.Length == 0 && notificationBody.Length == 0))
            {
                return;
            }

            if (notificationBody.Length == 0)
            {
                DrawCenteredStatusText(TrimStatusText(notificationTitle), TopMenuHeight + 14, 220, 220, 220);
                return;
            }

            DrawCenteredStatusText(TrimStatusText(notificationTitle), TopMenuHeight + 8, 245, 245, 245);
            DrawCenteredStatusText(TrimStatusText(notificationBody), TopMenuHeight + 21, 160, 160, 160);
        }

        private void DrawCenteredStatusText(string text, int y, byte red, byte green, byte blue)
        {
            int x = Math.Max(0, (logicalWidth - GetRendererTextWidth(text)) / 2);
            DrawRendererText(text, x, y, red, green, blue);
        }

        private string TrimStatusText(string text)
        {
            int maxCharacters = Math.Max(1, (logicalWidth - 20) / MenuTextCellWidth);
            if (text.Length <= maxCharacters)
                return text;

            return maxCharacters <= 3 ? text[..maxCharacters] : text[..(maxCharacters - 3)] + "...";
        }

        private void DrawMenuBar()
        {
            SdlRect bar = new SdlRect(0, 0, logicalWidth, TopMenuHeight);
            _ = SDL_SetRenderDrawColor(renderer, 18, 18, 18, 255);
            _ = SDL_RenderFillRect(renderer, ref bar);

            SdlRect bottomLine = new SdlRect(0, TopMenuHeight - 1, logicalWidth, 1);
            _ = SDL_SetRenderDrawColor(renderer, 72, 72, 72, 255);
            _ = SDL_RenderFillRect(renderer, ref bottomLine);

            int x = MenuPaddingX;
            for (int i = 0; i < menus.Length; i++)
            {
                int width = GetTopMenuWidth(menus[i].Title);
                bool active = i == activeMenuIndex || i == hoveredMenuIndex;
                if (active)
                {
                    SdlRect hover = new SdlRect(x - 4, 3, width + 8, TopMenuHeight - 6);
                    _ = SDL_SetRenderDrawColor(renderer, 42, 42, 42, 255);
                    _ = SDL_RenderFillRect(renderer, ref hover);
                    _ = SDL_SetRenderDrawColor(renderer, 96, 96, 96, 255);
                    DrawRectOutline(hover);
                }

                DrawRendererText(menus[i].Title, x, 8, active ? (byte)245 : (byte)190, active ? (byte)245 : (byte)190, active ? (byte)245 : (byte)190);
                x += width + MenuPaddingX;
            }

            DrawTubeMenuStatus();

            if (activeMenuIndex >= 0 && !IsBottomOverlayMenu(activeMenuIndex))
                DrawOpenMenu(activeMenuIndex);

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        private void DrawTubeMenuStatus()
        {
            if (!Tube6502Enabled)
                return;

            int labelWidth = GetRendererTextWidth(TubeMenuStatusLabel);
            int totalWidth = StatusLedDiameter + TubeMenuStatusGap + labelWidth;
            int x = logicalWidth - MenuPaddingX - totalWidth;
            int ledCenterX = x + (StatusLedDiameter / 2);
            int ledCenterY = (TopMenuHeight / 2) - 1;
            int labelX = x + StatusLedDiameter + TubeMenuStatusGap;

            DrawRoundLed(ledCenterX, ledCenterY, StatusLedDiameter / 2, 220, 0, 0);
            DrawRendererText(TubeMenuStatusLabel, labelX, 8, 190, 190, 190);
        }

        private void DrawTubeCoProcessorImage()
        {
            if (!Tube6502Enabled || tubeCoProcessorTexture == IntPtr.Zero)
                return;

            SdlRect target = new SdlRect(
                logicalWidth - TubeCoProcessorImageWidth - TubeCoProcessorImageRightInset,
                TopMenuHeight + TubeCoProcessorImageTopInset,
                TubeCoProcessorImageWidth,
                TubeCoProcessorImageHeight);
            _ = SDL_RenderCopy(renderer, tubeCoProcessorTexture, IntPtr.Zero, ref target);
        }

        private void DrawBbcLogo()
        {
            if (!showBbcLogo || bbcLogoTexture == IntPtr.Zero)
                return;

            SdlRect target = new SdlRect(
                BbcLogoLeftInset,
                TopMenuHeight + BbcLogoTopInset,
                bbcLogoTextureWidth,
                bbcLogoTextureHeight);
            _ = SDL_RenderCopy(renderer, bbcLogoTexture, IntPtr.Zero, ref target);
        }

        private void DrawCassetteImage()
        {
            if (!TapePlayerEnabled || cassetteTexture == IntPtr.Zero)
                return;

            SdlRect target = GetCassetteImageRect();
            _ = SDL_RenderCopy(renderer, cassetteTexture, IntPtr.Zero, ref target);
            DrawCassetteLoadedImage(target);
            DrawCassetteLed(target, TapePlaying);
            DrawCassetteCounter(target);
        }

        private void DrawCassetteLoadedImage(SdlRect cassette)
        {
            if (!TapeMounted || cassetteLoadedTexture == IntPtr.Zero)
                return;

            SdlRect target = new SdlRect(
                cassette.X + CassetteLoadedOffsetX,
                cassette.Y + CassetteLoadedOffsetY,
                Math.Max(1, cassetteLoadedTextureWidth * CassetteLoadedScalePercent / 100),
                Math.Max(1, cassetteLoadedTextureHeight * CassetteLoadedScalePercent / 100));
            _ = SDL_RenderCopy(renderer, cassetteLoadedTexture, IntPtr.Zero, ref target);
        }

        private void DrawOpenMenu(int menuIndex)
        {
            MenuDefinition menu = GetMenuDefinition(menuIndex);
            int menuWidth = GetDropDownWidth(menu);
            int menuHeight = GetDropDownHeight(menu);
            int menuX = GetDropDownX(menuIndex, menuWidth);
            int menuY = GetDropDownY(menuIndex, menuHeight);

            SdlRect panel = new SdlRect(menuX, menuY, menuWidth, menuHeight);
            _ = SDL_SetRenderDrawColor(renderer, 24, 24, 24, 235);
            _ = SDL_RenderFillRect(renderer, ref panel);
            _ = SDL_SetRenderDrawColor(renderer, 150, 150, 150, 255);
            DrawRectOutline(panel);

            int itemY = menuY + MenuDropDownPadding;
            for (int i = 0; i < menu.Items.Length; i++)
            {
                MenuItem item = menu.Items[i];
                int itemHeight = GetMenuItemHeight(item);

                if (item.Separator)
                {
                    SdlRect line = new SdlRect(menuX + 8, itemY + (itemHeight / 2), menuWidth - 16, 1);
                    _ = SDL_SetRenderDrawColor(renderer, 96, 96, 96, 255);
                    _ = SDL_RenderFillRect(renderer, ref line);
                    itemY += itemHeight;
                    continue;
                }

                if (i == hoveredMenuItemIndex)
                {
                    SdlRect row = new SdlRect(menuX + 3, itemY - 2, menuWidth - 6, MenuItemHeight - 1);
                    _ = SDL_SetRenderDrawColor(renderer, 56, 56, 56, 255);
                    _ = SDL_RenderFillRect(renderer, ref row);
                }

                bool enabled = IsMenuItemEnabled(item);
                byte textGrey = enabled ? (byte)230 : (byte)96;
                string itemText = GetMenuItemText(item);
                string label = IsMenuItemChecked(item.Command) ? "* " + itemText : "  " + itemText;
                int textX = menuX + 10;
                TransportSymbol symbol = GetMenuItemSymbol(item);
                if (symbol != TransportSymbol.None)
                {
                    DrawTransportSymbol(symbol, menuX + 13, itemY + 5, textGrey, textGrey, textGrey);
                    textX += 18;
                }

                DrawRendererText(label, textX, itemY + 4, textGrey, textGrey, textGrey);

                if (item.Shortcut.Length > 0)
                {
                    int shortcutX = menuX + menuWidth - 10 - GetRendererTextWidth(item.Shortcut);
                    DrawRendererText(item.Shortcut, shortcutX, itemY + 4, 160, 160, 160);
                }

                itemY += itemHeight;
            }
        }

        private void DrawTransportSymbol(TransportSymbol symbol, int x, int y, byte red, byte green, byte blue)
        {
            _ = SDL_SetRenderDrawColor(renderer, red, green, blue, 255);

            switch (symbol)
            {
                case TransportSymbol.Record:
                    DrawRoundLed(x + 5, y + 5, 4, red, green, blue);
                    break;
                case TransportSymbol.Play:
                    FillRightTriangle(x + 2, y + 1, 8, 9);
                    break;
                case TransportSymbol.Rewind:
                    FillLeftTriangle(x + 0, y + 1, 7, 9);
                    FillLeftTriangle(x + 6, y + 1, 7, 9);
                    break;
                case TransportSymbol.Cue:
                    FillRightTriangle(x + 0, y + 1, 7, 9);
                    FillRightTriangle(x + 6, y + 1, 7, 9);
                    break;
                case TransportSymbol.Stop:
                    FillMenuRect(x + 2, y + 2, 8, 8);
                    break;
                case TransportSymbol.Eject:
                    FillUpTriangle(x + 2, y + 1, 8, 6);
                    FillMenuRect(x + 1, y + 8, 10, 2);
                    break;
                case TransportSymbol.Pause:
                    FillMenuRect(x + 2, y + 1, 3, 9);
                    FillMenuRect(x + 7, y + 1, 3, 9);
                    break;
                case TransportSymbol.CounterReset:
                    DrawTinyText("000", x, y + 3, red, green, blue);
                    break;
            }

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        private void FillMenuRect(int x, int y, int width, int height)
        {
            SdlRect rect = new SdlRect(x, y, width, height);
            _ = SDL_RenderFillRect(renderer, ref rect);
        }

        private void FillRightTriangle(int x, int y, int width, int height)
        {
            for (int row = 0; row < height; row++)
            {
                int rowWidth = Math.Max(1, (row + 1) * width / height);
                FillMenuRect(x, y + row, rowWidth, 1);
            }
        }

        private void FillLeftTriangle(int x, int y, int width, int height)
        {
            for (int row = 0; row < height; row++)
            {
                int rowWidth = Math.Max(1, (row + 1) * width / height);
                FillMenuRect(x + width - rowWidth, y + row, rowWidth, 1);
            }
        }

        private void FillUpTriangle(int x, int y, int width, int height)
        {
            for (int row = 0; row < height; row++)
            {
                int rowWidth = Math.Max(1, width - (row * width / height));
                FillMenuRect(x + ((width - rowWidth) / 2), y + row, rowWidth, 1);
            }
        }

        private void DrawArchiveBrowser()
        {
            if (archiveEntries.Count == 0)
                return;

            SdlRect panel = GetArchivePanelRect();
            _ = SDL_SetRenderDrawColor(renderer, 24, 24, 24, 235);
            _ = SDL_RenderFillRect(renderer, ref panel);
            _ = SDL_SetRenderDrawColor(renderer, 150, 150, 150, 255);
            DrawRectOutline(panel);

            string title = TrimRendererText(Path.GetFileName(archivePath), 48);
            DrawRendererText(title, panel.X + 12, panel.Y + 10, 235, 235, 235);
            DrawRendererText($"Drive {archiveDrive}", panel.X + panel.W - 72, panel.Y + 10, 170, 170, 170);

            SdlRect search = new SdlRect(panel.X + 12, panel.Y + 32, panel.W - 24, ArchiveSearchHeight);
            _ = SDL_SetRenderDrawColor(renderer, 8, 8, 8, 255);
            _ = SDL_RenderFillRect(renderer, ref search);
            _ = SDL_SetRenderDrawColor(renderer, 160, 160, 120, 255);
            DrawRectOutline(search);
            int searchColumns = Math.Max(1, (search.W - 10) / MenuTextCellWidth);
            string searchText = archiveSearchText.Length == 0 ? "Type to search" : archiveSearchText;
            string visibleSearchText = TrimRendererText(searchText, searchColumns);
            int searchTextX = archiveSearchText.Length == 0 ? search.X + 17 : search.X + 5;
            DrawRendererText(visibleSearchText, searchTextX, search.Y + 4,
                archiveSearchText.Length == 0 ? (byte)95 : (byte)220,
                archiveSearchText.Length == 0 ? (byte)95 : (byte)220,
                archiveSearchText.Length == 0 ? (byte)95 : (byte)220);
            int caretX = archiveSearchText.Length == 0
                ? search.X + 6
                : Math.Min(search.X + search.W - 5, search.X + 5 + GetRendererTextWidth(visibleSearchText));
            SdlRect caret = new SdlRect(caretX, search.Y + 4, 1, MenuTextCellHeight);
            _ = SDL_SetRenderDrawColor(renderer, 230, 230, 170, 255);
            _ = SDL_RenderFillRect(renderer, ref caret);

            int listY = panel.Y + 58;
            int listHeight = panel.H - 70;
            int folderWidth = 150;
            int visibleRows = GetArchiveVisibleRows();
            SdlRect divider = new SdlRect(panel.X + folderWidth, listY, 1, listHeight);
            _ = SDL_SetRenderDrawColor(renderer, 72, 72, 72, 255);
            _ = SDL_RenderFillRect(renderer, ref divider);

            for (int rowIndex = 0; rowIndex < visibleRows; rowIndex++)
            {
                int i = archiveFolderScroll + rowIndex;
                if (i >= archiveFolders.Length)
                    break;

                int rowY = listY + (rowIndex * MenuItemHeight);
                if (i == activeArchiveFolder || i == hoveredArchiveFolder)
                {
                    SdlRect row = new SdlRect(panel.X + 6, rowY - 1, folderWidth - 12, MenuItemHeight - 1);
                    bool focused = !archiveEntryFocus && i == activeArchiveFolder;
                    _ = SDL_SetRenderDrawColor(renderer, focused ? (byte)78 : i == activeArchiveFolder ? (byte)58 : (byte)42, 58, 58, 255);
                    _ = SDL_RenderFillRect(renderer, ref row);
                }

                DrawRendererText(TrimRendererText(archiveFolders[i], 18), panel.X + 12, rowY + 4, 220, 220, 220);
            }

            string folder = activeArchiveFolder >= 0 && activeArchiveFolder < archiveFolders.Length
                ? archiveFolders[activeArchiveFolder]
                : string.Empty;
            List<ArchiveDiscEntry> discs = GetArchiveFolderEntries(folder);
            int discX = panel.X + folderWidth + 12;
            int discColumns = Math.Max(8, (panel.X + panel.W - 12 - discX) / MenuTextCellWidth);
            if (archiveFolders.Length == 0)
                DrawRendererText("No matches", discX, listY + 4, 140, 140, 140);

            for (int rowIndex = 0; rowIndex < visibleRows; rowIndex++)
            {
                int i = archiveEntryScroll + rowIndex;
                if (i >= discs.Count)
                    break;

                int rowY = listY + (rowIndex * MenuItemHeight);
                if (i == activeArchiveEntry || i == hoveredArchiveEntry)
                {
                    SdlRect row = new SdlRect(discX - 6, rowY - 1, panel.X + panel.W - discX - 6, MenuItemHeight - 1);
                    bool focused = archiveEntryFocus && i == activeArchiveEntry;
                    _ = SDL_SetRenderDrawColor(renderer, focused ? (byte)78 : i == activeArchiveEntry ? (byte)58 : (byte)54, 54, 54, 255);
                    _ = SDL_RenderFillRect(renderer, ref row);
                }

                DrawRendererText(TrimRendererText(discs[i].FileName, discColumns), discX, rowY + 4, 230, 230, 230);
            }
        }

        private void DrawInputMapper()
        {
            if (!inputMapperOpen)
                return;

            SdlRect panel = GetInputPanelRect();
            _ = SDL_SetRenderDrawColor(renderer, 24, 24, 24, 235);
            _ = SDL_RenderFillRect(renderer, ref panel);
            _ = SDL_SetRenderDrawColor(renderer, 150, 150, 150, 255);
            DrawRectOutline(panel);

            DrawRendererText("Input Mapper", panel.X + 14, panel.Y + 12, 240, 240, 240);
            DrawRendererText(TrimRendererText(inputProfileDirty ? "Unsaved" : inputProfile.Name, 40), panel.X + 130, panel.Y + 12, 170, 170, 170);

            string prompt = selectedInputKey >= 0
                ? $"Press host key for {GetSelectedInputLabel()}"
                : "Click a BBC key to bind";
            DrawRendererText(prompt, panel.X + 14, panel.Y + panel.H - 40, 220, 220, 160);
            DrawRendererText("White = BBC Keys;", panel.X + 14, panel.Y + panel.H - 26, 235, 235, 235);
            DrawRendererText(" Green =Locally mapped keys.", panel.X + 14 + GetRendererTextWidth("White = BBC Keys;"), panel.Y + panel.H - 26, 150, 190, 150);
            DrawInputMapperActionButtons(panel);

            for (int i = 0; i < InputKeys.Length; i++)
                DrawInputKey(panel, i, InputKeys[i]);
        }

        private void DrawInputMapperActionButtons(SdlRect panel)
        {
            DrawInputMapperActionButton(GetInputShiftLockButtonRect(panel), "SHIFT LOCK", bbcShiftLockEnabled);
            DrawInputMapperActionButton(GetInputLoadMapButtonRect(panel), "Import");
            DrawInputMapperActionButton(GetInputSaveMapButtonRect(panel), "Export");
            DrawInputMapperActionButton(GetInputResetMapButtonRect(panel), "Reset");
        }

        private void DrawInputMapperActionButton(SdlRect rect, string label, bool active = false)
        {
            bool hovered = uiMouseX >= rect.X && uiMouseX < rect.X + rect.W && uiMouseY >= rect.Y && uiMouseY < rect.Y + rect.H;
            byte fillR = active ? hovered ? (byte)72 : (byte)56 : hovered ? (byte)42 : (byte)24;
            byte fillG = active ? hovered ? (byte)72 : (byte)56 : hovered ? (byte)42 : (byte)24;
            byte fillB = active ? hovered ? (byte)36 : (byte)28 : hovered ? (byte)42 : (byte)24;
            _ = SDL_SetRenderDrawColor(renderer, fillR, fillG, fillB, 255);
            _ = SDL_RenderFillRect(renderer, ref rect);
            _ = SDL_SetRenderDrawColor(renderer, active ? (byte)210 : (byte)125, active ? (byte)190 : (byte)125, active ? (byte)90 : (byte)125, 255);
            DrawRectOutline(rect);
            DrawRendererText(label, rect.X + 6, rect.Y + 5, 230, 230, active ? (byte)160 : (byte)230);
        }

        private void DrawInputKey(SdlRect panel, int index, BbcInputKey key)
        {
            SdlRect rect = GetInputKeyRect(panel, key);
            bool hovered = index == hoveredInputKey;
            BbcKeyBinding binding = GetInputMapperBinding(key);
            bool selected = selectedInputKey == binding.InternalKey
                && selectedInputShiftAdjustment == binding.ShiftAdjustment;
            bool functionKey = IsFunctionInputKey(key);
            bool oliveKey = IsOliveInputKey(key);
            byte fillR = selected ? (byte)48 : hovered ? (byte)30 : (byte)0;
            byte fillG = selected ? (byte)48 : hovered ? (byte)30 : (byte)0;
            byte fillB = selected ? (byte)48 : hovered ? (byte)30 : (byte)0;

            if (functionKey)
            {
                fillR = selected ? (byte)188 : hovered ? (byte)172 : (byte)154;
                fillG = selected ? (byte)54 : hovered ? (byte)46 : (byte)38;
                fillB = selected ? (byte)44 : hovered ? (byte)36 : (byte)30;
            }
            else if (oliveKey)
            {
                fillR = selected ? (byte)123 : hovered ? (byte)113 : (byte)103;
                fillG = selected ? (byte)123 : hovered ? (byte)113 : (byte)103;
                fillB = selected ? (byte)100 : hovered ? (byte)92 : (byte)84;
            }

            _ = SDL_SetRenderDrawColor(renderer, fillR, fillG, fillB, 255);
            _ = SDL_RenderFillRect(renderer, ref rect);
            _ = SDL_SetRenderDrawColor(renderer, selected ? (byte)245 : (byte)125, selected ? (byte)220 : (byte)125, selected ? (byte)130 : (byte)125, 255);
            DrawRectOutline(rect);

            DrawRendererText(FormatInputKeyText(GetInputMapperKeyLabel(key), rect), rect.X + 4, rect.Y + 4, 235, 235, 235);
            string hostKey = inputProfile.GetPrimaryHostKeyName(binding.InternalKey, binding.ShiftAdjustment);
            if (hostKey.Length > 0)
                DrawRendererText(FormatInputKeyText(hostKey, rect), rect.X + 4, rect.Y + 17, 150, 190, 150);
        }

        private static string FormatInputKeyText(string text, SdlRect keyRect)
        {
            text = ShortInputKeyText(text);
            int maxCharacters = keyRect.W >= 52 ? 8 : 5;
            return InputKeyEllipsisEnabled
                ? TrimRendererText(text, maxCharacters)
                : text.Length > maxCharacters ? text[..maxCharacters] : text;
        }

        private static string ShortInputKeyText(string text)
        {
            return text switch
            {
                "Escape" => "Esc",
                "Caps Lock" => "Caps",
                "Left Ctrl" => "LCtrl",
                "Right Ctrl" => "RCtrl",
                "Left Shift" => "L Shift",
                "Right Shift" => "R Shift",
                "Backspace" => "Del",
                "Delete" => "Del",
                "Insert" => "Ins",
                "Section" => "Sect",
                "Keypad *" => "KP *",
                "Keypad Enter" => "KP Enter",
                _ => text
            };
        }

        private string GetSelectedInputLabel()
        {
            return selectedInputLabel.Length > 0
                ? selectedInputLabel
                : BbcKeyLabel((byte)selectedInputKey);
        }

        private BbcKeyBinding GetInputMapperBinding(BbcInputKey key)
        {
            string label = GetInputMapperKeyLabel(key);
            return bbcShiftLockEnabled
                && label.Length == 1
                && BbcKeyboard.TryMapCharacter(label[0], out BbcKeyBinding shifted)
                ? shifted
                : new BbcKeyBinding(key.InternalKey, BbcShiftAdjustment.Preserve);
        }

        private string GetInputMapperKeyLabel(BbcInputKey key)
        {
            return bbcShiftLockEnabled && TryGetShiftedInputKeyLabel(key.Label, out string shifted)
                ? shifted
                : key.Label;
        }

        private static bool TryGetShiftedInputKeyLabel(string label, out string shifted)
        {
            shifted = label switch
            {
                "1" => "!",
                "2" => "\"",
                "3" => "#",
                "4" => "$",
                "5" => "%",
                "6" => "^",
                "7" => "&",
                "8" => "*",
                "9" => "(",
                "0" => ")",
                "-" => "=",
                "^" => "~",
                "@" => "`",
                "[" => "{",
                "_" => "£",
                "]" => "}",
                ":" => "*",
                ";" => ":",
                "," => "<",
                "." => ">",
                "/" => "?",
                "\\" => "|",
                _ => string.Empty
            };

            return shifted.Length > 0;
        }

        private static bool IsFunctionInputKey(BbcInputKey key)
        {
            return key.InternalKey is 0x14 or 0x16 or 0x20 or >= 0x71 and <= 0x77;
        }

        private static bool IsOliveInputKey(BbcInputKey key)
        {
            return key.InternalKey is 0x19 or 0x29 or 0x39 or 0x69 or 0x79;
        }

        private bool HandleInputMapperMouseMotion(int hostX, int hostY)
        {
            if (!inputMapperOpen)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            uiMouseX = (int)Math.Round(logicalX);
            uiMouseY = (int)Math.Round(logicalY);
            if (uiMouseY < TopMenuHeight)
                return false;

            hoveredInputKey = GetInputMapperActionAt(uiMouseX, uiMouseY) == InputMapperAction.None
                ? GetInputKeyIndexAt(uiMouseX, uiMouseY)
                : -1;
            return IsInInputPanel(uiMouseX, uiMouseY);
        }

        private bool HandleInputMapperMouseButton(byte button, bool pressed, int hostX, int hostY)
        {
            if (!inputMapperOpen || button != SDL_BUTTON_LEFT)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            int x = (int)logicalX;
            int y = (int)logicalY;
            if (y < TopMenuHeight)
                return false;

            if (!pressed)
                return IsInInputPanel(x, y);

            InputMapperAction action = GetInputMapperActionAt(x, y);
            if (action != InputMapperAction.None)
            {
                ExecuteInputMapperAction(action);
                return true;
            }

            int keyIndex = GetInputKeyIndexAt(x, y);
            if (keyIndex >= 0)
            {
                BbcInputKey clickedKey = InputKeys[keyIndex];
                BbcKeyBinding binding = GetInputMapperBinding(clickedKey);
                bool alreadySelected = selectedInputKey == binding.InternalKey
                    && selectedInputShiftAdjustment == binding.ShiftAdjustment;
                selectedInputKey = alreadySelected ? -1 : binding.InternalKey;
                selectedInputShiftAdjustment = alreadySelected ? BbcShiftAdjustment.Preserve : binding.ShiftAdjustment;
                selectedInputLabel = alreadySelected ? string.Empty : GetInputMapperKeyLabel(clickedKey);
                return true;
            }

            if (selectedInputKey >= 0)
            {
                ClearInputMapperSelection();
                return true;
            }

            if (!IsInInputPanel(x, y))
            {
                inputMapperOpen = false;
                ClearInputMapperSelection();
            }

            return true;
        }

        private void ExecuteInputMapperAction(InputMapperAction action)
        {
            ClearInputMapperSelection();
            switch (action)
            {
                case InputMapperAction.ToggleShiftLock:
                    ToggleBbcShiftLock();
                    break;
                case InputMapperAction.Load:
                    LoadInputProfile();
                    break;
                case InputMapperAction.Save:
                    SaveInputProfile();
                    break;
                case InputMapperAction.Reset:
                    ResetInputProfile();
                    break;
            }
        }

        private void ClearInputMapperSelection()
        {
            selectedInputKey = -1;
            selectedInputShiftAdjustment = BbcShiftAdjustment.Preserve;
            selectedInputLabel = string.Empty;
        }

        private void HandleInputMapperKeyDown(int keySym, int modifiers)
        {
            if (keySym == SDLK_ESCAPE)
            {
                inputMapperOpen = false;
                ClearInputMapperSelection();
                return;
            }

            if (keySym == SDLK_S && (modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
            {
                SaveInputProfile();
                return;
            }

            if (selectedInputKey < 0)
                return;

            ClearLiveInputState();
            inputProfile.BindHostKey(keySym, (byte)selectedInputKey, selectedInputShiftAdjustment);
            inputProfileDirty = true;
            ShowNotification(
                "Input mapped",
                $"{SdlKey.GetName(keySym)} -> {GetSelectedInputLabel()}",
                2000);
            ClearInputMapperSelection();
        }

        private bool HandleMenuMouseMotion(int hostX, int hostY)
        {
            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            uiMouseX = (int)Math.Round(logicalX);
            uiMouseY = (int)Math.Round(logicalY);

            hoveredMenuIndex = GetMenuIndexAt((int)logicalX, (int)logicalY);
            hoveredMenuItemIndex = IsOpenMenuIndex(activeMenuIndex)
                ? GetMenuItemIndexAt(activeMenuIndex, (int)logicalX, (int)logicalY)
                : -1;

            if (IsOpenMenuIndex(activeMenuIndex) && IsOpenMenuIndex(hoveredMenuIndex))
            {
                activeMenuIndex = hoveredMenuIndex;
                hoveredMenuItemIndex = GetMenuItemIndexAt(activeMenuIndex, (int)logicalX, (int)logicalY);
            }

            return IsMenuArea((int)logicalX, (int)logicalY);
        }

        private bool HandleArchiveMouseMotion(int hostX, int hostY)
        {
            if (archiveEntries.Count == 0)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            uiMouseX = (int)Math.Round(logicalX);
            uiMouseY = (int)Math.Round(logicalY);
            hoveredArchiveFolder = GetArchiveFolderIndexAt(uiMouseX, uiMouseY);
            hoveredArchiveEntry = GetArchiveEntryIndexAt(uiMouseX, uiMouseY);
            return true;
        }

        private bool HandleMenuMouseButton(byte button, bool pressed, int hostX, int hostY)
        {
            if (button != SDL_BUTTON_LEFT)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            int x = (int)logicalX;
            int y = (int)logicalY;

            if (!pressed)
                return IsMenuArea(x, y);

            int menuIndex = GetMenuIndexAt(x, y);
            if (IsDirectMenu(menuIndex))
            {
                ExecuteMenuCommand(menus[menuIndex].DirectCommand!.Value);
                activeMenuIndex = -1;
                hoveredMenuIndex = menuIndex;
                hoveredMenuItemIndex = -1;
                activeRomSlot = -1;
                movingRomSlot = -1;
                hoveredRomSlot = -1;
                infoRomSlot = -1;
                return true;
            }

            if (IsOpenMenuIndex(menuIndex))
            {
                CloseWindowPanels();
                activeMenuIndex = activeMenuIndex == menuIndex ? -1 : menuIndex;
                hoveredMenuIndex = menuIndex;
                hoveredMenuItemIndex = -1;
                return true;
            }

            if (IsOpenMenuIndex(activeMenuIndex))
            {
                int itemIndex = GetMenuItemIndexAt(activeMenuIndex, x, y);
                if (itemIndex >= 0)
                {
                    MenuItem item = GetMenuDefinition(activeMenuIndex).Items[itemIndex];
                    if (IsMenuItemEnabled(item))
                        ExecuteMenuCommand(item.Command);

                    activeMenuIndex = -1;
                    hoveredMenuItemIndex = -1;
                    return true;
                }

                activeMenuIndex = -1;
                hoveredMenuItemIndex = -1;
                return true;
            }

            return false;
        }

        private bool HandleArchiveMouseButton(byte button, bool pressed, int hostX, int hostY)
        {
            if (archiveEntries.Count == 0 || button != SDL_BUTTON_LEFT)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            int x = (int)logicalX;
            int y = (int)logicalY;
            if (!pressed)
                return true;

            int folderIndex = GetArchiveFolderIndexAt(x, y);
            if (folderIndex >= 0)
            {
                activeArchiveFolder = folderIndex;
                activeArchiveEntry = 0;
                hoveredArchiveEntry = -1;
                archiveEntryScroll = 0;
                archiveEntryFocus = false;
                return true;
            }

            int entryIndex = GetArchiveEntryIndexAt(x, y);
            if (entryIndex >= 0)
            {
                archiveEntryFocus = true;
                activeArchiveEntry = entryIndex;
                string folder = archiveFolders[activeArchiveFolder];
                ArchiveDiscEntry entry = GetArchiveFolderEntries(folder)[entryIndex];
                pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.MountArchiveEntry, archivePath, archiveDrive, entry.EntryPath));
                CloseArchiveBrowser();
                return true;
            }

            if (!IsInArchivePanel(x, y))
                CloseArchiveBrowser();

            return true;
        }

        private bool HandleArchiveMouseWheel(int wheelY)
        {
            if (archiveEntries.Count == 0 || wheelY == 0)
                return false;

            SdlRect panel = GetArchivePanelRect();
            int folderWidth = 150;
            int rows = GetArchiveVisibleRows();
            int direction = wheelY > 0 ? -1 : 1;
            if (!IsInArchivePanel(uiMouseX, uiMouseY))
                return true;

            if (uiMouseX >= panel.X && uiMouseX < panel.X + folderWidth)
            {
                archiveFolderScroll = ClampScroll(archiveFolderScroll + direction, archiveFolders.Length, rows);
                return true;
            }

            if (activeArchiveFolder >= 0 && activeArchiveFolder < archiveFolders.Length)
            {
                int count = GetArchiveFolderEntries(archiveFolders[activeArchiveFolder]).Count;
                archiveEntryScroll = ClampScroll(archiveEntryScroll + direction, count, rows);
                return true;
            }

            return true;
        }

        private bool HandleArchiveTextInput(byte[] textBytes)
        {
            if (archiveEntries.Count == 0)
                return false;

            string text = DecodeSdlText(textBytes);
            if (text.Length == 0)
                return true;

            foreach (char ch in text)
            {
                if (!char.IsControl(ch))
                    archiveSearchText += ch;
            }

            UpdateArchiveFilter();
            return true;
        }

        private void HandleBbcTextInput(byte[] textBytes)
        {
            string text = DecodeSdlText(textBytes);
            if (text.Length == 0)
                return;

            foreach (char ch in text)
            {
                if (suppressedTextInputCharacters > 0)
                {
                    suppressedTextInputCharacters--;
                    continue;
                }

                if (ch == '£')
                {
                    _ = BbcKeyboard.TryMapCharacter(ch, out BbcKeyBinding poundKey);
                    bool hostShiftDown = (SDL_GetModState() & KMOD_SHIFT) != 0;
                    bool shiftAdjusted = ApplyShiftAdjustment(poundKey.ShiftAdjustment, hostShiftDown);
                    PressBbcMatrixKey(poundKey.MatrixKey);
                    ReleaseBbcMatrixKey(poundKey.MatrixKey);
                    RestoreAdjustedShift(
                        new ActiveHostKey(poundKey.MatrixKey, poundKey.ShiftAdjustment, shiftAdjusted),
                        hostShiftDown);
                    continue;
                }

                EnqueueHostText(ch.ToString());
            }
        }

        private bool HandleArchiveKeyDown(int keySym)
        {
            if (archiveEntries.Count == 0)
                return false;

            switch (keySym)
            {
                case SDLK_ESCAPE:
                    CloseArchiveBrowser();
                    return true;

                case SDLK_BACKSPACE:
                case SDLK_DELETE:
                    if (archiveSearchText.Length > 0)
                    {
                        archiveSearchText = archiveSearchText[..^1];
                        UpdateArchiveFilter();
                    }
                    return true;

                case SDLK_LEFT:
                    archiveEntryFocus = false;
                    EnsureArchiveSelectionVisible();
                    return true;

                case SDLK_RIGHT:
                    if (GetActiveArchiveFolderEntries().Count > 0)
                        archiveEntryFocus = true;
                    EnsureArchiveSelectionVisible();
                    return true;

                case SDLK_UP:
                    MoveArchiveSelection(-1);
                    return true;

                case SDLK_DOWN:
                    MoveArchiveSelection(1);
                    return true;

                case SDLK_RETURN:
                case SDLK_RETURN2:
                case SDLK_KP_ENTER:
                    ActivateArchiveSelection();
                    return true;

                default:
                    return true;
            }
        }

        private bool IsMenuArea(int x, int y)
        {
            if (y >= 0 && y < TopMenuHeight)
                return true;

            if (IsInHayesMenuLabel(x, y))
                return true;

            if (IsInCassetteMenuLabel(x, y))
                return true;

            if (IsInDriveMenuLabel(x, y))
                return true;

            if (romManagerOpen && IsInRomManagerPanel(x, y))
                return true;

            return IsOpenMenuIndex(activeMenuIndex)
                && GetMenuItemIndexAt(activeMenuIndex, x, y) >= 0;
        }

        private SdlRect GetArchivePanelRect()
        {
            int width = Math.Min(560, logicalWidth - 48);
            int y = TopMenuHeight + ArchivePanelTopGap;
            int height = Math.Min(360, logicalHeight - y - GetBottomOverlayHeight() - 18);
            return new SdlRect((logicalWidth - width) / 2, y, width, height);
        }

        private bool IsInArchivePanel(int x, int y)
        {
            SdlRect panel = GetArchivePanelRect();
            return x >= panel.X && x < panel.X + panel.W && y >= panel.Y && y < panel.Y + panel.H;
        }

        private int GetArchiveFolderIndexAt(int x, int y)
        {
            SdlRect panel = GetArchivePanelRect();
            int listY = GetArchiveListY(panel);
            int folderWidth = 150;
            if (x < panel.X || x >= panel.X + folderWidth || y < listY || y >= panel.Y + panel.H - 6)
                return -1;

            int index = archiveFolderScroll + ((y - listY) / MenuItemHeight);
            return index >= 0 && index < archiveFolders.Length ? index : -1;
        }

        private int GetArchiveEntryIndexAt(int x, int y)
        {
            if (activeArchiveFolder < 0 || activeArchiveFolder >= archiveFolders.Length)
                return -1;

            SdlRect panel = GetArchivePanelRect();
            int listY = GetArchiveListY(panel);
            int discX = panel.X + 162;
            if (x < discX || x >= panel.X + panel.W || y < listY || y >= panel.Y + panel.H - 6)
                return -1;

            int index = archiveEntryScroll + ((y - listY) / MenuItemHeight);
            int count = GetArchiveFolderEntries(archiveFolders[activeArchiveFolder]).Count;
            return index >= 0 && index < count ? index : -1;
        }

        private int GetArchiveVisibleRows()
        {
            SdlRect panel = GetArchivePanelRect();
            return Math.Max(1, (panel.H - 70) / MenuItemHeight);
        }

        private static int GetArchiveListY(SdlRect panel)
        {
            return panel.Y + 58;
        }

        private static int ClampScroll(int scroll, int itemCount, int visibleRows)
        {
            int max = Math.Max(0, itemCount - visibleRows);
            return Math.Clamp(scroll, 0, max);
        }

        private List<ArchiveDiscEntry> GetArchiveFolderEntries(string folder)
        {
            string filter = archiveSearchText.Trim();
            return archiveEntries
                .Where(entry => string.Equals(entry.Folder, folder, StringComparison.OrdinalIgnoreCase))
                .Where(entry => filter.Length == 0
                    || entry.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || entry.Folder.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string[] GetFilteredArchiveFolders()
        {
            string filter = archiveSearchText.Trim();
            IEnumerable<string> folders = archiveEntries
                .Select(entry => entry.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            if (filter.Length > 0)
            {
                folders = folders.Where(folder =>
                    folder.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || archiveEntries.Any(entry =>
                        string.Equals(entry.Folder, folder, StringComparison.OrdinalIgnoreCase)
                        && entry.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            }

            return folders
                .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private List<ArchiveDiscEntry> GetActiveArchiveFolderEntries()
        {
            string folder = activeArchiveFolder >= 0 && activeArchiveFolder < archiveFolders.Length
                ? archiveFolders[activeArchiveFolder]
                : string.Empty;
            return GetArchiveFolderEntries(folder);
        }

        private void UpdateArchiveFilter()
        {
            string previousFolder = activeArchiveFolder >= 0 && activeArchiveFolder < archiveFolders.Length
                ? archiveFolders[activeArchiveFolder]
                : string.Empty;

            archiveFolders = GetFilteredArchiveFolders();
            activeArchiveFolder = Array.FindIndex(archiveFolders, folder => string.Equals(folder, previousFolder, StringComparison.OrdinalIgnoreCase));
            if (activeArchiveFolder < 0)
                activeArchiveFolder = archiveFolders.Length == 0 ? -1 : 0;

            activeArchiveEntry = Math.Min(Math.Max(activeArchiveEntry, 0), Math.Max(0, GetActiveArchiveFolderEntries().Count - 1));
            if (GetActiveArchiveFolderEntries().Count == 0)
            {
                activeArchiveEntry = -1;
                archiveEntryFocus = false;
            }

            archiveFolderScroll = ClampScroll(archiveFolderScroll, archiveFolders.Length, GetArchiveVisibleRows());
            archiveEntryScroll = ClampScroll(archiveEntryScroll, GetActiveArchiveFolderEntries().Count, GetArchiveVisibleRows());
            hoveredArchiveFolder = -1;
            hoveredArchiveEntry = -1;
            EnsureArchiveSelectionVisible();
        }

        private void MoveArchiveSelection(int delta)
        {
            if (archiveEntryFocus)
            {
                List<ArchiveDiscEntry> discs = GetActiveArchiveFolderEntries();
                if (discs.Count == 0)
                    return;

                activeArchiveEntry = Math.Clamp(activeArchiveEntry + delta, 0, discs.Count - 1);
            }
            else if (archiveFolders.Length > 0)
            {
                activeArchiveFolder = Math.Clamp(activeArchiveFolder + delta, 0, archiveFolders.Length - 1);
                activeArchiveEntry = GetActiveArchiveFolderEntries().Count == 0 ? -1 : 0;
                archiveEntryScroll = 0;
            }

            EnsureArchiveSelectionVisible();
        }

        private void ActivateArchiveSelection()
        {
            if (!archiveEntryFocus)
            {
                if (GetActiveArchiveFolderEntries().Count > 0)
                    archiveEntryFocus = true;
                return;
            }

            List<ArchiveDiscEntry> discs = GetActiveArchiveFolderEntries();
            if (activeArchiveEntry < 0 || activeArchiveEntry >= discs.Count)
                return;

            ArchiveDiscEntry entry = discs[activeArchiveEntry];
            pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.MountArchiveEntry, archivePath, archiveDrive, entry.EntryPath));
            CloseArchiveBrowser();
        }

        private void EnsureArchiveSelectionVisible()
        {
            int rows = GetArchiveVisibleRows();
            archiveFolderScroll = ClampScroll(archiveFolderScroll, archiveFolders.Length, rows);
            archiveEntryScroll = ClampScroll(archiveEntryScroll, GetActiveArchiveFolderEntries().Count, rows);

            if (!archiveEntryFocus && activeArchiveFolder >= 0)
                archiveFolderScroll = ScrollToItem(archiveFolderScroll, activeArchiveFolder, rows, archiveFolders.Length);

            if (archiveEntryFocus && activeArchiveEntry >= 0)
                archiveEntryScroll = ScrollToItem(archiveEntryScroll, activeArchiveEntry, rows, GetActiveArchiveFolderEntries().Count);
        }

        private static int ScrollToItem(int scroll, int index, int visibleRows, int itemCount)
        {
            if (index < scroll)
                return ClampScroll(index, itemCount, visibleRows);

            if (index >= scroll + visibleRows)
                return ClampScroll(index - visibleRows + 1, itemCount, visibleRows);

            return ClampScroll(scroll, itemCount, visibleRows);
        }

        private static string DecodeSdlText(byte[] textBytes)
        {
            int length = Array.IndexOf(textBytes, (byte)0);
            if (length < 0)
                length = textBytes.Length;

            return length == 0 ? string.Empty : Encoding.UTF8.GetString(textBytes, 0, length);
        }

        private void CloseArchiveBrowser()
        {
            archivePath = string.Empty;
            archiveEntries.Clear();
            archiveFolders = [];
            activeArchiveFolder = -1;
            hoveredArchiveFolder = -1;
            hoveredArchiveEntry = -1;
            activeArchiveEntry = -1;
            archiveFolderScroll = 0;
            archiveEntryScroll = 0;
            archiveEntryFocus = false;
            archiveSearchText = string.Empty;
        }

        private int GetMenuIndexAt(int x, int y)
        {
            if (IsInHayesMenuLabel(x, y))
                return HayesMenuIndex;

            if (IsInCassetteMenuLabel(x, y))
                return CassetteMenuIndex;

            int driveMenuIndex = GetDriveMenuIndexAt(x, y);
            if (driveMenuIndex != -1)
                return driveMenuIndex;

            if (y < 0 || y >= TopMenuHeight)
                return -1;

            int menuX = MenuPaddingX;
            for (int i = 0; i < menus.Length; i++)
            {
                int width = GetTopMenuWidth(menus[i].Title);
                if (x >= menuX - 4 && x < menuX + width + 4)
                    return i;

                menuX += width + MenuPaddingX;
            }

            return -1;
        }

        private int GetMenuItemIndexAt(int menuIndex, int x, int y)
        {
            if (!IsOpenMenuIndex(menuIndex))
                return -1;

            MenuDefinition menu = GetMenuDefinition(menuIndex);
            int menuWidth = GetDropDownWidth(menu);
            int menuHeight = GetDropDownHeight(menu);
            int menuX = GetDropDownX(menuIndex, menuWidth);
            int menuY = GetDropDownY(menuIndex, menuHeight);
            int itemY = y - menuY - MenuDropDownPadding;

            if (x < menuX || x >= menuX + menuWidth || itemY < 0)
                return -1;

            int top = 0;
            for (int i = 0; i < menu.Items.Length; i++)
            {
                int itemHeight = GetMenuItemHeight(menu.Items[i]);
                if (itemY >= top && itemY < top + itemHeight)
                    return menu.Items[i].Separator ? -1 : i;

                top += itemHeight;
            }

            return -1;
        }

        private int GetTopMenuX(int menuIndex)
        {
            if (menuIndex == HayesMenuIndex)
                return GetHayesMenuLabelRect().X;
            if (IsDriveMenu(menuIndex))
                return GetDriveGlyphRect(GetDriveMenuDrive(menuIndex)).X;

            int x = MenuPaddingX;
            for (int i = 0; i < menuIndex; i++)
                x += GetTopMenuWidth(menus[i].Title) + MenuPaddingX;

            return x;
        }

        private bool IsInHayesMenuLabel(int x, int y)
        {
            if (!HayesModemEnabled)
                return false;

            SdlRect rect = GetHayesMenuLabelRect();
            return x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;
        }

        private SdlRect GetHayesMenuLabelRect()
        {
            SdlRect panel = GetHayesPanelRect();
            int brandWidth = GetRendererTextWidth(HayesMenuTitle);
            return new SdlRect(
                panel.X + HayesPanelPaddingX - 2,
                panel.Y + HayesPanelPaddingY,
                brandWidth + 4,
                panel.H - (HayesPanelPaddingY * 2));
        }

        private SdlRect GetHayesPanelRect()
        {
            int panelWidth = GetHayesPanelWidth();
            int panelHeight = GetHayesPanelHeight();
            int x = (logicalWidth - panelWidth) / 2;
            SdlRect drive = GetDriveGlyphRect(0);
            int y = drive.Y + drive.H - panelHeight;
            return new SdlRect(x, y, panelWidth, panelHeight);
        }

        private int GetHayesDropDownX(int menuWidth)
        {
            SdlRect panel = GetHayesPanelRect();
            int centerX = panel.X + (panel.W / 2);
            return Math.Clamp(centerX - (menuWidth / 2), 0, logicalWidth - menuWidth);
        }

        private int GetDriveDropDownX(int menuWidth, int drive)
        {
            SdlRect glyph = GetDriveGlyphRect(drive);
            int centerX = glyph.X + (glyph.W / 2);
            return Math.Clamp(centerX - (menuWidth / 2), 0, logicalWidth - menuWidth);
        }

        private int GetCassetteDropDownX(int menuWidth)
        {
            SdlRect cassette = GetCassetteImageRect();
            int centerX = cassette.X + (cassette.W / 2);
            return Math.Clamp(centerX - (menuWidth / 2), 0, logicalWidth - menuWidth);
        }

        private int GetDropDownX(int menuIndex, int menuWidth)
        {
            if (menuIndex == HayesMenuIndex)
                return GetHayesDropDownX(menuWidth);
            if (IsDriveMenu(menuIndex))
                return GetDriveDropDownX(menuWidth, GetDriveMenuDrive(menuIndex));
            if (IsCassetteMenu(menuIndex))
                return GetCassetteDropDownX(menuWidth);

            return GetTopMenuX(menuIndex) - 4;
        }

        private int GetDropDownY(int menuIndex, int menuHeight)
        {
            if (menuIndex == HayesMenuIndex)
                return GetHayesPanelRect().Y - menuHeight;
            if (IsDriveMenu(menuIndex))
                return GetDriveGlyphRect(GetDriveMenuDrive(menuIndex)).Y - menuHeight;
            if (IsCassetteMenu(menuIndex))
                return GetCassetteImageRect().Y - menuHeight;

            return TopMenuHeight;
        }

        private static int GetHayesPanelWidth()
        {
            int brandWidth = GetRendererTextWidth(HayesMenuTitle);
            int ledGroupWidth = ((HayesPanelLedCount - 1) * HayesPanelLedGap) + StatusLedDiameter;
            return (HayesPanelPaddingX * 2) + brandWidth + HayesPanelBrandGap + ledGroupWidth;
        }

        private static int GetHayesPanelHeight()
        {
            int contentHeight = StatusLedDiameter + StatusLabelLedGap + StatusLabelGlyphHeight;
            return contentHeight + (HayesPanelPaddingY * 2);
        }

        private MenuDefinition GetMenuDefinition(int menuIndex)
        {
            return menuIndex switch
            {
                HayesMenuIndex => HayesMenu,
                Drive0MenuIndex => Drive0Mounted ? LoadedDrive0Menu : EmptyDrive0Menu,
                Drive1MenuIndex => Drive1Mounted ? LoadedDrive1Menu : EmptyDrive1Menu,
                CassetteMenuIndex => TapeMounted ? LoadedCassetteMenu : EmptyCassetteMenu,
                _ => menus[menuIndex]
            };
        }

        private static int GetTopMenuWidth(string text)
        {
            return GetRendererTextWidth(text) + 2;
        }

        private int GetDropDownWidth(MenuDefinition menu)
        {
            if (menu.Items.Length == 0)
                return GetRomManagerPanelWidth();

            int width = 0;
            foreach (MenuItem item in menu.Items)
            {
                if (item.Separator)
                    continue;

                int itemWidth = GetRendererTextWidth("  " + GetMenuItemText(item))
                    + (GetMenuItemSymbol(item) == TransportSymbol.None ? 0 : 18)
                    + (item.Shortcut.Length == 0 ? 0 : MenuShortcutGap + GetRendererTextWidth(item.Shortcut));
                width = Math.Max(width, itemWidth);
            }

            return width + 20;
        }

        private static int GetDropDownHeight(MenuDefinition menu)
        {
            if (menu.Items.Length == 0)
                return GetRomManagerPanelHeight();

            int height = MenuDropDownPadding * 2;
            foreach (MenuItem item in menu.Items)
                height += GetMenuItemHeight(item);

            return height;
        }

        private static int GetMenuItemHeight(MenuItem item)
        {
            return item.Separator ? MenuSeparatorHeight : MenuItemHeight;
        }

        private void DrawRomManager()
        {
            if (!romManagerOpen)
                return;

            DrawRomManagerPanel();
        }

        private void DrawRomManagerPanel()
        {
            SdlRect panel = GetRomManagerPanelRect();
            _ = SDL_SetRenderDrawColor(renderer, 18, 18, 18, 245);
            _ = SDL_RenderFillRect(renderer, ref panel);
            _ = SDL_SetRenderDrawColor(renderer, 150, 150, 150, 255);
            DrawRectOutline(panel);

            DrawRendererText("ROM Manager", panel.X + 14, panel.Y + 12, 240, 240, 240);

            for (int bank = 0; bank < romSlots.Length; bank++)
                DrawRomSlot(bank, GetRomSlotRect(panel, bank));

            if (movingRomSlot >= 0)
                DrawRendererText("Click an empty bank", panel.X + 12, panel.Y + panel.H - 18, 210, 210, 130);
            else
                DrawRomLayoutButtons(panel);

            if (activeRomSlot >= 0 && movingRomSlot < 0)
                DrawRomActionPopup(GetRomSlotRect(panel, activeRomSlot));

            if (infoRomSlot >= 0)
                DrawRomInfoPanel(panel, romSlots[infoRomSlot]);
        }

        private void DrawRomLayoutButtons(SdlRect panel)
        {
            DrawRomLayoutButton(GetRomImportButtonRect(panel), "Import");
            DrawRomLayoutButton(GetRomExportButtonRect(panel), "Export");
        }

        private void DrawRomLayoutButton(SdlRect rect, string label)
        {
            bool hovered = uiMouseX >= rect.X && uiMouseX < rect.X + rect.W && uiMouseY >= rect.Y && uiMouseY < rect.Y + rect.H;
            _ = SDL_SetRenderDrawColor(renderer, hovered ? (byte)42 : (byte)24, hovered ? (byte)42 : (byte)24, hovered ? (byte)42 : (byte)24, 255);
            _ = SDL_RenderFillRect(renderer, ref rect);
            _ = SDL_SetRenderDrawColor(renderer, 125, 125, 125, 255);
            DrawRectOutline(rect);
            DrawRendererText(label, rect.X + 8, rect.Y + 5, 230, 230, 230);
        }

        private void DrawRomSlot(int bank, SdlRect slotRect)
        {
            SidewaysRomSlot slot = romSlots[bank];
            bool occupied = slot.Occupied;
            bool hovered = bank == hoveredRomSlot;
            bool movingSource = bank == movingRomSlot;

            int numberX = slotRect.X + (slotRect.W / 2) - (GetRendererTextWidth(bank.ToString(CultureInfo.InvariantCulture)) / 2);
            DrawRendererText(bank.ToString(CultureInfo.InvariantCulture), numberX, slotRect.Y - RomBankNumberHeight, 210, 210, 210);

            IntPtr glyphTexture = occupied ? occupiedRomSocketTexture : emptyRomSocketTexture;
            if (glyphTexture != IntPtr.Zero)
                _ = SDL_RenderCopy(renderer, glyphTexture, IntPtr.Zero, ref slotRect);

            if (hovered || movingSource)
            {
                _ = SDL_SetRenderDrawColor(renderer, movingSource ? (byte)255 : (byte)230, movingSource ? (byte)210 : (byte)230, movingSource ? (byte)90 : (byte)230, 255);
                DrawRectOutline(new SdlRect(slotRect.X - 2, slotRect.Y - 2, slotRect.W + 4, slotRect.H + 4));
            }

            string label = occupied ? slot.DisplayName : "EMPTY";
            byte labelGrey = slot.Missing ? (byte)245 : occupied ? (byte)235 : (byte)90;
            DrawRomSlotLabel(label, slotRect, labelGrey, labelGrey, slot.Missing ? (byte)80 : labelGrey);
        }

        private void DrawRomSlotLabel(string label, SdlRect slotRect, byte red, byte green, byte blue)
        {
            string[] lines = WrapRomSlotLabel(label);
            int firstLineX = lines.Length == 1 ? slotRect.X + 25 : slotRect.X + 20;
            for (int i = 0; i < lines.Length; i++)
                DrawRendererTextRotatedCcw(lines[i], firstLineX + (i * 10), slotRect.Y + slotRect.H - 14, red, green, blue);
        }

        private static string[] WrapRomSlotLabel(string label)
        {
            string trimmed = label.Trim();
            if (trimmed.Length <= RomLabelMaxCharacters)
                return [trimmed];

            int split = trimmed.LastIndexOf(' ', RomLabelMaxCharacters);
            if (split <= 0)
                split = trimmed.IndexOf(' ', RomLabelMaxCharacters);
            if (split <= 0)
                split = RomLabelMaxCharacters;

            string first = trimmed[..split].Trim();
            string second = trimmed[split..].Trim();
            if (second.Length > RomLabelMaxCharacters)
                second = second[..RomLabelMaxCharacters].TrimEnd();

            return string.IsNullOrEmpty(second) ? [first] : [first, second];
        }

        private void DrawRomActionPopup(SdlRect slotRect)
        {
            int actionRows = GetRomActionRowCount(activeRomSlot);
            SdlRect popup = GetRomActionRect(slotRect, actionRows);
            _ = SDL_SetRenderDrawColor(renderer, 28, 28, 28, 255);
            _ = SDL_RenderFillRect(renderer, ref popup);
            _ = SDL_SetRenderDrawColor(renderer, 175, 175, 175, 255);
            DrawRectOutline(popup);

            if (IsBasicRomBank(activeRomSlot))
            {
                DrawRendererText("Info", popup.X + 8, popup.Y + 6, 230, 230, 230);
                return;
            }

            if (IsDfsRomBank(activeRomSlot))
            {
                DrawRendererText("Replace", popup.X + 8, popup.Y + 6, 230, 230, 230);
                DrawRendererText("Info", popup.X + 8, popup.Y + 6 + RomActionRowHeight, 230, 230, 230);
                return;
            }

            DrawRendererText("Remove", popup.X + 8, popup.Y + 6, 230, 230, 230);
            DrawRendererText("Move", popup.X + 8, popup.Y + 6 + RomActionRowHeight, 230, 230, 230);
            DrawRendererText("Info", popup.X + 8, popup.Y + 6 + (RomActionRowHeight * 2), 230, 230, 230);
        }

        private void DrawRomInfoPanel(SdlRect panel, SidewaysRomSlot slot)
        {
            const int infoWidth = 318;
            const int infoHeight = 112;
            SdlRect info = new SdlRect(
                panel.X + ((panel.W - infoWidth) / 2),
                panel.Y + ((panel.H - infoHeight) / 2),
                infoWidth,
                infoHeight);

            _ = SDL_SetRenderDrawColor(renderer, 10, 10, 10, 250);
            _ = SDL_RenderFillRect(renderer, ref info);
            _ = SDL_SetRenderDrawColor(renderer, 220, 220, 220, 255);
            DrawRectOutline(info);

            string languageEntry = slot.LanguageEntry.HasValue ? $"Language entry ${slot.LanguageEntry.Value:X4}" : "No language entry";
            string serviceEntry = slot.ServiceEntry.HasValue ? $"Service entry ${slot.ServiceEntry.Value:X4}" : "No service entry";
            string fileName = slot.Path is null ? string.Empty : Path.GetFileName(slot.Path);
            int columns = Math.Max(1, (info.W - 18) / MenuTextCellWidth);

            int y = info.Y + 10;
            DrawRendererText(TrimRendererText($"Bank {slot.Bank}: {slot.Title}", columns), info.X + 9, y, 245, 245, 245);
            y += 18;
            DrawRendererText(TrimRendererText(slot.RomType, columns), info.X + 9, y, 190, 190, 190);
            y += 14;
            DrawRendererText(TrimRendererText(languageEntry, columns), info.X + 9, y, 190, 190, 190);
            y += 14;
            DrawRendererText(TrimRendererText(serviceEntry, columns), info.X + 9, y, 190, 190, 190);
            y += 14;
            if (!string.IsNullOrWhiteSpace(slot.Copyright))
            {
                DrawRendererText(TrimRendererText(slot.Copyright, columns), info.X + 9, y, 160, 160, 160);
                y += 14;
            }
            DrawRendererText(TrimRendererText(fileName, columns), info.X + 9, y, 160, 160, 160);
        }

        private bool HandleRomManagerMouseMotion(int hostX, int hostY)
        {
            if (!romManagerOpen)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            uiMouseX = (int)Math.Round(logicalX);
            uiMouseY = (int)Math.Round(logicalY);
            hoveredRomSlot = -1;
            if (uiMouseY < TopMenuHeight)
                return false;

            hoveredRomSlot = GetRomSlotAt(uiMouseX, uiMouseY);
            return IsInRomManagerPanel(uiMouseX, uiMouseY);
        }

        private bool HandleRomManagerMouseButton(byte button, bool pressed, int hostX, int hostY)
        {
            if (!romManagerOpen || button != SDL_BUTTON_LEFT)
                return false;

            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);
            int x = (int)logicalX;
            int y = (int)logicalY;
            if (y < TopMenuHeight)
                return false;

            if (!pressed)
                return IsInRomManagerPanel(x, y);

            if (HandleRomManagerClick(x, y))
                return true;

            CloseRomManager();
            return true;
        }

        private bool HandleRomManagerClick(int x, int y)
        {
            if (activeRomSlot >= 0 && movingRomSlot < 0)
            {
                int action = GetRomActionAt(x, y);
                if (action >= 0)
                {
                    HandleRomActionChoice(action);
                    return true;
                }
            }

            if (movingRomSlot < 0 && HandleRomLayoutButtonClick(x, y))
                return true;

            int bank = GetRomSlotAt(x, y);
            if (bank < 0)
                return IsInRomManagerPanel(x, y);

            SidewaysRomSlot slot = romSlots[bank];
            if (movingRomSlot >= 0)
            {
                if (!slot.Occupied && movingRomSlot != bank)
                    pendingRomActions.Enqueue(new HostRomAction(HostRomActionKind.Move, movingRomSlot, bank, string.Empty));

                movingRomSlot = -1;
                activeRomSlot = -1;
                return true;
            }

            if (slot.Occupied)
            {
                activeRomSlot = bank;
                infoRomSlot = -1;
                return true;
            }

            string? path = SelectNativeRomFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingRomActions.Enqueue(new HostRomAction(HostRomActionKind.Add, bank, -1, path));

            activeRomSlot = -1;
            return true;
        }

        private bool HandleRomLayoutButtonClick(int x, int y)
        {
            SdlRect panel = GetRomManagerPanelRect();
            SdlRect import = GetRomImportButtonRect(panel);
            if (x >= import.X && x < import.X + import.W && y >= import.Y && y < import.Y + import.H)
            {
                string? path = SelectNativeLoadRomLayoutFile();
                if (!string.IsNullOrWhiteSpace(path))
                    pendingRomActions.Enqueue(new HostRomAction(HostRomActionKind.ImportLayout, -1, -1, path));
                activeRomSlot = -1;
                infoRomSlot = -1;
                return true;
            }

            SdlRect export = GetRomExportButtonRect(panel);
            if (x >= export.X && x < export.X + export.W && y >= export.Y && y < export.Y + export.H)
            {
                string? path = SelectNativeSaveRomLayoutFile("BBC-ROM-Layout.json");
                if (!string.IsNullOrWhiteSpace(path))
                    pendingRomActions.Enqueue(new HostRomAction(HostRomActionKind.ExportLayout, -1, -1, EnsureRomLayoutExtension(path)));
                activeRomSlot = -1;
                infoRomSlot = -1;
                return true;
            }

            return false;
        }

        private void HandleRomActionChoice(int action)
        {
            int bank = activeRomSlot;
            activeRomSlot = -1;

            if (IsBasicRomBank(bank))
            {
                infoRomSlot = bank;
                return;
            }

            if (IsDfsRomBank(bank))
            {
                if (action == 0)
                {
                    string? path = SelectNativeRomFile();
                    if (!string.IsNullOrWhiteSpace(path))
                        pendingRomActions.Enqueue(new HostRomAction(HostRomActionKind.Add, bank, -1, path));
                    return;
                }

                infoRomSlot = bank;
                return;
            }

            switch (action)
            {
                case 0:
                    pendingRomActions.Enqueue(new HostRomAction(HostRomActionKind.Remove, bank, -1, string.Empty));
                    break;
                case 1:
                    movingRomSlot = bank;
                    break;
                case 2:
                    infoRomSlot = bank;
                    break;
            }
        }

        private int GetRomActionAt(int x, int y)
        {
            if (activeRomSlot < 0)
                return -1;

            SdlRect panel = GetRomManagerPanelRect();
            int actionRows = GetRomActionRowCount(activeRomSlot);
            SdlRect popup = GetRomActionRect(GetRomSlotRect(panel, activeRomSlot), actionRows);
            if (x < popup.X || x >= popup.X + popup.W || y < popup.Y || y >= popup.Y + popup.H)
                return -1;

            int action = (y - popup.Y) / RomActionRowHeight;
            if (IsBasicRomBank(activeRomSlot))
                return action == 0 ? 2 : -1;

            if (IsDfsRomBank(activeRomSlot))
                return action is >= 0 and <= 1 ? action : -1;

            return action is >= 0 and <= 2 ? action : -1;
        }

        private int GetRomSlotAt(int x, int y)
        {
            if (!romManagerOpen)
                return -1;

            SdlRect panel = GetRomManagerPanelRect();
            for (int bank = 0; bank < romSlots.Length; bank++)
            {
                SdlRect slot = GetRomSlotRect(panel, bank);
                if (x >= slot.X && x < slot.X + slot.W && y >= slot.Y && y < slot.Y + slot.H)
                    return bank;
            }

            return -1;
        }

        private bool IsInRomManagerPanel(int x, int y)
        {
            if (!romManagerOpen)
                return false;

            SdlRect panel = GetRomManagerPanelRect();
            return x >= panel.X && x < panel.X + panel.W && y >= panel.Y && y < panel.Y + panel.H;
        }

        private SdlRect GetRomManagerPanelRect()
        {
            int x = Math.Max(0, (logicalWidth - GetRomManagerPanelWidth()) / 2);
            int y = TopMenuHeight + 38;
            return new SdlRect(x, y, GetRomManagerPanelWidth(), GetRomManagerPanelHeight());
        }

        private static SdlRect GetRomSlotRect(SdlRect panel, int bank)
        {
            int column = bank % RomSlotColumns;
            int row = bank / RomSlotColumns;
            int x = panel.X + RomPanelPadding + (column * (RomSlotWidth + RomSlotGapX));
            int y = panel.Y + RomPanelPadding + RomPanelTitleHeight + RomBankNumberHeight + (row * (RomSlotHeight + RomSlotGapY + RomBankNumberHeight));
            return new SdlRect(x, y, RomSlotWidth, RomSlotHeight);
        }

        private SdlRect GetRomActionRect(SdlRect slotRect, int rows)
        {
            int height = RomActionRowHeight * rows;
            int x = Math.Min(slotRect.X + slotRect.W + 4, logicalWidth - RomActionWidth - 4);
            int y = Math.Min(slotRect.Y + 20, logicalHeight - height - 4);
            return new SdlRect(x, y, RomActionWidth, height);
        }

        private static SdlRect GetRomImportButtonRect(SdlRect panel)
        {
            int y = panel.Y + panel.H - RomLayoutButtonHeight - RomLayoutButtonBottomInset;
            int x = panel.X + panel.W - 14 - (RomLayoutButtonWidth * 2) - InputActionButtonGap;
            return new SdlRect(x, y, RomLayoutButtonWidth, RomLayoutButtonHeight);
        }

        private static SdlRect GetRomExportButtonRect(SdlRect panel)
        {
            SdlRect import = GetRomImportButtonRect(panel);
            return new SdlRect(import.X + RomLayoutButtonWidth + InputActionButtonGap, import.Y, RomLayoutButtonWidth, RomLayoutButtonHeight);
        }

        private static int GetRomActionRowCount(int bank)
        {
            if (IsBasicRomBank(bank))
                return 1;
            if (IsDfsRomBank(bank))
                return 2;
            return 3;
        }

        private static bool IsBasicRomBank(int bank)
        {
            return bank == BasicRomBank;
        }

        private static bool IsDfsRomBank(int bank)
        {
            return bank == DfsRomBank;
        }

        private static int GetRomManagerPanelWidth()
        {
            return (RomPanelPadding * 2) + (RomSlotColumns * RomSlotWidth) + ((RomSlotColumns - 1) * RomSlotGapX);
        }

        private static int GetRomManagerPanelHeight()
        {
            return (RomPanelPadding * 2)
                + RomPanelTitleHeight
                + (RomSlotRows * (RomSlotHeight + RomBankNumberHeight))
                + ((RomSlotRows - 1) * RomSlotGapY)
                + 34;
        }

        private SdlRect GetInputPanelRect()
        {
            int width = Math.Min(InputPanelWidth, logicalWidth - 36);
            int height = Math.Min(InputPanelHeight, logicalHeight - TopMenuHeight - 28);
            int x = Math.Max(0, (logicalWidth - width) / 2);
            int y = TopMenuHeight + 38;
            return new SdlRect(x, y, width, height);
        }

        private bool IsInInputPanel(int x, int y)
        {
            SdlRect panel = GetInputPanelRect();
            return x >= panel.X && x < panel.X + panel.W
                && y >= panel.Y && y < panel.Y + panel.H;
        }

        private InputMapperAction GetInputMapperActionAt(int x, int y)
        {
            SdlRect panel = GetInputPanelRect();
            if (IsInRect(x, y, GetInputShiftLockButtonRect(panel)))
                return InputMapperAction.ToggleShiftLock;
            if (IsInRect(x, y, GetInputLoadMapButtonRect(panel)))
                return InputMapperAction.Load;
            if (IsInRect(x, y, GetInputSaveMapButtonRect(panel)))
                return InputMapperAction.Save;
            if (IsInRect(x, y, GetInputResetMapButtonRect(panel)))
                return InputMapperAction.Reset;

            return InputMapperAction.None;
        }

        private static SdlRect GetInputShiftLockButtonRect(SdlRect panel)
        {
            int totalWidth = InputShiftLockButtonWidth
                + InputActionButtonGap
                + (InputActionButtonWidth * 3)
                + (InputActionButtonGap * 2);
            int x = panel.X + panel.W - 14 - totalWidth;
            int y = panel.Y + panel.H - 31;
            return new SdlRect(x, y, InputShiftLockButtonWidth, InputActionButtonHeight);
        }

        private static SdlRect GetInputLoadMapButtonRect(SdlRect panel)
        {
            SdlRect shiftLock = GetInputShiftLockButtonRect(panel);
            int x = shiftLock.X + shiftLock.W + InputActionButtonGap;
            int y = shiftLock.Y;
            return new SdlRect(x, y, InputActionButtonWidth, InputActionButtonHeight);
        }

        private static SdlRect GetInputSaveMapButtonRect(SdlRect panel)
        {
            SdlRect open = GetInputLoadMapButtonRect(panel);
            return new SdlRect(open.X + InputActionButtonWidth + InputActionButtonGap, open.Y, InputActionButtonWidth, InputActionButtonHeight);
        }

        private static SdlRect GetInputResetMapButtonRect(SdlRect panel)
        {
            SdlRect save = GetInputSaveMapButtonRect(panel);
            return new SdlRect(save.X + InputActionButtonWidth + InputActionButtonGap, save.Y, InputActionButtonWidth, InputActionButtonHeight);
        }

        private static bool IsInRect(int x, int y, SdlRect rect)
        {
            return x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;
        }

        private int GetInputKeyIndexAt(int x, int y)
        {
            SdlRect panel = GetInputPanelRect();
            for (int i = 0; i < InputKeys.Length; i++)
            {
                SdlRect key = GetInputKeyRect(panel, InputKeys[i]);
                if (x >= key.X && x < key.X + key.W && y >= key.Y && y < key.Y + key.H)
                    return i;
            }

            return -1;
        }

        private static SdlRect GetInputKeyRect(SdlRect panel, BbcInputKey key)
        {
            return new SdlRect(panel.X + key.X, panel.Y + key.Y, key.W, key.H);
        }

        private static string BbcKeyLabel(byte internalKey)
        {
            if (internalKey == BbcKeyboard.LeftShiftKey)
                return "Left SHIFT";

            if (internalKey == BbcKeyboard.RightShiftKey)
                return "Right SHIFT";

            for (int i = 0; i < InputKeys.Length; i++)
            {
                if (InputKeys[i].InternalKey == internalKey)
                    return InputKeys[i].Label;
            }

            return $"${internalKey:X2}";
        }

        private bool IsDirectMenu(int menuIndex)
        {
            return menuIndex >= 0 && menuIndex < menus.Length && menus[menuIndex].DirectCommand.HasValue;
        }

        private void ExecuteMenuCommand(MenuCommand command)
        {
            switch (command)
            {
                case MenuCommand.MountDrive0:
                    EnqueueSelectedFile(0);
                    break;
                case MenuCommand.MountDrive1:
                    EnqueueSelectedFile(1);
                    break;
                case MenuCommand.CreateBlankSsdDrive0:
                    EnqueueBlankSsd(0);
                    break;
                case MenuCommand.CreateBlankSsdDrive1:
                    EnqueueBlankSsd(1);
                    break;
                case MenuCommand.EjectDrive0:
                    pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Eject, string.Empty, 0));
                    break;
                case MenuCommand.EjectDrive1:
                    pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Eject, string.Empty, 1));
                    break;
                case MenuCommand.LoadTape:
                    EnqueueSelectedTape();
                    break;
                case MenuCommand.CreateUefTape:
                    EnqueueBlankUefTape();
                    break;
                case MenuCommand.RecordTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.Record, string.Empty));
                    break;
                case MenuCommand.PlayTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.Play, string.Empty));
                    break;
                case MenuCommand.PauseTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.TogglePause, string.Empty));
                    break;
                case MenuCommand.StopTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(IsTapeStopped ? HostTapeActionKind.Eject : HostTapeActionKind.Stop, string.Empty));
                    break;
                case MenuCommand.RewindTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.Rewind, string.Empty));
                    break;
                case MenuCommand.FastForwardTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.FastForward, string.Empty));
                    break;
                case MenuCommand.ResetTapeCounter:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.ResetCounter, string.Empty));
                    break;
                case MenuCommand.EjectTape:
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.Eject, string.Empty));
                    break;
                case MenuCommand.SaveScreenshot:
                    pendingScreenshotRequests++;
                    break;
                case MenuCommand.PrintScreen:
                    pendingPrintScreenRequests++;
                    break;
                case MenuCommand.PrintSavedScreenshot:
                    EnqueueSavedScreenshot();
                    break;
                case MenuCommand.TogglePrinterPageInversion:
                    printer?.TogglePageInversion();
                    break;
                case MenuCommand.TogglePrinterSound:
                    printer?.ToggleSound();
                    break;
                case MenuCommand.SavePrinterPng:
                    printer?.SaveDocumentPng();
                    break;
                case MenuCommand.NewPrinterPaper:
                    printer?.NewPaper();
                    break;
                case MenuCommand.NewPrinterPage:
                    printer?.StartNewPage();
                    break;
                case MenuCommand.CancelPrinterActivity:
                    printer?.CancelPrinting();
                    break;
                case MenuCommand.SaveState:
                    EnqueueSaveState();
                    break;
                case MenuCommand.LoadState:
                    EnqueueLoadState();
                    break;
                case MenuCommand.LoadRecentState1:
                case MenuCommand.LoadRecentState2:
                case MenuCommand.LoadRecentState3:
                case MenuCommand.LoadRecentState4:
                case MenuCommand.LoadRecentState5:
                    EnqueueRecentState(command);
                    break;
                case MenuCommand.Quit:
                    QuitRequested = true;
                    break;
                case MenuCommand.Break:
                    pendingBreaks.Enqueue(new BreakKeyPress(false, false));
                    break;
                case MenuCommand.ShiftBreak:
                    pendingBreaks.Enqueue(new BreakKeyPress(true, false));
                    break;
                case MenuCommand.ControlBreak:
                    pendingBreaks.Enqueue(new BreakKeyPress(false, true));
                    break;
                case MenuCommand.PowerReset:
                    pendingPowerResetRequests++;
                    break;
                case MenuCommand.TogglePause:
                    pendingPauseToggleRequests++;
                    break;
                case MenuCommand.ToggleSoundOutput:
                    pendingSoundToggleRequests++;
                    break;
                case MenuCommand.ToggleTapePause:
                    pendingTapePauseToggleRequests++;
                    break;
                case MenuCommand.ToggleTapePlayer:
                    pendingTapePlayerToggleRequests++;
                    break;
                case MenuCommand.ToggleDiscDrive0:
                    pendingDrive0ToggleRequests++;
                    break;
                case MenuCommand.ToggleDiscDrive1:
                    pendingDrive1ToggleRequests++;
                    break;
                case MenuCommand.ToggleTube6502:
                    pendingTube6502ToggleRequests++;
                    break;
                case MenuCommand.ToggleHayesModem:
                    pendingHayesModemToggleRequests++;
                    break;
                case MenuCommand.TogglePrinter:
                    pendingPrinterToggleRequests++;
                    break;
                case MenuCommand.ToggleHayesLoopback:
                    pendingHayesLoopbackToggleRequests++;
                    break;
                case MenuCommand.ResetHayesModem:
                    pendingHayesResetRequests++;
                    break;
                case MenuCommand.ToggleScanlines:
                    scanlinesEnabled = !scanlinesEnabled;
                    break;
                case MenuCommand.ToggleBbcLogo:
                    showBbcLogo = !showBbcLogo;
                    break;
                case MenuCommand.ToggleFullScreen:
                    SetFullScreen(!fullScreenEnabled);
                    break;
                case MenuCommand.OpenRomManager:
                    romManagerOpen = true;
                    inputMapperOpen = false;
                    activeMenuIndex = -1;
                    activeRomSlot = -1;
                    movingRomSlot = -1;
                    hoveredRomSlot = -1;
                    infoRomSlot = -1;
                    ClearInputMapperSelection();
                    break;
                case MenuCommand.OpenInputMapper:
                    OpenInputMapper();
                    romManagerOpen = false;
                    activeMenuIndex = -1;
                    break;
            }
        }

        private void CloseRomManager()
        {
            romManagerOpen = false;
            activeRomSlot = -1;
            movingRomSlot = -1;
            hoveredRomSlot = -1;
            infoRomSlot = -1;
        }

        private void CloseInputMapper()
        {
            inputMapperOpen = false;
            ClearInputMapperSelection();
        }

        public void ResetInputProfileForPowerCycle()
        {
            inputProfile = InputProfile.CreateEmulatorDefault();
            activeInputProfileName = inputProfile.Name;
            inputProfileDirty = false;
            ClearInputMapperSelection();
            ClearLiveInputState();
        }

        private void OpenInputMapper()
        {
            inputMapperOpen = true;
            ClearInputMapperSelection();
            ClearLiveInputState();
        }

        private void CloseWindowPanels()
        {
            CloseRomManager();
            CloseInputMapper();
        }

        private void ResetInputProfile()
        {
            inputProfile.ResetToDefault();
            ClearInputMapperSelection();
            ClearLiveInputState();
            inputProfileDirty = true;
            ShowNotification("Input map reset", "Unsaved", 2000);
        }

        private void LoadInputProfile()
        {
            string? path = SelectNativeLoadInputProfileFile();
            if (string.IsNullOrWhiteSpace(path))
                return;

            inputProfile = InputProfile.Load(path);
            activeInputProfileName = inputProfile.Name;
            inputProfileDirty = false;
            ClearInputMapperSelection();
            ClearLiveInputState();
            ShowNotification("Input map loaded", inputProfile.Name, 2000);
        }

        private void SaveInputProfile(string title = "Input map saved")
        {
            string? path = SelectNativeSaveInputProfileFile(CreateInputProfileFileName());
            if (string.IsNullOrWhiteSpace(path))
                return;

            path = EnsureInputProfileExtension(path);
            try
            {
                inputProfile.Save(path);
                activeInputProfileName = inputProfile.Name;
                inputProfileDirty = false;
                ShowNotification(title, inputProfile.Name, 2000);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowNotification("Input map not saved", ex.Message, 4000);
            }
        }

        private string CreateInputProfileFileName()
        {
            return string.IsNullOrWhiteSpace(activeInputProfileName)
                ? "Default.json"
                : EnsureInputProfileExtension(activeInputProfileName);
        }

        private void LoadRecentStatePaths()
        {
            recentStatePaths.Clear();
            string path = GetRecentStatePath();
            if (!File.Exists(path))
                return;

            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string statePath = line.Trim();
                    if (statePath.Length == 0 || recentStatePaths.Contains(statePath, StringComparer.OrdinalIgnoreCase))
                        continue;

                    recentStatePaths.Add(statePath);
                    if (recentStatePaths.Count == MaxRecentStateFiles)
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Recent save states ignored: {ex.Message}");
            }
        }

        private void SaveRecentStatePaths()
        {
            try
            {
                File.WriteAllLines(GetRecentStatePath(), recentStatePaths);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Recent save states not saved: {ex.Message}");
            }
        }

        private bool IsMenuItemChecked(MenuCommand command)
        {
            return command switch
            {
                MenuCommand.ToggleScanlines => scanlinesEnabled,
                MenuCommand.ToggleBbcLogo => showBbcLogo,
                MenuCommand.ToggleFullScreen => fullScreenEnabled,
                MenuCommand.TogglePause => EmulationPaused,
                MenuCommand.ToggleSoundOutput => SoundOutputEnabled,
                MenuCommand.ToggleTapePause => TapePaused,
                MenuCommand.RecordTape => TapeRecording,
                MenuCommand.PlayTape => TapePlaying,
                MenuCommand.PauseTape => TapePaused,
                MenuCommand.ToggleTapePlayer => TapePlayerEnabled,
                MenuCommand.ToggleDiscDrive0 => Drive0Enabled,
                MenuCommand.ToggleDiscDrive1 => Drive1Enabled,
                MenuCommand.ToggleTube6502 => Tube6502Enabled,
                MenuCommand.ToggleHayesModem => HayesModemEnabled,
                MenuCommand.TogglePrinter => PrinterEnabled,
                MenuCommand.TogglePrinterPageInversion => printer?.PageInverted == true,
                MenuCommand.TogglePrinterSound => printer?.SoundEnabled == true,
                MenuCommand.ToggleHayesLoopback => HayesLoopbackEnabled,
                _ => false
            };
        }

        private string GetMenuItemText(MenuItem item)
        {
            return item.Command switch
            {
                MenuCommand.StopTape => IsTapeStopped ? "EJECT" : "STOP",
                _ => item.Text
            };
        }

        private TransportSymbol GetMenuItemSymbol(MenuItem item)
        {
            return item.Symbol == TransportSymbol.StopOrEject
                ? IsTapeStopped ? TransportSymbol.Eject : TransportSymbol.Stop
                : item.Symbol;
        }

        private bool IsTapeStopped => TapeMounted && !TapePlaying && !TapePaused && !TapeFastTransportActive;

        private bool IsMenuItemEnabled(MenuItem item)
        {
            return item.Enabled && item.Command switch
            {
                MenuCommand.MountDrive0 => Drive0Enabled && !Drive0Mounted,
                MenuCommand.MountDrive1 => Drive1Enabled && !Drive1Mounted,
                MenuCommand.CreateBlankSsdDrive0 => Drive0Enabled && !Drive0Mounted,
                MenuCommand.CreateBlankSsdDrive1 => Drive1Enabled && !Drive1Mounted,
                MenuCommand.EjectDrive0 => Drive0Enabled && Drive0Mounted,
                MenuCommand.EjectDrive1 => Drive1Enabled && Drive1Mounted,
                MenuCommand.LoadTape => TapePlayerEnabled && !TapeMounted,
                MenuCommand.CreateUefTape => TapePlayerEnabled && !TapeMounted,
                MenuCommand.RecordTape => TapePlayerEnabled && TapeMounted && TapeRecordable,
                MenuCommand.PlayTape => TapePlayerEnabled && TapeMounted && !TapePlaying && !TapeRecording,
                MenuCommand.PauseTape => TapePlayerEnabled && TapeMounted,
                MenuCommand.StopTape => TapeMounted,
                MenuCommand.RewindTape => TapePlayerEnabled && TapeMounted,
                MenuCommand.FastForwardTape => TapePlayerEnabled && TapeMounted,
                MenuCommand.ResetTapeCounter => TapePlayerEnabled && TapeMounted,
                MenuCommand.EjectTape => TapePlayerEnabled && TapeMounted,
                MenuCommand.LoadRecentState1
                    or MenuCommand.LoadRecentState2
                    or MenuCommand.LoadRecentState3
                    or MenuCommand.LoadRecentState4
                    or MenuCommand.LoadRecentState5 => IsRecentStateAvailable(item.Command),
                MenuCommand.CancelPrinterActivity => printer?.Busy == true,
                _ => true
            };
        }

        private void DrawRectOutline(SdlRect rect)
        {
            SdlRect top = new SdlRect(rect.X, rect.Y, rect.W, 1);
            SdlRect bottom = new SdlRect(rect.X, rect.Y + rect.H - 1, rect.W, 1);
            SdlRect left = new SdlRect(rect.X, rect.Y, 1, rect.H);
            SdlRect right = new SdlRect(rect.X + rect.W - 1, rect.Y, 1, rect.H);
            _ = SDL_RenderFillRect(renderer, ref top);
            _ = SDL_RenderFillRect(renderer, ref bottom);
            _ = SDL_RenderFillRect(renderer, ref left);
            _ = SDL_RenderFillRect(renderer, ref right);
        }

        private void DrawRendererText(string text, int x, int y, byte red, byte green, byte blue)
        {
            if (text.Length == 0)
                return;

            CachedTextTexture cached = GetCachedRendererText(text, red, green, blue);
            SdlRect destination = new SdlRect(x, y, cached.Width, cached.Height);
            _ = SDL_RenderCopy(renderer, cached.Texture, IntPtr.Zero, ref destination);
        }

        private CachedTextTexture GetCachedRendererText(string text, byte red, byte green, byte blue)
        {
            CachedTextKey key = new CachedTextKey(text, red, green, blue);
            if (rendererTextCache.TryGetValue(key, out CachedTextTexture cached))
                return cached;

            cached = CreateCachedTextTexture(text, red, green, blue, GetRendererTextWidth(text), MenuTextCellWidth, NotificationGlyphWidth, NotificationGlyphHeight, NotificationFont.GetRows);
            rendererTextCache.Add(key, cached);
            return cached;
        }

        private CachedTextTexture GetCachedTinyText(string text, byte red, byte green, byte blue)
        {
            CachedTextKey key = new CachedTextKey(text, red, green, blue);
            if (tinyTextCache.TryGetValue(key, out CachedTextTexture cached))
                return cached;

            cached = CreateCachedTextTexture(text, red, green, blue, GetTinyTextWidth(text), StatusLabelGlyphWidth + StatusLabelGlyphGap, StatusLabelGlyphWidth, StatusLabelGlyphHeight, TinyOverlayFont.GetRows);
            tinyTextCache.Add(key, cached);
            return cached;
        }

        private CachedTextTexture CreateCachedTextTexture(
            string text,
            byte red,
            byte green,
            byte blue,
            int width,
            int cellWidth,
            int glyphWidth,
            int glyphHeight,
            Func<char, byte[]> getRows)
        {
            width = Math.Max(1, width);
            uint[] pixels = new uint[width * glyphHeight];
            uint colour = 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;

            for (int i = 0; i < text.Length; i++)
            {
                byte[] glyph = getRows(text[i]);
                int charX = i * cellWidth;
                for (int row = 0; row < glyphHeight; row++)
                {
                    byte mask = glyph[row];
                    for (int column = 0; column < glyphWidth; column++)
                    {
                        if ((mask & (1 << (glyphWidth - 1 - column))) == 0)
                            continue;

                        pixels[(row * width) + charX + column] = colour;
                    }
                }
            }

            IntPtr textTexture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, glyphHeight);
            ThrowIfNull(textTexture, "SDL_CreateTexture");
            _ = SDL_SetTextureBlendMode(textTexture, SDL_BLENDMODE_BLEND);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                ThrowIfSdlFailed(SDL_UpdateTexture(textTexture, IntPtr.Zero, handle.AddrOfPinnedObject(), width * sizeof(uint)), "SDL_UpdateTexture");
            }
            finally
            {
                handle.Free();
            }

            return new CachedTextTexture(textTexture, width, glyphHeight);
        }

        private void DrawRendererTextRotatedCcw(string text, int x, int baselineY, byte red, byte green, byte blue)
        {
            _ = SDL_SetRenderDrawColor(renderer, red, green, blue, 255);
            for (int i = 0; i < text.Length; i++)
            {
                byte[] glyph = NotificationFont.GetRows(text[i]);
                int glyphY = baselineY - (i * MenuTextCellWidth);
                for (int row = 0; row < glyph.Length; row++)
                {
                    byte mask = glyph[row];
                    for (int column = 0; column < NotificationGlyphWidth; column++)
                    {
                        if ((mask & (1 << (NotificationGlyphWidth - 1 - column))) == 0)
                            continue;

                        SdlRect pixel = new SdlRect(x + row, glyphY - column, 1, 1);
                        _ = SDL_RenderFillRect(renderer, ref pixel);
                    }
                }
            }
        }

        private static int GetRendererTextWidth(string text)
        {
            return text.Length * MenuTextCellWidth;
        }

        private static string TrimRendererText(string text, int columns)
        {
            string trimmed = text.Trim();
            if (trimmed.Length <= columns)
                return trimmed;

            return columns <= 3 ? trimmed[..columns] : trimmed[..(columns - 3)] + "...";
        }

        private void DrawDriveGlyphs()
        {
            int bottomOverlayHeight = GetBottomOverlayHeight();
            int bottomOverlayY = logicalHeight - bottomOverlayHeight;
            int driveBlockHeight = GetDriveBlockHeight();
            int drive1X = logicalWidth - DriveGlyphMargin - DriveGlyphWidth;
            int drive0X = drive1X - DriveGlyphGap - DriveGlyphWidth;
            int glyphY = bottomOverlayY + ((bottomOverlayHeight - BottomOverlayExtraHeight - driveBlockHeight) / 2) + BottomOverlayContentOffsetY;

            SdlRect bottomOverlay = new SdlRect(0, bottomOverlayY, logicalWidth, bottomOverlayHeight);
            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            _ = SDL_RenderFillRect(renderer, ref bottomOverlay);

            DrawStatusLeds(bottomOverlayY + BottomOverlayContentOffsetY);
            DrawHayesModemPanel();
            DrawCassetteImage();
            if (Drive0Enabled)
            {
                DrawDriveGlyph(drive0X, glyphY, 0, Drive0Mounted, Drive0ActivityLedActive, Drive0DoubleSided);
                DrawDriveNumber(drive0X, glyphY, 0);
            }
            if (Drive1Enabled)
            {
                DrawDriveGlyph(drive1X, glyphY, 1, Drive1Mounted, Drive1ActivityLedActive, Drive1DoubleSided);
                DrawDriveNumber(drive1X, glyphY, 1);
            }
            if (!IsBottomOverlayMenu(activeMenuIndex))
            {
                DrawHoveredCassetteLabel();
                DrawHoveredDriveLabel(drive0X, drive1X, glyphY);
            }
        }

        private int GetBottomOverlayHeight()
        {
            int statusBlockHeight = (StatusLabelGlyphHeight * 2)
                + StatusLabelLineGap
                + StatusLabelLedGap
                + StatusLedDiameter;

            int contentHeight = Math.Max(Math.Max(GetDriveBlockHeight(), statusBlockHeight), GetHayesPanelHeight());
            return contentHeight + (BottomOverlayPadding * 2) + BottomOverlayExtraHeight;
        }

        private static int GetDriveBlockHeight()
        {
            return DriveGlyphHeight + DriveNumberGap + DriveNumberHeight;
        }

        private void DrawStatusLeds(int bottomOverlayY)
        {
            int leftmostCenterX = StatusLedLeftMargin + (StatusLedDiameter / 2);
            int labelY = bottomOverlayY + BottomOverlayPadding;
            int ledCenterY = labelY
                + (StatusLabelGlyphHeight * 2)
                + StatusLabelLineGap
                + StatusLabelLedGap
                + (StatusLedDiameter / 2);

            DrawStatusLed(leftmostCenterX, labelY, ledCenterY, "CASSETTE", "MOTOR", CassetteMotorLedActive);
            DrawStatusLed(leftmostCenterX + StatusLedGap, labelY, ledCenterY, "CAPS", "LOCK", CapsLockLedActive);
            DrawStatusLed(leftmostCenterX + (StatusLedGap * 2), labelY, ledCenterY, "SHIFT", "LOCK", ShiftLockLedActive);
        }

        private void DrawStatusLed(int centerX, int labelY, int centerY, string topLabel, string bottomLabel, bool active)
        {
            DrawTinyLabel(topLabel, centerX, labelY, OverlayTextGrey, OverlayTextGrey, OverlayTextGrey);
            DrawTinyLabel(bottomLabel, centerX, labelY + StatusLabelGlyphHeight + StatusLabelLineGap, OverlayTextGrey, OverlayTextGrey, OverlayTextGrey);
            DrawRoundLed(centerX, centerY, StatusLedDiameter / 2, active ? (byte)220 : (byte)38, 0, 0);
        }

        private void DrawHayesModemPanel()
        {
            if (!HayesModemEnabled)
                return;

            SdlRect panel = GetHayesPanelRect();
            int brandWidth = GetRendererTextWidth(HayesMenuTitle);

            _ = SDL_SetRenderDrawColor(renderer, 12, 12, 12, 240);
            _ = SDL_RenderFillRect(renderer, ref panel);
            _ = SDL_SetRenderDrawColor(renderer, 65, 65, 65, 255);
            DrawRectOutline(panel);

            if (activeMenuIndex == HayesMenuIndex || hoveredMenuIndex == HayesMenuIndex)
            {
                SdlRect hover = GetHayesMenuLabelRect();
                _ = SDL_SetRenderDrawColor(renderer, 42, 42, 42, 235);
                _ = SDL_RenderFillRect(renderer, ref hover);
                _ = SDL_SetRenderDrawColor(renderer, 96, 96, 96, 255);
                DrawRectOutline(hover);
            }

            int brandY = panel.Y + HayesPanelPaddingY + 3;
            int ledCenterY = panel.Y + HayesPanelPaddingY + (StatusLedDiameter / 2);
            int labelY = ledCenterY + (StatusLedDiameter / 2) + StatusLabelLedGap;
            int firstLedCenterX = panel.X + HayesPanelPaddingX + brandWidth + HayesPanelBrandGap + (StatusLedDiameter / 2);

            byte brandColour = activeMenuIndex == HayesMenuIndex || hoveredMenuIndex == HayesMenuIndex ? (byte)245 : (byte)190;
            DrawRendererText(HayesMenuTitle, panel.X + HayesPanelPaddingX, brandY, brandColour, brandColour, brandColour);
            DrawHayesLed(firstLedCenterX, ledCenterY, labelY, "HS", HayesHighSpeedLedActive);
            DrawHayesLed(firstLedCenterX + HayesPanelLedGap, ledCenterY, labelY, "AA", HayesAutoAnswerLedActive);
            DrawHayesLed(firstLedCenterX + (HayesPanelLedGap * 2), ledCenterY, labelY, "CD", HayesCarrierDetectLedActive);
            DrawHayesLed(firstLedCenterX + (HayesPanelLedGap * 3), ledCenterY, labelY, "OH", HayesOffHookLedActive);
            DrawHayesLed(firstLedCenterX + (HayesPanelLedGap * 4), ledCenterY, labelY, "RD", HayesReceiveDataLedActive);
            DrawHayesLed(firstLedCenterX + (HayesPanelLedGap * 5), ledCenterY, labelY, "SD", HayesSendDataLedActive);
            DrawHayesLed(firstLedCenterX + (HayesPanelLedGap * 6), ledCenterY, labelY, "TR", HayesTerminalReadyLedActive);
            DrawHayesLed(firstLedCenterX + (HayesPanelLedGap * 7), ledCenterY, labelY, "MR", HayesModemReadyLedActive);
        }

        private void DrawHayesLed(int centerX, int centerY, int labelY, string label, bool active)
        {
            DrawRoundLed(centerX, centerY, StatusLedDiameter / 2, active ? (byte)220 : (byte)58, 0, 0);
            DrawTinyLabel(label, centerX, labelY, OverlayTextGrey, OverlayTextGrey, OverlayTextGrey);
        }

        private void DrawDriveGlyph(int glyphX, int glyphY, int drive, bool mounted, bool activityLedActive, bool doubleSided)
        {
            SdlRect glyphRect = new SdlRect(glyphX, glyphY, DriveGlyphWidth, DriveGlyphHeight);
            int driveMenuIndex = GetDriveMenuIndex(drive);
            bool active = activeMenuIndex == driveMenuIndex || hoveredMenuIndex == driveMenuIndex;
            _ = SDL_SetRenderDrawColor(renderer, DriveGlyphBodyRed, DriveGlyphBodyGreen, DriveGlyphBodyBlue, 255);
            _ = SDL_RenderFillRect(renderer, ref glyphRect);
            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            DrawRectOutline(glyphRect);
            if (active)
            {
                _ = SDL_SetRenderDrawColor(renderer, 96, 96, 96, 255);
                DrawRectOutline(new SdlRect(glyphX - 2, glyphY - 2, DriveGlyphWidth + 4, DriveGlyphHeight + 4));
            }

            DrawDriveMediaSelector(glyphX, glyphY, doubleSided);

            SdlRect slot = new SdlRect(glyphX + 5, glyphY + 11, DriveGlyphWidth - 10, 5);
            _ = SDL_SetRenderDrawColor(renderer, 118, 93, 0, 255);
            _ = SDL_RenderFillRect(renderer, ref slot);
            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            DrawRectOutline(slot);

            DrawDriveLever(glyphX, glyphY, mounted);
            DrawDriveLed(glyphX, glyphY, activityLedActive);
        }

        private void DrawDriveNumber(int glyphX, int glyphY, int drive)
        {
            int x = glyphX + ((DriveGlyphWidth - DriveNumberWidth) / 2);
            int y = glyphY + DriveGlyphHeight + DriveNumberGap;

            DrawTinyGlyph((char)('0' + drive), x, y, OverlayTextGrey, OverlayTextGrey, OverlayTextGrey);
        }

        private void DrawDriveMediaSelector(int glyphX, int glyphY, bool doubleSided)
        {
            DrawTinyText("40", glyphX + 57, glyphY + 4, 128, 96, 0);
            DrawTinyText("80", glyphX + 82, glyphY + 4, 128, 96, 0);

            SdlRect selector = new SdlRect(glyphX + 66, glyphY + 3, 15, 7);
            _ = SDL_SetRenderDrawColor(renderer, 235, 226, 174, 255);
            _ = SDL_RenderFillRect(renderer, ref selector);
            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            DrawRectOutline(selector);

            SdlRect selected = doubleSided
                ? new SdlRect(selector.X + 8, selector.Y + 1, 6, selector.H - 2)
                : new SdlRect(selector.X + 1, selector.Y + 1, 6, selector.H - 2);
            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            _ = SDL_RenderFillRect(renderer, ref selected);
        }

        private void DrawDriveLever(int glyphX, int glyphY, bool mounted)
        {
            if (mounted)
            {
                SdlRect disc = new SdlRect(glyphX + 7, glyphY + 13, DriveGlyphWidth - 14, 1);
                _ = SDL_SetRenderDrawColor(renderer, 255, 255, 255, 255);
                _ = SDL_RenderFillRect(renderer, ref disc);
                _ = SDL_SetRenderDrawColor(renderer, 255, 255, 255, 255);
                DrawRectOutline(disc);


                SdlRect stem = new SdlRect(glyphX + 22, glyphY + 5, 5, 15);
                _ = SDL_SetRenderDrawColor(renderer, 190, 190, 190, 255);
                _ = SDL_RenderFillRect(renderer, ref stem);
                _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
                DrawRectOutline(stem);
                return;
            }

            SdlRect handle = new SdlRect(glyphX + 19, glyphY + 5, 20, 5);
            _ = SDL_SetRenderDrawColor(renderer, 190, 190, 190, 255);
            _ = SDL_RenderFillRect(renderer, ref handle);
            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            DrawRectOutline(handle);
        }

        private void DrawDriveLed(int glyphX, int glyphY, bool active)
        {
            int radius = CassetteLedDiameter / 2;
            int centerX = glyphX + 7;
            int centerY = glyphY + 6;

            if (active)
            {
                DrawRoundLed(centerX, centerY, radius, 238, 32, 42);
                return;
            }

            DrawRoundLed(centerX, centerY, radius, 38, 0, 0);
        }

        private void DrawCassetteLed(SdlRect glyph, bool active)
        {
            int radius = CassetteLedDiameter / 2;
            int centerX = glyph.X + CassetteLedOffsetX;
            int centerY = glyph.Y + CassetteLedOffsetY;

            if (active)
            {
                DrawRoundLed(centerX, centerY, radius, 238, 32, 42);
                return;
            }

            DrawRoundLed(centerX, centerY, radius, 38, 0, 0);
        }

        private void DrawCassetteCounter(SdlRect glyph)
        {
            string text = Math.Clamp(TapeCounter, 0, 999).ToString("D3", CultureInfo.InvariantCulture);
            int x = glyph.X + 22;
            int y = glyph.Y + 4;

            DrawTinyText(text, x, y, 255, 255, 255);
        }

        private void DrawHoveredDriveLabel(int drive0X, int drive1X, int glyphY)
        {
            string? label = null;
            int centerX = 0;

            if (Drive0Enabled && Drive0Mounted && IsMouseOverDriveGlyph(drive0X, glyphY))
            {
                label = FormatDriveLabel(0, Drive0Label);
                centerX = drive0X + (DriveGlyphWidth / 2);
            }
            else if (Drive1Enabled && Drive1Mounted && IsMouseOverDriveGlyph(drive1X, glyphY))
            {
                label = FormatDriveLabel(1, Drive1Label);
                centerX = drive1X + (DriveGlyphWidth / 2);
            }

            if (string.IsNullOrWhiteSpace(label))
                return;

            DrawHoverLabel(label, centerX, glyphY - 6);
        }

        private void DrawHoveredCassetteLabel()
        {
            if (!TapeMounted || !IsInCassetteMenuLabel(uiMouseX, uiMouseY))
                return;

            SdlRect cassette = GetCassetteImageRect();
            DrawHoverLabel(FormatCassetteLabel(TapeLabel), cassette.X + (cassette.W / 2), cassette.Y - 6);
        }

        private void DrawHoverLabel(string label, int centerX, int bottomY)
        {
            if (string.IsNullOrWhiteSpace(label))
                return;

            const int paddingX = 6;
            const int paddingY = 4;
            const int maxColumns = 34;
            string text = TrimRendererText(label, maxColumns);
            int width = GetRendererTextWidth(text) + (paddingX * 2);
            int height = NotificationGlyphHeight + (paddingY * 2);
            int x = Math.Clamp(centerX - (width / 2), 4, logicalWidth - width - 4);
            int y = Math.Max(TopMenuHeight + 2, bottomY - height);

            SdlRect background = new SdlRect(x, y, width, height);
            _ = SDL_SetRenderDrawColor(renderer, 12, 12, 12, 235);
            _ = SDL_RenderFillRect(renderer, ref background);
            _ = SDL_SetRenderDrawColor(renderer, 112, 112, 112, 255);
            DrawRectOutline(background);
            DrawRendererText(text, x + paddingX, y + paddingY, 220, 220, 220);
        }

        private bool IsMouseOverDriveGlyph(int glyphX, int glyphY)
        {
            return uiMouseX >= glyphX
                && uiMouseX < glyphX + DriveGlyphWidth
                && uiMouseY >= glyphY
                && uiMouseY < glyphY + DriveGlyphHeight;
        }

        private SdlRect GetDriveGlyphRect(int drive)
        {
            int bottomOverlayHeight = GetBottomOverlayHeight();
            int bottomOverlayY = logicalHeight - bottomOverlayHeight;
            int driveBlockHeight = GetDriveBlockHeight();
            int drive1X = logicalWidth - DriveGlyphMargin - DriveGlyphWidth;
            int drive0X = drive1X - DriveGlyphGap - DriveGlyphWidth;
            int glyphY = bottomOverlayY + ((bottomOverlayHeight - BottomOverlayExtraHeight - driveBlockHeight) / 2) + BottomOverlayContentOffsetY;

            return new SdlRect(drive == 0 ? drive0X : drive1X, glyphY, DriveGlyphWidth, DriveGlyphHeight);
        }

        private SdlRect GetCassetteImageRect()
        {
            if (cassetteTexture == IntPtr.Zero)
                return new SdlRect(0, 0, 0, 0);

            SdlRect drive0 = GetDriveGlyphRect(0);
            int width = cassetteTextureWidth;
            int height = cassetteTextureHeight;
            int x = drive0.X - DriveGlyphGap - width - 10;
            int y = drive0.Y + drive0.H - height;
            return new SdlRect(x, y, width, height);
        }

        private bool IsInDriveMenuLabel(int x, int y)
        {
            return GetDriveMenuIndexAt(x, y) != -1;
        }

        private bool IsInCassetteMenuLabel(int x, int y)
        {
            if (!TapePlayerEnabled)
                return false;

            SdlRect rect = GetCassetteImageRect();
            return rect.W > 0 && x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;
        }

        private int GetDriveMenuIndexAt(int x, int y)
        {
            for (int drive = 0; drive <= 1; drive++)
            {
                if (!IsDriveEnabled(drive))
                    continue;

                SdlRect rect = GetDriveGlyphRect(drive);
                if (x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H)
                    return GetDriveMenuIndex(drive);
            }

            return -1;
        }

        private static bool IsDriveMenu(int menuIndex)
        {
            return menuIndex is Drive0MenuIndex or Drive1MenuIndex;
        }

        private bool IsDriveEnabled(int drive)
        {
            return drive == 0 ? Drive0Enabled : Drive1Enabled;
        }

        private static bool IsCassetteMenu(int menuIndex)
        {
            return menuIndex == CassetteMenuIndex;
        }

        private static bool IsBottomOverlayMenu(int menuIndex)
        {
            return menuIndex == HayesMenuIndex || IsDriveMenu(menuIndex) || IsCassetteMenu(menuIndex);
        }

        private bool IsOpenMenuIndex(int menuIndex)
        {
            return menuIndex >= 0 && !IsDirectMenu(menuIndex) || menuIndex == HayesMenuIndex || IsDriveMenu(menuIndex) || IsCassetteMenu(menuIndex);
        }

        private static int GetDriveMenuIndex(int drive)
        {
            return drive == 0 ? Drive0MenuIndex : Drive1MenuIndex;
        }

        private static int GetDriveMenuDrive(int menuIndex)
        {
            return menuIndex == Drive0MenuIndex ? 0 : 1;
        }

        private static string FormatDriveLabel(int drive, string? label)
        {
            string disc = string.IsNullOrWhiteSpace(label) ? "disc" : label.Trim();
            return $"{drive}: {disc}";
        }

        private static string FormatCassetteLabel(string? label)
        {
            return string.IsNullOrWhiteSpace(label) ? "Tape" : label.Trim();
        }

        private void DrawRoundLed(int centerX, int centerY, int radius, byte red, byte green, byte blue)
        {
            _ = SDL_SetRenderDrawColor(renderer, red, green, blue, 255);
            for (int y = -radius; y < radius; y++)
            {
                int halfWidth = (int)Math.Sqrt((radius * radius) - (y * y));
                SdlRect row = new SdlRect(centerX - halfWidth, centerY + y, halfWidth * 2, 1);
                _ = SDL_RenderFillRect(renderer, ref row);
            }

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        private void DrawTinyLabel(string text, int centerX, int y, byte red, byte green, byte blue)
        {
            int width = GetTinyTextWidth(text);
            int x = centerX - (width / 2);

            DrawTinyText(text, x, y, red, green, blue);
        }

        private void DrawTinyText(string text, int x, int y, byte red, byte green, byte blue)
        {
            if (text.Length == 0)
                return;

            CachedTextTexture cached = GetCachedTinyText(text, red, green, blue);
            SdlRect destination = new SdlRect(x, y, GetTinyTextWidth(text), cached.Height);
            _ = SDL_RenderCopy(renderer, cached.Texture, IntPtr.Zero, ref destination);
        }

        private static int GetTinyTextWidth(string text)
        {
            return text.Length == 0
                ? 0
                : (text.Length * StatusLabelGlyphWidth) + ((text.Length - 1) * StatusLabelGlyphGap);
        }

        private void DrawTinyGlyph(char character, int x, int y, byte red, byte green, byte blue)
        {
            byte[] rows = TinyOverlayFont.GetRows(character);

            _ = SDL_SetRenderDrawColor(renderer, red, green, blue, 255);
            for (int row = 0; row < StatusLabelGlyphHeight; row++)
            {
                byte mask = rows[row];
                for (int column = 0; column < StatusLabelGlyphWidth; column++)
                {
                    if ((mask & (1 << (StatusLabelGlyphWidth - 1 - column))) == 0)
                        continue;

                    SdlRect pixel = new SdlRect(x + column, y + row, 1, 1);
                    _ = SDL_RenderFillRect(renderer, ref pixel);
                }
            }

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        private static class TinyOverlayFont
        {
            private static readonly byte[] Blank = [0, 0, 0, 0, 0];
            private static readonly Dictionary<char, byte[]> Glyphs = new Dictionary<char, byte[]>
            {
                ['0'] = [0b111, 0b101, 0b101, 0b101, 0b111],
                ['1'] = [0b010, 0b110, 0b010, 0b010, 0b111],
                ['2'] = [0b111, 0b001, 0b111, 0b100, 0b111],
                ['3'] = [0b111, 0b001, 0b111, 0b001, 0b111],
                ['4'] = [0b101, 0b101, 0b111, 0b001, 0b001],
                ['5'] = [0b111, 0b100, 0b111, 0b001, 0b111],
                ['6'] = [0b111, 0b100, 0b111, 0b101, 0b111],
                ['7'] = [0b111, 0b001, 0b010, 0b010, 0b010],
                ['8'] = [0b111, 0b101, 0b111, 0b101, 0b111],
                ['9'] = [0b111, 0b101, 0b111, 0b001, 0b111],
                ['A'] = [0b010, 0b101, 0b111, 0b101, 0b101],
                ['C'] = [0b111, 0b100, 0b100, 0b100, 0b111],
                ['D'] = [0b110, 0b101, 0b101, 0b101, 0b110],
                ['E'] = [0b111, 0b100, 0b110, 0b100, 0b111],
                ['F'] = [0b111, 0b100, 0b110, 0b100, 0b100],
                ['H'] = [0b101, 0b101, 0b111, 0b101, 0b101],
                ['I'] = [0b111, 0b010, 0b010, 0b010, 0b111],
                ['K'] = [0b101, 0b101, 0b110, 0b101, 0b101],
                ['L'] = [0b100, 0b100, 0b100, 0b100, 0b111],
                ['M'] = [0b101, 0b111, 0b111, 0b101, 0b101],
                ['O'] = [0b111, 0b101, 0b101, 0b101, 0b111],
                ['P'] = [0b110, 0b101, 0b110, 0b100, 0b100],
                ['R'] = [0b110, 0b101, 0b110, 0b101, 0b101],
                ['S'] = [0b111, 0b100, 0b111, 0b001, 0b111],
                ['T'] = [0b111, 0b010, 0b010, 0b010, 0b010],
                ['Y'] = [0b101, 0b101, 0b010, 0b010, 0b010],
            };

            public static byte[] GetRows(char character)
            {
                return Glyphs.TryGetValue(character, out byte[]? rows) ? rows : Blank;
            }
        }

        private void DrawNotificationOverlay()
        {
            if (notificationVisibleUntilTicks <= Stopwatch.GetTimestamp()
                || (notificationTitle.Length == 0 && notificationBody.Length == 0))
            {
                return;
            }

            int maxPanelWidth = Width - (NotificationMargin * 2);
            int bodyColumns = Math.Max(1, (maxPanelWidth - (NotificationPadding * 2)) / NotificationBodyCellWidth);
            List<string> bodyLines = WrapNotificationText(notificationBody, bodyColumns);
            int titleColumns = Math.Max(1, (maxPanelWidth - (NotificationPadding * 2)) / NotificationTitleCellWidth);
            List<string> titleLines = WrapNotificationText(notificationTitle, titleColumns);

            int titleWidth = titleLines.Count == 0 ? 0 : titleLines.Max(line => line.Length) * NotificationTitleCellWidth;
            int bodyWidth = bodyLines.Count == 0 ? 0 : bodyLines.Max(line => line.Length) * NotificationBodyCellWidth;
            int contentWidth = Math.Max(titleWidth, bodyWidth);
            int panelWidth = Math.Min(maxPanelWidth, contentWidth + (NotificationPadding * 2));
            int titleHeight = titleLines.Count * NotificationTitleCellHeight;
            int bodyHeight = bodyLines.Count * NotificationBodyCellHeight;
            int panelHeight = NotificationPadding + titleHeight + NotificationGap + bodyHeight + NotificationPadding;
            int x = (Width - panelWidth) / 2;
            int y = (Height - panelHeight) / 2;

            FillPixelRect(frameBuffer, Width, Height, x + 4, y + 5, panelWidth, panelHeight, NotificationShadow);
            FillPixelRect(frameBuffer, Width, Height, x, y, panelWidth, panelHeight, NotificationBackground);
            DrawPixelRectOutline(frameBuffer, Width, Height, x, y, panelWidth, panelHeight, NotificationBorder);
            FillPixelRect(frameBuffer, Width, Height, x, y, 6, panelHeight, NotificationAccent);

            int textX = x + NotificationPadding;
            int textY = y + NotificationPadding;
            foreach (string line in titleLines)
            {
                DrawNotificationText(line, textX, textY, NotificationTitleCellWidth, NotificationTitleCellHeight, NotificationTitleColour);
                textY += NotificationTitleCellHeight;
            }

            textY += NotificationGap;
            foreach (string line in bodyLines)
            {
                DrawNotificationText(line, textX, textY, NotificationBodyCellWidth, NotificationBodyCellHeight, NotificationBodyColour);
                textY += NotificationBodyCellHeight;
            }
        }

        private void DrawNotificationText(string text, int x, int y, int cellWidth, int cellHeight, uint colour)
        {
            int scale = Math.Max(1, Math.Min(cellWidth / (NotificationGlyphWidth + 1), cellHeight / NotificationGlyphHeight));
            int glyphPixelWidth = NotificationGlyphWidth * scale;
            int glyphPixelHeight = NotificationGlyphHeight * scale;
            int glyphYOffset = Math.Max(0, (cellHeight - glyphPixelHeight) / 2);

            for (int i = 0; i < text.Length; i++)
            {
                int charX = x + (i * cellWidth);
                byte[] glyph = NotificationFont.GetRows(text[i]);
                int glyphXOffset = Math.Max(0, (cellWidth - glyphPixelWidth) / 2);

                for (int row = 0; row < glyph.Length; row++)
                {
                    byte mask = glyph[row];
                    for (int column = 0; column < NotificationGlyphWidth; column++)
                    {
                        if ((mask & (1 << (NotificationGlyphWidth - 1 - column))) == 0)
                            continue;

                        FillPixelRect(
                            frameBuffer,
                            Width,
                            Height,
                            charX + glyphXOffset + (column * scale),
                            y + glyphYOffset + (row * scale),
                            scale,
                            scale,
                            colour);
                    }
                }
            }
        }

        private static List<string> WrapNotificationText(string text, int columns)
        {
            List<string> lines = new List<string>();
            foreach (string paragraph in text.Replace('\r', '\n').Split('\n'))
            {
                string remaining = paragraph.Trim();
                if (remaining.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                while (remaining.Length > columns)
                {
                    int split = remaining.LastIndexOfAny([' ', '/', '\\', '-'], columns);
                    if (split <= 0)
                        split = columns;

                    int take = split == columns ? split : split + 1;
                    lines.Add(remaining[..take].Trim());
                    remaining = remaining[take..].TrimStart();
                }

                if (remaining.Length > 0)
                    lines.Add(remaining);
            }

            return lines;
        }

        private static class NotificationFont
        {
            private static readonly byte[] Fallback = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100];
            private static readonly Dictionary<char, byte[]> Glyphs = new Dictionary<char, byte[]>
            {
                [' '] = [0, 0, 0, 0, 0, 0, 0],
                ['!'] = [0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0, 0b00100],
                ['"'] = [0b01010, 0b01010, 0b01010, 0, 0, 0, 0],
                ['#'] = [0b01010, 0b11111, 0b01010, 0b01010, 0b11111, 0b01010, 0b01010],
                ['$'] = [0b00100, 0b01111, 0b10100, 0b01110, 0b00101, 0b11110, 0b00100],
                ['%'] = [0b11001, 0b11010, 0b00100, 0b01000, 0b10110, 0b00110, 0],
                ['&'] = [0b01100, 0b10010, 0b10100, 0b01000, 0b10101, 0b10010, 0b01101],
                ['\''] = [0b00100, 0b00100, 0b01000, 0, 0, 0, 0],
                ['('] = [0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010],
                [')'] = [0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000],
                ['*'] = [0, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0],
                ['+'] = [0, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0],
                [','] = [0, 0, 0, 0, 0b00100, 0b00100, 0b01000],
                ['-'] = [0, 0, 0, 0b11111, 0, 0, 0],
                ['.'] = [0, 0, 0, 0, 0, 0b01100, 0b01100],
                ['/'] = [0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000],
                ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
                ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
                ['2'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
                ['3'] = [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
                ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
                ['5'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110],
                ['6'] = [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
                ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
                ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
                ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
                [':'] = [0, 0b01100, 0b01100, 0, 0b01100, 0b01100, 0],
                [';'] = [0, 0b01100, 0b01100, 0, 0b01100, 0b00100, 0b01000],
                ['<'] = [0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010],
                ['='] = [0, 0, 0b11111, 0, 0b11111, 0, 0],
                ['>'] = [0b01000, 0b00100, 0b00010, 0b00001, 0b00010, 0b00100, 0b01000],
                ['?'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0, 0b00100],
                ['@'] = [0b01110, 0b10001, 0b10111, 0b10101, 0b10111, 0b10000, 0b01110],
                ['A'] = [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
                ['B'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
                ['C'] = [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
                ['D'] = [0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110],
                ['E'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
                ['F'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
                ['G'] = [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110],
                ['H'] = [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
                ['I'] = [0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
                ['J'] = [0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100],
                ['K'] = [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
                ['L'] = [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
                ['M'] = [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
                ['N'] = [0b10001, 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001],
                ['O'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
                ['P'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
                ['Q'] = [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
                ['R'] = [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
                ['S'] = [0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110],
                ['T'] = [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
                ['U'] = [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
                ['V'] = [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
                ['W'] = [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010],
                ['X'] = [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
                ['Y'] = [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
                ['Z'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
                ['['] = [0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110],
                ['\\'] = [0b10000, 0b01000, 0b01000, 0b00100, 0b00010, 0b00010, 0b00001],
                [']'] = [0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110],
                ['^'] = [0b00100, 0b01010, 0b10001, 0, 0, 0, 0],
                ['_'] = [0, 0, 0, 0, 0, 0, 0b11111],
                ['`'] = [0b01000, 0b00100, 0b00010, 0, 0, 0, 0],
                ['a'] = [0, 0, 0b01110, 0b00001, 0b01111, 0b10001, 0b01111],
                ['b'] = [0b10000, 0b10000, 0b10110, 0b11001, 0b10001, 0b10001, 0b11110],
                ['c'] = [0, 0, 0b01110, 0b10001, 0b10000, 0b10001, 0b01110],
                ['d'] = [0b00001, 0b00001, 0b01101, 0b10011, 0b10001, 0b10001, 0b01111],
                ['e'] = [0, 0, 0b01110, 0b10001, 0b11111, 0b10000, 0b01110],
                ['f'] = [0b00110, 0b01001, 0b01000, 0b11100, 0b01000, 0b01000, 0b01000],
                ['g'] = [0, 0, 0b01111, 0b10001, 0b01111, 0b00001, 0b01110],
                ['h'] = [0b10000, 0b10000, 0b10110, 0b11001, 0b10001, 0b10001, 0b10001],
                ['i'] = [0b00100, 0, 0b01100, 0b00100, 0b00100, 0b00100, 0b01110],
                ['j'] = [0b00010, 0, 0b00110, 0b00010, 0b00010, 0b10010, 0b01100],
                ['k'] = [0b10000, 0b10000, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010],
                ['l'] = [0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
                ['m'] = [0, 0, 0b11010, 0b10101, 0b10101, 0b10101, 0b10101],
                ['n'] = [0, 0, 0b10110, 0b11001, 0b10001, 0b10001, 0b10001],
                ['o'] = [0, 0, 0b01110, 0b10001, 0b10001, 0b10001, 0b01110],
                ['p'] = [0, 0, 0b11110, 0b10001, 0b11110, 0b10000, 0b10000],
                ['q'] = [0, 0, 0b01111, 0b10001, 0b01111, 0b00001, 0b00001],
                ['r'] = [0, 0, 0b10110, 0b11001, 0b10000, 0b10000, 0b10000],
                ['s'] = [0, 0, 0b01111, 0b10000, 0b01110, 0b00001, 0b11110],
                ['t'] = [0b01000, 0b01000, 0b11100, 0b01000, 0b01000, 0b01001, 0b00110],
                ['u'] = [0, 0, 0b10001, 0b10001, 0b10001, 0b10011, 0b01101],
                ['v'] = [0, 0, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
                ['w'] = [0, 0, 0b10001, 0b10001, 0b10101, 0b10101, 0b01010],
                ['x'] = [0, 0, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001],
                ['y'] = [0, 0, 0b10001, 0b10001, 0b01111, 0b00001, 0b01110],
                ['z'] = [0, 0, 0b11111, 0b00010, 0b00100, 0b01000, 0b11111],
                ['{'] = [0b00010, 0b00100, 0b00100, 0b01000, 0b00100, 0b00100, 0b00010],
                ['|'] = [0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
                ['}'] = [0b01000, 0b00100, 0b00100, 0b00010, 0b00100, 0b00100, 0b01000],
                ['~'] = [0, 0, 0b01000, 0b10101, 0b00010, 0, 0],
                ['£'] = [0b00110, 0b01001, 0b01000, 0b11100, 0b01000, 0b01001, 0b11110],
            };

            public static byte[] GetRows(char character)
            {
                return Glyphs.TryGetValue(character, out byte[]? rows) ? rows : Fallback;
            }
        }

        private IntPtr CreateScanlineTexture(int width, int height)
        {
            IntPtr overlay = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, height);
            if (overlay == IntPtr.Zero)
                return IntPtr.Zero;

            _ = SDL_SetTextureBlendMode(overlay, SDL_BLENDMODE_BLEND);

            uint[] pixels = new uint[width * height];
            for (int y = 0; y < height; y++)
            {
                uint rowColour = (y & 1) == 1 ? ScanlineColour : 0x00000000u;
                int offset = y * width;
                for (int x = 0; x < width; x++)
                    pixels[offset + x] = rowColour;
            }

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(overlay, IntPtr.Zero, handle.AddrOfPinnedObject(), pitchBytes);
            }
            finally
            {
                handle.Free();
            }

            return overlay;
        }

        private IntPtr CreateDriveGlyphTexture(uint colour)
        {
            IntPtr glyph = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, DriveGlyphWidth, DriveGlyphHeight);
            if (glyph == IntPtr.Zero)
                return IntPtr.Zero;

            _ = SDL_SetTextureBlendMode(glyph, SDL_BLENDMODE_BLEND);

            uint[] pixels = new uint[DriveGlyphWidth * DriveGlyphHeight];
            DrawPixelRectOutline(pixels, DriveGlyphWidth, DriveGlyphHeight, 0, 0, DriveGlyphWidth, DriveGlyphHeight, colour);
            DrawPixelRectOutline(pixels, DriveGlyphWidth, DriveGlyphHeight, 5, 3, 15, 4, colour);
            FillPixelRect(pixels, DriveGlyphWidth, DriveGlyphHeight, 5, DriveGlyphHeight - 4, DriveGlyphWidth - 10, 2, colour);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(glyph, IntPtr.Zero, handle.AddrOfPinnedObject(), DriveGlyphWidth * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            return glyph;
        }

        private IntPtr CreateTubeCoProcessorTexture()
        {
            if (!TryLoadTubeCoProcessorPng(out uint[] pixels, out int width, out int height))
                return IntPtr.Zero;

            IntPtr image = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, height);
            if (image == IntPtr.Zero)
                return IntPtr.Zero;

            _ = SDL_SetTextureBlendMode(image, SDL_BLENDMODE_BLEND);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(image, IntPtr.Zero, handle.AddrOfPinnedObject(), width * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            return image;
        }

        private IntPtr CreateCassetteTexture()
        {
            if (!TryLoadCassettePng(out uint[] pixels, out int width, out int height))
                return IntPtr.Zero;

            IntPtr image = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, height);
            if (image == IntPtr.Zero)
                return IntPtr.Zero;

            cassetteTextureWidth = width;
            cassetteTextureHeight = height;
            _ = SDL_SetTextureBlendMode(image, SDL_BLENDMODE_BLEND);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(image, IntPtr.Zero, handle.AddrOfPinnedObject(), width * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            return image;
        }

        private IntPtr CreateCassetteLoadedTexture()
        {
            if (!TryLoadCassetteLoadedPng(out uint[] pixels, out int width, out int height))
                return IntPtr.Zero;

            IntPtr image = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, height);
            if (image == IntPtr.Zero)
                return IntPtr.Zero;

            cassetteLoadedTextureWidth = width;
            cassetteLoadedTextureHeight = height;
            _ = SDL_SetTextureBlendMode(image, SDL_BLENDMODE_BLEND);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(image, IntPtr.Zero, handle.AddrOfPinnedObject(), width * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            return image;
        }

        private IntPtr CreateBbcLogoTexture()
        {
            if (!TryLoadBbcLogoPng(out uint[] pixels, out int width, out int height))
                return IntPtr.Zero;

            IntPtr image = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, height);
            if (image == IntPtr.Zero)
                return IntPtr.Zero;

            bbcLogoTextureWidth = width;
            bbcLogoTextureHeight = height;
            _ = SDL_SetTextureBlendMode(image, SDL_BLENDMODE_BLEND);
            _ = SDL_SetTextureAlphaMod(image, BbcLogoAlpha);

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(image, IntPtr.Zero, handle.AddrOfPinnedObject(), width * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            return image;
        }

        private static bool TryLoadTubeCoProcessorPng(out uint[] pixels, out int width, out int height)
        {
            pixels = [];
            width = 0;
            height = 0;
            using Stream? resource = typeof(Display).Assembly.GetManifestResourceStream(TubeCoProcessorImageResourceName);
            if (resource is null)
                return false;

            using MemoryStream pngStream = new MemoryStream();
            resource.CopyTo(pngStream);
            byte[] png = pngStream.ToArray();
            return TryReadPng(png, out pixels, out width, out height);
        }

        private static bool TryLoadBbcLogoPng(out uint[] pixels, out int width, out int height)
        {
            pixels = [];
            width = 0;
            height = 0;
            using Stream? resource = typeof(Display).Assembly.GetManifestResourceStream(BbcLogoImageResourceName);
            if (resource is null)
                return false;

            using MemoryStream pngStream = new MemoryStream();
            resource.CopyTo(pngStream);
            byte[] png = pngStream.ToArray();
            return TryReadPng(png, out pixels, out width, out height);
        }

        private static bool TryLoadCassettePng(out uint[] pixels, out int width, out int height)
        {
            pixels = [];
            width = 0;
            height = 0;
            using Stream? resource = typeof(Display).Assembly.GetManifestResourceStream(CassetteImageResourceName);
            if (resource is null)
                return false;

            using MemoryStream pngStream = new MemoryStream();
            resource.CopyTo(pngStream);
            byte[] png = pngStream.ToArray();
            return TryReadPng(png, out pixels, out width, out height);
        }

        private static bool TryLoadCassetteLoadedPng(out uint[] pixels, out int width, out int height)
        {
            pixels = [];
            width = 0;
            height = 0;
            using Stream? resource = typeof(Display).Assembly.GetManifestResourceStream(CassetteLoadedImageResourceName);
            if (resource is null)
                return false;

            using MemoryStream pngStream = new MemoryStream();
            resource.CopyTo(pngStream);
            byte[] png = pngStream.ToArray();
            return TryReadPng(png, out pixels, out width, out height);
        }

        private IntPtr CreateRomSocketTexture(bool occupied)
        {
            IntPtr texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, RomSlotWidth, RomSlotHeight);
            if (texture == IntPtr.Zero)
                return IntPtr.Zero;

            _ = SDL_SetTextureBlendMode(texture, SDL_BLENDMODE_BLEND);

            uint[] pixels = new uint[RomSlotWidth * RomSlotHeight];
            uint socket = 0xFFE8E8E8;
            uint shadow = 0xFF707070;
            uint hole = 0xFF050505;
            uint chip = 0xFF181818;
            uint chipEdge = 0xFFBBBBBB;
            uint leg = 0xFFE0E0E0;

            const int firstPinY = 10;
            const int pinPitch = 8;
            const int pinCount = 14;
            int lastPinY = firstPinY + ((pinCount - 1) * pinPitch);

            DrawPixelRectOutline(pixels, RomSlotWidth, RomSlotHeight, 9, 4, RomSlotWidth - 18, RomSlotHeight - 8, socket);
            DrawPixelRectOutline(pixels, RomSlotWidth, RomSlotHeight, 14, 9, RomSlotWidth - 28, RomSlotHeight - 18, shadow);
            FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, (RomSlotWidth / 2) - 5, 6, 10, 2, socket);
            FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, (RomSlotWidth / 2) - 5, RomSlotHeight - 8, 10, 2, socket);

            for (int pin = 0; pin < pinCount; pin++)
            {
                int y = firstPinY + (pin * pinPitch);
                FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, 6, y, 4, 3, hole);
                FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, RomSlotWidth - 10, y, 4, 3, hole);
            }

            if (occupied)
            {
                int chipX = 15;
                int chipY = 8;
                int chipWidth = RomSlotWidth - 30;
                int chipHeight = RomSlotHeight - 16;
                FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, chipX, chipY, chipWidth, chipHeight, chip);
                DrawPixelRectOutline(pixels, RomSlotWidth, RomSlotHeight, chipX, chipY, chipWidth, chipHeight, chipEdge);
                FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, (RomSlotWidth / 2) - 5, chipY + 2, 10, 3, 0xFF000000);
                FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, (RomSlotWidth / 2) - 4, chipY + 36, 8, 8, 0xFF252525);
                FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, (RomSlotWidth / 2) - 4, chipY + 72, 8, 8, 0xFF252525);

                for (int pin = 0; pin < pinCount; pin++)
                {
                    int y = Math.Min(lastPinY, firstPinY + (pin * pinPitch));
                    FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, 10, y + 1, 5, 2, leg);
                    FillPixelRect(pixels, RomSlotWidth, RomSlotHeight, RomSlotWidth - 15, y + 1, 5, 2, leg);
                }
            }

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _ = SDL_UpdateTexture(texture, IntPtr.Zero, handle.AddrOfPinnedObject(), RomSlotWidth * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            return texture;
        }

        private static void DrawPixelRectOutline(uint[] pixels, int textureWidth, int textureHeight, int x, int y, int width, int height, uint colour)
        {
            FillPixelRect(pixels, textureWidth, textureHeight, x, y, width, 1, colour);
            FillPixelRect(pixels, textureWidth, textureHeight, x, y + height - 1, width, 1, colour);
            FillPixelRect(pixels, textureWidth, textureHeight, x, y, 1, height, colour);
            FillPixelRect(pixels, textureWidth, textureHeight, x + width - 1, y, 1, height, colour);
        }

        private static void FillPixelRect(uint[] pixels, int textureWidth, int textureHeight, int x, int y, int width, int height, uint colour)
        {
            int x0 = Math.Clamp(x, 0, textureWidth);
            int y0 = Math.Clamp(y, 0, textureHeight);
            int x1 = Math.Clamp(x + width, 0, textureWidth);
            int y1 = Math.Clamp(y + height, 0, textureHeight);

            for (int py = y0; py < y1; py++)
            {
                int offset = (py * textureWidth) + x0;
                for (int px = x0; px < x1; px++)
                    pixels[offset++] = colour;
            }
        }

        private static bool TryReadPng(ReadOnlySpan<byte> png, out uint[] pixels, out int width, out int height)
        {
            pixels = [];
            width = 0;
            height = 0;
            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            if (png.Length < signature.Length || !png[..signature.Length].SequenceEqual(signature))
                return false;

            using MemoryStream idat = new MemoryStream();
            int offset = signature.Length;
            bool sawHeader = false;
            int bytesPerPixel = 0;
            while (offset + 12 <= png.Length)
            {
                int length = ReadBigEndian(png, offset);
                offset += 4;
                if (length < 0 || offset + 4 + length + 4 > png.Length)
                    return false;

                ReadOnlySpan<byte> type = png.Slice(offset, 4);
                offset += 4;
                ReadOnlySpan<byte> data = png.Slice(offset, length);
                offset += length;
                offset += 4;

                if (type.SequenceEqual("IHDR"u8))
                {
                    if (data.Length != 13 || data[8] != 8 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                        return false;

                    width = ReadBigEndian(data, 0);
                    height = ReadBigEndian(data, 4);
                    if (width <= 0 || height <= 0)
                        return false;

                    bytesPerPixel = data[9] switch
                    {
                        2 => 3,
                        6 => 4,
                        _ => 0
                    };

                    if (bytesPerPixel == 0)
                        return false;

                    sawHeader = true;
                    continue;
                }

                if (type.SequenceEqual("IDAT"u8))
                {
                    idat.Write(data);
                    continue;
                }

                if (type.SequenceEqual("IEND"u8))
                    break;
            }

            if (!sawHeader || idat.Length == 0)
                return false;

            int stride = width * bytesPerPixel;
            byte[] raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (ZLibStream zlib = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int bytesRead = 0;
                while (bytesRead < raw.Length)
                {
                    int read = zlib.Read(raw, bytesRead, raw.Length - bytesRead);
                    if (read == 0)
                        break;

                    bytesRead += read;
                }

                if (bytesRead != raw.Length)
                    return false;
            }

            pixels = new uint[width * height];
            byte[] previous = new byte[stride];
            byte[] current = new byte[stride];

            int rawOffset = 0;
            for (int y = 0; y < height; y++)
            {
                byte filter = raw[rawOffset++];
                raw.AsSpan(rawOffset, stride).CopyTo(current.AsSpan());
                rawOffset += stride;

                if (!UnfilterPngRow(current, previous, filter, bytesPerPixel))
                    return false;

                int pixelOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int source = x * bytesPerPixel;
                    uint alpha = bytesPerPixel == 4 ? current[source + 3] : 255u;
                    pixels[pixelOffset + x] =
                        (alpha << 24)
                        | ((uint)current[source] << 16)
                        | ((uint)current[source + 1] << 8)
                        | current[source + 2];
                }

                current.CopyTo(previous, 0);
            }

            return true;
        }

        private static bool UnfilterPngRow(Span<byte> row, ReadOnlySpan<byte> previous, byte filter, int bytesPerPixel)
        {
            for (int i = 0; i < row.Length; i++)
            {
                byte left = i >= bytesPerPixel ? row[i - bytesPerPixel] : (byte)0;
                byte above = previous[i];
                byte upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : (byte)0;
                row[i] = filter switch
                {
                    0 => row[i],
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + above),
                    3 => (byte)(row[i] + ((left + above) >> 1)),
                    4 => (byte)(row[i] + Paeth(left, above, upperLeft)),
                    _ => row[i]
                };
            }

            return filter <= 4;
        }

        private static byte Paeth(byte left, byte above, byte upperLeft)
        {
            int estimate = left + above - upperLeft;
            int leftDistance = Math.Abs(estimate - left);
            int aboveDistance = Math.Abs(estimate - above);
            int upperLeftDistance = Math.Abs(estimate - upperLeft);
            if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
                return left;

            return aboveDistance <= upperLeftDistance ? above : upperLeft;
        }

        public void Present(ReadOnlySpan<uint> pixels)
        {
            CopyFrame(pixels);
            Present();
        }

        public void SavePng(string path)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            WritePng(path, frameBuffer, Width, Height);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            DisposeTextCache(rendererTextCache);
            DisposeTextCache(tinyTextCache);

            if (scanlineTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(scanlineTexture);
                scanlineTexture = IntPtr.Zero;
            }

            if (tubeCoProcessorTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(tubeCoProcessorTexture);
                tubeCoProcessorTexture = IntPtr.Zero;
            }

            if (cassetteTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(cassetteTexture);
                cassetteTexture = IntPtr.Zero;
            }

            if (bbcLogoTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(bbcLogoTexture);
                bbcLogoTexture = IntPtr.Zero;
            }

            if (cassetteLoadedTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(cassetteLoadedTexture);
                cassetteLoadedTexture = IntPtr.Zero;
            }

            if (emptyDriveGlyphTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(emptyDriveGlyphTexture);
                emptyDriveGlyphTexture = IntPtr.Zero;
            }

            if (mountedDriveGlyphTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(mountedDriveGlyphTexture);
                mountedDriveGlyphTexture = IntPtr.Zero;
            }

            if (emptyRomSocketTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(emptyRomSocketTexture);
                emptyRomSocketTexture = IntPtr.Zero;
            }

            if (occupiedRomSocketTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(occupiedRomSocketTexture);
                occupiedRomSocketTexture = IntPtr.Zero;
            }

            if (texture != IntPtr.Zero)
            {
                SDL_DestroyTexture(texture);
                texture = IntPtr.Zero;
            }

            if (renderer != IntPtr.Zero)
            {
                SDL_DestroyRenderer(renderer);
                renderer = IntPtr.Zero;
            }

            if (window != IntPtr.Zero)
            {
                SDL_DestroyWindow(window);
                window = IntPtr.Zero;
            }

            CloseGameInput();
            SDL_StopTextInput();
            SDL_QuitSubSystem(SDL_INIT_VIDEO | SDL_INIT_GAMECONTROLLER | SDL_INIT_JOYSTICK);
            disposed = true;
        }

        private static void DisposeTextCache(Dictionary<CachedTextKey, CachedTextTexture> cache)
        {
            foreach (CachedTextTexture cached in cache.Values)
                SDL_DestroyTexture(cached.Texture);

            cache.Clear();
        }

        private void EnqueueKeyDown(int keySym)
        {
            int modifiers = SDL_GetModState();

            if (inputMapperOpen)
            {
                HandleInputMapperKeyDown(keySym, modifiers);
                return;
            }

            if (romManagerOpen && keySym == SDLK_ESCAPE)
            {
                CloseRomManager();
                return;
            }

            if (archiveEntries.Count > 0)
            {
                HandleArchiveKeyDown(keySym);
                return;
            }

            if (TryToggleBbcShiftLock(keySym, modifiers))
                return;

            if (keySym == SDLK_CAPSLOCK && inputProfile.SyncHostCapsLock)
            {
                SyncHostCapsLockState();
                return;
            }

            if (keySym == SDLK_F12)
            {
                pendingBreaks.Enqueue(new BreakKeyPress(
                    (modifiers & KMOD_SHIFT) != 0,
                    (modifiers & KMOD_CTRL) != 0));
                return;
            }

            if (TryExecuteMenuShortcut(keySym, modifiers))
                return;

            if (keySym == SDLK_V && (modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
            {
                EnqueueClipboardText();
                return;
            }

            if (keySym == SDLK_S && (modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
            {
                pendingScreenshotRequests++;
                return;
            }

            if (keySym == SDLK_T && (modifiers & KMOD_CTRL) != 0)
            {
                pendingTraceToggleRequests++;
                return;
            }

            if (keySym == SDLK_Q && (modifiers & KMOD_CTRL) != 0)
            {
                pendingSoundToggleRequests++;
                return;
            }

            if (keySym == SDLK_P && (modifiers & KMOD_CTRL) != 0 && (modifiers & KMOD_SHIFT) != 0)
            {
                pendingTapePauseToggleRequests++;
                return;
            }

            if (keySym == SDLK_P && (modifiers & KMOD_CTRL) != 0)
            {
                pendingPauseToggleRequests++;
                return;
            }

            if (EmulationPaused && keySym == SDLK_SPACE)
            {
                pendingFrameAdvanceRequests++;
                return;
            }

            if (keySym == SDLK_F11)
            {
                scanlinesEnabled = !scanlinesEnabled;
                return;
            }

            if (keySym == SDLK_L && (modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
            {
                EnqueueSelectedFile(0);
                return;
            }

            EnqueueKeyboardJoystickChange(keySym, true);

            BbcKeyBinding? key = inputProfile.MapHostKey(keySym, modifiers);
            if (IsHostTextKey(keySym) && ShouldUseTextInputForKey(keySym, modifiers, key))
            {
                textInputHostKeys.Add(keySym);
                return;
            }

            if (key.HasValue)
            {
                if (activeHostKeys.ContainsKey(keySym))
                    return;

                bool shiftAdjusted = ApplyShiftAdjustment(key.Value.ShiftAdjustment, (modifiers & KMOD_SHIFT) != 0);
                activeHostKeys[keySym] = new ActiveHostKey(key.Value.MatrixKey, key.Value.ShiftAdjustment, shiftAdjusted);
                PressBbcMatrixKey(key.Value.MatrixKey);
                if (IsHostTextKey(keySym))
                    suppressedTextInputCharacters++;
            }
        }

        private bool TryExecuteMenuShortcut(int keySym, int modifiers)
        {
            if ((modifiers & KMOD_CTRL) == 0 || (modifiers & KMOD_SHIFT) == 0)
                return false;

            MenuCommand? command = keySym switch
            {
                SDLK_O => MenuCommand.LoadState,
                SDLK_V => MenuCommand.SaveState,
                SDLK_T => MenuCommand.ToggleTapePlayer,
                SDLK_M => MenuCommand.ToggleHayesModem,
                SDLK_D => MenuCommand.ToggleDiscDrive1,
                SDLK_C => MenuCommand.ToggleTube6502,
                SDLK_F => MenuCommand.ToggleFullScreen,
                SDLK_R => MenuCommand.OpenRomManager,
                SDLK_K => MenuCommand.OpenInputMapper,
                _ => null
            };

            if (!command.HasValue)
                return false;

            ExecuteMenuCommand(command.Value);
            activeMenuIndex = -1;
            hoveredMenuItemIndex = -1;
            return true;
        }

        private bool ShouldUseTextInputForKey(int keySym, int modifiers, BbcKeyBinding? key)
        {
            if ((modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
                return false;

            if (!key.HasValue)
                return true;

            if ((modifiers & KMOD_ALT) == 0
                && (modifiers & KMOD_SHIFT) != 0
                && key.Value.ShiftAdjustment == BbcShiftAdjustment.Preserve
                && IsShiftedMatrixGameplayKey(keySym))
                return false;

            return (modifiers & (KMOD_SHIFT | KMOD_ALT)) != 0
                || key.Value.ShiftAdjustment != BbcShiftAdjustment.Preserve;
        }

        private static bool IsHostTextKey(int keySym)
        {
            return keySym >= 32 && keySym <= 126
                || keySym == SDLK_SECTION;
        }

        private static bool IsShiftedMatrixGameplayKey(int keySym)
        {
            return keySym == SDLK_SPACE
                || keySym is >= SDLK_A and <= SDLK_Z;
        }

        private void EnqueueKeyUp(int keySym)
        {
            if (inputMapperOpen)
                return;

            if (keySym == SDLK_CAPSLOCK && inputProfile.SyncHostCapsLock)
            {
                SyncHostCapsLockState();
                return;
            }

            EnqueueKeyboardJoystickChange(keySym, false);

            if (textInputHostKeys.Remove(keySym))
                return;

            if (activeHostKeys.Remove(keySym, out ActiveHostKey activeKey))
            {
                ReleaseBbcMatrixKey(activeKey.MatrixKey);
                RestoreAdjustedShift(activeKey, (SDL_GetModState() & KMOD_SHIFT) != 0);
                return;
            }

            BbcKeyBinding? key = inputProfile.MapHostKey(keySym, SDL_GetModState());
            if (key.HasValue)
                EnqueueBbcKeyChange(key.Value.MatrixKey, false);
        }

        private void EnqueueKeyboardJoystickChange(int keySym, bool pressed)
        {
            JoystickControl? control = inputProfile.MapKeyboardJoystick(keySym);
            if (control.HasValue)
                SetJoystickSource(control.Value, HostJoystickSource.Keyboard, pressed);
        }

        private void ClearLiveInputState()
        {
            foreach (byte matrixKey in activeMatrixKeys.Keys.ToArray())
                EnqueueBbcKeyChange(matrixKey, false);

            activeHostKeys.Clear();
            textInputHostKeys.Clear();
            activeMatrixKeys.Clear();
            suppressedTextInputCharacters = 0;
            ClearJoystickSource(HostJoystickSource.Keyboard);
        }

        public void ClearInputQueuedBeforeBreak()
        {
            pendingInput.Clear();
            pendingKeyChanges.Clear();
            pendingJoystickChanges.Clear();
            activeHostKeys.Clear();
            textInputHostKeys.Clear();
            activeMatrixKeys.Clear();
            suppressedTextInputCharacters = 0;
            for (int i = 0; i < joystickSources.Length; i++)
                joystickSources[i] &= ~HostJoystickSource.Keyboard;
        }

        private void SetJoystickSource(JoystickControl control, HostJoystickSource source, bool pressed)
        {
            int index = (int)control;
            bool wasPressed = joystickSources[index] != HostJoystickSource.None;
            joystickSources[index] = pressed
                ? joystickSources[index] | source
                : joystickSources[index] & ~source;
            bool isPressed = joystickSources[index] != HostJoystickSource.None;
            if (wasPressed != isPressed)
                pendingJoystickChanges.Enqueue(new HostJoystickChange(control, isPressed));
        }

        private void OpenFirstGameInput()
        {
            if (gameController != IntPtr.Zero || joystick != IntPtr.Zero)
                return;

            int count = SDL_NumJoysticks();
            for (int i = 0; i < count; i++)
            {
                if (SDL_IsGameController(i) == SDL_TRUE)
                {
                    gameController = SDL_GameControllerOpen(i);
                    if (gameController != IntPtr.Zero)
                    {
                        IntPtr controllerJoystick = SDL_GameControllerGetJoystick(gameController);
                        activeJoystickInstanceId = controllerJoystick == IntPtr.Zero ? -1 : SDL_JoystickInstanceID(controllerJoystick);
                        return;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                joystick = SDL_JoystickOpen(i);
                if (joystick != IntPtr.Zero)
                {
                    activeJoystickInstanceId = SDL_JoystickInstanceID(joystick);
                    return;
                }
            }
        }

        private void HandleGameInputRemoved(int instanceId)
        {
            if (instanceId != activeJoystickInstanceId)
                return;

            CloseGameInput();
            ClearJoystickSource(HostJoystickSource.ControllerButton | HostJoystickSource.ControllerAxis);
        }

        private void CloseGameInput()
        {
            if (gameController != IntPtr.Zero)
            {
                SDL_GameControllerClose(gameController);
                gameController = IntPtr.Zero;
            }

            if (joystick != IntPtr.Zero)
            {
                SDL_JoystickClose(joystick);
                joystick = IntPtr.Zero;
            }

            activeJoystickInstanceId = -1;
        }

        private void ClearJoystickSource(HostJoystickSource source)
        {
            foreach (JoystickControl control in Enum.GetValues<JoystickControl>())
                SetJoystickSource(control, source, false);
        }

        private void UpdateControllerAxis(byte axis, short value)
        {
            if (!inputProfile.TryMapControllerAxis(axis, out JoystickAxis joystickAxis, out JoystickControl negative, out JoystickControl positive))
                return;

            EnqueueAnalogJoystickChange(joystickAxis, value);
            SetJoystickSource(negative, HostJoystickSource.ControllerAxis, value < -JoystickAxisThreshold);
            SetJoystickSource(positive, HostJoystickSource.ControllerAxis, value > JoystickAxisThreshold);
        }

        private void UpdateControllerButton(byte button, bool pressed)
        {
            JoystickControl? control = inputProfile.MapControllerButton(button);
            if (control.HasValue)
                SetJoystickSource(control.Value, HostJoystickSource.ControllerButton, pressed);
        }

        private void UpdateJoystickAxis(byte axis, short value)
        {
            if (!inputProfile.TryMapJoystickAxis(axis, out JoystickAxis joystickAxis, out JoystickControl negative, out JoystickControl positive))
                return;

            EnqueueAnalogJoystickChange(joystickAxis, value);
            SetJoystickSource(negative, HostJoystickSource.ControllerAxis, value < -JoystickAxisThreshold);
            SetJoystickSource(positive, HostJoystickSource.ControllerAxis, value > JoystickAxisThreshold);
        }

        private void EnqueueAnalogJoystickChange(JoystickAxis axis, short value)
        {
            pendingAnalogJoystickChanges.Enqueue(new HostAnalogJoystickChange(axis, value));
        }

        private void UpdateJoystickHat(byte value)
        {
            SetJoystickSource(JoystickControl.Up, HostJoystickSource.ControllerButton, (value & SDL_HAT_UP) != 0);
            SetJoystickSource(JoystickControl.Down, HostJoystickSource.ControllerButton, (value & SDL_HAT_DOWN) != 0);
            SetJoystickSource(JoystickControl.Left, HostJoystickSource.ControllerButton, (value & SDL_HAT_LEFT) != 0);
            SetJoystickSource(JoystickControl.Right, HostJoystickSource.ControllerButton, (value & SDL_HAT_RIGHT) != 0);
        }

        private void UpdateJoystickButton(byte button, bool pressed)
        {
            JoystickControl? control = inputProfile.MapJoystickButton(button);
            if (control.HasValue)
                SetJoystickSource(control.Value, HostJoystickSource.ControllerButton, pressed);
        }

        private void UpdateMouseState(int hostX, int hostY, int relativeX, int relativeY, byte buttons)
        {
            float logicalX = hostX;
            float logicalY = hostY;
            if (renderer != IntPtr.Zero)
                RenderWindowToLogical(hostX, hostY, out logicalX, out logicalY);

            uiMouseX = (int)Math.Round(logicalX);
            uiMouseY = (int)Math.Round(logicalY);

            int bbcX = relativeMouseMode
                ? mouseState.X + relativeX
                : (int)Math.Round(logicalX - viewportRect.X);
            int bbcY = relativeMouseMode
                ? mouseState.Y + relativeY
                : (int)Math.Round(logicalY - viewportRect.Y);

            mouseState = new HostMouseState(
                Math.Clamp(bbcX, 0, Width - 1),
                Math.Clamp(bbcY, 0, Height - 1),
                buttons,
                mouseState.DeltaX + relativeX,
                mouseState.DeltaY + relativeY);
        }

        private void RenderWindowToLogical(int windowX, int windowY, out float logicalX, out float logicalY)
        {
            logicalX = windowX;
            logicalY = windowY;
        }

        private void UpdateMouseButtonState(byte button, bool pressed, int hostX, int hostY)
        {
            byte mask = button switch
            {
                SDL_BUTTON_LEFT => 0x01,
                SDL_BUTTON_RIGHT => 0x02,
                SDL_BUTTON_MIDDLE => 0x04,
                _ => 0x00
            };

            byte buttons = mouseState.Buttons;
            if (mask != 0)
                buttons = pressed ? (byte)(buttons | mask) : (byte)(buttons & ~mask);

            UpdateMouseState(hostX, hostY, 0, 0, buttons);
        }

        private void SyncHostCapsLockState()
        {
            bool enabled = IsHostCapsLockEnabled();
            if (enabled == hostCapsLockEnabled)
                return;

            hostCapsLockEnabled = enabled;
            pendingKeyChanges.Enqueue(new HostKeyChange(BbcCapsLockKey, enabled));
        }

        private static bool IsHostCapsLockEnabled()
        {
            return (SDL_GetModState() & KMOD_CAPS) != 0;
        }

        private bool TryToggleBbcShiftLock(int keySym, int modifiers)
        {
            bool chordPressed =
                keySym == SDLK_LSHIFT && (modifiers & KMOD_LCTRL) != 0
                || keySym == SDLK_LCTRL && (modifiers & KMOD_LSHIFT) != 0;

            if (!chordPressed)
                return false;

            ToggleBbcShiftLock();
            return true;
        }

        private void ToggleBbcShiftLock()
        {
            bbcShiftLockEnabled = !bbcShiftLockEnabled;
            ShiftLockLedActive = bbcShiftLockEnabled;
            EnqueueBbcKeyChange(BbcShiftKey, bbcShiftLockEnabled);
        }

        private void SetFullScreen(bool enabled)
        {
            ThrowIfSdlFailed(SDL_SetWindowFullscreen(window, enabled ? SDL_WINDOW_FULLSCREEN_DESKTOP : 0), "SDL_SetWindowFullscreen");
            fullScreenEnabled = enabled;
        }

        private void EnqueueBbcKeyChange(byte internalKey, bool pressed)
        {
            if (internalKey == BbcShiftKey && !pressed && bbcShiftLockEnabled)
                return;

            pendingKeyChanges.Enqueue(new HostKeyChange(internalKey, pressed));
        }

        private void PressBbcMatrixKey(byte matrixKey)
        {
            activeMatrixKeys.TryGetValue(matrixKey, out int count);
            activeMatrixKeys[matrixKey] = count + 1;
            if (count == 0)
                EnqueueBbcKeyChange(matrixKey, true);
        }

        private void ReleaseBbcMatrixKey(byte matrixKey)
        {
            if (!activeMatrixKeys.TryGetValue(matrixKey, out int count))
            {
                EnqueueBbcKeyChange(matrixKey, false);
                return;
            }

            if (count > 1)
            {
                activeMatrixKeys[matrixKey] = count - 1;
                return;
            }

            activeMatrixKeys.Remove(matrixKey);
            EnqueueBbcKeyChange(matrixKey, false);
        }

        private bool ApplyShiftAdjustment(BbcShiftAdjustment adjustment, bool hostShiftDown)
        {
            if (adjustment == BbcShiftAdjustment.Suppress && hostShiftDown)
            {
                EnqueueBbcKeyChange(BbcShiftKey, false);
                return true;
            }

            if (adjustment == BbcShiftAdjustment.Force && !hostShiftDown)
            {
                EnqueueBbcKeyChange(BbcShiftKey, true);
                return true;
            }

            return false;
        }

        private void RestoreAdjustedShift(ActiveHostKey activeKey, bool hostShiftDown)
        {
            if (!activeKey.ShiftAdjusted)
                return;

            if (activeKey.ShiftAdjustment == BbcShiftAdjustment.Suppress && hostShiftDown)
                EnqueueBbcKeyChange(BbcShiftKey, true);

            if (activeKey.ShiftAdjustment == BbcShiftAdjustment.Force && !hostShiftDown)
                EnqueueBbcKeyChange(BbcShiftKey, false);
        }

        private readonly record struct ActiveHostKey(byte MatrixKey, BbcShiftAdjustment ShiftAdjustment, bool ShiftAdjusted);

        private readonly record struct CachedTextKey(string Text, byte Red, byte Green, byte Blue);

        private readonly record struct CachedTextTexture(IntPtr Texture, int Width, int Height);

        private readonly record struct BbcInputKey(byte InternalKey, string Label, int X, int Y, int W = 36, int H = 28);

        private static readonly BbcInputKey[] InputKeys =
        [
            new BbcInputKey(0x71, "F1", 120, 38, 38, 38),
            new BbcInputKey(0x72, "F2", 162, 38, 38, 38),
            new BbcInputKey(0x73, "F3", 204, 38, 38, 38),
            new BbcInputKey(0x14, "F4", 246, 38, 38, 38),
            new BbcInputKey(0x74, "F5", 288, 38, 38, 38),
            new BbcInputKey(0x75, "F6", 330, 38, 38, 38),
            new BbcInputKey(0x16, "F7", 372, 38, 38, 38),
            new BbcInputKey(0x76, "F8", 414, 38, 38, 38),
            new BbcInputKey(0x77, "F9", 456, 38, 38, 38),
            new BbcInputKey(0x20, "F0", 498, 38, 38, 38),

            new BbcInputKey(0x70, "ESC", 31, 84, 38, 38),
            new BbcInputKey(0x30, "1", 72, 84, 38, 38),
            new BbcInputKey(0x31, "2", 114, 84, 38, 38),
            new BbcInputKey(0x11, "3", 156, 84, 38, 38),
            new BbcInputKey(0x12, "4", 198, 84, 38, 38),
            new BbcInputKey(0x13, "5", 240, 84, 38, 38),
            new BbcInputKey(0x34, "6", 282, 84, 38, 38),
            new BbcInputKey(0x24, "7", 324, 84, 38, 38),
            new BbcInputKey(0x15, "8", 366, 84, 38, 38),
            new BbcInputKey(0x26, "9", 408, 84, 38, 38),
            new BbcInputKey(0x27, "0", 450, 84, 38, 38),
            new BbcInputKey(0x17, "-", 492, 84, 38, 38),
            new BbcInputKey(0x18, "^", 534, 84, 38, 38),
            new BbcInputKey(0x78, "\\", 576, 84, 38, 38),
            new BbcInputKey(0x19, "LEFT", 622, 84, 38, 38),
            new BbcInputKey(0x79, "RIGHT", 664, 84, 38, 38),

            new BbcInputKey(0x60, "TAB", 36, 128, 52, 38),
            new BbcInputKey(0x10, "Q", 90, 128, 38, 38),
            new BbcInputKey(0x21, "W", 132, 128, 38, 38),
            new BbcInputKey(0x22, "E", 174, 128, 38, 38),
            new BbcInputKey(0x33, "R", 216, 128, 38, 38),
            new BbcInputKey(0x23, "T", 258, 128, 38, 38),
            new BbcInputKey(0x44, "Y", 300, 128, 38, 38),
            new BbcInputKey(0x35, "U", 342, 128, 38, 38),
            new BbcInputKey(0x25, "I", 384, 128, 38, 38),
            new BbcInputKey(0x36, "O", 426, 128, 38, 38),
            new BbcInputKey(0x37, "P", 468, 128, 38, 38),
            new BbcInputKey(0x47, "@", 510, 128, 38, 38),
            new BbcInputKey(0x38, "[", 552, 128, 38, 38),
            new BbcInputKey(0x28, "_", 594, 128, 38, 38),
            new BbcInputKey(0x58, "]", 576, 172, 38, 38),
            new BbcInputKey(0x39, "UP", 640, 128, 38, 38),
            new BbcInputKey(0x29, "DOWN", 682, 128, 38, 38),

            new BbcInputKey(0x40, "CAPS", 26, 172, 42, 38),
            new BbcInputKey(0x01, "CTRL", 73, 172, 42, 38),
            new BbcInputKey(0x41, "A", 114, 172, 38, 38),
            new BbcInputKey(0x51, "S", 156, 172, 38, 38),
            new BbcInputKey(0x32, "D", 198, 172, 38, 38),
            new BbcInputKey(0x43, "F", 240, 172, 38, 38),
            new BbcInputKey(0x53, "G", 282, 172, 38, 38),
            new BbcInputKey(0x54, "H", 324, 172, 38, 38),
            new BbcInputKey(0x45, "J", 366, 172, 38, 38),
            new BbcInputKey(0x46, "K", 408, 172, 38, 38),
            new BbcInputKey(0x56, "L", 450, 172, 38, 38),
            new BbcInputKey(0x57, ";", 492, 172, 38, 38),
            new BbcInputKey(0x48, ":", 534, 172, 38, 38),
            new BbcInputKey(0x49, "RETURN", 620, 172, 76, 38),

            new BbcInputKey(BbcKeyboard.LeftShiftKey, "SHIFT", 64, 216, 76, 38),
            new BbcInputKey(0x61, "Z", 132, 216, 38, 38),
            new BbcInputKey(0x42, "X", 174, 216, 38, 38),
            new BbcInputKey(0x52, "C", 216, 216, 38, 38),
            new BbcInputKey(0x63, "V", 258, 216, 38, 38),
            new BbcInputKey(0x64, "B", 300, 216, 38, 38),
            new BbcInputKey(0x55, "N", 342, 216, 38, 38),
            new BbcInputKey(0x65, "M", 384, 216, 38, 38),
            new BbcInputKey(0x66, ",", 426, 216, 38, 38),
            new BbcInputKey(0x67, ".", 468, 216, 38, 38),
            new BbcInputKey(0x68, "/", 510, 216, 38, 38),
            new BbcInputKey(BbcKeyboard.RightShiftKey, "SHIFT", 552, 216, 74, 38),
            new BbcInputKey(0x59, "DEL", 632, 216, 38, 38),
            new BbcInputKey(0x69, "COPY", 674, 216, 38, 38),

            new BbcInputKey(0x62, "SPACE", 160, 258, 360, 46)
        ];

        private readonly record struct MenuDefinition(string Title, MenuItem[] Items, MenuCommand? DirectCommand = null);

        private readonly record struct MenuItem(string Text, string Shortcut, MenuCommand Command, bool Enabled = true, bool Separator = false, TransportSymbol Symbol = TransportSymbol.None);

        private enum TransportSymbol
        {
            None,
            Record,
            Play,
            Rewind,
            Cue,
            Stop,
            StopOrEject,
            Eject,
            Pause,
            CounterReset
        }

        private enum InputMapperAction
        {
            None,
            ToggleShiftLock,
            Load,
            Save,
            Reset
        }

        private static readonly MenuDefinition HayesMenu = new MenuDefinition(HayesMenuTitle,
        [
            new MenuItem("Loopback", "", MenuCommand.ToggleHayesLoopback),
            new MenuItem("Reset", "", MenuCommand.ResetHayesModem)
        ]);

        private static readonly MenuDefinition EmptyDrive0Menu = new MenuDefinition("Drive 0",
        [
            new MenuItem("LOAD DISC", "", MenuCommand.MountDrive0),
            new MenuItem("CREATE SSD", "", MenuCommand.CreateBlankSsdDrive0)
        ]);

        private static readonly MenuDefinition LoadedDrive0Menu = new MenuDefinition("Drive 0",
        [
            new MenuItem("EJECT DISC", "", MenuCommand.EjectDrive0)
        ]);

        private static readonly MenuDefinition EmptyDrive1Menu = new MenuDefinition("Drive 1",
        [
            new MenuItem("LOAD DISC", "", MenuCommand.MountDrive1),
            new MenuItem("CREATE SSD", "", MenuCommand.CreateBlankSsdDrive1)
        ]);

        private static readonly MenuDefinition LoadedDrive1Menu = new MenuDefinition("Drive 1",
        [
            new MenuItem("EJECT DISC", "", MenuCommand.EjectDrive1)
        ]);

        private static readonly MenuDefinition EmptyCassetteMenu = new MenuDefinition("Cassette",
        [
            new MenuItem("LOAD", "", MenuCommand.LoadTape),
            new MenuItem("CREATE UEF", "", MenuCommand.CreateUefTape)
        ]);

        private static readonly MenuDefinition LoadedCassetteMenu = new MenuDefinition("Cassette",
        [
            new MenuItem("REC", "", MenuCommand.RecordTape, Symbol: TransportSymbol.Record),
            new MenuItem("PLAY", "", MenuCommand.PlayTape, Symbol: TransportSymbol.Play),
            new MenuItem("REW", "", MenuCommand.RewindTape, Symbol: TransportSymbol.Rewind),
            new MenuItem("CUE", "", MenuCommand.FastForwardTape, Symbol: TransportSymbol.Cue),
            new MenuItem("STOP", "", MenuCommand.StopTape, Symbol: TransportSymbol.StopOrEject),
            new MenuItem("PAUSE", "", MenuCommand.PauseTape, Symbol: TransportSymbol.Pause),
            new MenuItem("CTR RESET", "", MenuCommand.ResetTapeCounter, Symbol: TransportSymbol.CounterReset)
        ]);

        private enum MenuCommand
        {
            MountDrive0,
            MountDrive1,
            CreateBlankSsdDrive0,
            CreateBlankSsdDrive1,
            EjectDrive0,
            EjectDrive1,
            LoadTape,
            CreateUefTape,
            RecordTape,
            PlayTape,
            PauseTape,
            StopTape,
            RewindTape,
            FastForwardTape,
            ResetTapeCounter,
            EjectTape,
            SaveScreenshot,
            PrintScreen,
            PrintSavedScreenshot,
            TogglePrinterPageInversion,
            TogglePrinterSound,
            SavePrinterPng,
            NewPrinterPaper,
            NewPrinterPage,
            CancelPrinterActivity,
            SaveState,
            LoadState,
            LoadRecentState1,
            LoadRecentState2,
            LoadRecentState3,
            LoadRecentState4,
            LoadRecentState5,
            Quit,
            Break,
            ShiftBreak,
            ControlBreak,
            PowerReset,
            TogglePause,
            ToggleSoundOutput,
            ToggleTapePause,
            ToggleTapePlayer,
            ToggleDiscDrive0,
            ToggleDiscDrive1,
            ToggleTube6502,
            ToggleHayesModem,
            TogglePrinter,
            ToggleHayesLoopback,
            ResetHayesModem,
            ToggleScanlines,
            ToggleBbcLogo,
            ToggleFullScreen,
            OpenRomManager,
            OpenInputMapper
        }

        private MenuDefinition[] CreateMenus()
        {
            List<MenuDefinition> definitions =
            [
                new MenuDefinition("File",
                    CreateFileMenuItems()),
                new MenuDefinition("Machine",
                [
                    new MenuItem("BREAK", "F12", MenuCommand.Break),
                    new MenuItem("Shift-BREAK", "Shift+F12", MenuCommand.ShiftBreak),
                    new MenuItem("Ctrl-BREAK", "Ctrl+F12", MenuCommand.ControlBreak),
                    new MenuItem("Power reset", "", MenuCommand.PowerReset),
                    MenuSeparator(),
                    new MenuItem("Tape Player", "Ctrl+Shift+T", MenuCommand.ToggleTapePlayer),
                    new MenuItem("Hayes Modem", "Ctrl+Shift+M", MenuCommand.ToggleHayesModem),
                    new MenuItem("Printer", "", MenuCommand.TogglePrinter),
                    new MenuItem("Disc Drive 0", "", MenuCommand.ToggleDiscDrive0),
                    new MenuItem("Disc Drive 1", "Ctrl+Shift+D", MenuCommand.ToggleDiscDrive1),
                    new MenuItem("6502 Co-Processor", "Ctrl+Shift+C", MenuCommand.ToggleTube6502),
                    MenuSeparator(),
                    new MenuItem("Sound output", "Ctrl+Q", MenuCommand.ToggleSoundOutput),
                    new MenuItem("Pause Emulator", "Ctrl+P", MenuCommand.TogglePause)
                ]),
                new MenuDefinition("ROM Manager", [], MenuCommand.OpenRomManager),
                new MenuDefinition("Keyboard Mapper", [], MenuCommand.OpenInputMapper)
            ];

            if (PrinterEnabled)
            {
                definitions.Add(new MenuDefinition("Printer",
                [
                    new MenuItem("Print screen", "", MenuCommand.PrintScreen),
                    new MenuItem("Print saved screenshot...", "", MenuCommand.PrintSavedScreenshot),
                    new MenuItem("Invert printer page", "", MenuCommand.TogglePrinterPageInversion),
                    new MenuItem("Printer sound", "", MenuCommand.TogglePrinterSound),
                    MenuSeparator(),
                    new MenuItem("Save PNG...", "", MenuCommand.SavePrinterPng),
                    MenuSeparator(),
                    new MenuItem("New paper", "", MenuCommand.NewPrinterPaper),
                    new MenuItem("New page", "", MenuCommand.NewPrinterPage),
                    MenuSeparator(),
                    new MenuItem("Cancel printing", "", MenuCommand.CancelPrinterActivity)
                ]));
            }

            definitions.Add(new MenuDefinition("View",
                [
                    new MenuItem("Fullscreen", "Ctrl+Shift+F", MenuCommand.ToggleFullScreen),
                    new MenuItem("Scanlines", "F11", MenuCommand.ToggleScanlines),
                    new MenuItem("BBC logo", "", MenuCommand.ToggleBbcLogo)
                ]));

            return definitions.ToArray();
        }

        private MenuItem[] CreateFileMenuItems()
        {
            List<MenuItem> items =
            [
                new MenuItem("Save screenshot", "Ctrl/Cmd+S", MenuCommand.SaveScreenshot),
                new MenuItem("Open state...", "Ctrl+Shift+O", MenuCommand.LoadState),
                new MenuItem("Save state...", "Ctrl+Shift+V", MenuCommand.SaveState)
            ];

            if (recentStatePaths.Count > 0)
            {
                items.Add(MenuSeparator());
                for (int i = 0; i < recentStatePaths.Count && i < MaxRecentStateFiles; i++)
                {
                    string label = $"{i + 1}. {Path.GetFileName(recentStatePaths[i])}";
                    items.Add(new MenuItem(TrimRendererText(label, 34), "", GetRecentStateCommand(i)));
                }
            }

            items.Add(MenuSeparator());
            items.Add(new MenuItem("Quit", "", MenuCommand.Quit));
            return items.ToArray();
        }

        private bool IsRecentStateAvailable(MenuCommand command)
        {
            int index = GetRecentStateIndex(command);
            return index >= 0 && index < recentStatePaths.Count && File.Exists(recentStatePaths[index]);
        }

        private static MenuCommand GetRecentStateCommand(int index)
        {
            return index switch
            {
                0 => MenuCommand.LoadRecentState1,
                1 => MenuCommand.LoadRecentState2,
                2 => MenuCommand.LoadRecentState3,
                3 => MenuCommand.LoadRecentState4,
                4 => MenuCommand.LoadRecentState5,
                _ => throw new ArgumentOutOfRangeException(nameof(index))
            };
        }

        private static int GetRecentStateIndex(MenuCommand command)
        {
            return command switch
            {
                MenuCommand.LoadRecentState1 => 0,
                MenuCommand.LoadRecentState2 => 1,
                MenuCommand.LoadRecentState3 => 2,
                MenuCommand.LoadRecentState4 => 3,
                MenuCommand.LoadRecentState5 => 4,
                _ => -1
            };
        }

        private void EnqueueSavedScreenshot()
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "Screenshots");
            string[] paths = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
                : [];
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

            if (paths.Length == 0)
            {
                ShowNotification("No saved screenshots", "The Screenshots folder is empty", 3000);
                return;
            }

            string[] fileNames = paths.Select(Path.GetFileName).ToArray()!;
            string? selected = SelectNativeSavedScreenshot(fileNames);
            if (string.IsNullOrWhiteSpace(selected))
                return;

            string? path = paths.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate), selected, StringComparison.Ordinal));
            if (path != null)
                pendingPrinterScreenshotPaths.Enqueue(path);
        }

        private static MenuItem MenuSeparator()
        {
            return new MenuItem(string.Empty, string.Empty, default, Enabled: false, Separator: true);
        }

        private void EnqueueClipboardText()
        {
            IntPtr textPointer = SDL_GetClipboardText();
            if (textPointer == IntPtr.Zero)
                return;

            try
            {
                string? text = Marshal.PtrToStringUTF8(textPointer);
                if (!string.IsNullOrEmpty(text))
                    EnqueueHostText(text);
            }
            finally
            {
                SDL_free(textPointer);
            }
        }

        private void EnqueueDroppedFile(IntPtr filePointer)
        {
            if (filePointer == IntPtr.Zero)
                return;

            try
            {
                string? path = Marshal.PtrToStringUTF8(filePointer);
                if (!string.IsNullOrWhiteSpace(path))
                    pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Mount, path, 0));
            }
            finally
            {
                SDL_free(filePointer);
            }
        }

        private void EnqueueSelectedFile(int drive)
        {
            if (!IsDriveEnabled(drive))
                return;

            string? path = SelectNativeDiscFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Mount, path, drive));
        }

        private void EnqueueSelectedTape()
        {
            string? path = SelectNativeTapeFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.Mount, path));
        }

        private void EnqueueBlankUefTape()
        {
            if (!TapePlayerEnabled || TapeMounted)
                return;

            string? path = SelectNativeUefSaveFile();
            if (!string.IsNullOrWhiteSpace(path))
            {
                string uefPath = EnsureUefExtension(path);
                try
                {
                    UefTape.CreateBlankTape(uefPath, overwrite: true);
                    pendingTapeActions.Enqueue(new HostTapeAction(HostTapeActionKind.Mount, uefPath));
                }
                catch (Exception ex)
                {
                    ShowNotification("Create UEF failed", ex.Message, 5000);
                }
            }
        }

        private void EnqueueBlankSsd(int drive)
        {
            if (drive is < 0 or > 1)
                return;
            if (!IsDriveEnabled(drive))
                return;

            string? path = SelectNativeSaveFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.CreateBlankSsd, path, drive));
        }

        private void EnqueueSaveState()
        {
            string defaultFileName = DefaultSaveStateFileNameProvider?.Invoke() ?? DefaultSaveStateFileName;
            string? path = SelectNativeSaveStateFile(defaultFileName);
            if (!string.IsNullOrWhiteSpace(path))
                pendingStateActions.Enqueue(new HostStateAction(HostStateActionKind.Save, EnsureSaveStateExtension(path)));
        }

        private void EnqueueLoadState()
        {
            string? path = SelectNativeLoadStateFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingStateActions.Enqueue(new HostStateAction(HostStateActionKind.Load, path));
        }

        private void EnqueueRecentState(MenuCommand command)
        {
            int index = GetRecentStateIndex(command);
            if (index >= 0 && index < recentStatePaths.Count)
                pendingStateActions.Enqueue(new HostStateAction(HostStateActionKind.Load, recentStatePaths[index]));
        }

        private static string? SelectNativeDiscFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Select a BBC disc or archive'; $dialog.Filter = 'BBC disc/archive (*.ssd;*.dsd;*.zip)|*.ssd;*.dsd;*.zip'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file with prompt \"Select a BBC disc or archive\" of type {\"ssd\", \"dsd\", \"zip\"})");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Select a BBC disc or archive", "--file-filter=BBC disc/archive (*.ssd *.dsd *.zip) | *.ssd *.dsd *.zip");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeSavedScreenshot(IReadOnlyList<string> fileNames)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string names = string.Join(",", fileNames.Select(name => $"'{name.Replace("'", "''")}'"));
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        $"Add-Type -AssemblyName System.Windows.Forms; $form=New-Object System.Windows.Forms.Form; $form.Text='Print saved screenshot'; $form.Width=560; $form.Height=420; $list=New-Object System.Windows.Forms.ListBox; $list.Dock='Fill'; $list.Items.AddRange([string[]]@({names})); $form.Controls.Add($list); $ok=New-Object System.Windows.Forms.Button; $ok.Text='Print'; $ok.Dock='Bottom'; $ok.Add_Click({{if($list.SelectedItem){{$list.SelectedItem; $form.Close()}}}}); $form.Controls.Add($ok); $form.AcceptButton=$ok; [void]$form.ShowDialog()");
                }

                if (OperatingSystem.IsMacOS())
                {
                    string names = string.Join(", ", fileNames.Select(name => $"\"{EscapeAppleScriptString(name)}\""));
                    string result = RunProcessForSingleLine(
                        "osascript",
                        "-e",
                        $"set chosen to choose from list {{{names}}} with title \"Print saved screenshot\" with prompt \"Select a screenshot to print:\" OK button name \"Print\"") ?? string.Empty;
                    return string.Equals(result, "false", StringComparison.OrdinalIgnoreCase) ? null : result;
                }

                if (OperatingSystem.IsLinux())
                {
                    List<string> arguments =
                    [
                        "--list",
                        "--title=Print saved screenshot",
                        "--text=Select a screenshot to print:",
                        "--column=Screenshot"
                    ];
                    arguments.AddRange(fileNames);
                    return RunProcessForSingleLine("zenity", arguments.ToArray());
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string EscapeAppleScriptString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string? SelectNativeTapeFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Select a BBC tape'; $dialog.Filter = 'UEF tape (*.uef)|*.uef'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file with prompt \"Select a BBC tape\" of type {\"uef\"})");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Select a BBC tape", "--file-filter=UEF tape (*.uef) | *.uef");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeSaveFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.SaveFileDialog; $dialog.Title = 'Create blank DFS SSD'; $dialog.Filter = 'DFS single-sided disc (*.ssd)|*.ssd'; $dialog.DefaultExt = 'ssd'; $dialog.FileName = 'Blank.ssd'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file name with prompt \"Create blank DFS SSD\" default name \"Blank.ssd\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Create blank DFS SSD", "--filename=Blank.ssd");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeUefSaveFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.SaveFileDialog; $dialog.Title = 'Create blank UEF tape'; $dialog.Filter = 'UEF tape (*.uef)|*.uef'; $dialog.DefaultExt = 'uef'; $dialog.FileName = 'Blank.uef'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file name with prompt \"Create blank UEF tape\" default name \"Blank.uef\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Create blank UEF tape", "--filename=Blank.uef");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeSaveStateFile(string defaultFileName)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        $"Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.SaveFileDialog; $dialog.Title = 'Save BBC state'; $dialog.Filter = 'BBC save state (*.sav)|*.sav'; $dialog.DefaultExt = 'sav'; $dialog.FileName = '{defaultFileName}'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{ $dialog.FileName }}");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", $"POSIX path of (choose file name with prompt \"Save BBC state\" default name \"{defaultFileName}\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Save BBC state", $"--filename={defaultFileName}");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeLoadStateFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Open BBC state'; $dialog.Filter = 'BBC save state (*.sav)|*.sav|All files (*.*)|*.*'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file of type {\"sav\"} with prompt \"Open BBC state\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Open BBC state", "--file-filter=BBC save state (*.sav) | *.sav");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeSaveInputProfileFile(string defaultFileName)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        $"Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.SaveFileDialog; $dialog.Title = 'Save BBC input profile'; $dialog.Filter = 'BBC input profile (*.json)|*.json'; $dialog.DefaultExt = 'json'; $dialog.FileName = '{defaultFileName}'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{ $dialog.FileName }}");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", $"POSIX path of (choose file name with prompt \"Save BBC input profile\" default name \"{defaultFileName}\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Save BBC input profile", $"--filename={defaultFileName}");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeLoadInputProfileFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Open BBC input profile'; $dialog.Filter = 'BBC input profile (*.json)|*.json|All files (*.*)|*.*'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file of type {\"json\"} with prompt \"Open BBC input profile\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Open BBC input profile", "--file-filter=BBC input profile (*.json) | *.json");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeRomFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Select BBC sideways ROM'; $dialog.Filter = 'BBC ROM (*.rom)|*.rom|All files (*.*)|*.*'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file of type {\"rom\"} with prompt \"Select BBC sideways ROM\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Select BBC sideways ROM", "--file-filter=BBC ROM (*.rom) | *.rom");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeSaveRomLayoutFile(string defaultFileName)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        $"Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.SaveFileDialog; $dialog.Title = 'Export BBC ROM layout'; $dialog.Filter = 'BBC ROM layout (*.json)|*.json'; $dialog.DefaultExt = 'json'; $dialog.FileName = '{defaultFileName}'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{ $dialog.FileName }}");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", $"POSIX path of (choose file name with prompt \"Export BBC ROM layout\" default name \"{defaultFileName}\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Export BBC ROM layout", $"--filename={defaultFileName}");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SelectNativeLoadRomLayoutFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Import BBC ROM layout'; $dialog.Filter = 'BBC ROM layout (*.json)|*.json|All files (*.*)|*.*'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file with prompt \"Import BBC ROM layout\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Import BBC ROM layout", "--file-filter=BBC ROM layout (*.json) | *.json");
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string EnsureSaveStateExtension(string path)
        {
            return Path.HasExtension(path) ? path : Path.ChangeExtension(path, ".sav");
        }

        private static string EnsureUefExtension(string path)
        {
            return Path.HasExtension(path) ? path : Path.ChangeExtension(path, ".uef");
        }

        private static string EnsureInputProfileExtension(string path)
        {
            return Path.HasExtension(path) ? path : Path.ChangeExtension(path, ".json");
        }

        private static string EnsureRomLayoutExtension(string path)
        {
            return Path.HasExtension(path) ? path : Path.ChangeExtension(path, ".json");
        }

        private static string GetRecentStatePath()
        {
            return Path.Combine(Environment.CurrentDirectory, "RecentSaveStates.txt");
        }

        private static string? RunProcessForSingleLine(string fileName, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(fileName)
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            string output = process.StandardOutput.ReadLine() ?? string.Empty;
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }

        private void EnqueueHostText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                if (ch == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;

                    pendingInput.Enqueue(13);
                    continue;
                }

                if (ch == '\n')
                {
                    pendingInput.Enqueue(13);
                    continue;
                }

                if (ch == '\t')
                {
                    pendingInput.Enqueue((byte)' ');
                    continue;
                }

                if (ch >= 32 && ch <= 126)
                    pendingInput.Enqueue((byte)ch);
            }
        }

        private static void ThrowIfNull(IntPtr value, string operation)
        {
            if (value == IntPtr.Zero)
                throw new InvalidOperationException($"{operation} failed: {GetSdlError()}");
        }

        private static void ThrowIfSdlFailed(int result, string operation)
        {
            if (result < 0)
                throw new InvalidOperationException($"{operation} failed: {GetSdlError()}");
        }

        private static void WritePng(string path, ReadOnlySpan<uint> argbPixels, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            using FileStream file = File.Create(path);
            file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

            byte[] ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, width);
            WriteBigEndian(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(file, "IHDR", ihdr);

            using MemoryStream raw = new MemoryStream((width * height * 4) + height);
            for (int y = 0; y < height; y++)
            {
                raw.WriteByte(0);
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    uint pixel = argbPixels[rowOffset + x];
                    raw.WriteByte((byte)(pixel >> 16));
                    raw.WriteByte((byte)(pixel >> 8));
                    raw.WriteByte((byte)pixel);
                    raw.WriteByte((byte)(pixel >> 24));
                }
            }

            using MemoryStream compressed = new MemoryStream();
            raw.Position = 0;
            using (ZLibStream zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                raw.CopyTo(zlib);

            WriteChunk(file, "IDAT", compressed.ToArray());
            WriteChunk(file, "IEND", []);
        }

        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> header = stackalloc byte[8];
            WriteBigEndian(header, 0, data.Length);
            for (int i = 0; i < 4; i++)
                header[4 + i] = (byte)type[i];

            stream.Write(header);
            stream.Write(data);

            uint crc = Crc32(header[4..8], data);
            Span<byte> crcBytes = stackalloc byte[4];
            WriteBigEndian(crcBytes, 0, unchecked((int)crc));
            stream.Write(crcBytes);
        }

        private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            crc = UpdateCrc32(crc, type);
            crc = UpdateCrc32(crc, data);
            return crc ^ 0xFFFFFFFF;
        }

        private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc ^= value;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }

            return crc;
        }

        private static int ReadBigEndian(ReadOnlySpan<byte> source, int offset)
        {
            return (source[offset] << 24)
                | (source[offset + 1] << 16)
                | (source[offset + 2] << 8)
                | source[offset + 3];
        }

        private static void WriteBigEndian(Span<byte> destination, int offset, int value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private static string GetSdlError()
        {
            IntPtr error = SDL_GetError();
            return error == IntPtr.Zero ? "unknown SDL error" : Marshal.PtrToStringAnsi(error) ?? "unknown SDL error";
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != SdlLibrary)
                return IntPtr.Zero;

            string[] candidates =
            [
                "SDL2",
                "libSDL2.dylib",
                "libSDL2-2.0.0.dylib",
                "/opt/homebrew/lib/libSDL2.dylib",
                "/opt/homebrew/lib/libSDL2-2.0.0.dylib",
                "/usr/local/lib/libSDL2.dylib",
                "/usr/local/lib/libSDL2-2.0.0.dylib",
                "SDL2.dll",
                "libSDL2-2.0.so.0",
                "libSDL2.so"
            ];

            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                    return handle;
            }

            return IntPtr.Zero;
        }

        private const string SdlLibrary = "SDL2";

        private const uint SDL_INIT_VIDEO = 0x00000020;
        private const uint SDL_INIT_JOYSTICK = 0x00000200;
        private const uint SDL_INIT_GAMECONTROLLER = 0x00002000;
        private const uint SDL_WINDOW_SHOWN = 0x00000004;
        private const uint SDL_WINDOW_FULLSCREEN_DESKTOP = 0x00001001;
        private const uint SDL_WINDOW_RESIZABLE = 0x00000020;
        private const uint SDL_WINDOW_ALLOW_HIGHDPI = 0x00002000;
        private const uint SDL_RENDERER_SOFTWARE = 0x00000001;
        private const uint SDL_RENDERER_ACCELERATED = 0x00000002;
        private const uint SDL_RENDERER_PRESENTVSYNC = 0x00000004;
        private const uint SDL_PIXELFORMAT_ARGB8888 = 0x16362004;
        private const int SDL_TEXTUREACCESS_STATIC = 0;
        private const int SDL_TEXTUREACCESS_STREAMING = 1;
        private const int SDL_BLENDMODE_BLEND = 0x00000001;
        private const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;
        private const int SDL_FALSE = 0;
        private const int SDL_TRUE = 1;
        private const uint SDL_QUIT = 0x100;
        private const uint SDL_WINDOWEVENT = 0x200;
        private const byte SDL_WINDOWEVENT_CLOSE = 14;
        private const uint SDL_KEYDOWN = 0x300;
        private const uint SDL_KEYUP = 0x301;
        private const uint SDL_TEXTINPUT = 0x303;
        private const uint SDL_MOUSEMOTION = 0x400;
        private const uint SDL_MOUSEBUTTONDOWN = 0x401;
        private const uint SDL_MOUSEBUTTONUP = 0x402;
        private const uint SDL_MOUSEWHEEL = 0x403;
        private const uint SDL_JOYAXISMOTION = 0x600;
        private const uint SDL_JOYHATMOTION = 0x602;
        private const uint SDL_JOYBUTTONDOWN = 0x603;
        private const uint SDL_JOYBUTTONUP = 0x604;
        private const uint SDL_JOYDEVICEADDED = 0x605;
        private const uint SDL_JOYDEVICEREMOVED = 0x606;
        private const uint SDL_CONTROLLERAXISMOTION = 0x650;
        private const uint SDL_CONTROLLERBUTTONDOWN = 0x651;
        private const uint SDL_CONTROLLERBUTTONUP = 0x652;
        private const uint SDL_CONTROLLERDEVICEADDED = 0x653;
        private const uint SDL_CONTROLLERDEVICEREMOVED = 0x654;
        private const uint SDL_DROPFILE = 0x1000;
        private const int SDL_ENABLE = 1;
        private const byte SDL_BUTTON_LEFT = 1;
        private const byte SDL_BUTTON_MIDDLE = 2;
        private const byte SDL_BUTTON_RIGHT = 3;
        private const byte SDL_CONTROLLER_AXIS_LEFTX = 0;
        private const byte SDL_CONTROLLER_AXIS_LEFTY = 1;
        private const byte SDL_CONTROLLER_BUTTON_A = 0;
        private const byte SDL_CONTROLLER_BUTTON_DPAD_UP = 11;
        private const byte SDL_CONTROLLER_BUTTON_DPAD_DOWN = 12;
        private const byte SDL_CONTROLLER_BUTTON_DPAD_LEFT = 13;
        private const byte SDL_CONTROLLER_BUTTON_DPAD_RIGHT = 14;
        private const byte SDL_HAT_UP = 0x01;
        private const byte SDL_HAT_RIGHT = 0x02;
        private const byte SDL_HAT_DOWN = 0x04;
        private const byte SDL_HAT_LEFT = 0x08;
        private const int SDLK_SPACE = 32;
        private const int SDLK_ASTERISK = 42;
        private const int SDLK_PLUS = 43;
        private const int SDLK_AT = 64;
        private const int SDLK_CARET = 94;
        private const int SDLK_HASH = 35;
        private const int SDLK_APOSTROPHE = 39;
        private const int SDLK_QUOTEDBL = 34;
        private const int SDLK_SECTION = 167;
        private const int SDLK_UNDERSCORE = 95;
        private const int SDLK_0 = 48;
        private const int SDLK_1 = 49;
        private const int SDLK_2 = 50;
        private const int SDLK_3 = 51;
        private const int SDLK_4 = 52;
        private const int SDLK_5 = 53;
        private const int SDLK_6 = 54;
        private const int SDLK_7 = 55;
        private const int SDLK_8 = 56;
        private const int SDLK_9 = 57;
        private const int SDLK_COLON = 58;
        private const int SDLK_SEMICOLON = 59;
        private const int SDLK_BACKSPACE = 8;
        private const int SDLK_TAB = 9;
        private const int SDLK_RETURN = 13;
        private const int SDLK_ESCAPE = 27;
        private const int SDLK_COMMA = 44;
        private const int SDLK_MINUS = 45;
        private const int SDLK_PERIOD = 46;
        private const int SDLK_SLASH = 47;
        private const int SDLK_EQUALS = 61;
        private const int SDLK_DELETE = 127;
        private const int SDLK_LEFTBRACKET = 91;
        private const int SDLK_BACKSLASH = 92;
        private const int SDLK_RIGHTBRACKET = 93;
        private const int SDLK_A = 97;
        private const int SDLK_B = 98;
        private const int SDLK_C = 99;
        private const int SDLK_D = 100;
        private const int SDLK_E = 101;
        private const int SDLK_F = 102;
        private const int SDLK_G = 103;
        private const int SDLK_H = 104;
        private const int SDLK_I = 105;
        private const int SDLK_J = 106;
        private const int SDLK_K = 107;
        private const int SDLK_L = 108;
        private const int SDLK_M = 109;
        private const int SDLK_N = 110;
        private const int SDLK_O = 111;
        private const int SDLK_P = 112;
        private const int SDLK_Q = 113;
        private const int SDLK_R = 114;
        private const int SDLK_S = 115;
        private const int SDLK_T = 116;
        private const int SDLK_U = 117;
        private const int SDLK_V = 118;
        private const int SDLK_W = 119;
        private const int SDLK_X = 120;
        private const int SDLK_Y = 121;
        private const int SDLK_Z = 122;
        private const int SDLK_RIGHT = 1073741903;
        private const int SDLK_LEFT = 1073741904;
        private const int SDLK_DOWN = 1073741905;
        private const int SDLK_UP = 1073741906;
        private const int SDLK_CAPSLOCK = 1073741881;
        private const int SDLK_F1 = 1073741882;
        private const int SDLK_F2 = 1073741883;
        private const int SDLK_F3 = 1073741884;
        private const int SDLK_F4 = 1073741885;
        private const int SDLK_F5 = 1073741886;
        private const int SDLK_F6 = 1073741887;
        private const int SDLK_F7 = 1073741888;
        private const int SDLK_F8 = 1073741889;
        private const int SDLK_F9 = 1073741890;
        private const int SDLK_F10 = 1073741891;
        private const int SDLK_F11 = 1073741892;
        private const int SDLK_INSERT = 1073741897;
        private const int SDLK_KP_MULTIPLY = 1073741909;
        private const int SDLK_KP_ENTER = 1073741912;
        private const int SDLK_RETURN2 = 1073741982;
        private const int SDLK_LCTRL = 1073742048;
        private const int SDLK_LSHIFT = 1073742049;
        private const int SDLK_RSHIFT = 1073742053;
        private const int SDLK_RCTRL = 1073742052;
        private const int SDLK_F12 = 1073741893;
        private const int KMOD_SHIFT = 0x0003;
        private const int KMOD_CTRL = 0x00C0;
        private const int KMOD_LSHIFT = 0x0001;
        private const int KMOD_LCTRL = 0x0040;
        private const int KMOD_ALT = 0x0300;
        private const int KMOD_GUI = 0x0C00;
        private const int KMOD_CAPS = 0x2000;

        [StructLayout(LayoutKind.Explicit, Size = 56)]
        private struct SdlEvent
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public uint WindowId;
            [FieldOffset(12)] public byte WindowEvent;
            [FieldOffset(13)] public byte KeyRepeat;
            [FieldOffset(20)] public int KeySym;
            [FieldOffset(8)] public IntPtr DropFile;
            [FieldOffset(16)] public byte MouseButton;
            [FieldOffset(20)] public int MouseX;
            [FieldOffset(24)] public int MouseY;
            [FieldOffset(28)] public int MouseRelativeX;
            [FieldOffset(32)] public int MouseRelativeY;
            [FieldOffset(20)] public int MouseWheelY;
            [FieldOffset(8)] public int JoystickDeviceInstanceId;
            [FieldOffset(12)] public byte JoystickAxis;
            [FieldOffset(12)] public byte JoystickButton;
            [FieldOffset(13)] public byte JoystickHatValue;
            [FieldOffset(16)] public short JoystickAxisValue;
            [FieldOffset(12)] public byte ControllerAxis;
            [FieldOffset(12)] public byte ControllerButton;
            [FieldOffset(16)] public short ControllerAxisValue;
            [FieldOffset(12)] public byte Text0;
            [FieldOffset(13)] public byte Text1;
            [FieldOffset(14)] public byte Text2;
            [FieldOffset(15)] public byte Text3;
            [FieldOffset(16)] public byte Text4;
            [FieldOffset(17)] public byte Text5;
            [FieldOffset(18)] public byte Text6;
            [FieldOffset(19)] public byte Text7;
            [FieldOffset(20)] public byte Text8;
            [FieldOffset(21)] public byte Text9;
            [FieldOffset(22)] public byte Text10;
            [FieldOffset(23)] public byte Text11;
            [FieldOffset(24)] public byte Text12;
            [FieldOffset(25)] public byte Text13;
            [FieldOffset(26)] public byte Text14;
            [FieldOffset(27)] public byte Text15;
            [FieldOffset(28)] public byte Text16;
            [FieldOffset(29)] public byte Text17;
            [FieldOffset(30)] public byte Text18;
            [FieldOffset(31)] public byte Text19;
            [FieldOffset(32)] public byte Text20;
            [FieldOffset(33)] public byte Text21;
            [FieldOffset(34)] public byte Text22;
            [FieldOffset(35)] public byte Text23;
            [FieldOffset(36)] public byte Text24;
            [FieldOffset(37)] public byte Text25;
            [FieldOffset(38)] public byte Text26;
            [FieldOffset(39)] public byte Text27;
            [FieldOffset(40)] public byte Text28;
            [FieldOffset(41)] public byte Text29;
            [FieldOffset(42)] public byte Text30;
            [FieldOffset(43)] public byte Text31;

            public byte[] Text =>
            [
                Text0, Text1, Text2, Text3, Text4, Text5, Text6, Text7,
                Text8, Text9, Text10, Text11, Text12, Text13, Text14, Text15,
                Text16, Text17, Text18, Text19, Text20, Text21, Text22, Text23,
                Text24, Text25, Text26, Text27, Text28, Text29, Text30, Text31
            ];
        }

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyWindow(IntPtr window);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_GetWindowID(IntPtr window);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GetWindowSize(IntPtr window, out int w, out int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetWindowFullscreen(IntPtr window, uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyRenderer(IntPtr renderer);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetRenderDrawBlendMode(IntPtr renderer, int blendMode);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetLogicalSize(IntPtr renderer, int w, int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetIntegerScale(IntPtr renderer, int enable);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetRendererOutputSize(IntPtr renderer, out int w, out int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_RenderGetViewport(IntPtr renderer, out SdlRect rect);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_RenderGetScale(IntPtr renderer, out float scaleX, out float scaleY);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetRelativeMouseMode(int enabled);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyTexture(IntPtr texture);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetTextureBlendMode(IntPtr texture, int blendMode);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetTextureAlphaMod(IntPtr texture, byte alpha);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_UpdateTexture")]
        private static extern int SDL_UpdateTexture(IntPtr texture, ref SdlRect rect, IntPtr pixels, int pitch);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderClear(IntPtr renderer);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, IntPtr dstrect);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_RenderCopy")]
        private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, ref SdlRect dstrect);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderFillRect(IntPtr renderer, ref SdlRect rect);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_RenderPresent(IntPtr renderer);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_PollEvent(out SdlEvent ev);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_NumJoysticks();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_IsGameController(int joystickIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerOpen(int joystickIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GameControllerClose(IntPtr controller);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerGetJoystick(IntPtr controller);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GameControllerEventState(int state);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_JoystickOpen(int joystickIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_JoystickClose(IntPtr joystick);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_JoystickInstanceID(IntPtr joystick);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_JoystickEventState(int state);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetModState();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetClipboardText();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_free(IntPtr memblock);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_StartTextInput();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_StopTextInput();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();
    }

    /// <summary>BREAK is a BBC key with Shift/Ctrl variants that MOS treats differently during reset.</summary>
    public readonly record struct BreakKeyPress(bool Shift, bool Control);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SdlRect
    {
        public int X;
        public int Y;
        public int W;
        public int H;

        public SdlRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }

    /// <summary>BBC keys are delivered as internal matrix positions, not host key symbols.</summary>
    public readonly record struct HostKeyChange(byte InternalKey, bool Pressed);

    /// <summary>Digital joystick input is converted to the active-low lines expected by BBC games.</summary>
    public readonly record struct HostJoystickChange(JoystickControl Control, bool Pressed);

    /// <summary>Analogue joystick movement eventually reaches software through the BBC's uPD7002 ADC path.</summary>
    public readonly record struct HostAnalogJoystickChange(JoystickAxis Axis, short Value);

    /// <summary>AMX-style mouse code works in BBC screen coordinates plus relative movement pulses.</summary>
    public readonly record struct HostMouseState(int X, int Y, byte Buttons, int DeltaX, int DeltaY);

    public readonly record struct HostDiscAction(HostDiscActionKind Kind, string Path, int Drive, string ArchiveEntryPath = "");

    public readonly record struct HostTapeAction(HostTapeActionKind Kind, string Path);

    public readonly record struct HostStateAction(HostStateActionKind Kind, string Path);

    public enum HostStateActionKind
    {
        Save,
        Load
    }

    public enum HostDiscActionKind
    {
        Mount,
        MountArchiveEntry,
        CreateBlankSsd,
        Eject
    }

    public enum HostTapeActionKind
    {
        Mount,
        Record,
        Play,
        TogglePause,
        Stop,
        Rewind,
        FastForward,
        ResetCounter,
        Eject
    }

    public readonly record struct ArchiveDiscEntry(string Folder, string FileName, string EntryPath);

    public enum JoystickControl
    {
        Left,
        Right,
        Up,
        Down,
        Fire
    }

    public enum JoystickAxis
    {
        X,
        Y
    }

    [Flags]
    internal enum HostJoystickSource
    {
        None = 0,
        Keyboard = 1,
        ControllerAxis = 2,
        ControllerButton = 4
    }
}
