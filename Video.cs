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
        private const int BitmapBytesPerRow10K = 40;
        private const int BitmapBytesPerRow20K = 80;
        private const int CrtcRegisterCount = 32;
        private const int PaletteRegisterCount = 16;
        private const byte UlaTeletext = 0x02;
        private const byte UlaCharactersPerLineMask = 0x0C;
        private const byte UlaClockHigh = 0x10;
        private const byte UlaCursorWidthMask = 0xE0;
        private const byte UlaCursorMode0Group = 0x80;
        private const byte UlaCursorMode1Group = 0xC0;
        private const byte UlaCursorMode2 = 0xE0;
        private const byte UlaCursorMode7 = 0x40;
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
        private byte selectedCrtcRegister;
        private byte lastPaletteWrite;

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
        }

        /// <summary>Resets video device state.</summary>
        public void Reset()
        {
            Array.Clear(crtcRegisters);
            ResetPalette();
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
                0xFE20 or 0xFE22 => UlaControl,
                0xFE21 or 0xFE23 => lastPaletteWrite,
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
                case 0xFE22:
                    UlaControl = value;
                    CurrentMode = DecodeModeFromUlaControl(value);
                    break;

                case 0xFE21:
                case 0xFE23:
                    lastPaletteWrite = value;
                    paletteRegisters[(value >> 4) & 0x0F] = DecodePhysicalColour(value);
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

        private void RenderBitmapMode0(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);

            for (int y = 0; y < BitmapHeight; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < BitmapBytesPerRow20K; byteX++)
                {
                    byte value = memory[GetBitmapAddress(y, byteX, BitmapBytesPerRow20K)];

                    for (int bit = 0; bit < 8; bit++)
                    {
                        int logicalColour = (value >> (7 - bit)) & 0x01;
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = (byteX * 8) + bit;

                        WriteScaledPixel1x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode1(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);

            for (int y = 0; y < BitmapHeight; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < BitmapBytesPerRow20K; byteX++)
                {
                    byte value = memory[GetBitmapAddress(y, byteX, BitmapBytesPerRow20K)];

                    for (int pixel = 0; pixel < 4; pixel++)
                    {
                        int logicalColour = DecodeTwoBitPixel(value, pixel);
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = ((byteX * 4) + pixel) * 2;

                        WriteScaledPixel2x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode2(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);

            for (int y = 0; y < BitmapHeight; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < BitmapBytesPerRow20K; byteX++)
                {
                    byte value = memory[GetBitmapAddress(y, byteX, BitmapBytesPerRow20K)];

                    for (int pixel = 0; pixel < 2; pixel++)
                    {
                        int logicalColour = DecodeFourBitPixel(value, pixel);
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = ((byteX * 2) + pixel) * 4;

                        WriteScaledPixel4x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode4(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);

            for (int y = 0; y < BitmapHeight; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < BitmapBytesPerRow10K; byteX++)
                {
                    byte value = memory[GetBitmapAddress(y, byteX, BitmapBytesPerRow10K)];

                    for (int bit = 0; bit < 8; bit++)
                    {
                        int logicalColour = (value >> (7 - bit)) & 0x01;
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = ((byteX * 8) + bit) * 2;

                        WriteScaledPixel2x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
        }

        private void RenderBitmapMode5(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            Array.Fill(pixels, Background);

            for (int y = 0; y < BitmapHeight; y++)
            {
                int targetY = y * 2;

                for (int byteX = 0; byteX < BitmapBytesPerRow10K; byteX++)
                {
                    byte value = memory[GetBitmapAddress(y, byteX, BitmapBytesPerRow10K)];

                    for (int pixel = 0; pixel < 4; pixel++)
                    {
                        int logicalColour = DecodeTwoBitPixel(value, pixel);
                        uint colour = GetPaletteColour(logicalColour);
                        int targetX = ((byteX * 4) + pixel) * 4;

                        WriteScaledPixel4x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                    }
                }
            }
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

        private int GetBitmapAddress(int y, int byteX, int bytesPerRow)
        {
            int crtcStart = ((crtcRegisters[12] & 0x3F) << 8) | crtcRegisters[13];
            int characterRow = y >> 3;
            int rasterLine = y & 0x07;
            int memoryAddress = ((crtcStart + (characterRow * bytesPerRow) + byteX) << 3) + rasterLine;
            return memoryAddress & 0x7FFF;
        }

        private uint GetPaletteColour(int logicalColour)
        {
            int paletteIndex = CurrentMode switch
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

            return ResolvePhysicalColour(paletteRegisters[paletteIndex]);
        }

        private void ResetPalette()
        {
            for (int i = 0; i < paletteRegisters.Length; i++)
                paletteRegisters[i] = (byte)(i & 0x07);

            lastPaletteWrite = 0;
        }

        private uint ResolvePhysicalColour(byte physicalColour)
        {
            int colour = physicalColour & 0x0F;

            if (colour >= 8 && (UlaControl & 0x01) != 0)
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
            return ((value >> highBit) & 0x01) | (((value >> lowBit) & 0x01) << 1);
        }

        private static int DecodeFourBitPixel(byte value, int pixel)
        {
            int offset = pixel == 0 ? 0 : 1;
            return ((value >> (7 - offset)) & 0x01)
                | (((value >> (3 - offset)) & 0x01) << 1)
                | (((value >> (5 - offset)) & 0x01) << 2)
                | (((value >> (1 - offset)) & 0x01) << 3);
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

        private static BbcScreenMode DecodeModeFromUlaControl(byte control)
        {
            byte cursorWidth = (byte)(control & UlaCursorWidthMask);

            if ((control & UlaTeletext) != 0 || cursorWidth == UlaCursorMode7)
                return BbcScreenMode.Mode7;

            if (cursorWidth == UlaCursorMode2)
                return BbcScreenMode.Mode2;

            if (cursorWidth == UlaCursorMode1Group)
                return (control & UlaClockHigh) != 0 ? BbcScreenMode.Mode1 : BbcScreenMode.Mode5;

            if (cursorWidth == UlaCursorMode0Group)
            {
                byte columns = (byte)(control & UlaCharactersPerLineMask);

                if ((control & UlaClockHigh) != 0 || columns == 0x0C)
                    return BbcScreenMode.Mode0;

                return BbcScreenMode.Mode4;
            }

            return BbcScreenMode.Unknown;
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
