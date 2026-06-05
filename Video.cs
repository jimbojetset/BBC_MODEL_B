// ============================================================================
// Project:     BBC
// File:        Video.cs
// Description: BBC Model B video state and display rendering.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{
    /// <summary>
    /// Tracks CRTC/Video ULA state and renders BBC video memory into the SDL display framebuffer.
    /// </summary>
    public sealed class Video
    {
        public const ushort Mode7ScreenStart = 0x7C00;
        public const int Mode7Columns = 40;
        public const int Mode7Rows = 25;
        public const int Mode7ScreenBytes = 1024;

        private const ushort TextCursorAddressLow = 0x034A;
        private const ushort TextCursorAddressHigh = 0x034B;
        private const uint Background = 0xFF000000;
        private const uint Foreground = 0xFFFFFFFF;
        private const int BitmapHeight = 256;
        private const int VideoFrameCpuCycles = 40_000;
        private const int VideoFrameScanlines = 312;
        private const int VisibleStartScanline = 36;
        private const int BitmapBytesPerRow10K = 40;
        private const int BitmapBytesPerRow20K = 80;
        private const int CrtcRegisterCount = 32;
        private const int CrtcHorizontalDisplayedRegister = 1;
        private const int CrtcVerticalDisplayedRegister = 6;
        private const int CrtcScanLinesPerCharacterRegister = 9;
        private const int CrtcCursorStartRegister = 10;
        private const int CrtcCursorEndRegister = 11;
        private const int CrtcDisplayStartHighRegister = 12;
        private const int CrtcDisplayStartLowRegister = 13;
        private const int CrtcCursorHighRegister = 14;
        private const int CrtcCursorLowRegister = 15;
        private const int PaletteRegisterCount = 16;
        private const byte UlaTeletext = 0x02;
        private const byte UlaCharactersPerLineMask = 0x0C;
        private const byte UlaClockHigh = 0x10;
        private static readonly uint[] BbcColours =
        [
            0xFF000000, // black
            0xFFFF0000, // red
            0xFF00FF00, // green
            0xFFFFFF00, // yellow
            0xFF0000FF, // blue
            0xFFFF00FF, // magenta
            0xFF00FFFF, // cyan
            0xFFFFFFFF  // white
        ];

        private readonly byte[] memory;
        private readonly ushort osRomStart;
        private readonly byte[] crtcRegisters = new byte[CrtcRegisterCount];
        private readonly byte[] paletteRegisters = new byte[PaletteRegisterCount];
        private readonly byte[] mode4PaletteRegisters = new byte[PaletteRegisterCount];
        private readonly byte[] mode5PaletteRegisters = new byte[PaletteRegisterCount];
        private readonly object frameSnapshotLock = new object();
        private readonly byte[] frameMemory = new byte[0x10000];
        private readonly byte[] frameCrtcRegisters = new byte[CrtcRegisterCount];
        private readonly byte[] framePaletteRegisters = new byte[PaletteRegisterCount];
        private readonly byte[] frameMode4PaletteRegisters = new byte[PaletteRegisterCount];
        private readonly byte[] frameMode5PaletteRegisters = new byte[PaletteRegisterCount];
        private readonly List<VideoRasterEvent> rasterEvents = new List<VideoRasterEvent>();
        private readonly List<VideoRasterEvent> frameRasterEvents = new List<VideoRasterEvent>();
        private readonly List<VideoRasterEvent> activeRasterEvents = new List<VideoRasterEvent>();
        private byte[] activeMemory;
        private byte[] activeCrtcRegisters;
        private byte[] activePaletteRegisters;
        private byte[] activeMode4PaletteRegisters;
        private byte[] activeMode5PaletteRegisters;
        private int screenMemoryStart = 0x3000;
        private int screenMemorySize = 0x5000;
        private int frameScreenMemoryStart = 0x3000;
        private int frameScreenMemorySize = 0x5000;
        private int activeScreenMemoryStart = 0x3000;
        private int activeScreenMemorySize = 0x5000;
        private byte selectedCrtcRegister;
        private byte lastPaletteWrite;
        private bool crtcCursorAddressWritten;
        private bool frameCrtcCursorAddressWritten;
        private bool hasFrameSnapshot;
        private byte frameUlaControl;
        private BbcScreenMode frameMode;
        private byte activeUlaControl;
        private BbcScreenMode activeMode;
        private bool activeCrtcCursorAddressWritten;
        private bool sawMode4ThisFrame;
        private bool sawMode5ThisFrame;
        private bool frameSawMode4;
        private bool frameSawMode5;
        private bool activeSawMode4;
        private bool activeSawMode5;
        private int frameSnapshotSequence;
        private int lastMode4Mode5SplitLine = 192;
        private bool hasLastMode4Mode5SplitLine;

        /// <summary>Gets the currently selected BBC screen mode.</summary>
        public BbcScreenMode CurrentMode { get; private set; } = BbcScreenMode.Mode7;

        /// <summary>Gets the current Video ULA control register value.</summary>
        public byte UlaControl { get; private set; }

        /// <summary>Returns whether a SHEILA address belongs to the video hardware.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when the address is handled by this component.</returns>
        public static bool IsSheilaAddress(ushort address)
        {
            return address is >= 0xFE00 and <= 0xFE01
                or >= 0xFE20 and <= 0xFE23;
        }

        /// <summary>Initializes a new video component.</summary>
        /// <param name="memory">The emulator's 64 KiB CPU-visible memory.</param>
        /// <param name="osRomStart">The start address of the OS ROM font data used by the temporary mode 7 renderer.</param>
        public Video(byte[] memory, ushort osRomStart)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
            this.osRomStart = osRomStart;
            activeMemory = memory;
            activeCrtcRegisters = crtcRegisters;
            activePaletteRegisters = paletteRegisters;
            activeMode4PaletteRegisters = mode4PaletteRegisters;
            activeMode5PaletteRegisters = mode5PaletteRegisters;
            activeUlaControl = UlaControl;
            activeMode = CurrentMode;
            ResetPaletteArray(mode4PaletteRegisters);
            ResetPaletteArray(mode5PaletteRegisters);
            AddRasterEvent(0);
        }

        /// <summary>Resets video device state.</summary>
        public void Reset()
        {
            Array.Clear(crtcRegisters);
            ResetPalette();
            selectedCrtcRegister = 0;
            crtcCursorAddressWritten = false;
            frameCrtcCursorAddressWritten = false;
            hasFrameSnapshot = false;
            sawMode4ThisFrame = false;
            sawMode5ThisFrame = false;
            frameSawMode4 = false;
            frameSawMode5 = false;
            rasterEvents.Clear();
            frameRasterEvents.Clear();
            activeRasterEvents.Clear();
            frameSnapshotSequence = 0;
            lastMode4Mode5SplitLine = 192;
            hasLastMode4Mode5SplitLine = false;
            CurrentMode = BbcScreenMode.Mode7;
            UlaControl = 0;
            screenMemoryStart = 0x3000;
            screenMemorySize = 0x5000;
            frameScreenMemoryStart = screenMemoryStart;
            frameScreenMemorySize = screenMemorySize;
            frameUlaControl = UlaControl;
            frameMode = CurrentMode;
            AddRasterEvent(0);
        }

        /// <summary>Sets the BBC video RAM window used by hardware scrolling wraparound.</summary>
        /// <param name="start">The first RAM address in the selected video window.</param>
        /// <param name="size">The selected video window size in bytes.</param>
        public void SetScreenMemoryWindow(int start, int size)
        {
            if (start < 0 || start >= memory.Length)
                throw new ArgumentOutOfRangeException(nameof(start));

            if (size <= 0 || start + size > 0x8000)
                throw new ArgumentOutOfRangeException(nameof(size));

            screenMemoryStart = start;
            screenMemorySize = size;
        }

        /// <summary>Captures a coherent frame of BBC-visible video state at emulated vsync.</summary>
        public void CaptureVisibleFrame()
        {
            lock (frameSnapshotLock)
            {
                memory.CopyTo(frameMemory, 0);
                crtcRegisters.CopyTo(frameCrtcRegisters, 0);
                paletteRegisters.CopyTo(framePaletteRegisters, 0);
                mode4PaletteRegisters.CopyTo(frameMode4PaletteRegisters, 0);
                mode5PaletteRegisters.CopyTo(frameMode5PaletteRegisters, 0);
                frameCrtcCursorAddressWritten = crtcCursorAddressWritten;
                frameUlaControl = UlaControl;
                frameMode = CurrentMode;
                frameScreenMemoryStart = screenMemoryStart;
                frameScreenMemorySize = screenMemorySize;
                frameSawMode4 = sawMode4ThisFrame;
                frameSawMode5 = sawMode5ThisFrame;
                frameSnapshotSequence++;
                frameRasterEvents.Clear();
                frameRasterEvents.AddRange(rasterEvents);
                rasterEvents.Clear();
                AddRasterEvent(0);
                sawMode4ThisFrame = CurrentMode == BbcScreenMode.Mode4;
                sawMode5ThisFrame = CurrentMode == BbcScreenMode.Mode5;
                hasFrameSnapshot = true;
            }
        }

        /// <summary>Reads a byte from the CRTC or Video ULA register area.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The byte value read.</returns>
        public byte ReadSheila(ushort address)
        {
            return address switch
            {
                0xFE00 => selectedCrtcRegister,
                0xFE01 => crtcRegisters[selectedCrtcRegister & 0x1F],
                0xFE20 or 0xFE22 => UlaControl,
                0xFE21 or 0xFE23 => lastPaletteWrite,
                _ => 0x00
            };
        }

        /// <summary>Writes a byte to the CRTC or Video ULA register area.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The value to write.</param>
        public void WriteSheila(ushort address, byte value, int frameCpuCycle = 0)
        {
            switch (address)
            {
                case 0xFE00:
                    selectedCrtcRegister = (byte)(value & 0x1F);
                    break;

                case 0xFE01:
                    crtcRegisters[selectedCrtcRegister & 0x1F] = value;
                    if ((selectedCrtcRegister & 0x1F) is CrtcCursorHighRegister or CrtcCursorLowRegister)
                        crtcCursorAddressWritten = true;
                    break;

                case 0xFE20:
                case 0xFE22:
                    UlaControl = value;
                    CurrentMode = DecodeModeFromUlaControl(value);
                    if (CurrentMode == BbcScreenMode.Mode4)
                        sawMode4ThisFrame = true;
                    else if (CurrentMode == BbcScreenMode.Mode5)
                        sawMode5ThisFrame = true;
                    AddRasterEvent(frameCpuCycle);
                    break;

                case 0xFE21:
                case 0xFE23:
                    lastPaletteWrite = value;
                    int paletteIndex = (value >> 4) & 0x0F;
                    byte physicalColour = DecodePhysicalColour(value);
                    paletteRegisters[paletteIndex] = physicalColour;
                    if (CurrentMode == BbcScreenMode.Mode4)
                        mode4PaletteRegisters[paletteIndex] = physicalColour;
                    else if (CurrentMode == BbcScreenMode.Mode5)
                        mode5PaletteRegisters[paletteIndex] = physicalColour;
                    AddRasterEvent(frameCpuCycle);
                    break;
            }
        }

        /// <summary>Renders the current video frame into the display framebuffer.</summary>
        /// <param name="display">The SDL-backed display to render into.</param>
        public void Render(Display display)
        {
            lock (frameSnapshotLock)
            {
                if (hasFrameSnapshot)
                {
                    activeMemory = frameMemory;
                    activeCrtcRegisters = frameCrtcRegisters;
                    activePaletteRegisters = framePaletteRegisters;
                    activeMode4PaletteRegisters = frameMode4PaletteRegisters;
                    activeMode5PaletteRegisters = frameMode5PaletteRegisters;
                    activeCrtcCursorAddressWritten = frameCrtcCursorAddressWritten;
                    activeUlaControl = frameUlaControl;
                    activeMode = frameMode;
                    activeScreenMemoryStart = frameScreenMemoryStart;
                    activeScreenMemorySize = frameScreenMemorySize;
                    activeSawMode4 = frameSawMode4;
                    activeSawMode5 = frameSawMode5;
                    activeRasterEvents.Clear();
                    activeRasterEvents.AddRange(frameRasterEvents);
                }
                else
                {
                    activeMemory = memory;
                    activeCrtcRegisters = crtcRegisters;
                    activePaletteRegisters = paletteRegisters;
                    activeMode4PaletteRegisters = mode4PaletteRegisters;
                    activeMode5PaletteRegisters = mode5PaletteRegisters;
                    activeCrtcCursorAddressWritten = crtcCursorAddressWritten;
                    activeUlaControl = UlaControl;
                    activeMode = CurrentMode;
                    activeScreenMemoryStart = screenMemoryStart;
                    activeScreenMemorySize = screenMemorySize;
                    activeSawMode4 = sawMode4ThisFrame;
                    activeSawMode5 = sawMode5ThisFrame;
                    activeRasterEvents.Clear();
                    activeRasterEvents.AddRange(rasterEvents);
                }

                if (ShouldRenderMode4Mode5Split())
                {
                    RenderMode4Mode5Split(display);
                    return;
                }

                switch (activeMode)
                {
                    case BbcScreenMode.Mode7:
                        RenderMode7TextScreen(display);
                        break;

                    case BbcScreenMode.Mode0:
                        RenderBitmapMode0(display);
                        break;

                    case BbcScreenMode.Mode1:
                        RenderBitmapMode1(display);
                        break;

                    case BbcScreenMode.Mode2:
                        RenderBitmapMode2(display);
                        break;

                    case BbcScreenMode.Mode4:
                        RenderBitmapMode4(display);
                        break;

                    case BbcScreenMode.Mode5:
                        RenderBitmapMode5(display);
                        break;

                    default:
                        RenderMode7TextScreen(display);
                        break;
                }
            }
        }

        /// <summary>Counts non-blank mode 7 screen cells for smoke tests.</summary>
        /// <returns>The number of non-blank cells in the physical mode 7 screen buffer.</returns>
        public int CountMode7NonBlankCells()
        {
            int count = 0;

            for (int i = 0; i < Mode7Columns * Mode7Rows; i++)
            {
                byte character = memory[Mode7ScreenStart + i];
                if (character != 0 && character != 32)
                    count++;
            }

            return count;
        }

        private void RenderMode7TextScreen(Display display)
        {
            const int cellWidth = Display.DefaultWidth / Mode7Columns;
            const int cellHeight = Display.DefaultHeight / Mode7Rows;

            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            bool flashVisible = (Environment.TickCount64 / 500 & 1) == 0;

            for (int row = 0; row < Mode7Rows; row++)
            {
                int cellY = row * cellHeight;
                TeletextState state = new TeletextState();

                for (int column = 0; column < Mode7Columns; column++)
                {
                    byte character = ReadMode7DisplayCharacter(row, column);
                    int cellX = column * cellWidth;

                    if (TryApplyTeletextControl(character, state))
                        continue;

                    if (state.Flashing && !flashVisible)
                        continue;

                    if (state.GraphicsMode && IsTeletextMosaicCharacter(character))
                    {
                        state.HeldMosaic = character;
                        DrawTeletextMosaic(pixels, display.Width, display.Height, cellX, cellY, cellWidth, cellHeight, character, state.ForegroundColour, state.SeparatedGraphics);
                    }
                    else
                    {
                        bool doubleHeightBottom = state.DoubleHeight && IsDoubleHeightBottomRow(row, column, character);
                        Saa5050Font.Draw(pixels, display.Width, display.Height, cellX, cellY, cellWidth, cellHeight, character, state.ForegroundColour, state.DoubleHeight, doubleHeightBottom);
                    }
                }
            }

            RenderMode7Cursor(display);
        }

        private bool TryApplyTeletextControl(byte character, TeletextState state)
        {
            int control = character & 0x7F;
            if (control >= 0x20)
                return false;

            switch (control)
            {
                case >= 0x01 and <= 0x07:
                    state.GraphicsMode = false;
                    state.ForegroundColour = BbcColours[control & 0x07];
                    break;

                case 0x0C:
                    state.DoubleHeight = false;
                    break;

                case 0x0D:
                    state.DoubleHeight = true;
                    break;

                case 0x08:
                    state.Flashing = true;
                    break;

                case 0x09:
                    state.Flashing = false;
                    break;

                case >= 0x10 and <= 0x17:
                    state.GraphicsMode = true;
                    state.ForegroundColour = BbcColours[control & 0x07];
                    break;

                case 0x19:
                    state.SeparatedGraphics = false;
                    break;

                case 0x1A:
                    state.SeparatedGraphics = true;
                    break;

                case 0x1C:
                    state.BackgroundColour = Background;
                    break;

                case 0x1D:
                    state.BackgroundColour = state.ForegroundColour;
                    break;

                case 0x1E:
                    state.HoldGraphics = true;
                    break;

                case 0x1F:
                    state.HoldGraphics = false;
                    state.HeldMosaic = null;
                    break;
            }

            return true;
        }

        private bool IsDoubleHeightBottomRow(int row, int column, byte character)
        {
            if (row <= 0)
                return false;

            TeletextState previousState = new TeletextState();
            byte previousCharacter = 32;

            for (int previousColumn = 0; previousColumn <= column; previousColumn++)
            {
                previousCharacter = ReadMode7DisplayCharacter(row - 1, previousColumn);
                if (TryApplyTeletextControl(previousCharacter, previousState))
                    continue;
            }

            return previousState.DoubleHeight && (previousCharacter & 0x7F) == (character & 0x7F);
        }

        private static void DrawTeletextMosaic(uint[] pixels, int width, int height, int cellX, int cellY, int cellWidth, int cellHeight, byte character, uint colour, bool separated)
        {
            int value = character & 0x7F;
            int pattern = (value & 0x1F) | ((value & 0x40) >> 1);
            int blockWidth = cellWidth / 2;
            int blockHeight = cellHeight / 3;
            int gap = separated ? 2 : 0;

            for (int block = 0; block < 6; block++)
            {
                if ((pattern & (1 << block)) == 0)
                    continue;

                int blockX = block & 1;
                int blockY = block / 2;
                int x0 = cellX + (blockX * blockWidth) + gap;
                int y0 = cellY + (blockY * blockHeight) + gap;
                int x1 = cellX + ((blockX + 1) * blockWidth) - gap;
                int y1 = blockY == 2
                    ? cellY + cellHeight - gap
                    : cellY + ((blockY + 1) * blockHeight) - gap;

                FillRect(pixels, width, height, x0, y0, x1, y1, colour);
            }
        }

        private static bool IsTeletextMosaicCharacter(byte character)
        {
            int value = character & 0x7F;
            return value is >= 0x20 and <= 0x3F
                or >= 0x60 and <= 0x7F;
        }

        private void RenderBitmapMode0(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);

            for (int y = 0; y < height; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < bytesPerRow; byteX++)
                {
                    byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                    for (int bit = 0; bit < 8; bit++)
                    {
                        int logicalColour = (value >> (7 - bit)) & 0x01;
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = xOffset + (byteX * 8) + bit;

                        WriteScaledPixel1x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode1(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);

            for (int y = 0; y < height; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < bytesPerRow; byteX++)
                {
                    byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                    for (int pixel = 0; pixel < 4; pixel++)
                    {
                        int logicalColour = DecodeTwoBitPixel(value, pixel);
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = xOffset + (((byteX * 4) + pixel) * 2);

                        WriteScaledPixel2x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode2(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);

            for (int y = 0; y < height; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < bytesPerRow; byteX++)
                {
                    byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                    for (int pixel = 0; pixel < 2; pixel++)
                    {
                        int logicalColour = DecodeFourBitPixel(value, pixel);
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = xOffset + (((byteX * 2) + pixel) * 4);

                        WriteScaledPixel4x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode4(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);

            for (int y = 0; y < height; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < bytesPerRow; byteX++)
                {
                    byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                    for (int bit = 0; bit < 8; bit++)
                    {
                        int logicalColour = (value >> (7 - bit)) & 0x01;
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = xOffset + (((byteX * 8) + bit) * 2);

                        WriteScaledPixel2x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode5(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);

            for (int y = 0; y < height; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < bytesPerRow; byteX++)
                {
                    byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                    for (int pixel = 0; pixel < 4; pixel++)
                    {
                        int logicalColour = DecodeTwoBitPixel(value, pixel);
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = xOffset + (((byteX * 4) + pixel) * 4);

                        WriteScaledPixel4x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private bool ShouldRenderMode4Mode5Split()
        {
            if (activeRasterEvents.Count == 0)
                return activeSawMode4 && activeSawMode5;

            bool hasMode4 = false;
            bool hasMode5 = false;

            foreach (VideoRasterEvent rasterEvent in activeRasterEvents)
            {
                hasMode4 |= rasterEvent.Mode == BbcScreenMode.Mode4;
                hasMode5 |= rasterEvent.Mode == BbcScreenMode.Mode5;

                if (hasMode4 && hasMode5)
                    return true;
            }

            return IsCarriedOverMode5SplitFrame();
        }

        private void RenderMode4Mode5Split(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);

            if (TryRenderCarriedOverMode5Split(display, height, bytesPerRow, xOffset))
                return;

            int eventIndex = 0;
            VideoRasterEvent state = activeRasterEvents.Count > 0
                ? activeRasterEvents[0]
                : new VideoRasterEvent(0, 0, 0, activeMode, activeUlaControl, activePaletteRegisters);

            for (int y = 0; y < height; y++)
            {
                while (eventIndex < activeRasterEvents.Count && activeRasterEvents[eventIndex].VisibleLine <= y)
                    state = activeRasterEvents[eventIndex++];

                if (state.Mode == BbcScreenMode.Mode5)
                    RenderMode5BitmapRow(display, y, bytesPerRow, xOffset, state.Palette);
                else
                    RenderMode4BitmapRow(display, y, bytesPerRow, xOffset, state.Palette);
            }

            if (TryGetFirstVisibleMode5Line(out int splitLine))
            {
                lastMode4Mode5SplitLine = splitLine;
                hasLastMode4Mode5SplitLine = true;
            }
        }

        private bool TryRenderCarriedOverMode5Split(Display display, int height, int bytesPerRow, int xOffset)
        {
            if (!IsCarriedOverMode5SplitFrame())
                return false;

            int splitLine = Math.Clamp(lastMode4Mode5SplitLine, 1, height - 1);
            for (int y = 0; y < height; y++)
            {
                if (y < splitLine)
                    RenderMode4BitmapRow(display, y, bytesPerRow, xOffset, activeMode4PaletteRegisters);
                else
                    RenderMode5BitmapRow(display, y, bytesPerRow, xOffset, activeMode5PaletteRegisters);
            }

            return true;
        }

        private bool IsCarriedOverMode5SplitFrame()
        {
            if (!hasLastMode4Mode5SplitLine || !activeSawMode5 || activeSawMode4 || activeMode != BbcScreenMode.Mode5)
                return false;

            return activeCrtcRegisters[CrtcHorizontalDisplayedRegister] == 32
                && GetBitmapDisplayStart() == 0x0C00;
        }

        private bool TryGetFirstVisibleMode5Line(out int splitLine)
        {
            splitLine = 0;

            foreach (VideoRasterEvent rasterEvent in activeRasterEvents)
            {
                if (rasterEvent.Mode != BbcScreenMode.Mode5 || rasterEvent.VisibleLine < 0)
                    continue;

                splitLine = rasterEvent.VisibleLine;
                return true;
            }

            return false;
        }

        private void RenderMode4BitmapRow(Display display, int y, int bytesPerRow, int xOffset, byte[] palette)
        {
            uint[] pixels = display.FrameBuffer;
            int targetY = y * 2;

            for (int byteX = 0; byteX < bytesPerRow; byteX++)
            {
                byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                for (int bit = 0; bit < 8; bit++)
                {
                    int logicalColour = (value >> (7 - bit)) & 0x01;
                    uint colour = GetPaletteColour(BbcScreenMode.Mode4, palette, logicalColour);
                    int targetX = xOffset + (((byteX * 8) + bit) * 2);

                    WriteScaledPixel2x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                }
            }
        }

        private void RenderMode5BitmapRow(Display display, int y, int bytesPerRow, int xOffset, byte[] palette)
        {
            uint[] pixels = display.FrameBuffer;
            int targetY = y * 2;

            for (int byteX = 0; byteX < bytesPerRow; byteX++)
            {
                byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow)];

                for (int pixel = 0; pixel < 4; pixel++)
                {
                    int logicalColour = DecodeTwoBitPixel(value, pixel);
                    uint colour = GetPaletteColour(BbcScreenMode.Mode5, palette, logicalColour);
                    int targetX = xOffset + (((byteX * 4) + pixel) * 4);

                    WriteScaledPixel4x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                }
            }
        }

        private void RenderMode7Cursor(Display display)
        {
            if (!IsCursorVisible())
                return;

            int cursorOffset = GetCursorDisplayOffset();

            if ((uint)cursorOffset >= (uint)(Mode7Columns * Mode7Rows))
                return;

            const int cellWidth = Display.DefaultWidth / Mode7Columns;
            const int cellHeight = Display.DefaultHeight / Mode7Rows;
            const int crtcScanlinesPerCell = 10;

            int column = cursorOffset % Mode7Columns;
            int row = cursorOffset / Mode7Columns;
            (int shapeStart, int shapeEnd) = GetCursorShape(crtcScanlinesPerCell);

            if (shapeEnd < shapeStart)
                return;

            int startX = column * cellWidth;
            int startY = (row * cellHeight) + (shapeStart * cellHeight / crtcScanlinesPerCell);
            int endX = Math.Min(startX + cellWidth, display.Width);
            int endY = Math.Min((row * cellHeight) + ((shapeEnd + 1) * cellHeight / crtcScanlinesPerCell), display.Height);

            uint[] pixels = display.FrameBuffer;
            for (int y = startY; y < endY; y++)
            {
                int offset = y * display.Width;
                for (int x = startX; x < endX; x++)
                    pixels[offset + x] ^= Foreground;
            }
        }

        private bool IsCursorVisible()
        {
            byte cursorStart = activeCrtcRegisters[CrtcCursorStartRegister];
            byte cursorMode = (byte)(cursorStart & 0x60);

            if (cursorMode == 0x20)
                return false;

            if (cursorMode != 0 && (Environment.TickCount64 / 320 & 1) == 0)
                return false;

            return true;
        }

        private (int Start, int End) GetCursorShape(int scanlinesPerCell)
        {
            if (activeCrtcRegisters[CrtcCursorStartRegister] == 0 && activeCrtcRegisters[CrtcCursorEndRegister] == 0)
                return (0, scanlinesPerCell - 1);

            int start = Math.Clamp(activeCrtcRegisters[CrtcCursorStartRegister] & 0x1F, 0, scanlinesPerCell - 1);
            int end = Math.Clamp(activeCrtcRegisters[CrtcCursorEndRegister] & 0x1F, 0, scanlinesPerCell - 1);
            return (start, end);
        }

        private int GetCursorDisplayOffset()
        {
            if (activeCrtcCursorAddressWritten)
            {
                int cursorAddress = ((activeCrtcRegisters[CrtcCursorHighRegister] & 0x3F) << 8)
                    | activeCrtcRegisters[CrtcCursorLowRegister];
                return (cursorAddress - GetMode7DisplayStartOffset()) & (Mode7ScreenBytes - 1);
            }

            ushort cursorAddressFallback = (ushort)(activeMemory[TextCursorAddressLow] | (activeMemory[TextCursorAddressHigh] << 8));
            return ((cursorAddressFallback - Mode7ScreenStart) - GetMode7DisplayStartOffset()) & (Mode7ScreenBytes - 1);
        }

        private byte ReadMode7DisplayCharacter(int row, int column)
        {
            int offset = (GetMode7DisplayStartOffset() + (row * Mode7Columns) + column) & (Mode7ScreenBytes - 1);
            return activeMemory[Mode7ScreenStart + offset];
        }

        private int GetMode7DisplayStartOffset()
        {
            int crtcStart = ((activeCrtcRegisters[CrtcDisplayStartHighRegister] & 0x3F) << 8)
                | activeCrtcRegisters[CrtcDisplayStartLowRegister];
            return crtcStart & (Mode7ScreenBytes - 1);
        }

        private int GetBitmapAddress(int y, int byteX, int bytesPerRow)
        {
            int crtcStart = GetBitmapDisplayStart();
            int characterRow = y >> 3;
            int rasterLine = y & 0x07;
            int memoryAddress = ((crtcStart + (characterRow * bytesPerRow) + byteX) << 3) + rasterLine;
            return WrapBitmapAddress(memoryAddress);
        }

        private int GetBitmapDisplayStart()
        {
            return ((activeCrtcRegisters[CrtcDisplayStartHighRegister] & 0x3F) << 8)
                | activeCrtcRegisters[CrtcDisplayStartLowRegister];
        }

        private int WrapBitmapAddress(int address)
        {
            if (address >= activeScreenMemoryStart && address < 0x8000)
                return address;

            int relative = address - activeScreenMemoryStart;
            relative %= activeScreenMemorySize;
            if (relative < 0)
                relative += activeScreenMemorySize;

            return activeScreenMemoryStart + relative;
        }

        private int GetBitmapBytesPerRow(int defaultBytesPerRow)
        {
            int displayed = activeCrtcRegisters[CrtcHorizontalDisplayedRegister];
            if (displayed <= 0)
                return defaultBytesPerRow;

            return Math.Clamp(displayed, 1, defaultBytesPerRow);
        }

        private int GetBitmapHeight()
        {
            int displayedRows = activeCrtcRegisters[CrtcVerticalDisplayedRegister];
            int scanlinesPerCharacter = (activeCrtcRegisters[CrtcScanLinesPerCharacterRegister] & 0x1F) + 1;
            int height = displayedRows * scanlinesPerCharacter;

            if (height <= 0)
                return BitmapHeight;

            return Math.Clamp(height, 1, BitmapHeight);
        }

        private int GetBitmapXOffset(int defaultBytesPerRow, int displayPixelsPerByte)
        {
            int bytesPerRow = GetBitmapBytesPerRow(defaultBytesPerRow);
            int unusedBytes = defaultBytesPerRow - bytesPerRow;
            if (unusedBytes <= 0)
                return 0;

            return unusedBytes * displayPixelsPerByte / 2;
        }

        private uint GetPaletteColour(int logicalColour)
        {
            int paletteIndex = activeMode switch
            {
                BbcScreenMode.Mode0 => (logicalColour & 0x01) == 0 ? 0x00 : 0x08,
                BbcScreenMode.Mode1 => ((logicalColour & 0x01) != 0 ? 0x02 : 0x00)
                    | ((logicalColour & 0x02) != 0 ? 0x08 : 0x00),
                BbcScreenMode.Mode2 => logicalColour & 0x0F,
                BbcScreenMode.Mode4 => (logicalColour & 0x01) == 0 ? 0x00 : 0x08,
                BbcScreenMode.Mode5 => ((logicalColour & 0x01) != 0 ? 0x02 : 0x00)
                    | ((logicalColour & 0x02) != 0 ? 0x08 : 0x00),
                _ => logicalColour & 0x0F
            };

            return ResolvePhysicalColour(activePaletteRegisters[paletteIndex]);
        }

        private uint GetPaletteColour(BbcScreenMode mode, byte[] palette, int logicalColour)
        {
            int paletteIndex = mode switch
            {
                BbcScreenMode.Mode4 => (logicalColour & 0x01) == 0 ? 0x00 : 0x08,
                BbcScreenMode.Mode5 => ((logicalColour & 0x01) != 0 ? 0x02 : 0x00)
                    | ((logicalColour & 0x02) != 0 ? 0x08 : 0x00),
                _ => logicalColour & 0x0F
            };

            return ResolvePhysicalColour(palette[paletteIndex]);
        }

        private void ResetPalette()
        {
            ResetPaletteArray(paletteRegisters);
            ResetPaletteArray(mode4PaletteRegisters);
            ResetPaletteArray(mode5PaletteRegisters);

            lastPaletteWrite = 0;
        }

        private static void ResetPaletteArray(byte[] palette)
        {
            for (int i = 0; i < palette.Length; i++)
                palette[i] = (byte)(i & 0x07);
        }

        private uint ResolvePhysicalColour(byte physicalColour)
        {
            int colour = physicalColour & 0x0F;

            if (colour >= 8 && (activeUlaControl & 0x01) != 0)
            {
                bool alternate = (Environment.TickCount64 / 500 & 1) != 0;
                colour = alternate ? (colour & 0x07) ^ 0x07 : colour & 0x07;
            }
            else
            {
                colour &= 0x07;
            }

            return BbcColours[colour];
        }

        private static byte DecodePhysicalColour(byte paletteRegisterValue)
        {
            return (byte)((paletteRegisterValue & 0x0F) ^ 0x07);
        }

        private static int DecodeTwoBitPixel(byte value, int pixel)
        {
            int highBit = 7 - pixel;
            int lowBit = 3 - pixel;
            return (((value >> highBit) & 0x01) << 1) | ((value >> lowBit) & 0x01);
        }

        private static int DecodeFourBitPixel(byte value, int pixel)
        {
            int offset = pixel == 0 ? 0 : 1;
            return ((value >> (1 - offset)) & 0x01)
                | (((value >> (3 - offset)) & 0x01) << 1)
                | (((value >> (5 - offset)) & 0x01) << 2)
                | (((value >> (7 - offset)) & 0x01) << 3);
        }

        private static void WriteScaledPixel1x2(uint[] pixels, int width, int height, int x, int y, uint colour)
        {
            if ((uint)x >= (uint)width || (uint)(y + 1) >= (uint)height)
                return;

            int offset = y * width + x;
            pixels[offset] = colour;
            pixels[offset + width] = colour;
        }

        private static void WriteScaledPixel2x2(uint[] pixels, int width, int height, int x, int y, uint colour)
        {
            if ((uint)(x + 1) >= (uint)width || (uint)(y + 1) >= (uint)height)
                return;

            int offset = y * width + x;
            pixels[offset] = colour;
            pixels[offset + 1] = colour;
            pixels[offset + width] = colour;
            pixels[offset + width + 1] = colour;
        }

        private static void WriteScaledPixel4x2(uint[] pixels, int width, int height, int x, int y, uint colour)
        {
            if ((uint)(x + 3) >= (uint)width || (uint)(y + 1) >= (uint)height)
                return;

            int offset = y * width + x;
            for (int i = 0; i < 4; i++)
            {
                pixels[offset + i] = colour;
                pixels[offset + width + i] = colour;
            }
        }

        private static void FillRect(uint[] pixels, int width, int height, int x0, int y0, int x1, int y1, uint colour)
        {
            x0 = Math.Clamp(x0, 0, width);
            x1 = Math.Clamp(x1, 0, width);
            y0 = Math.Clamp(y0, 0, height);
            y1 = Math.Clamp(y1, 0, height);

            for (int y = y0; y < y1; y++)
            {
                int offset = y * width;
                for (int x = x0; x < x1; x++)
                    pixels[offset + x] = colour;
            }
        }

        private static BbcScreenMode DecodeModeFromUlaControl(byte control)
        {
            if ((control & UlaTeletext) != 0)
                return BbcScreenMode.Mode7;

            byte modeBits = (byte)(control & (UlaClockHigh | UlaCharactersPerLineMask));
            return modeBits switch
            {
                0x00 => BbcScreenMode.Mode2,
                0x04 => BbcScreenMode.Mode5,
                0x08 => BbcScreenMode.Mode4,
                0x0C => BbcScreenMode.Mode0,
                0x10 => BbcScreenMode.Mode2,
                0x14 => BbcScreenMode.Mode2,
                0x18 => BbcScreenMode.Mode1,
                0x1C => BbcScreenMode.Mode0,
                _ => BbcScreenMode.Unknown
            };
        }

        private void AddRasterEvent(int frameCpuCycle)
        {
            int scanline = Math.Clamp(frameCpuCycle, 0, VideoFrameCpuCycles - 1) * VideoFrameScanlines / VideoFrameCpuCycles;
            int visibleLine = scanline - VisibleStartScanline;
            rasterEvents.Add(new VideoRasterEvent(frameCpuCycle, scanline, visibleLine, CurrentMode, UlaControl, paletteRegisters));
        }

        private readonly struct VideoRasterEvent
        {
            public VideoRasterEvent(int frameCpuCycle, int scanline, int visibleLine, BbcScreenMode mode, byte ulaControl, byte[] palette)
            {
                FrameCpuCycle = frameCpuCycle;
                Scanline = scanline;
                VisibleLine = visibleLine;
                Mode = mode;
                UlaControl = ulaControl;
                Palette = new byte[PaletteRegisterCount];
                Array.Copy(palette, Palette, Palette.Length);
            }

            public int FrameCpuCycle { get; }

            public int Scanline { get; }

            public int VisibleLine { get; }

            public BbcScreenMode Mode { get; }

            public byte UlaControl { get; }

            public byte[] Palette { get; }
        }

        private sealed class TeletextState
        {
            public bool GraphicsMode { get; set; }
            public bool SeparatedGraphics { get; set; }
            public bool HoldGraphics { get; set; }
            public bool DoubleHeight { get; set; }
            public bool Flashing { get; set; }
            public byte? HeldMosaic { get; set; }
            public uint ForegroundColour { get; set; } = Foreground;
            public uint BackgroundColour { get; set; } = Background;
        }
    }

    /// <summary>BBC Micro display modes supported by the video component.</summary>
    public enum BbcScreenMode
    {
        /// <summary>Unknown or currently undecodable video mode.</summary>
        Unknown = -1,

        /// <summary>Mode 0: 640 x 256, 2 logical colours.</summary>
        Mode0 = 0,

        /// <summary>Mode 1: 320 x 256, 4 logical colours.</summary>
        Mode1 = 1,

        /// <summary>Mode 2: 160 x 256, 16 logical colours.</summary>
        Mode2 = 2,

        /// <summary>Mode 3: 80 column text. Currently detected as the mode 0 ULA group.</summary>
        Mode3 = 3,

        /// <summary>Mode 4: 320 x 256, 2 logical colours.</summary>
        Mode4 = 4,

        /// <summary>Mode 5: 160 x 256, 4 logical colours.</summary>
        Mode5 = 5,

        /// <summary>Mode 6: 40 column text. Currently detected as the mode 4 ULA group.</summary>
        Mode6 = 6,

        /// <summary>Mode 7 teletext/text display. This is the current baseline renderer.</summary>
        Mode7 = 7
    }
}
