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

namespace BBC
{
    /// <summary>
    /// Owns an SDL2 window, renderer, texture, and ARGB framebuffer suitable for BBC video output.
    /// </summary>
    public sealed class Display : IDisposable
    {
        public const int DefaultWidth = 640;
        public const int DefaultHeight = 512;
        public const int DefaultScale = 2;

        private const byte BbcShiftKey = 0x00;
        private const uint Black = 0xFF000000;

        private readonly uint[] frameBuffer;
        private readonly Queue<byte> pendingInput = new Queue<byte>();
        private readonly Queue<BreakKeyPress> pendingBreaks = new Queue<BreakKeyPress>();
        private readonly Queue<HostKeyChange> pendingKeyChanges = new Queue<HostKeyChange>();
        private readonly Queue<string> pendingDiscLoads = new Queue<string>();
        private readonly Dictionary<int, ActiveHostKey> activeHostKeys = new Dictionary<int, ActiveHostKey>();
        private readonly int pitchBytes;

        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private bool disposed;

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

        /// <summary>Initializes a new SDL display window.</summary>
        /// <param name="title">Window title.</param>
        /// <param name="width">Framebuffer width in pixels.</param>
        /// <param name="height">Framebuffer height in pixels.</param>
        /// <param name="scale">Initial integer window scale.</param>
        public Display(string title = "BBC Model B", int width = DefaultWidth, int height = DefaultHeight, int scale = DefaultScale)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));

            Width = width;
            Height = height;
            pitchBytes = width * sizeof(uint);
            frameBuffer = new uint[width * height];
            Array.Fill(frameBuffer, Black);

            ThrowIfSdlFailed(SDL_InitSubSystem(SDL_INIT_VIDEO), "SDL_InitSubSystem");

            window = SDL_CreateWindow(
                title,
                SDL_WINDOWPOS_CENTERED,
                SDL_WINDOWPOS_CENTERED,
                width * scale,
                height * scale,
                SDL_WINDOW_SHOWN | SDL_WINDOW_RESIZABLE | SDL_WINDOW_ALLOW_HIGHDPI);
            ThrowIfNull(window, "SDL_CreateWindow");

            renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
            if (renderer == IntPtr.Zero)
                renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_SOFTWARE | SDL_RENDERER_PRESENTVSYNC);
            ThrowIfNull(renderer, "SDL_CreateRenderer");

            ThrowIfSdlFailed(SDL_RenderSetLogicalSize(renderer, width, height), "SDL_RenderSetLogicalSize");
            _ = SDL_RenderSetIntegerScale(renderer, SDL_TRUE);

            texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STREAMING, width, height);
            ThrowIfNull(texture, "SDL_CreateTexture");

            SDL_StartTextInput();
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

        /// <summary>Copies pending host disc/file mount requests into a caller-provided list.</summary>
        /// <param name="destination">The destination collection.</param>
        public void DrainDiscLoads(ICollection<string> destination)
        {
            while (pendingDiscLoads.Count > 0)
                destination.Add(pendingDiscLoads.Dequeue());
        }

        /// <summary>Fills the framebuffer with an ARGB8888 colour.</summary>
        /// <param name="argb">The colour to write.</param>
        public void Clear(uint argb = Black)
        {
            Array.Fill(frameBuffer, argb);
        }

        /// <summary>Writes one ARGB8888 pixel into the framebuffer.</summary>
        /// <param name="x">Horizontal pixel coordinate.</param>
        /// <param name="y">Vertical pixel coordinate.</param>
        /// <param name="argb">The colour to write.</param>
        public void SetPixel(int x, int y, uint argb)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return;

            frameBuffer[(y * Width) + x] = argb;
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
            ThrowIfSdlFailed(SDL_RenderCopy(renderer, texture, IntPtr.Zero, IntPtr.Zero), "SDL_RenderCopy");
            SDL_RenderPresent(renderer);
        }

        /// <summary>Copies and displays a complete ARGB8888 frame.</summary>
        /// <param name="pixels">A complete width * height frame.</param>
        public void Present(ReadOnlySpan<uint> pixels)
        {
            CopyFrame(pixels);
            Present();
        }

        /// <summary>Releases SDL resources owned by this display.</summary>
        public void Dispose()
        {
            if (disposed)
                return;

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

            if (keySym == SDLK_L && (modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
            {
                EnqueueSelectedFile();
                return;
            }

            BbcKeyChord? chord = MapHostKeyToBbcKey(keySym, modifiers);
            if (chord.HasValue)
            {
                bool shiftAdjusted = ApplyShiftAdjustment(chord.Value.ShiftAdjustment, (modifiers & KMOD_SHIFT) != 0);
                activeHostKeys[keySym] = new ActiveHostKey(chord.Value.InternalKey, chord.Value.ShiftAdjustment, shiftAdjusted);
                pendingKeyChanges.Enqueue(new HostKeyChange(chord.Value.InternalKey, true));
            }

            if (keySym == SDLK_ESCAPE)
                pendingInput.Enqueue(27);
        }

        private void EnqueueKeyUp(int keySym)
        {
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
                SDLK_HASH => Key(0x28, ShiftAdjustment.Suppress),
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
                SDLK_CAPSLOCK => Key(0x40),
                SDLK_A => Key(0x41),
                SDLK_X => Key(0x42),
                SDLK_F => Key(0x43),
                SDLK_Y => Key(0x44),
                SDLK_J => Key(0x45),
                SDLK_K => Key(0x46),
                SDLK_AT => Key(0x47),
                SDLK_COLON => Key(0x48, ShiftAdjustment.Suppress),
                SDLK_ASTERISK or SDLK_KP_MULTIPLY => Key(0x48, ShiftAdjustment.Force),
                SDLK_RETURN or SDLK_KP_ENTER => Key(0x49),
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
                SDLK_HASH => Key(0x28, ShiftAdjustment.Suppress),
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
                SDLK_3 or SDLK_HASH => Key(0x28, ShiftAdjustment.Suppress),
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
                    pendingInput.Enqueue((byte)char.ToUpperInvariant(ch));
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
        private const int SDL_TEXTUREACCESS_STREAMING = 1;
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
        private const int SDLK_KP_MULTIPLY = 1073741909;
        private const int SDLK_KP_ENTER = 1073741912;
        private const int SDLK_LCTRL = 1073742048;
        private const int SDLK_LSHIFT = 1073742049;
        private const int SDLK_RSHIFT = 1073742053;
        private const int SDLK_RCTRL = 1073742052;
        private const int SDLK_F12 = 1073741893;
        private const int KMOD_SHIFT = 0x0003;
        private const int KMOD_CTRL = 0x00C0;
        private const int KMOD_ALT = 0x0300;
        private const int KMOD_GUI = 0x0C00;

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
        private static extern int SDL_RenderSetLogicalSize(IntPtr renderer, int w, int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetIntegerScale(IntPtr renderer, int enable);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyTexture(IntPtr texture);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderClear(IntPtr renderer);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, IntPtr dstrect);

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

    /// <summary>Describes a BBC keyboard matrix key transition from the host keyboard.</summary>
    /// <param name="InternalKey">The BBC internal key number.</param>
    /// <param name="Pressed">Whether the key is now pressed.</param>
    public readonly record struct HostKeyChange(byte InternalKey, bool Pressed);
}
