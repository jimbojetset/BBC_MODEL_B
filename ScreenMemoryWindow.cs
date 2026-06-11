// ============================================================================
// Project:     BBC
// File:        ScreenMemoryWindow.cs
// Description: BBC video RAM window selected by the system VIA addressable latch.
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
    /// Describes the BBC video RAM wrap window and hardware scroll mapping selected by the system VIA.
    /// </summary>
    public readonly record struct ScreenMemoryWindow(int Start, int Size, int HardwareScroll, int AddressSubtract);
}
