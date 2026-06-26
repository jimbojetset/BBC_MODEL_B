// ============================================================================
// Project:     BBC
// File:        Display.cs
// Description: Host display and input boundary for BBC video frames, keyboard
//              matrix events, BREAK, disc drops, and joystick inputs.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See COPYING in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO.Compression;

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
        private const byte BbcShiftKey = 0x00;
        private const byte BbcCapsLockKey = 0x40;
        private const uint Black = 0xFF000000;
        private const uint ScanlineColour = 0x40000000;
        private const int DriveLedDiameter = 8;
        private const int DriveLedInset = 2;
        private const int DriveGlyphWidth = 34;
        private const int DriveGlyphHeight = 12;
        private const int DriveGlyphMargin = 8;
        private const int DriveGlyphGap = 6;
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
        private const int BottomOverlayPadding = 4;
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
        private readonly Queue<HostStateAction> pendingStateActions = new Queue<HostStateAction>();
        private readonly HostJoystickSource[] joystickSources = new HostJoystickSource[Enum.GetValues<JoystickControl>().Length];
        private readonly MenuDefinition[] menus;
        private int pendingScreenshotRequests;
        private int pendingTraceToggleRequests;
        private int pendingPauseToggleRequests;
        private int pendingFrameAdvanceRequests;
        private int pendingTube6502ToggleRequests;
        private HostMouseState mouseState;
        private bool relativeMouseMode;
        private readonly Dictionary<int, ActiveHostKey> activeHostKeys = new Dictionary<int, ActiveHostKey>();
        private readonly int pitchBytes;

        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private IntPtr scanlineTexture;
        private IntPtr emptyDriveGlyphTexture;
        private IntPtr mountedDriveGlyphTexture;
        private IntPtr gameController;
        private IntPtr joystick;
        private int activeJoystickInstanceId = -1;
        private bool scanlinesEnabled;
        private bool disposed;
        private bool hostCapsLockEnabled;
        private bool bbcShiftLockEnabled;
        private bool fullScreenEnabled;
        private int activeMenuIndex = -1;
        private int hoveredMenuIndex = -1;
        private int hoveredMenuItemIndex = -1;
        private int logicalWidth;
        private int logicalHeight;
        private SdlRect viewportRect;
        private string notificationTitle = string.Empty;
        private string notificationBody = string.Empty;
        private long notificationVisibleUntilTicks;

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

        public bool Drive0Mounted { get; set; }

        public bool Drive1Mounted { get; set; }

        public bool CassetteMotorLedActive { get; set; }

        public bool CapsLockLedActive { get; set; }

        public bool ShiftLockLedActive { get; set; }

        public bool EmulationPaused { get; set; }

        public bool Tube6502Enabled { get; set; }

        public string DefaultSaveStateFileName { get; set; } = "bbc-untitled.sav";

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
            pitchBytes = width * sizeof(uint);
            frameBuffer = new uint[width * height];
            Array.Fill(frameBuffer, Black);
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
            emptyDriveGlyphTexture = CreateDriveGlyphTexture(0xFF404040);
            mountedDriveGlyphTexture = CreateDriveGlyphTexture(0xFF005020);

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

                if (ev.Type == SDL_DROPFILE)
                {
                    EnqueueDroppedFile(ev.DropFile);
                    continue;
                }

                if (ev.Type == SDL_KEYDOWN && ev.KeyRepeat == 0)
                    EnqueueKeyDown(ev.KeySym);

                if (ev.Type == SDL_KEYUP)
                    EnqueueKeyUp(ev.KeySym);

                if (ev.Type == SDL_MOUSEMOTION)
                {
                    if (HandleMenuMouseMotion(ev.MouseX, ev.MouseY))
                        continue;

                    UpdateMouseState(ev.MouseX, ev.MouseY, ev.MouseRelativeX, ev.MouseRelativeY, mouseState.Buttons);
                }

                if (ev.Type is SDL_MOUSEBUTTONDOWN or SDL_MOUSEBUTTONUP)
                {
                    if (HandleMenuMouseButton(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY))
                        continue;

                    UpdateMouseButtonState(ev.MouseButton, ev.Type == SDL_MOUSEBUTTONDOWN, ev.MouseX, ev.MouseY);
                }

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

        public void DrainStateActions(ICollection<HostStateAction> destination)
        {
            while (pendingStateActions.Count > 0)
                destination.Add(pendingStateActions.Dequeue());
        }

        public int DrainScreenshotRequests()
        {
            int count = pendingScreenshotRequests;
            pendingScreenshotRequests = 0;
            return count;
        }

        public int DrainTraceToggleRequests()
        {
            int count = pendingTraceToggleRequests;
            pendingTraceToggleRequests = 0;
            return count;
        }

        public int DrainPauseToggleRequests()
        {
            int count = pendingPauseToggleRequests;
            pendingPauseToggleRequests = 0;
            return count;
        }

        public int DrainFrameAdvanceRequests()
        {
            int count = pendingFrameAdvanceRequests;
            pendingFrameAdvanceRequests = 0;
            return count;
        }

        public int DrainTube6502ToggleRequests()
        {
            int count = pendingTube6502ToggleRequests;
            pendingTube6502ToggleRequests = 0;
            return count;
        }

        public void CopyFrame(ReadOnlySpan<uint> pixels)
        {
            if (pixels.Length != frameBuffer.Length)
                throw new ArgumentException("Frame length must match display dimensions.", nameof(pixels));

            pixels.CopyTo(frameBuffer);
        }

        public void Present()
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            GCHandle handle = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);
            try
            {
                ThrowIfSdlFailed(SDL_UpdateTexture(texture, IntPtr.Zero, handle.AddrOfPinnedObject(), pitchBytes), "SDL_UpdateTexture");
            }
            finally
            {
                handle.Free();
            }

            ThrowIfSdlFailed(SDL_RenderClear(renderer), "SDL_RenderClear");
            ThrowIfSdlFailed(SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref viewportRect), "SDL_RenderCopy");

            if (scanlinesEnabled && scanlineTexture != IntPtr.Zero)
                _ = SDL_RenderCopy(renderer, scanlineTexture, IntPtr.Zero, ref viewportRect);

            DrawTopBorderStatusMessage();
            DrawDriveGlyphs();
            DrawMenuBar();

            SDL_RenderPresent(renderer);
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

            if (activeMenuIndex >= 0)
                DrawOpenMenu(activeMenuIndex);

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        private void DrawOpenMenu(int menuIndex)
        {
            MenuDefinition menu = menus[menuIndex];
            int menuX = GetTopMenuX(menuIndex) - 4;
            int menuY = TopMenuHeight;
            int menuWidth = GetDropDownWidth(menu);
            int menuHeight = GetDropDownHeight(menu);

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
                string label = IsMenuItemChecked(item.Command) ? "* " + item.Text : "  " + item.Text;
                DrawRendererText(label, menuX + 10, itemY + 4, textGrey, textGrey, textGrey);

                if (item.Shortcut.Length > 0)
                {
                    int shortcutX = menuX + menuWidth - 10 - GetRendererTextWidth(item.Shortcut);
                    DrawRendererText(item.Shortcut, shortcutX, itemY + 4, 160, 160, 160);
                }

                itemY += itemHeight;
            }
        }

        private bool HandleMenuMouseMotion(int hostX, int hostY)
        {
            RenderWindowToLogical(hostX, hostY, out float logicalX, out float logicalY);

            hoveredMenuIndex = GetMenuIndexAt((int)logicalX, (int)logicalY);
            hoveredMenuItemIndex = activeMenuIndex >= 0
                ? GetMenuItemIndexAt(activeMenuIndex, (int)logicalX, (int)logicalY)
                : -1;

            if (activeMenuIndex >= 0 && hoveredMenuIndex >= 0)
            {
                activeMenuIndex = hoveredMenuIndex;
                hoveredMenuItemIndex = GetMenuItemIndexAt(activeMenuIndex, (int)logicalX, (int)logicalY);
            }

            return IsMenuArea((int)logicalX, (int)logicalY);
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
            if (menuIndex >= 0)
            {
                activeMenuIndex = activeMenuIndex == menuIndex ? -1 : menuIndex;
                hoveredMenuIndex = menuIndex;
                hoveredMenuItemIndex = -1;
                return true;
            }

            if (activeMenuIndex >= 0)
            {
                int itemIndex = GetMenuItemIndexAt(activeMenuIndex, x, y);
                if (itemIndex >= 0)
                {
                    MenuItem item = menus[activeMenuIndex].Items[itemIndex];
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

        private bool IsMenuArea(int x, int y)
        {
            if (y >= 0 && y < TopMenuHeight)
                return true;

            return activeMenuIndex >= 0 && GetMenuItemIndexAt(activeMenuIndex, x, y) >= 0;
        }

        private int GetMenuIndexAt(int x, int y)
        {
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
            if (menuIndex < 0 || menuIndex >= menus.Length)
                return -1;

            MenuDefinition menu = menus[menuIndex];
            int menuX = GetTopMenuX(menuIndex) - 4;
            int menuY = TopMenuHeight;
            int menuWidth = GetDropDownWidth(menu);
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
            int x = MenuPaddingX;
            for (int i = 0; i < menuIndex; i++)
                x += GetTopMenuWidth(menus[i].Title) + MenuPaddingX;

            return x;
        }

        private static int GetTopMenuWidth(string text)
        {
            return GetRendererTextWidth(text) + 2;
        }

        private static int GetDropDownWidth(MenuDefinition menu)
        {
            int width = 0;
            foreach (MenuItem item in menu.Items)
            {
                if (item.Separator)
                    continue;

                int itemWidth = GetRendererTextWidth("  " + item.Text)
                    + (item.Shortcut.Length == 0 ? 0 : MenuShortcutGap + GetRendererTextWidth(item.Shortcut));
                width = Math.Max(width, itemWidth);
            }

            return width + 20;
        }

        private static int GetDropDownHeight(MenuDefinition menu)
        {
            int height = MenuDropDownPadding * 2;
            foreach (MenuItem item in menu.Items)
                height += GetMenuItemHeight(item);

            return height;
        }

        private static int GetMenuItemHeight(MenuItem item)
        {
            return item.Separator ? MenuSeparatorHeight : MenuItemHeight;
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
                case MenuCommand.CreateBlankSsd:
                    EnqueueBlankSsd();
                    break;
                case MenuCommand.EjectDrive0:
                    pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Eject, string.Empty, 0));
                    break;
                case MenuCommand.EjectDrive1:
                    pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Eject, string.Empty, 1));
                    break;
                case MenuCommand.SaveScreenshot:
                    pendingScreenshotRequests++;
                    break;
                case MenuCommand.SaveState:
                    EnqueueSaveState();
                    break;
                case MenuCommand.LoadState:
                    EnqueueLoadState();
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
                case MenuCommand.TogglePause:
                    pendingPauseToggleRequests++;
                    break;
                case MenuCommand.ToggleTube6502:
                    pendingTube6502ToggleRequests++;
                    break;
                case MenuCommand.ToggleScanlines:
                    scanlinesEnabled = !scanlinesEnabled;
                    break;
                case MenuCommand.ToggleFullScreen:
                    SetFullScreen(!fullScreenEnabled);
                    break;
                case MenuCommand.PasteClipboard:
                    EnqueueClipboardText();
                    break;
                case MenuCommand.ToggleShiftLock:
                    ToggleBbcShiftLock();
                    break;
            }
        }

        private bool IsMenuItemChecked(MenuCommand command)
        {
            return command switch
            {
                MenuCommand.ToggleScanlines => scanlinesEnabled,
                MenuCommand.ToggleFullScreen => fullScreenEnabled,
                MenuCommand.ToggleShiftLock => bbcShiftLockEnabled,
                MenuCommand.TogglePause => EmulationPaused,
                MenuCommand.ToggleTube6502 => Tube6502Enabled,
                _ => false
            };
        }

        private bool IsMenuItemEnabled(MenuItem item)
        {
            return item.Enabled && item.Command switch
            {
                MenuCommand.MountDrive0 => !Drive0Mounted,
                MenuCommand.MountDrive1 => !Drive1Mounted,
                MenuCommand.CreateBlankSsd => !Drive0Mounted || !Drive1Mounted,
                MenuCommand.EjectDrive0 => Drive0Mounted,
                MenuCommand.EjectDrive1 => Drive1Mounted,
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
            _ = SDL_SetRenderDrawColor(renderer, red, green, blue, 255);
            for (int i = 0; i < text.Length; i++)
            {
                byte[] glyph = NotificationFont.GetRows(text[i]);
                int charX = x + (i * MenuTextCellWidth);
                for (int row = 0; row < glyph.Length; row++)
                {
                    byte mask = glyph[row];
                    for (int column = 0; column < NotificationGlyphWidth; column++)
                    {
                        if ((mask & (1 << (NotificationGlyphWidth - 1 - column))) == 0)
                            continue;

                        SdlRect pixel = new SdlRect(charX + column, y + row, 1, 1);
                        _ = SDL_RenderFillRect(renderer, ref pixel);
                    }
                }
            }
        }

        private static int GetRendererTextWidth(string text)
        {
            return text.Length * MenuTextCellWidth;
        }

        private void DrawDriveGlyphs()
        {
            int bottomOverlayHeight = GetBottomOverlayHeight();
            int bottomOverlayY = logicalHeight - bottomOverlayHeight;
            int driveBlockHeight = GetDriveBlockHeight();
            int drive1X = logicalWidth - DriveGlyphMargin - DriveGlyphWidth;
            int drive0X = drive1X - DriveGlyphGap - DriveGlyphWidth;
            int glyphY = bottomOverlayY + ((bottomOverlayHeight - driveBlockHeight) / 2);

            DrawStatusLeds(bottomOverlayY);
            DrawDriveGlyph(drive0X, glyphY, Drive0Mounted, Drive0ActivityLedActive);
            DrawDriveGlyph(drive1X, glyphY, Drive1Mounted, Drive1ActivityLedActive);
            DrawDriveNumber(drive0X, glyphY, 0);
            DrawDriveNumber(drive1X, glyphY, 1);
        }

        private static int GetBottomOverlayHeight()
        {
            int statusBlockHeight = (StatusLabelGlyphHeight * 2)
                + StatusLabelLineGap
                + StatusLabelLedGap
                + StatusLedDiameter;

            return Math.Max(GetDriveBlockHeight(), statusBlockHeight) + (BottomOverlayPadding * 2);
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

        private void DrawDriveGlyph(int glyphX, int glyphY, bool mounted, bool activityLedActive)
        {
            SdlRect glyphRect = new SdlRect(glyphX, glyphY, DriveGlyphWidth, DriveGlyphHeight);

            IntPtr glyphTexture = mounted ? mountedDriveGlyphTexture : emptyDriveGlyphTexture;
            if (glyphTexture != IntPtr.Zero)
                _ = SDL_RenderCopy(renderer, glyphTexture, IntPtr.Zero, ref glyphRect);

            if (activityLedActive)
                DrawDriveLed(glyphX, glyphY);

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        private void DrawDriveNumber(int glyphX, int glyphY, int drive)
        {
            int x = glyphX + ((DriveGlyphWidth - DriveNumberWidth) / 2);
            int y = glyphY + DriveGlyphHeight + DriveNumberGap;

            DrawTinyGlyph((char)('0' + drive), x, y, OverlayTextGrey, OverlayTextGrey, OverlayTextGrey);
        }

        private void DrawDriveLed(int glyphX, int glyphY)
        {
            int radius = DriveLedDiameter / 2;
            int centerX = glyphX + DriveGlyphWidth - DriveLedInset - radius;
            int centerY = glyphY + DriveLedInset + radius;

            DrawRoundLed(centerX, centerY, radius, 220, 0, 0);
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
            int width = (text.Length * StatusLabelGlyphWidth) + ((text.Length - 1) * StatusLabelGlyphGap);
            int x = centerX - (width / 2);

            for (int i = 0; i < text.Length; i++)
                DrawTinyGlyph(text[i], x + (i * (StatusLabelGlyphWidth + StatusLabelGlyphGap)), y, red, green, blue);
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
                ['A'] = [0b010, 0b101, 0b111, 0b101, 0b101],
                ['C'] = [0b111, 0b100, 0b100, 0b100, 0b111],
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

            if (scanlineTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(scanlineTexture);
                scanlineTexture = IntPtr.Zero;
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

        private void EnqueueKeyDown(int keySym)
        {
            int modifiers = SDL_GetModState();

            if (TryToggleBbcShiftLock(keySym, modifiers))
                return;

            if (keySym == SDLK_CAPSLOCK)
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

            BbcKeyChord? chord = MapHostKeyToBbcKey(keySym, modifiers);
            if (chord.HasValue)
            {
                bool shiftAdjusted = ApplyShiftAdjustment(chord.Value.ShiftAdjustment, (modifiers & KMOD_SHIFT) != 0);
                activeHostKeys[keySym] = new ActiveHostKey(chord.Value.InternalKey, chord.Value.ShiftAdjustment, shiftAdjusted);
                EnqueueBbcKeyChange(chord.Value.InternalKey, true);
            }
        }

        private void EnqueueKeyUp(int keySym)
        {
            if (keySym == SDLK_CAPSLOCK)
            {
                SyncHostCapsLockState();
                return;
            }

            EnqueueKeyboardJoystickChange(keySym, false);

            if (activeHostKeys.Remove(keySym, out ActiveHostKey activeKey))
            {
                EnqueueBbcKeyChange(activeKey.InternalKey, false);
                RestoreAdjustedShift(activeKey, (SDL_GetModState() & KMOD_SHIFT) != 0);
                return;
            }

            BbcKeyChord? chord = MapHostKeyToBbcKey(keySym, SDL_GetModState());
            if (chord.HasValue)
                EnqueueBbcKeyChange(chord.Value.InternalKey, false);
        }

        private void EnqueueKeyboardJoystickChange(int keySym, bool pressed)
        {
            JoystickControl? control = keySym switch
            {
                SDLK_LEFT => JoystickControl.Left,
                SDLK_RIGHT => JoystickControl.Right,
                SDLK_UP => JoystickControl.Up,
                SDLK_DOWN => JoystickControl.Down,
                SDLK_SPACE => JoystickControl.Fire,
                _ => null
            };

            if (control.HasValue)
                SetJoystickSource(control.Value, HostJoystickSource.Keyboard, pressed);
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
            if (axis == SDL_CONTROLLER_AXIS_LEFTX)
            {
                EnqueueAnalogJoystickChange(JoystickAxis.X, value);
                SetJoystickSource(JoystickControl.Left, HostJoystickSource.ControllerAxis, value < -JoystickAxisThreshold);
                SetJoystickSource(JoystickControl.Right, HostJoystickSource.ControllerAxis, value > JoystickAxisThreshold);
            }
            else if (axis == SDL_CONTROLLER_AXIS_LEFTY)
            {
                EnqueueAnalogJoystickChange(JoystickAxis.Y, value);
                SetJoystickSource(JoystickControl.Up, HostJoystickSource.ControllerAxis, value < -JoystickAxisThreshold);
                SetJoystickSource(JoystickControl.Down, HostJoystickSource.ControllerAxis, value > JoystickAxisThreshold);
            }
        }

        private void UpdateControllerButton(byte button, bool pressed)
        {
            switch (button)
            {
                case SDL_CONTROLLER_BUTTON_A:
                    SetJoystickSource(JoystickControl.Fire, HostJoystickSource.ControllerButton, pressed);
                    break;

                case SDL_CONTROLLER_BUTTON_DPAD_UP:
                    SetJoystickSource(JoystickControl.Up, HostJoystickSource.ControllerButton, pressed);
                    break;

                case SDL_CONTROLLER_BUTTON_DPAD_DOWN:
                    SetJoystickSource(JoystickControl.Down, HostJoystickSource.ControllerButton, pressed);
                    break;

                case SDL_CONTROLLER_BUTTON_DPAD_LEFT:
                    SetJoystickSource(JoystickControl.Left, HostJoystickSource.ControllerButton, pressed);
                    break;

                case SDL_CONTROLLER_BUTTON_DPAD_RIGHT:
                    SetJoystickSource(JoystickControl.Right, HostJoystickSource.ControllerButton, pressed);
                    break;
            }
        }

        private void UpdateJoystickAxis(byte axis, short value)
        {
            if (axis == 0)
            {
                EnqueueAnalogJoystickChange(JoystickAxis.X, value);
                SetJoystickSource(JoystickControl.Left, HostJoystickSource.ControllerAxis, value < -JoystickAxisThreshold);
                SetJoystickSource(JoystickControl.Right, HostJoystickSource.ControllerAxis, value > JoystickAxisThreshold);
            }
            else if (axis == 1)
            {
                EnqueueAnalogJoystickChange(JoystickAxis.Y, value);
                SetJoystickSource(JoystickControl.Up, HostJoystickSource.ControllerAxis, value < -JoystickAxisThreshold);
                SetJoystickSource(JoystickControl.Down, HostJoystickSource.ControllerAxis, value > JoystickAxisThreshold);
            }
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
            if (button == 0)
                SetJoystickSource(JoystickControl.Fire, HostJoystickSource.ControllerButton, pressed);
        }

        private void UpdateMouseState(int hostX, int hostY, int relativeX, int relativeY, byte buttons)
        {
            float logicalX = hostX;
            float logicalY = hostY;
            if (renderer != IntPtr.Zero)
                RenderWindowToLogical(hostX, hostY, out logicalX, out logicalY);

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

        private bool ApplyShiftAdjustment(ShiftAdjustment adjustment, bool hostShiftDown)
        {
            if (adjustment == ShiftAdjustment.Suppress && hostShiftDown)
            {
                EnqueueBbcKeyChange(BbcShiftKey, false);
                return true;
            }

            if (adjustment == ShiftAdjustment.Force && !hostShiftDown)
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

            if (activeKey.ShiftAdjustment == ShiftAdjustment.Suppress && hostShiftDown)
                EnqueueBbcKeyChange(BbcShiftKey, true);

            if (activeKey.ShiftAdjustment == ShiftAdjustment.Force && !hostShiftDown)
                EnqueueBbcKeyChange(BbcShiftKey, false);
        }

        private static BbcKeyChord? MapHostKeyToBbcKey(int keySym, int modifiers)
        {
            if ((modifiers & KMOD_ALT) != 0)
            {
                BbcKeyChord? optionKey = MapOptionHostKeyToBbcKey(keySym);
                if (optionKey.HasValue)
                    return optionKey;
            }

            if ((modifiers & KMOD_SHIFT) != 0)
            {
                BbcKeyChord? shiftedKey = MapShiftedHostKeyToBbcKey(keySym);
                if (shiftedKey.HasValue)
                    return shiftedKey;
            }

            return keySym switch
            {
                SDLK_LSHIFT or SDLK_RSHIFT => Key(0x00),
                SDLK_LCTRL or SDLK_RCTRL => Key(0x01),
                SDLK_Q => Key(0x10),
                SDLK_3 => Key(0x11),
                SDLK_4 => Key(0x12),
                SDLK_5 => Key(0x13),
                SDLK_F4 => Key(0x14),
                SDLK_8 => Key(0x15),
                SDLK_F7 => Key(0x16),
                SDLK_MINUS => Key(0x17),
                SDLK_EQUALS => Key(0x17, ShiftAdjustment.Force),
                SDLK_CARET => Key(0x18),
                SDLK_LEFT => Key(0x19),
                SDLK_F10 => Key(0x20),
                SDLK_W => Key(0x21),
                SDLK_E => Key(0x22),
                SDLK_T => Key(0x23),
                SDLK_7 => Key(0x24),
                SDLK_APOSTROPHE => Key(0x24, ShiftAdjustment.Force),
                SDLK_I => Key(0x25),
                SDLK_9 => Key(0x26),
                SDLK_0 => Key(0x27),
                SDLK_UNDERSCORE => Key(0x28),
                SDLK_HASH => Key(0x11, ShiftAdjustment.Force),
                SDLK_DOWN => Key(0x29),
                SDLK_1 => Key(0x30),
                SDLK_2 => Key(0x31),
                SDLK_D => Key(0x32),
                SDLK_R => Key(0x33),
                SDLK_6 => Key(0x34),
                SDLK_U => Key(0x35),
                SDLK_O => Key(0x36),
                SDLK_P => Key(0x37),
                SDLK_LEFTBRACKET => Key(0x38),
                SDLK_UP => Key(0x39),
                SDLK_A => Key(0x41),
                SDLK_X => Key(0x42),
                SDLK_F => Key(0x43),
                SDLK_Y => Key(0x44),
                SDLK_J => Key(0x45),
                SDLK_K => Key(0x46),
                SDLK_AT => Key(0x47),
                SDLK_COLON => Key(0x48, ShiftAdjustment.Suppress),
                SDLK_ASTERISK or SDLK_KP_MULTIPLY => Key(0x48, ShiftAdjustment.Force),
                SDLK_RETURN or SDLK_RETURN2 or SDLK_KP_ENTER => Key(0x49),
                SDLK_S => Key(0x51),
                SDLK_C => Key(0x52),
                SDLK_G => Key(0x53),
                SDLK_H => Key(0x54),
                SDLK_N => Key(0x55),
                SDLK_L => Key(0x56),
                SDLK_SEMICOLON => Key(0x57),
                SDLK_PLUS => Key(0x57, ShiftAdjustment.Force),
                SDLK_RIGHTBRACKET => Key(0x58),
                SDLK_BACKSPACE or SDLK_DELETE => Key(0x59),
                SDLK_TAB => Key(0x60),
                SDLK_Z => Key(0x61),
                SDLK_SPACE => Key(0x62),
                SDLK_V => Key(0x63),
                SDLK_B => Key(0x64),
                SDLK_M => Key(0x65),
                SDLK_COMMA => Key(0x66),
                SDLK_PERIOD => Key(0x67),
                SDLK_SLASH => Key(0x68),
                SDLK_INSERT or SDLK_SECTION => Key(0x69),
                SDLK_ESCAPE => Key(0x70),
                SDLK_F1 => Key(0x71),
                SDLK_F2 => Key(0x72),
                SDLK_F3 => Key(0x73),
                SDLK_F5 => Key(0x74),
                SDLK_F6 => Key(0x75),
                SDLK_F8 => Key(0x76),
                SDLK_F9 => Key(0x77),
                SDLK_BACKSLASH => Key(0x78),
                SDLK_RIGHT => Key(0x79),
                _ => null
            };
        }

        private static BbcKeyChord? MapShiftedHostKeyToBbcKey(int keySym)
        {
            return keySym switch
            {
                SDLK_0 => Key(0x27, ShiftAdjustment.Suppress),
                SDLK_2 => Key(0x47, ShiftAdjustment.Suppress),
                SDLK_AT => Key(0x47, ShiftAdjustment.Suppress),
                SDLK_APOSTROPHE or SDLK_QUOTEDBL => Key(0x31, ShiftAdjustment.Force),
                SDLK_HASH => Key(0x11, ShiftAdjustment.Force),
                SDLK_UNDERSCORE => Key(0x17),
                SDLK_8 => Key(0x48),
                SDLK_9 => Key(0x15),
                SDLK_EQUALS or SDLK_PLUS => Key(0x57),
                SDLK_SEMICOLON or SDLK_COLON => Key(0x48, ShiftAdjustment.Suppress),
                _ => null
            };
        }

        private static BbcKeyChord? MapOptionHostKeyToBbcKey(int keySym)
        {
            return keySym switch
            {
                SDLK_3 or SDLK_HASH => Key(0x11, ShiftAdjustment.Force),
                _ => null
            };
        }

        private static BbcKeyChord Key(byte internalKey, ShiftAdjustment shiftAdjustment = ShiftAdjustment.Preserve)
        {
            return new BbcKeyChord(internalKey, shiftAdjustment);
        }

        private readonly record struct ActiveHostKey(byte InternalKey, ShiftAdjustment ShiftAdjustment, bool ShiftAdjusted);

        private readonly record struct BbcKeyChord(byte InternalKey, ShiftAdjustment ShiftAdjustment);

        private readonly record struct MenuDefinition(string Title, MenuItem[] Items);

        private readonly record struct MenuItem(string Text, string Shortcut, MenuCommand Command, bool Enabled = true, bool Separator = false);

        private enum MenuCommand
        {
            MountDrive0,
            MountDrive1,
            CreateBlankSsd,
            EjectDrive0,
            EjectDrive1,
            SaveScreenshot,
            SaveState,
            LoadState,
            Quit,
            Break,
            ShiftBreak,
            ControlBreak,
            TogglePause,
            ToggleTube6502,
            ToggleScanlines,
            ToggleFullScreen,
            PasteClipboard,
            ToggleShiftLock
        }

        private enum ShiftAdjustment
        {
            Preserve,
            Suppress,
            Force
        }

        private static MenuDefinition[] CreateMenus()
        {
            return
            [
                new MenuDefinition("File",
                [
                    new MenuItem("Save screenshot", "Ctrl/Cmd+S", MenuCommand.SaveScreenshot),
                    new MenuItem("Save state...", "", MenuCommand.SaveState),
                    new MenuItem("Load state...", "", MenuCommand.LoadState),
                    MenuSeparator(),
                    new MenuItem("Quit", "", MenuCommand.Quit)
                ]),
                new MenuDefinition("Disc",
                [
                    new MenuItem("Mount drive 0...", "D0", MenuCommand.MountDrive0),
                    new MenuItem("Eject drive 0", "", MenuCommand.EjectDrive0),
                    MenuSeparator(),
                    new MenuItem("Mount drive 1...", "D1", MenuCommand.MountDrive1),
                    new MenuItem("Eject drive 1", "", MenuCommand.EjectDrive1),
                    MenuSeparator(),
                    new MenuItem("Create blank SSD", "", MenuCommand.CreateBlankSsd)
                ]),
                new MenuDefinition("Machine",
                [
                    new MenuItem("BREAK", "F12", MenuCommand.Break),
                    new MenuItem("Shift-BREAK", "Shift+F12", MenuCommand.ShiftBreak),
                    new MenuItem("Ctrl-BREAK", "Ctrl+F12", MenuCommand.ControlBreak),
                    MenuSeparator(),
                    new MenuItem("6502 Co-Processor", "", MenuCommand.ToggleTube6502),
                    MenuSeparator(),
                    new MenuItem("Pause", "Ctrl+P", MenuCommand.TogglePause)
                ]),
                new MenuDefinition("View",
                [
                    new MenuItem("Fullscreen", "", MenuCommand.ToggleFullScreen),
                    new MenuItem("Scanlines", "F11", MenuCommand.ToggleScanlines)
                ]),
                new MenuDefinition("Input",
                [
                    new MenuItem("Paste clipboard", "Ctrl/Cmd+V", MenuCommand.PasteClipboard),
                    new MenuItem("Shift Lock", "L Ctrl+L Shift", MenuCommand.ToggleShiftLock)
                ])
            ];
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
            string? path = SelectNativeFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.Mount, path, drive));
        }

        private void EnqueueBlankSsd()
        {
            int drive = GetFirstEmptyPhysicalDrive();
            if (drive < 0)
                return;

            string? path = SelectNativeSaveFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingDiscActions.Enqueue(new HostDiscAction(HostDiscActionKind.CreateBlankSsd, path, drive));
        }

        private void EnqueueSaveState()
        {
            string? path = SelectNativeSaveStateFile(DefaultSaveStateFileName);
            if (!string.IsNullOrWhiteSpace(path))
                pendingStateActions.Enqueue(new HostStateAction(HostStateActionKind.Save, EnsureSaveStateExtension(path)));
        }

        private void EnqueueLoadState()
        {
            string? path = SelectNativeLoadStateFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingStateActions.Enqueue(new HostStateAction(HostStateActionKind.Load, path));
        }

        private int GetFirstEmptyPhysicalDrive()
        {
            if (!Drive0Mounted)
                return 0;

            return Drive1Mounted ? -1 : 1;
        }

        private static string? SelectNativeFile()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Select a BBC disc or file'; $dialog.Filter = 'BBC files (*.ssd;*.dsd)|*.ssd;*.dsd|All files (*.*)|*.*'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file with prompt \"Select a BBC disc or file\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Select a BBC disc or file");
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
                        "Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Title = 'Load BBC state'; $dialog.Filter = 'BBC save state (*.sav)|*.sav|All files (*.*)|*.*'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.FileName }");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", "POSIX path of (choose file of type {\"sav\"} with prompt \"Load BBC state\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--title=Load BBC state", "--file-filter=BBC save state (*.sav) | *.sav");
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
        private const uint SDL_KEYDOWN = 0x300;
        private const uint SDL_KEYUP = 0x301;
        private const uint SDL_MOUSEMOTION = 0x400;
        private const uint SDL_MOUSEBUTTONDOWN = 0x401;
        private const uint SDL_MOUSEBUTTONUP = 0x402;
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
            [FieldOffset(13)] public byte KeyRepeat;
            [FieldOffset(20)] public int KeySym;
            [FieldOffset(8)] public IntPtr DropFile;
            [FieldOffset(16)] public byte MouseButton;
            [FieldOffset(20)] public int MouseX;
            [FieldOffset(24)] public int MouseY;
            [FieldOffset(28)] public int MouseRelativeX;
            [FieldOffset(32)] public int MouseRelativeY;
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
        private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);

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

    public readonly record struct HostDiscAction(HostDiscActionKind Kind, string Path, int Drive);

    public readonly record struct HostStateAction(HostStateActionKind Kind, string Path);

    public enum HostStateActionKind
    {
        Save,
        Load
    }

    public enum HostDiscActionKind
    {
        Mount,
        CreateBlankSsd,
        Eject
    }

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
