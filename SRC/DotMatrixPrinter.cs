// ============================================================================
// Project:     BBC
// File:        DotMatrixPrinter.cs
// Description: Host-side dot-matrix printer attached to the BBC printer port.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace BBC
{
    public sealed class DotMatrixPrinter : IDisposable
    {
        private const int PrinterDpi = 240;
        private const int PageWidthDots = 1984;
        private const int PageHeightDots = 2806;
        private const int PaperViewWidth = 595;
        private const int PaperViewHeight = 842;
        private const int ScrollBarWidth = 18;
        private const int PageGap = 6;
        private const int PreviewRefreshIntervalMs = 16;
        private const int WindowWidth = PaperViewWidth + ScrollBarWidth;
        private const int PaperAreaHeight = PaperViewHeight;
        private const int WindowHeight = PaperAreaHeight;
        private const int DefaultLeftMarginDots = 32;
        private const int TopMarginDots = 120;
        private const int DefaultRightMarginDots = PageWidthDots - 32;
        private const int BottomMarginDots = PageHeightDots - 120;
        private const int DefaultLineFeedDots = PrinterDpi / 6;
        private const int PrinterFontSizeDots = 31;
        private const int PrintScreenBitImageMode = 1;
        private const int PrintScreenHorizontalDensity = 120;
        private const int PrintScreenDitherSize = 8;
        private const int DraftCharactersPerSecond = 160;
        private const int GraphicsCharactersPerSecond = 80;
        private const double DraftPrintHeadDotsPerSecond = PrinterDpi / 10.0 * DraftCharactersPerSecond;
        private const double GraphicsPrintHeadDotsPerSecond = PrinterDpi / 10.0 * GraphicsCharactersPerSecond;
        private const double MaxPrintHeadBudgetDots = DraftPrintHeadDotsPerSecond;
        private const double PinStrikeDotCost = 0.5;
        private const uint PaperColour = 0xFFF8F8F0;
        private const int SDL_WINDOW_SHOWN = 0x00000004;
        private const uint SDL_RENDERER_ACCELERATED = 0x00000002;
        private const uint SDL_RENDERER_SOFTWARE = 0x00000001;
        private const uint SDL_PIXELFORMAT_ARGB8888 = 0x16362004;
        private const int SDL_TEXTUREACCESS_STREAMING = 1;
        private const uint SDL_WINDOWEVENT = 0x200;
        private const byte SDL_WINDOWEVENT_CLOSE = 14;
        private const uint SDL_KEYDOWN = 0x300;
        private const uint SDL_KEYUP = 0x301;
        private const uint SDL_TEXTINPUT = 0x303;
        private const uint SDL_MOUSEMOTION = 0x400;
        private const uint SDL_MOUSEBUTTONDOWN = 0x401;
        private const uint SDL_MOUSEBUTTONUP = 0x402;
        private const uint SDL_MOUSEWHEEL = 0x403;
        private const byte SDL_BUTTON_LEFT = 1;

        private static readonly byte[] Bayer8x8 =
        [
            0, 48, 12, 60, 3, 51, 15, 63,
            32, 16, 44, 28, 35, 19, 47, 31,
            8, 56, 4, 52, 11, 59, 7, 55,
            40, 24, 36, 20, 43, 27, 39, 23,
            2, 50, 14, 62, 1, 49, 13, 61,
            34, 18, 46, 30, 33, 17, 45, 29,
            10, 58, 6, 54, 9, 57, 5, 53,
            42, 26, 38, 22, 41, 25, 37, 21
        ];

        private readonly object inputFifoLock = new object();
        private readonly List<Page> pages = new List<Page>();
        private readonly Queue<byte> inputFifo = new Queue<byte>();
        private readonly List<byte> escParameters = new List<byte>();
        private readonly Dictionary<char, RenderedGlyph> renderedGlyphs = new Dictionary<char, RenderedGlyph>();
        private SKTypeface? printerTypeface;
        private IntPtr window;
        private IntPtr renderer;
        private bool disposed;
        private bool enabled;
        private int x;
        private int y;
        private int cpi = 10;
        private int lineFeedDots = DefaultLineFeedDots;
        private int leftMarginDots = DefaultLeftMarginDots;
        private int rightMarginDots = DefaultRightMarginDots;
        private int escCommand = -1;
        private int escParameterCount;
        private int bitImageRemaining;
        private int bitImageBytesPerColumn;
        private int bitImageColumnByte;
        private readonly byte[] bitImagePreviousColumnBytes = new byte[3];
        private double bitImageAdvanceDots;
        private double bitImageX;
        private bool bitImageSuppressAdjacentDots;
        private bool bitImageDiscarding;
        private bool bitImageRowReturnPending;
        private int scrollOffset;
        private bool pageInverted;
        private bool soundEnabled = true;
        private bool fastGraphicsEnabled;
        private bool condensedPrint;
        private bool expandedPrint;
        private bool expandedPrintOneLine;
        private bool emphasizedPrint;
        private bool doubleStrikePrint;
        private bool underlinePrint;
        private long printHeadLastTicks;
        private double printHeadBudgetDots;
        private List<int> horizontalTabs = DefaultHorizontalTabs();

        public bool Enabled => enabled;

        public bool Busy
        {
            get
            {
                lock (inputFifoLock)
                    return inputFifo.Count > 0 || bitImageRemaining > 0;
            }
        }

        public uint WindowId { get; private set; }

        public event Action<int, double>? PrintHeadAdvanced;
        public event Action? PrintingCancelled;
        public event Action? SoundOutputCancelled;

        public bool PageInverted => pageInverted;

        public bool SoundEnabled => soundEnabled;

        public bool FastGraphicsEnabled => fastGraphicsEnabled;

        public byte[] CreatePrintScreenBytes(ReadOnlySpan<uint> argbPixels, int width, int height)
        {
            if (width <= 0 || height <= 0 || argbPixels.Length != width * height)
                throw new ArgumentException("Frame size does not match pixel data.", nameof(argbPixels));

            int printableWidthDots = DefaultRightMarginDots - DefaultLeftMarginDots;
            int targetColumns = Math.Max(1, printableWidthDots * PrintScreenHorizontalDensity / PrinterDpi);
            int targetHeight = Math.Max(8, (int)Math.Round((double)height * printableWidthDots / width));
            targetHeight = ((targetHeight + 7) / 8) * 8;

            List<byte> bytes = new List<byte>((targetHeight / 8) * (targetColumns + 8) + 8);
            if (y + targetHeight > BottomMarginDots)
                bytes.Add(0x0C);

            bytes.Add(0x0D);

            byte[] rowBytes = new byte[targetColumns];
            for (int row = 0; row < targetHeight; row += 8)
            {
                int lastInkColumn = -1;
                for (int column = 0; column < targetColumns; column++)
                {
                    byte columnDots = BuildPrintScreenColumn(
                        argbPixels,
                        width,
                        height,
                        column,
                        row,
                        targetColumns,
                        targetHeight,
                        pageInverted);
                    rowBytes[column] = columnDots;
                    if (columnDots != 0)
                        lastInkColumn = column;
                }

                if (lastInkColumn < 0)
                {
                    bytes.Add(0x1B);
                    bytes.Add((byte)'J');
                    bytes.Add(8);
                    continue;
                }

                int columnsToPrint = lastInkColumn + 1;
                bytes.Add(0x1B);
                bytes.Add((byte)'*');
                bytes.Add(PrintScreenBitImageMode);
                bytes.Add((byte)(columnsToPrint & 0xFF));
                bytes.Add((byte)(columnsToPrint >> 8));

                for (int column = 0; column < columnsToPrint; column++)
                    bytes.Add(rowBytes[column]);

                bytes.Add(0x0D);
                bytes.Add(0x1B);
                bytes.Add((byte)'J');
                bytes.Add(8);
            }

            bytes.Add(0x0A);
            return bytes.ToArray();
        }

        public byte[] CreatePrintScreenBytes(string path)
        {
            using SKBitmap bitmap = SKBitmap.Decode(path)
                ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a readable PNG image.");

            uint[] pixels = new uint[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * bitmap.Width;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor colour = bitmap.GetPixel(x, y);
                    pixels[row + x] =
                        ((uint)colour.Alpha << 24)
                        | ((uint)colour.Red << 16)
                        | ((uint)colour.Green << 8)
                        | colour.Blue;
                }
            }

            return CreatePrintScreenBytes(pixels, bitmap.Width, bitmap.Height);
        }

        public void SetEnabled(bool enabled)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (this.enabled == enabled)
                return;

            this.enabled = enabled;
            if (enabled)
            {
                if (window != IntPtr.Zero)
                    SDL_ShowWindow(window);
            }
            else
            {
                ResetInputFifo();
                if (window != IntPtr.Zero)
                    SDL_HideWindow(window);
            }
        }

        public void TogglePageInversion()
        {
            pageInverted = !pageInverted;
        }

        public void ToggleSound()
        {
            soundEnabled = !soundEnabled;
            if (!soundEnabled)
                SoundOutputCancelled?.Invoke();
        }

        public void ToggleFastGraphics()
        {
            fastGraphicsEnabled = !fastGraphicsEnabled;
            if (fastGraphicsEnabled)
                SoundOutputCancelled?.Invoke();
        }

        public void CancelPrinting()
        {
            ResetInputFifo();
            escCommand = -1;
            escParameterCount = 0;
            escParameters.Clear();
            bitImageRemaining = 0;
            bitImageBytesPerColumn = 0;
            bitImageColumnByte = 0;
            bitImageAdvanceDots = 0;
            bitImageX = x;
            bitImageSuppressAdjacentDots = false;
            bitImageDiscarding = false;
            bitImageRowReturnPending = false;
            Array.Clear(bitImagePreviousColumnBytes);
            PrintingCancelled?.Invoke();
        }

        public void SaveDocumentPng()
        {
            EnsureFirstPage();

            string? path = SelectNativePrinterPngFile(CreatePngFileName());
            if (path != null)
                SavePages(path);
        }

        public void NewPaper()
        {
            foreach (Page page in pages)
            {
                if (page.Texture != IntPtr.Zero)
                    SDL_DestroyTexture(page.Texture);
            }

            pages.Clear();
            ResetPrintState();
            ResetInputFifo();
            x = leftMarginDots;
            y = TopMarginDots;
            scrollOffset = 0;
            EnsureFirstPage();
        }

        public void StartNewPage()
        {
            EnsureFirstPage();
            NewPage();
        }

        public void Write(byte value)
        {
            if (!enabled)
                return;

            lock (inputFifoLock)
            {
                inputFifo.Enqueue(value);
                if (printHeadLastTicks == 0)
                    printHeadLastTicks = Stopwatch.GetTimestamp();
            }
        }

        private void ProcessQueuedByte(byte value)
        {
            EnsureFirstPage();

            if (bitImageRemaining > 0)
            {
                PrintBitImageByte(value);
                return;
            }

            if (escCommand >= 0)
            {
                if (escParameterCount == 0)
                {
                    StartEscCommand(value);
                    return;
                }

                escParameters.Add(value);
                if (escParameterCount < 0)
                {
                    if (value == 0)
                        FinishEscCommand();
                }
                else if (escParameters.Count == escParameterCount)
                {
                    FinishEscCommand();
                }

                return;
            }

            if (value == 0x1B)
            {
                escCommand = 0;
                escParameterCount = 0;
                escParameters.Clear();
                return;
            }

            PrintCharacter(value);
        }

        public bool HandleEvent(uint type, uint windowId, byte windowEvent, byte mouseButton, int mouseX, int mouseY, int mouseWheelY)
        {
            if (WindowId == 0 || windowId != WindowId)
                return false;

            if (type == SDL_WINDOWEVENT && windowEvent == SDL_WINDOWEVENT_CLOSE)
            {
                SetEnabled(false);
                return true;
            }

            if (type == SDL_MOUSEMOTION)
                return true;

            if (type == SDL_MOUSEBUTTONDOWN && mouseButton == SDL_BUTTON_LEFT)
                return true;

            if (type == SDL_MOUSEBUTTONUP || type == SDL_KEYDOWN || type == SDL_KEYUP || type == SDL_TEXTINPUT || type == SDL_WINDOWEVENT)
                return true;

            if (type == SDL_MOUSEWHEEL)
            {
                Scroll(mouseWheelY);
                return true;
            }

            return false;
        }

        public void Render()
        {
            if (!enabled || window == IntPtr.Zero || renderer == IntPtr.Zero)
            {
                if (enabled)
                    EnsureWindow();
            }

            if (!enabled || window == IntPtr.Zero || renderer == IntPtr.Zero)
                return;

            EnsureFirstPage();
            DrainInputFifo();
            UpdateScrollBounds();

            SDL_SetRenderDrawColor(renderer, 188, 190, 190, 255);
            SDL_RenderClear(renderer);

            for (int i = 0; i < pages.Count; i++)
            {
                int top = (i * (PaperViewHeight + PageGap)) - scrollOffset;
                if (top > WindowHeight || top + PaperViewHeight < 0)
                    continue;

                Page page = pages[i];
                UpdatePageTexture(page);
                SdlRect destination = new SdlRect(0, top, PaperViewWidth, PaperViewHeight);
                SDL_RenderCopy(renderer, page.Texture, IntPtr.Zero, ref destination);
            }

            DrawScrollBar();
            SDL_RenderPresent(renderer);
        }

        private void DrainInputFifo()
        {
            lock (inputFifoLock)
            {
                if (inputFifo.Count == 0)
                {
                    printHeadLastTicks = 0;
                    printHeadBudgetDots = 0;
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                if (printHeadLastTicks == 0)
                    printHeadLastTicks = now;

                double elapsedSeconds = Math.Max(0, (now - printHeadLastTicks) / (double)Stopwatch.Frequency);
                printHeadLastTicks = now;
                printHeadBudgetDots = Math.Min(
                    printHeadBudgetDots + elapsedSeconds * DraftPrintHeadDotsPerSecond,
                    MaxPrintHeadBudgetDots);

                while (inputFifo.Count > 0)
                {
                    byte value = inputFifo.Peek();
                    double cost = GetPrintHeadCost(value);
                    if (cost > printHeadBudgetDots)
                        break;

                    ProcessQueuedByte(inputFifo.Dequeue());
                    printHeadBudgetDots -= cost;
                }
            }
        }

        private double GetPrintHeadCost(byte value)
        {
            if (bitImageRemaining > 0)
                return GetBitImageByteCost(value);

            if (escCommand >= 0)
                return 1.0;

            return value switch
            {
                0x08 => CharacterAdvanceDots(),
                0x09 => Math.Max(1, GetNextTabX() - x),
                0x0D => GetCarriageReturnCost(),
                >= 32 => CharacterAdvanceDots() + (value == (byte)' ' ? 0 : PinStrikeDotCost),
                _ => 1.0
            };
        }

        private double GetCarriageReturnCost()
        {
            double cost = Math.Max(1, x - leftMarginDots);
            return fastGraphicsEnabled && bitImageRowReturnPending ? cost / 10 : cost;
        }

        private double GetBitImageByteCost(byte value)
        {
            return GetBitImageHeadCost(value)
                * DraftPrintHeadDotsPerSecond
                / EffectiveGraphicsPrintHeadDotsPerSecond();
        }

        private double EffectiveGraphicsPrintHeadDotsPerSecond()
        {
            return fastGraphicsEnabled
                ? GraphicsPrintHeadDotsPerSecond * 10
                : GraphicsPrintHeadDotsPerSecond;
        }

        private double GetBitImageHeadCost(byte value)
        {
            double pinCost = CountBits(value) * PinStrikeDotCost;
            if (bitImageBytesPerColumn <= 1 || bitImageColumnByte + 1 >= bitImageBytesPerColumn)
                return bitImageAdvanceDots + pinCost;

            return Math.Max(1.0, pinCost);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            foreach (Page page in pages)
            {
                if (page.Texture != IntPtr.Zero)
                    SDL_DestroyTexture(page.Texture);
            }

            printerTypeface?.Dispose();

            if (renderer != IntPtr.Zero)
                SDL_DestroyRenderer(renderer);

            if (window != IntPtr.Zero)
                SDL_DestroyWindow(window);

            disposed = true;
        }

        private void PrintCharacter(byte value)
        {
            switch (value)
            {
                case 0x08:
                    x = Math.Max(leftMarginDots, x - CharacterAdvanceDots());
                    return;
                case 0x09:
                    HorizontalTab();
                    return;
                case 0x0E:
                    SelectExpandedPrintOneLine();
                    return;
                case 0x0A:
                    LineFeed();
                    expandedPrintOneLine = false;
                    renderedGlyphs.Clear();
                    return;
                case 0x0F:
                    SelectCondensedPrint();
                    return;
                case 0x0C:
                    NewPage();
                    return;
                case 0x0D:
                    x = leftMarginDots;
                    bitImageRowReturnPending = false;
                    expandedPrintOneLine = false;
                    renderedGlyphs.Clear();
                    return;
                case 0x12:
                    CancelCondensedPrint();
                    return;
                case 0x14:
                    CancelExpandedPrint();
                    return;
            }

            if (value < 32)
                return;

            int advance = CharacterAdvanceDots();
            if (x + advance > rightMarginDots)
            {
                x = leftMarginDots;
                LineFeed();
            }

            DrawGlyph((char)value, x, y);
            x += advance;
        }

        private void LineFeed()
        {
            y += lineFeedDots;
            if (y + lineFeedDots > BottomMarginDots)
                NewPage();
        }

        private void NewPage()
        {
            pages.Add(new Page());
            x = leftMarginDots;
            y = TopMarginDots;
            scrollOffset = Math.Max(0, DocumentHeight() - PaperAreaHeight);
        }

        private void FinishEscCommand()
        {
            byte command = escParameters[0];
            switch (command)
            {
                case (byte)'@':
                    ResetPrintState();
                    break;
                case (byte)'0':
                    SelectEightLinesPerInch();
                    break;
                case (byte)'2':
                    SelectSixLinesPerInch();
                    break;
                case (byte)'4':
                    SelectItalicPrint();
                    break;
                case (byte)'5':
                    CancelItalicPrint();
                    break;
                case (byte)'-':
                    SetUnderlineMode(GetEscParameter(1));
                    break;
                case (byte)'A':
                    SetLineSpacing72(GetEscParameter(1));
                    break;
                case (byte)'D':
                    SetHorizontalTabs(escParameters);
                    break;
                case (byte)'E':
                    SetEmphasizedPrint();
                    break;
                case (byte)'F':
                    CancelEmphasizedPrint();
                    break;
                case (byte)'G':
                    SetDoubleStrikePrint();
                    break;
                case (byte)'H':
                    CancelDoubleStrikePrint();
                    break;
                case (byte)'P':
                    SetCpi(10);
                    break;
                case (byte)'M':
                    SetCpi(12);
                    break;
                case (byte)'Q':
                    SetRightMargin(GetEscParameter(1));
                    break;
                case (byte)'W':
                    SetExpandedPrint(GetEscParameter(1));
                    break;
                case (byte)'3':
                    SetLineSpacing216(GetEscParameter(1));
                    break;
                case (byte)'J':
                    if (escParameters.Count >= 2)
                        y += Math.Clamp((int)escParameters[1], 1, 255);
                    break;
                case (byte)'K':
                    SetupBitImage(60, 1);
                    break;
                case (byte)'L':
                    SetupBitImage(120, 1);
                    break;
                case (byte)'Y':
                    SetupBitImage(120, 1, suppressAdjacentDots: true);
                    break;
                case (byte)'Z':
                    SetupBitImage(240, 1, suppressAdjacentDots: true);
                    break;
                case (byte)'*':
                    SetupEscStarBitImage();
                    break;
                case (byte)'l':
                    SetLeftMargin(GetEscParameter(1));
                    break;
            }

            escCommand = -1;
            escParameters.Clear();
        }

        private void StartEscCommand(byte command)
        {
            escCommand = command;
            escParameters.Clear();
            escParameters.Add(command);
            escParameterCount = command switch
            {
                (byte)'@'
                or (byte)'0'
                or (byte)'2'
                or (byte)'4'
                or (byte)'5'
                or (byte)'E'
                or (byte)'F'
                or (byte)'G'
                or (byte)'H'
                or (byte)'P'
                or (byte)'M' => 1,
                (byte)'-'
                or (byte)'3'
                or (byte)'A'
                or (byte)'J'
                or (byte)'Q'
                or (byte)'W'
                or (byte)'l' => 2,
                (byte)'D' => -1,
                (byte)'K' or (byte)'L' or (byte)'Y' or (byte)'Z' => 3,
                (byte)'*' => 4,
                _ => 2
            };

            if (escParameterCount == 1)
                FinishEscCommand();
        }

        private int GetEscParameter(int index)
        {
            return escParameters.Count > index ? escParameters[index] : 0;
        }

        private void SelectExpandedPrintOneLine()
        {
            expandedPrintOneLine = true;
            renderedGlyphs.Clear();
        }

        private void SelectCondensedPrint()
        {
            condensedPrint = true;
            renderedGlyphs.Clear();
        }

        private void CancelCondensedPrint()
        {
            condensedPrint = false;
            renderedGlyphs.Clear();
        }

        private void CancelExpandedPrint()
        {
            expandedPrint = false;
            expandedPrintOneLine = false;
            renderedGlyphs.Clear();
        }

        private void SelectEightLinesPerInch()
        {
            SetLineFeedDots(PrinterDpi / 8);
        }

        private void SelectSixLinesPerInch()
        {
            SetLineFeedDots(DefaultLineFeedDots);
        }

        private void SelectItalicPrint()
        {
        }

        private void CancelItalicPrint()
        {
        }

        private void SetUnderlineMode(int mode)
        {
            underlinePrint = (mode & 1) != 0;
            renderedGlyphs.Clear();
        }

        private void SetLineSpacing72(int lineSpacing)
        {
            SetLineFeedDots(Math.Clamp((int)Math.Round(lineSpacing * PrinterDpi / 72.0), 1, 255));
        }

        private void SetHorizontalTabs(IReadOnlyList<byte> tabStops)
        {
            List<int> stops = new List<int>();
            int previous = 0;
            for (int i = 1; i < tabStops.Count; i++)
            {
                int stop = tabStops[i];
                if (stop == 0)
                    break;

                if (stop <= previous)
                    break;

                stops.Add(stop);
                previous = stop;
            }

            horizontalTabs = stops;
        }

        private void SetEmphasizedPrint()
        {
            emphasizedPrint = true;
            renderedGlyphs.Clear();
        }

        private void CancelEmphasizedPrint()
        {
            emphasizedPrint = false;
            renderedGlyphs.Clear();
        }

        private void SetDoubleStrikePrint()
        {
            doubleStrikePrint = true;
            renderedGlyphs.Clear();
        }

        private void CancelDoubleStrikePrint()
        {
            doubleStrikePrint = false;
            renderedGlyphs.Clear();
        }

        private void SetRightMargin(int columns)
        {
            int margin = DefaultLeftMarginDots + Math.Max(1, columns) * CharacterAdvanceDots();
            rightMarginDots = Math.Clamp(margin, leftMarginDots + CharacterAdvanceDots(), DefaultRightMarginDots);
            if (x > rightMarginDots)
                x = rightMarginDots;
        }

        private void SetExpandedPrint(int mode)
        {
            expandedPrint = (mode & 1) != 0;
            renderedGlyphs.Clear();
        }

        private void SetLeftMargin(int columns)
        {
            int margin = DefaultLeftMarginDots + Math.Max(0, columns) * CharacterAdvanceDots();
            leftMarginDots = Math.Clamp(margin, DefaultLeftMarginDots, rightMarginDots - CharacterAdvanceDots());
            if (x < leftMarginDots)
                x = leftMarginDots;
        }

        private void SetLineSpacing216(int lineSpacing)
        {
            SetLineFeedDots(Math.Clamp((int)Math.Round(lineSpacing * PrinterDpi / 216.0), 1, 255));
        }

        private void SetLineFeedDots(int dots)
        {
            lineFeedDots = Math.Clamp(dots, 1, 255);
            renderedGlyphs.Clear();
        }

        private void SetCpi(int charactersPerInch)
        {
            cpi = charactersPerInch;
            renderedGlyphs.Clear();
        }

        private bool IsExpandedPrint()
        {
            return expandedPrint || expandedPrintOneLine;
        }

        private void HorizontalTab()
        {
            x = GetNextTabX();
        }

        private int GetNextTabX()
        {
            int currentColumn = Math.Max(0, (x - leftMarginDots + CharacterAdvanceDots() - 1) / CharacterAdvanceDots());
            foreach (int tab in horizontalTabs)
            {
                if (tab <= currentColumn)
                    continue;

                return Math.Min(rightMarginDots, leftMarginDots + tab * CharacterAdvanceDots());
            }

            return rightMarginDots;
        }

        private static List<int> DefaultHorizontalTabs()
        {
            List<int> tabs = new List<int>();
            for (int column = 8; column < 160; column += 8)
                tabs.Add(column);
            return tabs;
        }

        private void SetupEscStarBitImage()
        {
            int mode = escParameters[1];
            (int horizontalDensity, bool suppressAdjacentDots) = mode switch
            {
                0 => (60, false),
                1 => (120, false),
                2 => (120, true),
                3 => (240, true),
                4 => (80, false),
                5 => (72, false),
                6 => (90, false),
                _ => (0, false)
            };

            if (horizontalDensity == 0)
                DiscardBitImageData(parameterOffset: 2);
            else
                SetupBitImage(horizontalDensity, 1, suppressAdjacentDots, parameterOffset: 2);
        }

        private void SetupBitImage(int horizontalDensity, int verticalBytes, bool suppressAdjacentDots = false, int parameterOffset = 1)
        {
            if (escParameters.Count < parameterOffset + 2)
                return;

            int count = escParameters[parameterOffset] | (escParameters[parameterOffset + 1] << 8);
            bitImageBytesPerColumn = verticalBytes;
            bitImageColumnByte = 0;
            bitImageRemaining = count * verticalBytes;
            bitImageAdvanceDots = (double)PrinterDpi / horizontalDensity;
            bitImageX = x;
            bitImageSuppressAdjacentDots = suppressAdjacentDots;
            bitImageDiscarding = false;
            bitImageRowReturnPending = false;
            Array.Clear(bitImagePreviousColumnBytes);
            escCommand = -1;
            escParameters.Clear();
        }

        private void DiscardBitImageData(int parameterOffset)
        {
            if (escParameters.Count < parameterOffset + 2)
                return;

            int count = escParameters[parameterOffset] | (escParameters[parameterOffset + 1] << 8);
            bitImageBytesPerColumn = 1;
            bitImageColumnByte = 0;
            bitImageRemaining = count;
            bitImageAdvanceDots = 0;
            bitImageSuppressAdjacentDots = false;
            bitImageDiscarding = true;
            bitImageRowReturnPending = false;
            Array.Clear(bitImagePreviousColumnBytes);
            escCommand = -1;
            escParameters.Clear();
        }

        private void PrintBitImageByte(byte value)
        {
            if (bitImageDiscarding)
            {
                bitImageRemaining--;
                if (bitImageRemaining == 0)
                    bitImageDiscarding = false;
                return;
            }

            int byteRow = bitImageColumnByte;
            byte dots = bitImageSuppressAdjacentDots
                ? (byte)(value & ~bitImagePreviousColumnBytes[byteRow])
                : value;

            for (int bit = 0; bit < 8; bit++)
            {
                if ((dots & (0x80 >> bit)) != 0)
                    PlotPrinterDot(x, y + byteRow * 8 + bit);
            }

            int pinsStruck = CountBits(dots);
            if (soundEnabled && !fastGraphicsEnabled)
            {
                PrintHeadAdvanced?.Invoke(
                    pinsStruck,
                    GetBitImageHeadCost(value) / EffectiveGraphicsPrintHeadDotsPerSecond());
            }

            bitImagePreviousColumnBytes[byteRow] = dots;
            bitImageRemaining--;
            if (bitImageRemaining == 0)
                bitImageRowReturnPending = true;
            bitImageColumnByte++;
            if (bitImageColumnByte >= bitImageBytesPerColumn)
            {
                bitImageColumnByte = 0;
                bitImageX += bitImageAdvanceDots;
                x = Math.Max(x + 1, (int)Math.Round(bitImageX));
            }
        }

        private static byte BuildPrintScreenColumn(
            ReadOnlySpan<uint> argbPixels,
            int sourceWidth,
            int sourceHeight,
            int targetX,
            int targetY,
            int targetWidth,
            int targetHeight,
            bool inverted)
        {
            byte dots = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int y = targetY + bit;
                if (y >= targetHeight)
                    continue;

                int sourceX = Math.Min(sourceWidth - 1, targetX * sourceWidth / targetWidth);
                int sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
                uint pixel = argbPixels[(sourceY * sourceWidth) + sourceX];
                if (IsDitheredBlack(pixel, targetX, y) != inverted)
                    dots |= (byte)(0x80 >> bit);
            }

            return dots;
        }

        private static bool IsDitheredBlack(uint argb, int x, int y)
        {
            int red = (int)((argb >> 16) & 0xFF);
            int green = (int)((argb >> 8) & 0xFF);
            int blue = (int)(argb & 0xFF);
            int luminance = (red * 299 + green * 587 + blue * 114) / 1000;
            int threshold = (Bayer8x8[(y & 7) * PrintScreenDitherSize + (x & 7)] * 4) + 2;
            return luminance > threshold;
        }

        private static int CountBits(byte value)
        {
            int count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }

            return count;
        }

        private void ResetPrintState()
        {
            cpi = 10;
            lineFeedDots = DefaultLineFeedDots;
            leftMarginDots = DefaultLeftMarginDots;
            rightMarginDots = DefaultRightMarginDots;
            horizontalTabs = DefaultHorizontalTabs();
            condensedPrint = false;
            expandedPrint = false;
            expandedPrintOneLine = false;
            emphasizedPrint = false;
            doubleStrikePrint = false;
            underlinePrint = false;
            escCommand = -1;
            escParameters.Clear();
            bitImageRemaining = 0;
            bitImageBytesPerColumn = 0;
            bitImageColumnByte = 0;
            Array.Clear(bitImagePreviousColumnBytes);
            bitImageAdvanceDots = 0;
            bitImageX = 0;
            bitImageSuppressAdjacentDots = false;
            bitImageDiscarding = false;
            bitImageRowReturnPending = false;
            renderedGlyphs.Clear();
        }

        private void ResetInputFifo()
        {
            lock (inputFifoLock)
            {
                inputFifo.Clear();
                printHeadLastTicks = 0;
                printHeadBudgetDots = 0;
            }
        }

        private int CharacterAdvanceDots()
        {
            return Math.Max(1, (int)Math.Round(BaseCharacterAdvanceDots() * CharacterWidthScale()));
        }

        private int BaseCharacterAdvanceDots()
        {
            return PrinterDpi / cpi;
        }

        private double CharacterWidthScale()
        {
            double scale = condensedPrint
                ? cpi == 12 ? 12.0 / 20.0 : 10.0 / 17.0
                : 1.0;

            if (IsExpandedPrint())
                scale *= 2.0;

            return scale;
        }

        private void DrawGlyph(char c, int originX, int originY)
        {
            RenderedGlyph glyph = GetRenderedGlyph(c);
            Page page = pages[^1];
            Span<int> pinsByColumn = stackalloc int[glyph.Width];
            for (int glyphY = 0; glyphY < glyph.Height; glyphY++)
            {
                int dotY = originY + glyphY;
                if (dotY >= PageHeightDots)
                {
                    NewPage();
                    page = pages[^1];
                    dotY = y + glyphY;
                }

                if (dotY < 0 || dotY >= PageHeightDots)
                    continue;

                int pageOffset = dotY * PageWidthDots;
                int glyphOffset = glyphY * glyph.Width;
                bool rowPrinted = false;
                for (int glyphX = 0; glyphX < glyph.Width; glyphX++)
                {
                    uint pixel = glyph.Pixels[glyphOffset + glyphX];
                    if (pixel == 0)
                        continue;

                    int dotX = originX + glyphX;
                    if (dotX < 0 || dotX >= PageWidthDots)
                        continue;

                    page.Pixels[pageOffset + dotX] = pixel;
                    rowPrinted = true;
                    pinsByColumn[glyphX]++;
                }

                if (rowPrinted)
                    page.MarkDirty(dotY);
            }

            int printedColumns = 0;
            foreach (int pinsStruck in pinsByColumn)
            {
                if (pinsStruck > 0)
                    printedColumns++;
            }

            double characterCost = CharacterAdvanceDots() + (printedColumns > 0 ? PinStrikeDotCost : 0);
            double columnDuration = characterCost
                / (DraftPrintHeadDotsPerSecond * Math.Max(1, printedColumns));
            if (printedColumns == 0)
            {
                if (soundEnabled)
                    PrintHeadAdvanced?.Invoke(0, columnDuration);
                return;
            }

            if (!soundEnabled)
                return;

            foreach (int pinsStruck in pinsByColumn)
            {
                if (pinsStruck > 0)
                    PrintHeadAdvanced?.Invoke(pinsStruck, columnDuration);
            }
        }

        private void PlotPrinterDot(int dotX, int dotY)
        {
            if (dotY >= PageHeightDots)
                NewPage();

            PlotInk(dotX, dotY, 0xFF101010);

            PlotInk(dotX - 1, dotY, 0xFF383838);
            PlotInk(dotX + 1, dotY, 0xFF383838);
            PlotInk(dotX, dotY - 1, 0xFF383838);
            PlotInk(dotX, dotY + 1, 0xFF383838);

            PlotInk(dotX - 1, dotY - 1, 0xFF707070);
            PlotInk(dotX + 1, dotY - 1, 0xFF707070);
            PlotInk(dotX - 1, dotY + 1, 0xFF707070);
            PlotInk(dotX + 1, dotY + 1, 0xFF707070);
        }

        private void PlotInk(int dotX, int dotY, uint ink)
        {
            if (dotX < 0 || dotX >= PageWidthDots || dotY < 0 || dotY >= PageHeightDots)
                return;

            Page page = pages[^1];
            int index = (dotY * PageWidthDots) + dotX;
            page.Pixels[index] = Darken(page.Pixels[index], ink);
            page.MarkDirty(dotY);
        }

        private static uint Darken(uint existing, uint ink)
        {
            uint red = Math.Min((existing >> 16) & 0xFF, (ink >> 16) & 0xFF);
            uint green = Math.Min((existing >> 8) & 0xFF, (ink >> 8) & 0xFF);
            uint blue = Math.Min(existing & 0xFF, ink & 0xFF);
            return 0xFF000000u | (red << 16) | (green << 8) | blue;
        }

        private void EnsureFirstPage()
        {
            if (pages.Count > 0)
                return;

            pages.Add(new Page());
            x = leftMarginDots;
            y = TopMarginDots;
        }

        private void EnsureWindow()
        {
            if (window != IntPtr.Zero)
                return;

            window = SDL_CreateWindow("Epson FX-80 Printer", 120, 120, WindowWidth, WindowHeight, SDL_WINDOW_SHOWN);
            if (window == IntPtr.Zero)
                throw new InvalidOperationException($"SDL_CreateWindow failed: {GetSdlError()}");

            WindowId = SDL_GetWindowID(window);
            renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED);
            if (renderer == IntPtr.Zero)
                renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_SOFTWARE);
            if (renderer == IntPtr.Zero)
                throw new InvalidOperationException($"SDL_CreateRenderer failed: {GetSdlError()}");
        }

        private void UpdatePageTexture(Page page)
        {
            long now = Environment.TickCount64;

            if (page.Texture == IntPtr.Zero)
            {
                page.Texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STREAMING, PaperViewWidth, PaperViewHeight);
                if (page.Texture == IntPtr.Zero)
                    throw new InvalidOperationException($"SDL_CreateTexture failed: {GetSdlError()}");
                page.NextPreviewRefreshTicks = now;
            }

            if (!page.Dirty)
                return;

            if (now < page.NextPreviewRefreshTicks)
                return;

            int previewTop = page.DirtyMinY * PaperViewHeight / PageHeightDots;
            int previewBottom = Math.Min(
                PaperViewHeight,
                ((page.DirtyMaxY + 1) * PaperViewHeight + PageHeightDots - 1) / PageHeightDots);
            BuildPreview(page, previewTop, previewBottom);

            SdlRect dirtyRect = new SdlRect(0, previewTop, PaperViewWidth, previewBottom - previewTop);
            GCHandle handle = GCHandle.Alloc(page.PreviewPixels, GCHandleType.Pinned);
            try
            {
                IntPtr pixels = IntPtr.Add(
                    handle.AddrOfPinnedObject(),
                    previewTop * PaperViewWidth * sizeof(uint));
                SDL_UpdateTexture(page.Texture, ref dirtyRect, pixels, PaperViewWidth * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            page.ClearDirty();
            page.NextPreviewRefreshTicks = now + PreviewRefreshIntervalMs;
        }

        private static void BuildPreview(Page page, int previewTop, int previewBottom)
        {
            for (int y = previewTop; y < previewBottom; y++)
            {
                int sourceTop = y * PageHeightDots / PaperViewHeight;
                int sourceBottom = Math.Max(sourceTop + 1, (y + 1) * PageHeightDots / PaperViewHeight);
                for (int x = 0; x < PaperViewWidth; x++)
                {
                    int sourceLeft = x * PageWidthDots / PaperViewWidth;
                    int sourceRight = Math.Max(sourceLeft + 1, (x + 1) * PageWidthDots / PaperViewWidth);
                    page.PreviewPixels[(y * PaperViewWidth) + x] = SamplePreviewPixel(page.Pixels, sourceLeft, sourceTop, sourceRight, sourceBottom);
                }
            }
        }

        private static uint SamplePreviewPixel(uint[] pixels, int left, int top, int right, int bottom)
        {
            long red = 0;
            long green = 0;
            long blue = 0;
            int samples = 0;

            for (int y = top; y < bottom; y++)
            {
                int offset = y * PageWidthDots;
                for (int x = left; x < right; x++)
                {
                    uint pixel = pixels[offset + x];
                    red += (pixel >> 16) & 0xFF;
                    green += (pixel >> 8) & 0xFF;
                    blue += pixel & 0xFF;
                    samples++;
                }
            }

            if (samples == 0)
                return PaperColour;

            uint averagedRed = (uint)(red / samples);
            uint averagedGreen = (uint)(green / samples);
            uint averagedBlue = (uint)(blue / samples);
            return 0xFF000000u | (averagedRed << 16) | (averagedGreen << 8) | averagedBlue;
        }

        private void DrawScrollBar()
        {
            SDL_SetRenderDrawColor(renderer, 68, 70, 74, 255);
            SdlRect track = new SdlRect(PaperViewWidth, 0, ScrollBarWidth, PaperAreaHeight);
            SDL_RenderFillRect(renderer, ref track);

            int documentHeight = DocumentHeight();
            int thumbHeight = Math.Max(32, PaperAreaHeight * PaperAreaHeight / Math.Max(PaperAreaHeight, documentHeight));
            int thumbTop = documentHeight <= PaperAreaHeight ? 0 : scrollOffset * (PaperAreaHeight - thumbHeight) / (documentHeight - PaperAreaHeight);
            SDL_SetRenderDrawColor(renderer, 188, 190, 194, 255);
            SdlRect thumb = new SdlRect(PaperViewWidth + 3, thumbTop + 3, ScrollBarWidth - 6, Math.Max(8, thumbHeight - 6));
            SDL_RenderFillRect(renderer, ref thumb);
        }

        private void Scroll(int wheelY)
        {
            scrollOffset -= wheelY * 64;
            UpdateScrollBounds();
        }

        private void UpdateScrollBounds()
        {
            scrollOffset = Math.Clamp(scrollOffset, 0, Math.Max(0, DocumentHeight() - PaperAreaHeight));
        }

        private int DocumentHeight()
        {
            return pages.Count * PaperViewHeight + Math.Max(0, pages.Count - 1) * PageGap;
        }

        private void SavePages(string selectedPath)
        {
            string path = EnsurePngExtension(selectedPath);
            if (pages.Count == 1)
            {
                WritePng(path, pages[0].Pixels, PageWidthDots, PageHeightDots);
                return;
            }

            string directory = Path.GetDirectoryName(path) ?? ".";
            string name = Path.GetFileNameWithoutExtension(path);
            for (int i = 0; i < pages.Count; i++)
            {
                string pagePath = Path.Combine(directory, $"{name}-page-{i + 1:000}.png");
                WritePng(pagePath, pages[i].Pixels, PageWidthDots, PageHeightDots);
            }
        }

        private static string CreatePngFileName()
        {
            return $"bbc-printer-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        }

        private static string EnsurePngExtension(string path)
        {
            return string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + ".png";
        }

        private static string? SelectNativePrinterPngFile(string defaultFileName)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return RunProcessForSingleLine(
                        "powershell",
                        "-NoProfile",
                        "-STA",
                        "-Command",
                        $"Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.SaveFileDialog; $dialog.Title = 'Save printer PNG'; $dialog.Filter = 'PNG image (*.png)|*.png'; $dialog.DefaultExt = 'png'; $dialog.FileName = '{defaultFileName}'; if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{ $dialog.FileName }}");

                if (OperatingSystem.IsMacOS())
                    return RunProcessForSingleLine("osascript", "-e", $"POSIX path of (choose file name with prompt \"Save printer PNG\" default name \"{defaultFileName}\")");

                if (OperatingSystem.IsLinux())
                    return RunProcessForSingleLine("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Save printer PNG", $"--filename={defaultFileName}");
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
            return Marshal.PtrToStringAnsi(SDL_GetError()) ?? "unknown SDL error";
        }

        private RenderedGlyph GetRenderedGlyph(char c)
        {
            if (renderedGlyphs.TryGetValue(c, out RenderedGlyph? glyph))
                return glyph;

            glyph = RenderGlyph(c);
            renderedGlyphs[c] = glyph;
            return glyph;
        }

        private RenderedGlyph RenderGlyph(char c)
        {
            int width = CharacterAdvanceDots();
            int height = Math.Max(lineFeedDots, PrinterFontSizeDots + 8);
            uint[] pixels = new uint[width * height];

            using SKBitmap bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using SKCanvas canvas = new SKCanvas(bitmap);
            using SKPaint paint = new SKPaint
            {
                Typeface = GetPrinterTypeface(),
                TextSize = PrinterFontSizeDots,
                IsAntialias = true,
                Color = new SKColor(16, 16, 16, 255),
                SubpixelText = false,
                LcdRenderText = false
            };

            canvas.Clear(SKColors.Transparent);
            string text = c.ToString();
            SKRect bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            paint.GetFontMetrics(out SKFontMetrics metrics);
            float horizontalScale = (float)CharacterWidthScale();
            float textX = MathF.Max(0, ((width - bounds.Width * horizontalScale) / 2) - bounds.Left * horizontalScale);
            float fontHeight = metrics.Descent - metrics.Ascent;
            float baseline = ((height - fontHeight) / 2) - metrics.Ascent;
            canvas.Save();
            canvas.Scale(horizontalScale, 1.0f);
            DrawPrinterText(canvas, text, textX / horizontalScale, baseline, paint);
            canvas.Restore();

            if (underlinePrint)
            {
                using SKPaint underlinePaint = new SKPaint
                {
                    Color = new SKColor(16, 16, 16, 255),
                    IsAntialias = true,
                    StrokeWidth = 2
                };
                float underlineY = MathF.Min(height - 3, baseline + 3);
                canvas.DrawLine(1, underlineY, width - 2, underlineY, underlinePaint);
            }

            for (int y = 0; y < height; y++)
            {
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    SKColor ink = bitmap.GetPixel(x, y);
                    if (ink.Alpha == 0)
                        continue;

                    pixels[offset + x] = BlendInkOverPaper(ink);
                }
            }

            return new RenderedGlyph(width, height, pixels);
        }

        private void DrawPrinterText(SKCanvas canvas, string text, float x, float baseline, SKPaint paint)
        {
            canvas.DrawText(text, x, baseline, paint);

            if (emphasizedPrint)
                canvas.DrawText(text, x + 1, baseline, paint);

            if (doubleStrikePrint)
            {
                canvas.DrawText(text, x, baseline + 1, paint);
                if (emphasizedPrint)
                    canvas.DrawText(text, x + 1, baseline + 1, paint);
            }
        }

        private SKTypeface GetPrinterTypeface()
        {
            if (printerTypeface != null)
                return printerTypeface;

            Assembly assembly = typeof(DotMatrixPrinter).Assembly;
            using Stream? resource = assembly.GetManifestResourceStream("BBC.DotMatrix-Regular.ttf");
            if (resource != null)
            {
                printerTypeface = SKTypeface.FromStream(resource);
                return printerTypeface;
            }

            string fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DotMatrix-Regular.ttf");
            printerTypeface = File.Exists(fontPath)
                ? SKTypeface.FromFile(fontPath)
                : SKTypeface.Default;
            return printerTypeface;
        }

        private static uint BlendInkOverPaper(SKColor ink)
        {
            byte paperR = (byte)((PaperColour >> 16) & 0xFF);
            byte paperG = (byte)((PaperColour >> 8) & 0xFF);
            byte paperB = (byte)(PaperColour & 0xFF);
            int alpha = ink.Alpha;

            byte red = (byte)((ink.Red * alpha + paperR * (255 - alpha)) / 255);
            byte green = (byte)((ink.Green * alpha + paperG * (255 - alpha)) / 255);
            byte blue = (byte)((ink.Blue * alpha + paperB * (255 - alpha)) / 255);
            return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
        }

        private static readonly byte[] FallbackGlyph = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0, 0b00100];
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

        private static byte[] GetGlyphRows(char c)
        {
            return Glyphs.TryGetValue(c, out byte[]? rows) ? rows : FallbackGlyph;
        }

        private sealed class RenderedGlyph(int width, int height, uint[] pixels)
        {
            public int Width { get; } = width;
            public int Height { get; } = height;
            public uint[] Pixels { get; } = pixels;
        }

        private sealed class Page
        {
            public readonly uint[] Pixels = CreateBlankPage();
            public readonly uint[] PreviewPixels = new uint[PaperViewWidth * PaperViewHeight];
            public IntPtr Texture;
            public bool Dirty = true;
            public int DirtyMinY;
            public int DirtyMaxY = PageHeightDots - 1;
            public long NextPreviewRefreshTicks;

            public void MarkDirty(int dotY)
            {
                if (!Dirty)
                {
                    Dirty = true;
                    DirtyMinY = dotY;
                    DirtyMaxY = dotY;
                    return;
                }

                DirtyMinY = Math.Min(DirtyMinY, dotY);
                DirtyMaxY = Math.Max(DirtyMaxY, dotY);
            }

            public void ClearDirty()
            {
                Dirty = false;
                DirtyMinY = PageHeightDots;
                DirtyMaxY = -1;
            }

            private static uint[] CreateBlankPage()
            {
                uint[] pixels = new uint[PageWidthDots * PageHeightDots];
                Array.Fill(pixels, PaperColour);
                return pixels;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct SdlRect
        {
            public SdlRect(int x, int y, int w, int h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }

            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;
        }

        [DllImport("SDL2")] private static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, int flags);
        [DllImport("SDL2")] private static extern void SDL_ShowWindow(IntPtr window);
        [DllImport("SDL2")] private static extern void SDL_HideWindow(IntPtr window);
        [DllImport("SDL2")] private static extern uint SDL_GetWindowID(IntPtr window);
        [DllImport("SDL2")] private static extern void SDL_DestroyWindow(IntPtr window);
        [DllImport("SDL2")] private static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);
        [DllImport("SDL2")] private static extern void SDL_DestroyRenderer(IntPtr renderer);
        [DllImport("SDL2")] private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);
        [DllImport("SDL2")] private static extern void SDL_DestroyTexture(IntPtr texture);
        [DllImport("SDL2")] private static extern int SDL_UpdateTexture(IntPtr texture, ref SdlRect rect, IntPtr pixels, int pitch);
        [DllImport("SDL2")] private static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);
        [DllImport("SDL2")] private static extern int SDL_RenderClear(IntPtr renderer);
        [DllImport("SDL2")] private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr source, ref SdlRect destination);
        [DllImport("SDL2")] private static extern int SDL_RenderFillRect(IntPtr renderer, ref SdlRect rect);
        [DllImport("SDL2")] private static extern void SDL_RenderPresent(IntPtr renderer);
        [DllImport("SDL2")] private static extern IntPtr SDL_GetError();
    }
}
