// ============================================================================
// Project:     BBC
// File:        Display.cs
// Description: SDL2 window, framebuffer presentation, keyboard/joystick input,
//              drag/drop disc loading, and native file picker integration.
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
        public const int DefaultWidth = 768;
        public const int DefaultHeight = 576;
        private const int HorizontalBorderPercent = 5;
        private const byte BbcShiftKey = 0x00;
        private const byte BbcCapsLockKey = 0x40;
        private const uint Black = 0xFF000000;
        private const uint ScanlineColour = 0x40000000;
        private const int DriveLedDiameter = 8;
        private const int DriveLedInset = 2;
        private const int DriveGlyphWidth = 34;
        private const int DriveGlyphHeight = 12;
        private const int DriveGlyphMargin = 8;
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

        private readonly uint[] frameBuffer;
        private readonly Queue<byte> pendingInput = new Queue<byte>();
        private readonly Queue<BreakKeyPress> pendingBreaks = new Queue<BreakKeyPress>();
        private readonly Queue<HostKeyChange> pendingKeyChanges = new Queue<HostKeyChange>();
        private readonly Queue<HostJoystickChange> pendingJoystickChanges = new Queue<HostJoystickChange>();
        private readonly Queue<string> pendingDiscLoads = new Queue<string>();
        private int pendingScreenshotRequests;
        private int pendingTraceToggleRequests;
        private readonly Dictionary<int, ActiveHostKey> activeHostKeys = new Dictionary<int, ActiveHostKey>();
        private readonly int pitchBytes;

        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private IntPtr scanlineTexture;
        private IntPtr emptyDriveGlyphTexture;
        private IntPtr mountedDriveGlyphTexture;
        private bool scanlinesEnabled;
        private bool disposed;
        private bool hostCapsLockEnabled;
        private int logicalWidth;
        private int logicalHeight;
        private SdlRect viewportRect;
        private string notificationTitle = string.Empty;
        private string notificationBody = string.Empty;
        private long notificationVisibleUntilTicks;

        /// <summary>Initializes a new Display instance.</summary>
        static Display()
        {
            NativeLibrary.SetDllImportResolver(typeof(Display).Assembly, ResolveNativeLibrary);
        }

        /// <summary>Gets the display texture width in pixels.</summary>
        public int Width { get; }

        /// <summary>Gets the display texture height in pixels.</summary>
        public int Height { get; }

        /// <summary>Gets the host-side display framebuffer.</summary>
        public uint[] FrameBuffer => frameBuffer;

        /// <summary>Gets whether the host display has requested emulator shutdown.</summary>
        public bool QuitRequested { get; private set; }

        /// <summary>Gets whether host Caps Lock is currently enabled.</summary>
        public bool HostCapsLockEnabled => hostCapsLockEnabled;

        /// <summary>Gets or sets whether the drive activity LED should be lit.</summary>
        public bool DiscActivityLedActive { get; set; }

        /// <summary>Gets or sets whether the drive glyph should show a mounted disc.</summary>
        public bool DiscMounted { get; set; }

        /// <summary>Shows a host-rendered notification over the BBC display.</summary>
        /// <param name="title">The title text.</param>
        /// <param name="body">The body text.</param>
        public void ShowNotification(string title, string body)
        {
            notificationTitle = title.Trim();
            notificationBody = body.Trim();
            notificationVisibleUntilTicks = Stopwatch.GetTimestamp()
                + (NotificationDurationMilliseconds * Stopwatch.Frequency / 1000);
        }

        /// <summary>Initializes a new SDL display window.</summary>
        /// <param name="title">Window title.</param>
        /// <param name="width">Framebuffer width in pixels.</param>
        /// <param name="height">Framebuffer height in pixels.</param>
        /// <param name="scanlines">Whether to draw a CRT-style scanline overlay.</param>
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

            ThrowIfSdlFailed(SDL_InitSubSystem(SDL_INIT_VIDEO), "SDL_InitSubSystem");
            int horizontalBorder = (int)Math.Round(width * HorizontalBorderPercent / 100.0);
            logicalWidth = width + (horizontalBorder * 2);
            logicalHeight = height;
            viewportRect = new SdlRect(horizontalBorder, 0, width, height);

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
            _ = SDL_RenderSetIntegerScale(renderer, SDL_FALSE);

            texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STREAMING, width, height);
            ThrowIfNull(texture, "SDL_CreateTexture");

            scanlineTexture = CreateScanlineTexture(width, height);
            emptyDriveGlyphTexture = CreateDriveGlyphTexture(0xFF404040);
            mountedDriveGlyphTexture = CreateDriveGlyphTexture(0xFF005020);

            SDL_StartTextInput();
            hostCapsLockEnabled = IsHostCapsLockEnabled();
            Present();
        }

        /// <summary>Pumps pending SDL events and returns false after the user requests to close the window.</summary>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Consumes queued screenshot requests events and applies them to emulator state.</summary>
        /// <returns>The number of requested screenshots.</returns>
        public int DrainScreenshotRequests()
        {
            int count = pendingScreenshotRequests;
            pendingScreenshotRequests = 0;
            return count;
        }

        /// <summary>Consumes queued trace toggle requests events and applies them to emulator state.</summary>
        /// <returns>The number of requested trace toggles.</returns>
        public int DrainTraceToggleRequests()
        {
            int count = pendingTraceToggleRequests;
            pendingTraceToggleRequests = 0;
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
            DrawNotificationOverlay();

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

            DrawDriveGlyph();

            SDL_RenderPresent(renderer);
        }

        /// <summary>Draws the small host-drive icon in the display status area.</summary>
        private void DrawDriveGlyph()
        {
            int glyphX = logicalWidth - DriveGlyphMargin - DriveGlyphWidth;
            int glyphY = DriveGlyphMargin;
            SdlRect glyphRect = new SdlRect(glyphX, glyphY, DriveGlyphWidth, DriveGlyphHeight);

            IntPtr glyphTexture = DiscMounted ? mountedDriveGlyphTexture : emptyDriveGlyphTexture;
            if (glyphTexture != IntPtr.Zero)
                _ = SDL_RenderCopy(renderer, glyphTexture, IntPtr.Zero, ref glyphRect);

            if (DiscActivityLedActive)
                DrawDriveLed(glyphX, glyphY);

            _ = SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
        }

        /// <summary>Draws the host-drive activity LED beside the drive icon.</summary>
        /// <param name="glyphX">The glyph x value.</param>
        /// <param name="glyphY">The glyph y value.</param>
        private void DrawDriveLed(int glyphX, int glyphY)
        {
            int radius = DriveLedDiameter / 2;
            int centerX = glyphX + DriveGlyphWidth - DriveLedInset - radius;
            int centerY = glyphY + DriveLedInset + radius;

            _ = SDL_SetRenderDrawColor(renderer, 220, 0, 0, 255);
            for (int y = -radius; y < radius; y++)
            {
                int halfWidth = (int)Math.Sqrt((radius * radius) - (y * y));
                SdlRect row = new SdlRect(centerX - halfWidth, centerY + y, halfWidth * 2, 1);
                _ = SDL_RenderFillRect(renderer, ref row);
            }
        }

        /// <summary>Draws the transient host notification into the presented framebuffer.</summary>
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

        /// <summary>Draws embedded bitmap text into the framebuffer.</summary>
        /// <param name="text">The text to draw.</param>
        /// <param name="x">The left coordinate.</param>
        /// <param name="y">The top coordinate.</param>
        /// <param name="cellWidth">The cell width.</param>
        /// <param name="cellHeight">The cell height.</param>
        /// <param name="colour">The ARGB colour.</param>
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

        /// <summary>Wraps notification text to the requested character width.</summary>
        /// <param name="text">The source text.</param>
        /// <param name="columns">The maximum character count per line.</param>
        /// <returns>The wrapped lines.</returns>
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

        /// <summary>Provides a small readable 5x7 bitmap font for host overlays.</summary>
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

        /// <summary>Builds a static overlay texture that darkens every other row for a CRT scanline look.</summary>
        /// <param name="width">The pixel width.</param>
        /// <param name="height">The height value.</param>
        /// <returns>The native pointer returned by the host API.</returns>
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

        /// <summary>Creates drive glyph texture.</summary>
        /// <param name="colour">The colour value.</param>
        /// <returns>The native pointer returned by the host API.</returns>
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

        /// <summary>Draws a rectangular outline directly into the pixel framebuffer.</summary>
        /// <param name="pixels">The pixels value.</param>
        /// <param name="textureWidth">The texture width.</param>
        /// <param name="textureHeight">The texture height value.</param>
        /// <param name="x">The low result byte value.</param>
        /// <param name="y">The high result byte value.</param>
        /// <param name="width">The pixel width.</param>
        /// <param name="height">The height value.</param>
        /// <param name="colour">The colour value.</param>
        private static void DrawPixelRectOutline(uint[] pixels, int textureWidth, int textureHeight, int x, int y, int width, int height, uint colour)
        {
            FillPixelRect(pixels, textureWidth, textureHeight, x, y, width, 1, colour);
            FillPixelRect(pixels, textureWidth, textureHeight, x, y + height - 1, width, 1, colour);
            FillPixelRect(pixels, textureWidth, textureHeight, x, y, 1, height, colour);
            FillPixelRect(pixels, textureWidth, textureHeight, x + width - 1, y, 1, height, colour);
        }

        /// <summary>Fills pixel rect.</summary>
        /// <param name="pixels">The pixels value.</param>
        /// <param name="textureWidth">The texture width.</param>
        /// <param name="textureHeight">The texture height value.</param>
        /// <param name="x">The low result byte value.</param>
        /// <param name="y">The high result byte value.</param>
        /// <param name="width">The pixel width.</param>
        /// <param name="height">The height value.</param>
        /// <param name="colour">The colour value.</param>
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
            DrawNotificationOverlay();
            WritePng(path, frameBuffer, Width, Height);
        }

        /// <summary>Releases SDL resources owned by this display.</summary>
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

            SDL_StopTextInput();
            SDL_QuitSubSystem(SDL_INIT_VIDEO);
            disposed = true;
        }

        /// <summary>Converts an SDL key-down event into BBC text, matrix, or host command input.</summary>
        /// <param name="keySym">The key sym value.</param>
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

            if (keySym == SDLK_T && (modifiers & KMOD_CTRL) != 0)
            {
                pendingTraceToggleRequests++;
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

        /// <summary>Converts an SDL key-up event into BBC matrix and joystick release events.</summary>
        /// <param name="keySym">The key sym value.</param>
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

        /// <summary>Queues an emulated joystick direction or fire-button transition.</summary>
        /// <param name="keySym">The key sym value.</param>
        /// <param name="pressed">The key press state.</param>
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

        /// <summary>Queues a BBC Caps Lock transition when the host Caps Lock state changes.</summary>
        private void SyncHostCapsLockState()
        {
            bool enabled = IsHostCapsLockEnabled();
            if (enabled == hostCapsLockEnabled)
                return;

            hostCapsLockEnabled = enabled;
            pendingKeyChanges.Enqueue(new HostKeyChange(BbcCapsLockKey, enabled));
        }

        /// <summary>Checks whether host caps lock enabled is true for the current emulator state.</summary>
        /// <returns>True when host caps lock enabled is true; otherwise, false.</returns>
        private static bool IsHostCapsLockEnabled()
        {
            return (SDL_GetModState() & KMOD_CAPS) != 0;
        }

        /// <summary>Applies synthetic Shift key transitions needed for host-to-BBC key mapping.</summary>
        /// <param name="adjustment">The adjustment value.</param>
        /// <param name="hostShiftDown">The host shift down.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Releases a synthetic shift adjustment after the host key transition is complete.</summary>
        /// <param name="activeKey">The active key value.</param>
        /// <param name="hostShiftDown">The host shift down.</param>
        private void RestoreAdjustedShift(ActiveHostKey activeKey, bool hostShiftDown)
        {
            if (!activeKey.ShiftAdjusted)
                return;

            if (activeKey.ShiftAdjustment == ShiftAdjustment.Suppress && hostShiftDown)
                pendingKeyChanges.Enqueue(new HostKeyChange(BbcShiftKey, true));

            if (activeKey.ShiftAdjustment == ShiftAdjustment.Force && !hostShiftDown)
                pendingKeyChanges.Enqueue(new HostKeyChange(BbcShiftKey, false));
        }

        /// <summary>Maps an SDL scancode and modifier state to a BBC keyboard matrix key.</summary>
        /// <param name="keySym">The key sym value.</param>
        /// <param name="modifiers">The modifiers value.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Maps shifted host punctuation keys to BBC keyboard positions.</summary>
        /// <param name="keySym">The key sym value.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Maps Option-modified host punctuation keys to BBC keyboard positions.</summary>
        /// <param name="keySym">The key sym value.</param>
        /// <returns>The resulting value.</returns>
        private static BbcKeyChord? MapOptionHostKeyToBbcKey(int keySym)
        {
            return keySym switch
            {
                SDLK_3 or SDLK_HASH => Key(0x11, ShiftAdjustment.Force),
                _ => null
            };
        }

        /// <summary>Creates a BBC keyboard matrix key mapping entry.</summary>
        /// <param name="internalKey">The BBC keyboard matrix key.</param>
        /// <param name="shiftAdjustment">The shift adjustment value.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Queues clipboard text as host keyboard input for the emulator.</summary>
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

        /// <summary>Queues a host file path dropped onto the SDL window.</summary>
        /// <param name="filePointer">The file pointer value.</param>
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

        /// <summary>Opens the native file picker and queues the chosen file for mounting.</summary>
        private void EnqueueSelectedFile()
        {
            string? path = SelectNativeFile();
            if (!string.IsNullOrWhiteSpace(path))
                pendingDiscLoads.Enqueue(path);
        }

        /// <summary>Shows the host file picker and returns the selected path.</summary>
        /// <returns>The resulting string.</returns>
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

        /// <summary>Runs a host utility process and returns its first line of standard output.</summary>
        /// <param name="fileName">The file name value.</param>
        /// <param name="arguments">The arguments.</param>
        /// <returns>The resulting string.</returns>
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

        /// <summary>Converts host text into BBC keypress text and queues it for the emulator.</summary>
        /// <param name="text">The text.</param>
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

        /// <summary>Throws when an SDL-created native pointer is null.</summary>
        /// <param name="value">The input value.</param>
        /// <param name="operation">The operation value.</param>
        private static void ThrowIfNull(IntPtr value, string operation)
        {
            if (value == IntPtr.Zero)
                throw new InvalidOperationException($"{operation} failed: {GetSdlError()}");
        }

        /// <summary>Throws an exception when an SDL call reports a failure.</summary>
        /// <param name="result">The result value.</param>
        /// <param name="operation">The operation value.</param>
        private static void ThrowIfSdlFailed(int result, string operation)
        {
            if (result < 0)
                throw new InvalidOperationException($"{operation} failed: {GetSdlError()}");
        }

        /// <summary>Encodes the display framebuffer as a PNG file on the host filesystem.</summary>
        /// <param name="path">The host file path.</param>
        /// <param name="argbPixels">The argb pixels value.</param>
        /// <param name="width">The pixel width.</param>
        /// <param name="height">The height value.</param>
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

        /// <summary>Writes one PNG chunk, including its length, type, payload, and CRC.</summary>
        /// <param name="stream">The stream value.</param>
        /// <param name="type">The type value.</param>
        /// <param name="data">The data byte or buffer.</param>
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

        /// <summary>Computes the PNG CRC-32 over a chunk type and payload.</summary>
        /// <param name="type">The type value.</param>
        /// <param name="data">The data byte or buffer.</param>
        /// <returns>The resulting value.</returns>
        private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            crc = UpdateCrc32(crc, type);
            crc = UpdateCrc32(crc, data);
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>Refreshes crc32 after related emulator state changes.</summary>
        /// <param name="crc">The crc value.</param>
        /// <param name="data">The data byte or buffer.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Writes a 32-bit integer to a stream in PNG big-endian byte order.</summary>
        /// <param name="destination">The destination value.</param>
        /// <param name="offset">The buffer or image offset.</param>
        /// <param name="value">The input value.</param>
        private static void WriteBigEndian(Span<byte> destination, int offset, int value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        /// <summary>Computes SDL error from the current emulated hardware state.</summary>
        /// <returns>The resulting string.</returns>
        private static string GetSdlError()
        {
            IntPtr error = SDL_GetError();
            return error == IntPtr.Zero ? "unknown SDL error" : Marshal.PtrToStringAnsi(error) ?? "unknown SDL error";
        }

        /// <summary>Resolves native library.</summary>
        /// <param name="libraryName">The library name value.</param>
        /// <param name="assembly">The assembly value.</param>
        /// <param name="searchPath">The search path.</param>
        /// <returns>The native pointer returned by the host API.</returns>
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
        private const int SDL_FALSE = 0;
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

        /// <summary>Imports SDL_InitSubSystem for starting the SDL video subsystem.</summary>
        /// <param name="flags">The flag mask.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);

        /// <summary>Imports SDL_QuitSubSystem for shutting down the SDL video subsystem.</summary>
        /// <param name="flags">The flag mask.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        /// <summary>Imports SDL_CreateWindow for opening the emulator display window.</summary>
        /// <param name="title">The title value.</param>
        /// <param name="x">The low result byte value.</param>
        /// <param name="y">The high result byte value.</param>
        /// <param name="w">The w value.</param>
        /// <param name="h">The h value.</param>
        /// <param name="flags">The flag mask.</param>
        /// <returns>The native pointer returned by the host API.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, uint flags);

        /// <summary>Imports SDL_DestroyWindow for closing the emulator display window.</summary>
        /// <param name="window">The screen memory window value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyWindow(IntPtr window);

        /// <summary>Imports SDL_CreateRenderer for creating the display renderer.</summary>
        /// <param name="window">The screen memory window value.</param>
        /// <param name="index">The index register value.</param>
        /// <param name="flags">The flag mask.</param>
        /// <returns>The native pointer returned by the host API.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);

        /// <summary>Imports SDL_DestroyRenderer for releasing the SDL renderer.</summary>
        /// <param name="renderer">The renderer value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyRenderer(IntPtr renderer);

        /// <summary>Imports SDL_SetRenderDrawColor for renderer clear and overlay colours.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="r">The r value.</param>
        /// <param name="g">The g value.</param>
        /// <param name="b">The b value.</param>
        /// <param name="a">The a value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);

        /// <summary>Imports SDL_RenderSetLogicalSize for BBC display scaling.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="w">The w value.</param>
        /// <param name="h">The h value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetLogicalSize(IntPtr renderer, int w, int h);

        /// <summary>Imports SDL_RenderSetIntegerScale for pixel-perfect integer scaling.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="enable">The enable value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderSetIntegerScale(IntPtr renderer, int enable);

        /// <summary>Imports SDL_CreateTexture for allocating the framebuffer texture.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="format">The format value.</param>
        /// <param name="access">The access value.</param>
        /// <param name="w">The w value.</param>
        /// <param name="h">The h value.</param>
        /// <returns>The native pointer returned by the host API.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);

        /// <summary>Imports SDL_DestroyTexture for releasing the framebuffer texture.</summary>
        /// <param name="texture">The texture value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_DestroyTexture(IntPtr texture);

        /// <summary>Imports SDL_SetTextureBlendMode for texture blending configuration.</summary>
        /// <param name="texture">The texture value.</param>
        /// <param name="blendMode">The blend mode value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetTextureBlendMode(IntPtr texture, int blendMode);

        /// <summary>Imports SDL_UpdateTexture for uploading framebuffer pixels.</summary>
        /// <param name="texture">The texture value.</param>
        /// <param name="rect">The rect value.</param>
        /// <param name="pixels">The pixels value.</param>
        /// <param name="pitch">The pitch value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);

        /// <summary>Imports SDL_RenderClear for clearing the host renderer.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderClear(IntPtr renderer);

        /// <summary>Imports SDL_RenderCopy for copying the framebuffer texture to the renderer.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="texture">The texture value.</param>
        /// <param name="srcrect">The srcrect value.</param>
        /// <param name="dstrect">The dstrect value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, IntPtr dstrect);

        /// <summary>Imports SDL_RenderCopy for copying the framebuffer texture to the renderer.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="texture">The texture value.</param>
        /// <param name="srcrect">The srcrect value.</param>
        /// <param name="dstrect">The dstrect value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_RenderCopy")]
        private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, ref SdlRect dstrect);

        /// <summary>Imports SDL_RenderFillRect for drawing host overlay rectangles.</summary>
        /// <param name="renderer">The renderer value.</param>
        /// <param name="rect">The rect value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_RenderFillRect(IntPtr renderer, ref SdlRect rect);

        /// <summary>Imports SDL_RenderPresent for presenting the completed host frame.</summary>
        /// <param name="renderer">The renderer value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_RenderPresent(IntPtr renderer);

        /// <summary>Imports SDL_PollEvent for reading host input and window events.</summary>
        /// <param name="ev">The ev value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_PollEvent(out SdlEvent ev);

        /// <summary>Imports SDL_GetModState for host modifier state queries.</summary>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetModState();

        /// <summary>Imports SDL_GetClipboardText for paste support.</summary>
        /// <returns>The native pointer returned by the host API.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetClipboardText();

        /// <summary>Imports SDL_free for releasing SDL-owned clipboard memory.</summary>
        /// <param name="memblock">The memblock value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_free(IntPtr memblock);

        /// <summary>Imports SDL_StartTextInput for enabling host text input.</summary>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_StartTextInput();

        /// <summary>Imports SDL_StopTextInput for disabling host text input.</summary>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_StopTextInput();

        /// <summary>Imports SDL_GetError for retrieving native SDL failure details.</summary>
        /// <returns>The native pointer returned by the host API.</returns>
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

        /// <summary>Initializes a new SdlRect instance.</summary>
        /// <param name="x">The low result byte value.</param>
        /// <param name="y">The high result byte value.</param>
        /// <param name="w">The w value.</param>
        /// <param name="h">The h value.</param>
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
