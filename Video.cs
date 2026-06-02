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
        private const int CrtcRegisterCount = 32;
        private const int PaletteRegisterCount = 16;

        private readonly byte[] memory;
        private readonly ushort osRomStart;
        private readonly byte[] crtcRegisters = new byte[CrtcRegisterCount];
        private readonly byte[] paletteRegisters = new byte[PaletteRegisterCount];
        private byte selectedCrtcRegister;

        /// <summary>Gets the currently selected BBC screen mode.</summary>
        public BbcScreenMode CurrentMode { get; private set; } = BbcScreenMode.Mode7;

        /// <summary>Gets the current Video ULA control register value.</summary>
        public byte UlaControl { get; private set; }

        /// <summary>Initializes a new video component.</summary>
        /// <param name="memory">The emulator's 64 KiB CPU-visible memory.</param>
        /// <param name="osRomStart">The start address of the OS ROM font data used by the temporary mode 7 renderer.</param>
        public Video(byte[] memory, ushort osRomStart)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
            this.osRomStart = osRomStart;
        }

        /// <summary>Resets video device state.</summary>
        public void Reset()
        {
            Array.Clear(crtcRegisters);
            Array.Clear(paletteRegisters);
            selectedCrtcRegister = 0;
            CurrentMode = BbcScreenMode.Mode7;
            UlaControl = 0;
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
                0xFE20 => UlaControl,
                0xFE21 => paletteRegisters[0],
                _ => 0x00
            };
        }

        /// <summary>Writes a byte to the CRTC or Video ULA register area.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The value to write.</param>
        public void WriteSheila(ushort address, byte value)
        {
            switch (address)
            {
                case 0xFE00:
                    selectedCrtcRegister = (byte)(value & 0x1F);
                    break;

                case 0xFE01:
                    crtcRegisters[selectedCrtcRegister & 0x1F] = value;
                    break;

                case 0xFE20:
                    UlaControl = value;
                    break;

                case 0xFE21:
                    paletteRegisters[(value >> 4) & 0x0F] = (byte)(value & 0x0F);
                    break;
            }
        }

        /// <summary>Renders the current video frame into the display framebuffer.</summary>
        /// <param name="display">The SDL-backed display to render into.</param>
        public void Render(Display display)
        {
            switch (CurrentMode)
            {
                case BbcScreenMode.Mode7:
                    RenderMode7TextScreen(display);
                    break;

                default:
                    RenderMode7TextScreen(display);
                    break;
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
            const int glyphWidth = 8;
            const int glyphHeight = 8;
            const int cellWidth = Display.DefaultWidth / Mode7Columns;
            const int cellHeight = Display.DefaultHeight / Mode7Rows;
            const int xScale = 2;
            const int yScale = 2;
            const int glyphXOffset = 0;
            const int glyphYOffset = 2;

            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);

            for (int row = 0; row < Mode7Rows; row++)
            {
                int cellY = row * cellHeight;

                for (int column = 0; column < Mode7Columns; column++)
                {
                    byte character = ReadMode7DisplayCharacter(row, column);

                    if (character < 32 || character > 127)
                        character = 32;

                    int glyphAddress = osRomStart + ((character - 32) * glyphHeight);
                    int cellX = column * cellWidth;

                    for (int glyphY = 0; glyphY < glyphHeight; glyphY++)
                    {
                        byte bits = memory[glyphAddress + glyphY];

                        for (int glyphX = 0; glyphX < glyphWidth; glyphX++)
                        {
                            if ((bits & (0x80 >> glyphX)) == 0)
                                continue;

                            int pixelX = cellX + glyphXOffset + (glyphX * xScale);
                            int pixelY = cellY + glyphYOffset + (glyphY * yScale);

                            for (int yy = 0; yy < yScale; yy++)
                            {
                                int y = pixelY + yy;
                                if ((uint)y >= (uint)display.Height)
                                    continue;

                                int offset = y * display.Width;
                                for (int xx = 0; xx < xScale; xx++)
                                {
                                    int x = pixelX + xx;
                                    if ((uint)x < (uint)display.Width)
                                        pixels[offset + x] = Foreground;
                                }
                            }
                        }
                    }
                }
            }

            RenderMode7Cursor(display);
        }

        private void RenderMode7Cursor(Display display)
        {
            if ((Environment.TickCount64 / 320 & 1) == 0)
                return;

            ushort cursorAddress = (ushort)(memory[TextCursorAddressLow] | (memory[TextCursorAddressHigh] << 8));
            int cursorOffset = ((cursorAddress - Mode7ScreenStart) - GetMode7DisplayStartOffset()) & (Mode7ScreenBytes - 1);

            if ((uint)cursorOffset >= (uint)(Mode7Columns * Mode7Rows))
                return;

            const int cellWidth = Display.DefaultWidth / Mode7Columns;
            const int cellHeight = Display.DefaultHeight / Mode7Rows;

            int column = cursorOffset % Mode7Columns;
            int row = cursorOffset / Mode7Columns;
            int startX = column * cellWidth;
            int startY = row * cellHeight;
            int endX = Math.Min(startX + cellWidth, display.Width);
            int endY = Math.Min(startY + cellHeight, display.Height);

            uint[] pixels = display.FrameBuffer;
            for (int y = startY; y < endY; y++)
            {
                int offset = y * display.Width;
                for (int x = startX; x < endX; x++)
                    pixels[offset + x] ^= Foreground;
            }
        }

        private byte ReadMode7DisplayCharacter(int row, int column)
        {
            int offset = (GetMode7DisplayStartOffset() + (row * Mode7Columns) + column) & (Mode7ScreenBytes - 1);
            return memory[Mode7ScreenStart + offset];
        }

        private int GetMode7DisplayStartOffset()
        {
            int crtcStart = ((crtcRegisters[12] & 0x3F) << 8) | crtcRegisters[13];
            return crtcStart & (Mode7ScreenBytes - 1);
        }
    }

    /// <summary>BBC Micro display modes supported by the video component.</summary>
    public enum BbcScreenMode
    {
        /// <summary>Mode 7 teletext/text display. This is the current baseline renderer.</summary>
        Mode7 = 7
    }
}
