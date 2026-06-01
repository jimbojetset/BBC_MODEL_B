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
using System.Text;

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

        private const uint Black = 0xFF000000;

        private readonly uint[] frameBuffer;
        private readonly Queue<byte> pendingInput = new Queue<byte>();
        private readonly Queue<BreakKeyPress> pendingBreaks = new Queue<BreakKeyPress>();
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

                if (ev.Type == SDL_TEXTINPUT)
                {
                    EnqueueTextInput(ev.Text);
                    continue;
                }

                if (ev.Type == SDL_KEYDOWN && ev.KeyRepeat == 0)
                    EnqueueSpecialKey(ev.KeySym);
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

        private void EnqueueTextInput(byte[] utf8Text)
        {
            int length = Array.IndexOf(utf8Text, (byte)0);
            if (length <= 0)
                return;

            foreach (Rune rune in Encoding.UTF8.GetString(utf8Text, 0, length).EnumerateRunes())
            {
                if (rune.Value >= 32 && rune.Value <= 126)
                    pendingInput.Enqueue((byte)char.ToUpperInvariant((char)rune.Value));
            }
        }

        private void EnqueueSpecialKey(int keySym)
        {
            if (keySym == SDLK_F12)
            {
                int modifiers = SDL_GetModState();
                pendingBreaks.Enqueue(new BreakKeyPress(
                    (modifiers & KMOD_SHIFT) != 0,
                    (modifiers & KMOD_CTRL) != 0));
                return;
            }

            byte? character = keySym switch
            {
                SDLK_RETURN => 13,
                SDLK_KP_ENTER => 13,
                SDLK_BACKSPACE => 127,
                SDLK_DELETE => 127,
                SDLK_ESCAPE => 27,
                SDLK_LEFT => 8,
                SDLK_RIGHT => 9,
                SDLK_DOWN => 10,
                SDLK_UP => 11,
                _ => null
            };

            if (character.HasValue)
                pendingInput.Enqueue(character.Value);
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
        private const uint SDL_TEXTINPUT = 0x303;
        private const int SDLK_BACKSPACE = 8;
        private const int SDLK_RETURN = 13;
        private const int SDLK_ESCAPE = 27;
        private const int SDLK_DELETE = 127;
        private const int SDLK_RIGHT = 1073741903;
        private const int SDLK_LEFT = 1073741904;
        private const int SDLK_DOWN = 1073741905;
        private const int SDLK_UP = 1073741906;
        private const int SDLK_KP_ENTER = 1073741912;
        private const int SDLK_F12 = 1073741893;
        private const int KMOD_SHIFT = 0x0003;
        private const int KMOD_CTRL = 0x00C0;

        [StructLayout(LayoutKind.Explicit, Size = 56)]
        private struct SdlEvent
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(13)] public byte KeyRepeat;
            [FieldOffset(20)] public int KeySym;
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
}
