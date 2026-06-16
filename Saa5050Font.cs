// ============================================================================
// Project:     BBC
// File:        Saa5050Font.cs
// Description: Mullard SAA5050 teletext alphanumeric glyph renderer.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              SAA5050 glyph data adapted from Bedstead/Teletext50, CC0.
// ============================================================================

namespace BBC
{
    /// <summary>
    /// Draws the SAA5050 English teletext alphanumeric character set used by BBC Micro MODE 7.
    /// </summary>
    internal static class Saa5050Font
    {
        private const int GlyphWidth = 5;
        private const int SourceCellWidth = 6;
        private const int SourceCellHeight = 10;
        private const int GlyphRowsPerCharacter = 10;
        private const int RoundedWidth = SourceCellWidth * 2;
        private const int RoundedHeight = SourceCellHeight * 2;
        private const int GlyphXOffset = 1;
        private const int GlyphYOffset = 2;
        private const int TeletextCellWidth = 16;

        private const string GlyphRowsEncoded =
            "AAAAAAAAAAEEEEEAEAAAKKKAAAAAAAGJIcIIfAAAOVUOFVOAAAYZCEITDAAAIUUIVSNAAAEEEAAAAAAA" +
            "CEIIIECAAAIECCCEIAAAEVOEOVEAAAAEEfEEAAAAAAAAAEEIAAAAAOAAAAAAAAAAAAEAAAABCEIQAAAA" +
            "EKRRRKEAAAEMEEEEOAAAORBGIQfAAAfBCGBROAAACGKSfCCAAAfQeBBROAAAGIQeRROAAAfBCEIIIAAA" +
            "ORRORROAAAORRPBCMAAAAAEAAAEAAAAAEAAEEIAACEIQIECAAAAAfAfAAAAAIECBCEIAAAORCEEAEAAA" +
            "ORXVXQOAAAEKRRfRRAAAeRReRReAAAORQQQROAAAeRRRRReAAAfQQeQQfAAAfQQeQQQAAAORQQTRPAAA" +
            "RRRfRRRAAAOEEEEEOAAABBBBBROAAARSUYUSRAAAQQQQQQfAAARbVVRRRAAARRZVTRRAAAORRRRROAAA" +
            "eRReQQQAAAORRRVSNAAAeRReUSRAAAORQOBROAAAfEEEEEEAAARRRRRROAAARRRKKEEAAARRRVVVKAAA" +
            "RRKEKRRAAARRKEEEEAAAfBCEIQfAAAAEIfIEAAAAQQQQWBCEHAAECfCEAAAAAEOVEEAAAAKKfKfKKAAA" +
            "AAAfAAAAAAAAOBPRPAAAQQeRRReAAAAAPQQQPAAABBPRRRPAAAAAORfQOAAACEEOEEEAAAAAPRRRPBOA" +
            "QQeRRRRAAAEAMEEEOAAAEAEEEEEEIAIIJKMKJAAAMEEEEEOAAAAAaVVVVAAAAAeRRRRAAAAAORRROAAA" +
            "AAeRRReQQAAAPRRRPBBAAALMIIIAAAAAPQOBeAAAEEOEEECAAAAARRRRPAAAAARRKKEAAAAARRVVKAAA" +
            "AARKEKRAAAAARRRRPBOAAAfCEIfAAAIIIIJDFHBAKKKKKKKAAAYEYEZDFHBAAEAfAEAAAAfffffffAAA";

        private static readonly byte[] GlyphRows = BuildGlyphRows();

        private static byte[] BuildGlyphRows()
        {
            if (GlyphRowsEncoded.Length != 96 * GlyphRowsPerCharacter)
                throw new InvalidOperationException("SAA5050 glyph table must contain 96 ten-row characters.");

            byte[] rows = new byte[GlyphRowsEncoded.Length];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = DecodeGlyphRow(GlyphRowsEncoded[i]);

            return rows;
        }

        private static byte DecodeGlyphRow(char value)
        {
            if (value is >= 'A' and <= 'Z')
                return (byte)(value - 'A');

            if (value is >= 'a' and <= 'f')
                return (byte)(26 + value - 'a');

            throw new InvalidOperationException("SAA5050 glyph table contains an invalid row code.");
        }

        public static ushort GetAlphanumericRowMask(byte character, int row)
        {
            character = (byte)(character & 0x7F);
            if (character < 32)
                character = 32;

            row -= GlyphYOffset;
            if ((uint)row >= RoundedHeight)
                return 0;

            ushort glyphRow = GetRoundedRow((character - 32) * GlyphRowsPerCharacter, row);
            ushort result = 0;

            for (int outputX = 0; outputX < RoundedWidth; outputX++)
            {
                if ((glyphRow & (0x800 >> outputX)) == 0)
                    continue;

                int pixel = GlyphXOffset + outputX;
                result |= (ushort)(1 << (15 - pixel));
                if (pixel + 1 < TeletextCellWidth)
                    result |= (ushort)(1 << (14 - pixel));
            }

            return result;
        }

        public static ushort GetMosaicRowMask(byte character, int row, bool separated)
        {
            int value = character & 0x7F;
            int pattern = (value & 0x1F) | ((value & 0x40) >> 1);
            row = Math.Clamp(row, 0, RoundedHeight - 1);
            ushort result = 0;

            for (int outputX = 0; outputX < TeletextCellWidth; outputX++)
            {
                int block = (row < 6 ? 0 : row < 14 ? 2 : 4) + (outputX < 8 ? 0 : 1);
                if ((pattern & (1 << block)) == 0)
                    continue;

                if (separated)
                {
                    int blockStartX = outputX < 8 ? 0 : 8;
                    int blockStartY = row < 6 ? 0 : row < 14 ? 6 : 14;
                    int blockEndX = blockStartX + 7;
                    int blockEndY = row < 6 ? 5 : row < 14 ? 13 : 19;
                    if (outputX == blockStartX || outputX == blockEndX || row == blockStartY || row == blockEndY)
                        continue;
                }

                result |= (ushort)(1 << (15 - outputX));
            }

            return result;
        }

        private static ushort GetRoundedRow(int glyphOffset, int outputY)
        {
            int sourceY = outputY >> 1;
            int phaseY = outputY & 1;
            ushort result = 0;

            for (int outputX = 0; outputX < RoundedWidth; outputX++)
            {
                int sourceX = outputX >> 1;
                int phaseX = outputX & 1;

                if (IsSourcePixelSet(glyphOffset, sourceX, sourceY) ||
                    IsRoundedDiagonalPixelSet(glyphOffset, sourceX, sourceY, phaseX, phaseY))
                    result |= (ushort)(0x800 >> outputX);
            }

            return result;
        }

        private static bool IsSourcePixelSet(int glyphOffset, int sourceX, int sourceY)
        {
            if ((uint)sourceX >= GlyphWidth || (uint)sourceY >= SourceCellHeight)
                return false;

            byte row = GlyphRows[glyphOffset + sourceY];
            return (row & (0x10 >> sourceX)) != 0;
        }

        private static bool IsRoundedDiagonalPixelSet(int glyphOffset, int sourceX, int sourceY, int phaseX, int phaseY)
        {
            if ((uint)sourceX >= SourceCellWidth || (uint)sourceY >= SourceCellHeight)
                return false;

            if (IsSourcePixelSet(glyphOffset, sourceX, sourceY))
                return false;

            bool left = IsSourcePixelSet(glyphOffset, sourceX - 1, sourceY);
            bool right = IsSourcePixelSet(glyphOffset, sourceX + 1, sourceY);
            bool up = IsSourcePixelSet(glyphOffset, sourceX, sourceY - 1);
            bool down = IsSourcePixelSet(glyphOffset, sourceX, sourceY + 1);
            bool upperLeft = IsSourcePixelSet(glyphOffset, sourceX - 1, sourceY - 1);
            bool upperRight = IsSourcePixelSet(glyphOffset, sourceX + 1, sourceY - 1);
            bool lowerLeft = IsSourcePixelSet(glyphOffset, sourceX - 1, sourceY + 1);
            bool lowerRight = IsSourcePixelSet(glyphOffset, sourceX + 1, sourceY + 1);

            if (left && up && !upperLeft && phaseX == 0 && phaseY == 0)
                return true;

            if (right && up && !upperRight && phaseX == 1 && phaseY == 0)
                return true;

            if (left && down && !lowerLeft && phaseX == 0 && phaseY == 1)
                return true;

            if (right && down && !lowerRight && phaseX == 1 && phaseY == 1)
                return true;

            return false;
        }

    }
}
