// ============================================================================
// Project:     BBC
// File:        Display.cs
// Description: SDL2-backed display window for the BBC Model B emulator.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO.Compression;

namespace BBC
{
    /// <summary>
    /// Owns an SDL2 window, renderer, texture, and ARGB framebuffer suitable for BBC video output.
    /// </summary>
    public sealed class Display : IDisposable
    {
        public const int DefaultWidth = 640;
        public const int DefaultHeight = 537;
        private const byte BbcShiftKey = 0x00;
        private const byte BbcCapsLockKey = 0x40;
        private const uint Black = 0xFF000000;
        private const uint ScanlineColour = 0x60000000;
        private const string MonitorImageFileName = "cub-monitor.png";
        private const int MonitorViewportWidth = 650;
        private const int MonitorViewportHeight = 520;
        private const int MonitorViewportX = 105;
        private const int MonitorViewportY = 110;

        private readonly uint[] frameBuffer;
        private readonly Queue<byte> pendingInput = new Queue<byte>();
        private readonly Queue<BreakKeyPress> pendingBreaks = new Queue<BreakKeyPress>();
        private readonly Queue<HostKeyChange> pendingKeyChanges = new Queue<HostKeyChange>();
        private readonly Queue<HostJoystickChange> pendingJoystickChanges = new Queue<HostJoystickChange>();
        private readonly Queue<string> pendingDiscLoads = new Queue<string>();
        private int pendingScreenshotRequests;
        private readonly Dictionary<int, ActiveHostKey> activeHostKeys = new Dictionary<int, ActiveHostKey>();
        private readonly int pitchBytes;

        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private IntPtr scanlineTexture;
        private IntPtr monitorTexture;
        private bool scanlinesEnabled;
        private bool disposed;
        private bool hostCapsLockEnabled;
        private readonly int logicalWidth;
        private readonly int logicalHeight;
        private SdlRect viewportRect;

        static Display()
        {
            NativeLibrary.SetDllImportResolver(typeof(Display).Assembly, ResolveNativeLibrary);
        }

        /// <summary>Gets the display texture width in pixels.</summary>
        public int Width { get; }

        /// <summary>Gets the display texture height in pixels.</summary>
        public int Height { get; }

        /// <summary>Gets a writable ARGB8888 framebuffer for the next frame.</summary>
        public uint[] FrameBuffer => frameBuffer;

        /// <summary>Gets whether an SDL quit event has been received.</summary>
        public bool QuitRequested { get; private set; }

        /// <summary>Gets whether the host keyboard Caps Lock state is currently enabled.</summary>
        public bool HostCapsLockEnabled => hostCapsLockEnabled;

        /// <summary>Initializes a new SDL display window.</summary>
        /// <param name="title">Window title.</param>
        /// <param name="width">Framebuffer width in pixels.</param>
        /// <param name="height">Framebuffer height in pixels.</param>
        /// <param name="scale">Initial integer window scale.</param>
        /// <param name="scanlines">Whether to draw a CRT-style scanline overlay.</param>
        public Display(string title = "BBC Model B", int width = DefaultWidth, int height = DefaultHeight, bool scanlines = false)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            scanlinesEnabled = scanlines;
            pitchBytes = width * sizeof(uint);
            frameBuffer = new uint[width * height];
            Array.Fill(frameBuffer, Black);

            ThrowIfSdlFailed(SDL_InitSubSystem(SDL_INIT_VIDEO), "SDL_InitSubSystem");
            uint[]? monitorPixels = TryLoadMonitorPixels(out int monitorWidth, out int monitorHeight);
            logicalWidth = monitorPixels is null ? width : monitorWidth;
            logicalHeight = monitorPixels is null ? height : monitorHeight;
            viewportRect = monitorPixels is null
                ? new SdlRect(0, 0, width, height)
                : new SdlRect(MonitorViewportX, MonitorViewportY, MonitorViewportWidth, MonitorViewportHeight);

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
            ThrowIfSdlFailed(SDL_RenderSetLogicalSize(renderer, logicalWidth, logicalHeight), "SDL_RenderSetLogicalSize");
            _ = SDL_RenderSetIntegerScale(renderer, SDL_TRUE);

            texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STREAMING, width, height);
            ThrowIfNull(texture, "SDL_CreateTexture");

            if (monitorPixels is not null)
                monitorTexture = CreateStaticTexture(monitorPixels, monitorWidth, monitorHeight);

            scanlineTexture = CreateScanlineTexture(width, height);

            SDL_StartTextInput();
            hostCapsLockEnabled = IsHostCapsLockEnabled();
            Present();
        }

        /// <summary>Pumps pending SDL events and returns false after the user requests to close the window.</summary>
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
            }

            SyncHostCapsLockState();
            return !QuitRequested;
        }

        /// <summary>Copies pending host keyboard input into a caller-provided buffer.</summary>
        /// <param name="destination">The destination buffer.</param>
        /// <returns>The number of bytes copied.</returns>
        public int DrainInput(Span<byte> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingInput.Count > 0)
                destination[count++] = pendingInput.Dequeue();

            return count;
        }

        /// <summary>Copies pending BREAK key requests into a caller-provided buffer.</summary>
        /// <param name="destination">The destination buffer.</param>
        /// <returns>The number of break requests copied.</returns>
        public int DrainBreaks(Span<BreakKeyPress> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingBreaks.Count > 0)
                destination[count++] = pendingBreaks.Dequeue();

            return count;
        }

        /// <summary>Copies pending BBC keyboard matrix changes into a caller-provided buffer.</summary>
        /// <param name="destination">The destination buffer.</param>
        /// <returns>The number of changes copied.</returns>
        public int DrainKeyChanges(Span<HostKeyChange> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingKeyChanges.Count > 0)
                destination[count++] = pendingKeyChanges.Dequeue();

            return count;
        }

        /// <summary>Copies pending joystick changes into a caller-provided buffer.</summary>
        /// <param name="destination">The destination buffer.</param>
        /// <returns>The number of changes copied.</returns>
        public int DrainJoystickChanges(Span<HostJoystickChange> destination)
        {
            int count = 0;

            while (count < destination.Length && pendingJoystickChanges.Count > 0)
                destination[count++] = pendingJoystickChanges.Dequeue();

            return count;
        }

        /// <summary>Copies pending host disc/file mount requests into a caller-provided list.</summary>
        /// <param name="destination">The destination collection.</param>
        public void DrainDiscLoads(ICollection<string> destination)
        {
            while (pendingDiscLoads.Count > 0)
                destination.Add(pendingDiscLoads.Dequeue());
        }

        /// <summary>Returns and clears the number of pending screenshot requests.</summary>
        /// <returns>The number of requested screenshots.</returns>
        public int DrainScreenshotRequests()
        {
            int count = pendingScreenshotRequests;
            pendingScreenshotRequests = 0;
            return count;
        }


        /// <summary>Copies ARGB8888 pixels into the display framebuffer.</summary>
        /// <param name="pixels">A complete width * height frame.</param>
        public void CopyFrame(ReadOnlySpan<uint> pixels)
        {
            if (pixels.Length != frameBuffer.Length)
                throw new ArgumentException("Frame length must match display dimensions.", nameof(pixels));

            pixels.CopyTo(frameBuffer);
        }

        /// <summary>Uploads and displays the current framebuffer.</summary>
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

            if (monitorTexture != IntPtr.Zero)
                ThrowIfSdlFailed(SDL_RenderCopy(renderer, monitorTexture, IntPtr.Zero, IntPtr.Zero), "SDL_RenderCopy");

            SDL_RenderPresent(renderer);
        }

        private IntPtr CreateStaticTexture(uint[] pixels, int width, int height)
        {
            IntPtr staticTexture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STATIC, width, height);
            ThrowIfNull(staticTexture, "SDL_CreateTexture");
            ThrowIfSdlFailed(SDL_SetTextureBlendMode(staticTexture, SDL_BLENDMODE_BLEND), "SDL_SetTextureBlendMode");

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                ThrowIfSdlFailed(SDL_UpdateTexture(staticTexture, IntPtr.Zero, handle.AddrOfPinnedObject(), width * sizeof(uint)), "SDL_UpdateTexture");
            }
            finally
            {
                handle.Free();
            }

            return staticTexture;
        }

        /// <summary>Builds a static overlay texture that darkens every other row for a CRT scanline look.</summary>
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

        /// <summary>Copies and displays a complete ARGB8888 frame.</summary>
        /// <param name="pixels">A complete width * height frame.</param>
        public void Present(ReadOnlySpan<uint> pixels)
        {
            CopyFrame(pixels);
            Present();
        }

        /// <summary>Saves the current framebuffer as a PNG image.</summary>
        /// <param name="path">The destination PNG path.</param>
        public void SavePng(string path)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            WritePng(path, frameBuffer, Width, Height);
        }

        /// <summary>Releases SDL resources owned by this display.</summary>
        public void Dispose()
        {
            if (disposed)
                return;

            if (monitorTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(monitorTexture);
                monitorTexture = IntPtr.Zero;
            }

            if (scanlineTexture != IntPtr.Zero)
            {
                SDL_DestroyTexture(scanlineTexture);
                scanlineTexture = IntPtr.Zero;
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

            SDL_StopTextInput();
            SDL_QuitSubSystem(SDL_INIT_VIDEO);
            disposed = true;
        }

        private void EnqueueKeyDown(int keySym)
        {
            int modifiers = SDL_GetModState();

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

            if (keySym == SDLK_F11)
            {
                scanlinesEnabled = !scanlinesEnabled;
                return;
            }

            if (keySym == SDLK_L && (modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
            {
                EnqueueSelectedFile();
                return;
            }

            EnqueueJoystickChange(keySym, true);

            BbcKeyChord? chord = MapHostKeyToBbcKey(keySym, modifiers);
            if (chord.HasValue)
            {
                bool shiftAdjusted = ApplyShiftAdjustment(chord.Value.ShiftAdjustment, (modifiers & KMOD_SHIFT) != 0);
                activeHostKeys[keySym] = new ActiveHostKey(chord.Value.InternalKey, chord.Value.ShiftAdjustment, shiftAdjusted);
                pendingKeyChanges.Enqueue(new HostKeyChange(chord.Value.InternalKey, true));
            }
        }

        private void EnqueueKeyUp(int keySym)
        {
            if (keySym == SDLK_CAPSLOCK)
            {
                SyncHostCapsLockState();
                return;
            }

            EnqueueJoystickChange(keySym, false);

            if (activeHostKeys.Remove(keySym, out ActiveHostKey activeKey))
            {
                pendingKeyChanges.Enqueue(new HostKeyChange(activeKey.InternalKey, false));
                RestoreAdjustedShift(activeKey, (SDL_GetModState() & KMOD_SHIFT) != 0);
                return;
            }

            BbcKeyChord? chord = MapHostKeyToBbcKey(keySym, SDL_GetModState());
            if (chord.HasValue)
                pendingKeyChanges.Enqueue(new HostKeyChange(chord.Value.InternalKey, false));
        }

        private void EnqueueJoystickChange(int keySym, bool pressed)
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
                pendingJoystickChanges.Enqueue(new HostJoystickChange(control.Value, pressed));
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

        private bool ApplyShiftAdjustment(ShiftAdjustment adjustment, bool hostShiftDown)
        {
            if (adjustment == ShiftAdjustment.Suppress && hostShiftDown)
            {
                pendingKeyChanges.Enqueue(new HostKeyChange(BbcShiftKey, false));
                return true;
            }

            if (adjustment == ShiftAdjustment.Force && !hostShiftDown)
            {
                pendingKeyChanges.Enqueue(new HostKeyChange(BbcShiftKey, true));
                return true;
            }

            return false;
        }

        private void RestoreAdjustedShift(ActiveHostKey activeKey, bool hostShiftDown)
        {
            if (!activeKey.ShiftAdjusted)
                return;

            if (activeKey.ShiftAdjustment == ShiftAdjustment.Suppress && hostShiftDown)
                pendingKeyChanges.Enqueue(new HostKeyChange(BbcShiftKey, true));

            if (activeKey.ShiftAdjustment == ShiftAdjustment.Force && !hostShiftDown)
                pendingKeyChanges.Enqueue(new HostKeyChange(BbcShiftKey, false));
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
                SDLK_LSHIFT or SDLK_RSHIFT => Key(BbcShiftKey),
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

        private enum ShiftAdjustment
        {
            Preserve,
            Suppress,
            Force
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
                    pendingDiscLoads.Enqueue(path);
            }
            finally
            {
                SDL_free(filePointer);
            }
        }

        private void EnqueueSelectedFile()
        {
            string? path = SelectNativeFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingDiscLoads.Enqueue(path);
        }

        private static string? SelectNativeFile()
        {
            try
            {
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

        private static string? RunProcessForSingleLine(string fileName, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(fileName)
            {
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

        private static uint[]? TryLoadMonitorPixels(out int width, out int height)
        {
            string path = Path.Combine(Environment.CurrentDirectory, MonitorImageFileName);
            if (!File.Exists(path))
            {
                width = 0;
                height = 0;
                return null;
            }

            return LoadPngArgb8888(path, out width, out height);
        }

        private static uint[] LoadPngArgb8888(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            byte[] file = File.ReadAllBytes(path);
            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            if (!file.AsSpan(0, 8).SequenceEqual(signature))
                throw new InvalidOperationException($"{path} is not a PNG file.");

            int offset = 8;
            int colourType = -1;
            int bitDepth = -1;
            int interlace = -1;
            using MemoryStream compressed = new MemoryStream();

            while (offset + 12 <= file.Length)
            {
                int length = ReadBigEndian(file.AsSpan(offset, 4));
                offset += 4;
                string type = System.Text.Encoding.ASCII.GetString(file, offset, 4);
                offset += 4;
                ReadOnlySpan<byte> data = file.AsSpan(offset, length);
                offset += length + 4;

                if (type == "IHDR")
                {
                    width = ReadBigEndian(data[..4]);
                    height = ReadBigEndian(data.Slice(4, 4));
                    bitDepth = data[8];
                    colourType = data[9];
                    interlace = data[12];
                }
                else if (type == "IDAT")
                {
                    compressed.Write(data);
                }
                else if (type == "IEND")
                {
                    break;
                }
            }

            if (width <= 0 || height <= 0 || bitDepth != 8 || colourType != 6 || interlace != 0)
                throw new InvalidOperationException($"{path} must be an 8-bit non-interlaced RGBA PNG.");

            compressed.Position = 0;
            byte[] raw = new byte[(width * 4 + 1) * height];
            using (ZLibStream zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int count = zlib.Read(raw, read, raw.Length - read);
                    if (count == 0)
                        break;

                    read += count;
                }

                if (read != raw.Length)
                    throw new InvalidOperationException($"{path} ended before all PNG pixel rows were decoded.");
            }

            byte[] previous = new byte[width * 4];
            byte[] current = new byte[width * 4];
            uint[] pixels = new uint[width * height];
            int rawOffset = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[rawOffset++];
                Array.Copy(raw, rawOffset, current, 0, current.Length);
                rawOffset += current.Length;
                UnfilterPngRow(current, previous, filter, 4);

                int pixelOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int source = x * 4;
                    byte r = current[source];
                    byte g = current[source + 1];
                    byte b = current[source + 2];
                    byte a = current[source + 3];
                    pixels[pixelOffset + x] = (uint)((a << 24) | (r << 16) | (g << 8) | b);
                }

                (previous, current) = (current, previous);
            }

            return pixels;
        }

        private static void UnfilterPngRow(byte[] row, byte[] previous, int filter, int bytesPerPixel)
        {
            for (int i = 0; i < row.Length; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => PaethPredictor(left, up, upLeft),
                    _ => throw new InvalidOperationException($"Unsupported PNG filter {filter}.")
                };

                row[i] = unchecked((byte)(row[i] + predictor));
            }
        }

        private static int PaethPredictor(int left, int up, int upLeft)
        {
            int estimate = left + up - upLeft;
            int leftDistance = Math.Abs(estimate - left);
            int upDistance = Math.Abs(estimate - up);
            int upLeftDistance = Math.Abs(estimate - upLeft);

            if (leftDistance <= upDistance && leftDistance <= upLeftDistance)
                return left;

            return upDistance <= upLeftDistance ? up : upLeft;
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

        private static int ReadBigEndian(ReadOnlySpan<byte> source)
        {
            return (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];
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
        private const uint SDL_WINDOW_SHOWN = 0x00000004;
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
        private const int SDL_TRUE = 1;
        private const uint SDL_QUIT = 0x100;
        private const uint SDL_KEYDOWN = 0x300;
        private const uint SDL_KEYUP = 0x301;
        private const uint SDL_DROPFILE = 0x1000;
        private const int SDLK_SPACE = 32;
        private const int SDLK_ASTERISK = 42;
        private const int SDLK_PLUS = 43;
        private const int SDLK_AT = 64;
        private const int SDLK_CARET = 94;
        private const int SDLK_HASH = 35;
        private const int SDLK_APOSTROPHE = 39;
        private const int SDLK_QUOTEDBL = 34;
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
        private static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyRenderer(IntPtr renderer);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetLogicalSize(IntPtr renderer, int w, int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetIntegerScale(IntPtr renderer, int enable);

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
        private static extern void SDL_RenderPresent(IntPtr renderer);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_PollEvent(out SdlEvent ev);

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

    /// <summary>Describes a host BREAK key request.</summary>
    /// <param name="Shift">Whether Shift was held.</param>
    /// <param name="Control">Whether Control was held.</param>
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

    /// <summary>Describes a BBC keyboard matrix key transition from the host keyboard.</summary>
    /// <param name="InternalKey">The BBC internal key number.</param>
    /// <param name="Pressed">Whether the key is now pressed.</param>
    public readonly record struct HostKeyChange(byte InternalKey, bool Pressed);

    /// <summary>Describes an emulated joystick transition from the host keyboard.</summary>
    /// <param name="Control">The joystick control that changed.</param>
    /// <param name="Pressed">Whether the control is now pressed.</param>
    public readonly record struct HostJoystickChange(JoystickControl Control, bool Pressed);

    /// <summary>Emulated joystick controls.</summary>
    public enum JoystickControl
    {
        Left,
        Right,
        Up,
        Down,
        Fire
    }
}
