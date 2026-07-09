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
        private const int PrinterMenuHeight = 24;
        private const int PrinterMenuTextScale = 1;
        private const int PrinterMenuGlyphAdvance = 7;
        private const int PrinterMenuTextX = 10;
        private const int PrinterMenuTextY = 8;
        private const int PrinterMenuFileWidth = 30;
        private const int PrinterMenuItemHeight = 20;
        private const int PrinterMenuDropDownWidth = 116;
        private const int PreviewRefreshIntervalMs = 150;
        private const int WindowWidth = PaperViewWidth + ScrollBarWidth;
        private const int PaperAreaHeight = PaperViewHeight;
        private const int WindowHeight = PrinterMenuHeight + PaperAreaHeight;
        private const int DefaultLeftMarginDots = 32;
        private const int TopMarginDots = 120;
        private const int DefaultRightMarginDots = PageWidthDots - 32;
        private const int BottomMarginDots = PageHeightDots - 120;
        private const int DefaultLineFeedDots = PrinterDpi / 6;
        private const int PrinterFontSizeDots = 31;
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

        private readonly List<Page> pages = new List<Page>();
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
        private int scrollOffset;
        private bool fileMenuOpen;
        private bool fileMenuHover;
        private bool savePngHover;
        private bool clearPaperHover;
        private bool condensedPrint;
        private bool expandedPrint;
        private bool expandedPrintOneLine;
        private bool emphasizedPrint;
        private bool doubleStrikePrint;
        private bool underlinePrint;
        private List<int> horizontalTabs = DefaultHorizontalTabs();

        public bool Enabled => enabled;

        public uint WindowId { get; private set; }

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
            else if (window != IntPtr.Zero)
            {
                SDL_HideWindow(window);
            }
        }

        public void Write(byte value)
        {
            if (!enabled)
                return;

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
            {
                fileMenuHover = IsInFileMenu(mouseX, mouseY);
                savePngHover = fileMenuOpen && IsInSavePngItem(mouseX, mouseY);
                clearPaperHover = fileMenuOpen && IsInClearPaperItem(mouseX, mouseY);
                return true;
            }

            if (type == SDL_MOUSEBUTTONDOWN && mouseButton == SDL_BUTTON_LEFT)
            {
                HandleMouseClick(mouseX, mouseY);
                return true;
            }

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
            UpdateScrollBounds();

            SDL_SetRenderDrawColor(renderer, 188, 190, 190, 255);
            SDL_RenderClear(renderer);
            DrawMenuBar();

            for (int i = 0; i < pages.Count; i++)
            {
                int top = PrinterMenuHeight + (i * (PaperViewHeight + PageGap)) - scrollOffset;
                if (top > WindowHeight || top + PaperViewHeight < 0)
                    continue;

                Page page = pages[i];
                UpdatePageTexture(page);
                SdlRect destination = new SdlRect(0, top, PaperViewWidth, PaperViewHeight);
                SDL_RenderCopy(renderer, page.Texture, IntPtr.Zero, ref destination);
            }

            DrawScrollBar();
            if (fileMenuOpen)
                DrawFileMenu();
            SDL_RenderPresent(renderer);
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
            int currentColumn = Math.Max(0, (x - leftMarginDots + CharacterAdvanceDots() - 1) / CharacterAdvanceDots());
            foreach (int tab in horizontalTabs)
            {
                if (tab <= currentColumn)
                    continue;

                x = Math.Min(rightMarginDots, leftMarginDots + tab * CharacterAdvanceDots());
                return;
            }

            x = rightMarginDots;
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

            bitImagePreviousColumnBytes[byteRow] = dots;
            bitImageRemaining--;
            bitImageColumnByte++;
            if (bitImageColumnByte >= bitImageBytesPerColumn)
            {
                bitImageColumnByte = 0;
                bitImageX += bitImageAdvanceDots;
                x = Math.Max(x + 1, (int)Math.Round(bitImageX));
            }
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
            renderedGlyphs.Clear();
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
                for (int glyphX = 0; glyphX < glyph.Width; glyphX++)
                {
                    uint pixel = glyph.Pixels[glyphOffset + glyphX];
                    if (pixel == 0)
                        continue;

                    int dotX = originX + glyphX;
                    if (dotX < 0 || dotX >= PageWidthDots)
                        continue;

                    page.Pixels[pageOffset + dotX] = pixel;
                    page.Dirty = true;
                }
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
            page.Dirty = true;
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

            window = SDL_CreateWindow("BBC Dot Matrix Printer", 120, 120, WindowWidth, WindowHeight, SDL_WINDOW_SHOWN);
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
                page.Dirty = true;
                page.NextPreviewRefreshTicks = now;
            }

            if (!page.Dirty)
                return;

            if (now < page.NextPreviewRefreshTicks)
                return;

            BuildPreview(page);
            GCHandle handle = GCHandle.Alloc(page.PreviewPixels, GCHandleType.Pinned);
            try
            {
                SDL_UpdateTexture(page.Texture, IntPtr.Zero, handle.AddrOfPinnedObject(), PaperViewWidth * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            page.Dirty = false;
            page.NextPreviewRefreshTicks = now + PreviewRefreshIntervalMs;
        }

        private static void BuildPreview(Page page)
        {
            for (int y = 0; y < PaperViewHeight; y++)
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
            SdlRect track = new SdlRect(PaperViewWidth, PrinterMenuHeight, ScrollBarWidth, PaperAreaHeight);
            SDL_RenderFillRect(renderer, ref track);

            int documentHeight = DocumentHeight();
            int thumbHeight = Math.Max(32, PaperAreaHeight * PaperAreaHeight / Math.Max(PaperAreaHeight, documentHeight));
            int thumbTop = documentHeight <= PaperAreaHeight ? 0 : scrollOffset * (PaperAreaHeight - thumbHeight) / (documentHeight - PaperAreaHeight);
            SDL_SetRenderDrawColor(renderer, 188, 190, 194, 255);
            SdlRect thumb = new SdlRect(PaperViewWidth + 3, PrinterMenuHeight + thumbTop + 3, ScrollBarWidth - 6, Math.Max(8, thumbHeight - 6));
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

        private void HandleMouseClick(int mouseX, int mouseY)
        {
            if (IsInFileMenu(mouseX, mouseY))
            {
                fileMenuOpen = !fileMenuOpen;
                fileMenuHover = true;
                savePngHover = false;
                clearPaperHover = false;
                return;
            }

            if (fileMenuOpen && IsInSavePngItem(mouseX, mouseY))
            {
                fileMenuOpen = false;
                SaveDocumentPng();
                return;
            }

            if (fileMenuOpen && IsInClearPaperItem(mouseX, mouseY))
            {
                fileMenuOpen = false;
                ClearPaper();
                return;
            }

            fileMenuOpen = false;
            savePngHover = false;
            clearPaperHover = false;
        }

        private static bool IsInFileMenu(int mouseX, int mouseY)
        {
            return mouseX >= PrinterMenuTextX
                && mouseX < PrinterMenuTextX + PrinterMenuFileWidth
                && mouseY >= 0
                && mouseY < PrinterMenuHeight;
        }

        private static bool IsInSavePngItem(int mouseX, int mouseY)
        {
            return IsInFileMenuItem(mouseX, mouseY, 0);
        }

        private static bool IsInClearPaperItem(int mouseX, int mouseY)
        {
            return IsInFileMenuItem(mouseX, mouseY, 1);
        }

        private static bool IsInFileMenuItem(int mouseX, int mouseY, int index)
        {
            return mouseX >= PrinterMenuTextX - 4
                && mouseX < PrinterMenuTextX - 4 + PrinterMenuDropDownWidth
                && mouseY >= PrinterMenuHeight + index * PrinterMenuItemHeight
                && mouseY < PrinterMenuHeight + (index + 1) * PrinterMenuItemHeight;
        }

        private void DrawMenuBar()
        {
            SDL_SetRenderDrawColor(renderer, 18, 18, 18, 255);
            SdlRect bar = new SdlRect(0, 0, WindowWidth, PrinterMenuHeight);
            SDL_RenderFillRect(renderer, ref bar);

            SDL_SetRenderDrawColor(renderer, 72, 72, 72, 255);
            SdlRect line = new SdlRect(0, PrinterMenuHeight - 1, WindowWidth, 1);
            SDL_RenderFillRect(renderer, ref line);

            if (fileMenuOpen || fileMenuHover)
            {
                SDL_SetRenderDrawColor(renderer, 42, 42, 42, 255);
                SdlRect hover = new SdlRect(PrinterMenuTextX - 4, 3, PrinterMenuFileWidth + 8, PrinterMenuHeight - 6);
                SDL_RenderFillRect(renderer, ref hover);
                SDL_SetRenderDrawColor(renderer, 96, 96, 96, 255);
                DrawRectOutline(hover);
            }

            byte colour = fileMenuOpen || fileMenuHover ? (byte)245 : (byte)190;
            DrawText("File", PrinterMenuTextX, PrinterMenuTextY, colour, colour, colour);
        }

        private void DrawFileMenu()
        {
            SDL_SetRenderDrawColor(renderer, 28, 28, 28, 245);
            SdlRect menu = new SdlRect(PrinterMenuTextX - 4, PrinterMenuHeight, PrinterMenuDropDownWidth, PrinterMenuItemHeight * 2 + 8);
            SDL_RenderFillRect(renderer, ref menu);

            SDL_SetRenderDrawColor(renderer, 150, 150, 150, 255);
            SdlRect top = new SdlRect(PrinterMenuTextX - 4, PrinterMenuHeight, PrinterMenuDropDownWidth, 1);
            SDL_RenderFillRect(renderer, ref top);

            DrawFileMenuItemHighlight(0, savePngHover);
            DrawFileMenuItemHighlight(1, clearPaperHover);

            DrawText("Save PNG...", PrinterMenuTextX + 10, PrinterMenuHeight + 7, 224, 224, 224);
            DrawText("Clear paper", PrinterMenuTextX + 10, PrinterMenuHeight + PrinterMenuItemHeight + 7, 224, 224, 224);
        }

        private void DrawFileMenuItemHighlight(int index, bool active)
        {
            if (!active)
                return;

            SDL_SetRenderDrawColor(renderer, 58, 58, 58, 255);
            SdlRect item = new SdlRect(
                PrinterMenuTextX - 1,
                PrinterMenuHeight + index * PrinterMenuItemHeight + 4,
                PrinterMenuDropDownWidth - 8,
                PrinterMenuItemHeight - 1);
            SDL_RenderFillRect(renderer, ref item);
        }

        private void DrawRectOutline(SdlRect rect)
        {
            SdlRect top = new SdlRect(rect.X, rect.Y, rect.W, 1);
            SdlRect bottom = new SdlRect(rect.X, rect.Y + rect.H - 1, rect.W, 1);
            SdlRect left = new SdlRect(rect.X, rect.Y, 1, rect.H);
            SdlRect right = new SdlRect(rect.X + rect.W - 1, rect.Y, 1, rect.H);
            SDL_RenderFillRect(renderer, ref top);
            SDL_RenderFillRect(renderer, ref bottom);
            SDL_RenderFillRect(renderer, ref left);
            SDL_RenderFillRect(renderer, ref right);
        }

        private void DrawText(string text, int x, int y, byte red, byte green, byte blue)
        {
            SDL_SetRenderDrawColor(renderer, red, green, blue, 255);
            for (int i = 0; i < text.Length; i++)
                DrawMenuGlyph(text[i], x + i * PrinterMenuGlyphAdvance, y);
        }

        private void DrawMenuGlyph(char c, int x, int y)
        {
            byte[] rows = GetGlyphRows(c);
            for (int row = 0; row < rows.Length; row++)
            {
                byte bits = rows[row];
                for (int col = 0; col < 5; col++)
                {
                    if ((bits & (1 << (4 - col))) == 0)
                        continue;

                    SdlRect pixel = new SdlRect(
                        x + col * PrinterMenuTextScale,
                        y + row * PrinterMenuTextScale,
                        PrinterMenuTextScale,
                        PrinterMenuTextScale);
                    SDL_RenderFillRect(renderer, ref pixel);
                }
            }
        }

        private void SaveDocumentPng()
        {
            EnsureFirstPage();

            string? path = SelectNativePrinterPngFile(CreatePngFileName());
            if (path == null)
                return;

            SavePages(path);
        }

        private void ClearPaper()
        {
            foreach (Page page in pages)
            {
                if (page.Texture != IntPtr.Zero)
                    SDL_DestroyTexture(page.Texture);
            }

            pages.Clear();
            ResetPrintState();
            x = leftMarginDots;
            y = TopMarginDots;
            scrollOffset = 0;
            EnsureFirstPage();
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
            public long NextPreviewRefreshTicks;

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
        [DllImport("SDL2")] private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);
        [DllImport("SDL2")] private static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);
        [DllImport("SDL2")] private static extern int SDL_RenderClear(IntPtr renderer);
        [DllImport("SDL2")] private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr source, ref SdlRect destination);
        [DllImport("SDL2")] private static extern int SDL_RenderFillRect(IntPtr renderer, ref SdlRect rect);
        [DllImport("SDL2")] private static extern void SDL_RenderPresent(IntPtr renderer);
        [DllImport("SDL2")] private static extern IntPtr SDL_GetError();
    }
}
