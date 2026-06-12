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
        private const int CrtcHorizontalTotalRegister = 0;
        private const int CrtcHorizontalDisplayedRegister = 1;
        private const int CrtcVerticalTotalRegister = 4;
        private const int CrtcVerticalAdjustRegister = 5;
        private const int CrtcVerticalDisplayedRegister = 6;
        private const int CrtcVerticalSyncRegister = 7;
        private const int CrtcInterlaceAndSkewRegister = 8;
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
        private ScreenMemoryWindow screenMemoryWindow = new ScreenMemoryWindow(0x3000, 0x5000, 2, 10);
        private ScreenMemoryWindow frameScreenMemoryWindow = new ScreenMemoryWindow(0x3000, 0x5000, 2, 10);
        private ScreenMemoryWindow activeScreenMemoryWindow = new ScreenMemoryWindow(0x3000, 0x5000, 2, 10);
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
        private bool frameInterlaceFieldOdd;
        private bool activeInterlaceFieldOdd;
        private int frameNumber;
        private int activeFrameNumber;
        private int lastMode4Mode5SplitLine = 192;
        private bool hasLastMode4Mode5SplitLine;
        private int capturedFrameCounter;

        // Stability tracking: the CRTC-derived vsync period and the gapped-text-mode decision
        // both fire only after the relevant CRTC registers have been observed unchanged for an
        // entire frame, so a single mid-frame rupture cannot retarget rendering or vsync.
        private int previousFrameCrtcSignature;
        private int stableCrtcSignature;
        private int previousFrameR9;
        private int stableR9;

        /// <summary>Gets the currently selected BBC screen mode.</summary>
        public BbcScreenMode CurrentMode { get; private set; } = BbcScreenMode.Mode7;

        /// <summary>Gets the current Video ULA control register value.</summary>
        public byte UlaControl { get; private set; }

        /// <summary>Gets whether a stable, displayable frame snapshot is available.</summary>
        public bool HasStableDisplaySnapshot
        {
            get
            {
                lock (frameSnapshotLock)
                {
                    return hasFrameSnapshot
                        && stableCrtcSignature != 0
                        && frameCrtcRegisters[CrtcHorizontalDisplayedRegister] > 0
                        && frameCrtcRegisters[CrtcVerticalDisplayedRegister] > 0;
                }
            }
        }

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
        public Video(byte[] memory)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
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
            lock (frameSnapshotLock)
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
                frameInterlaceFieldOdd = false;
                activeInterlaceFieldOdd = false;
                frameNumber = 0;
                activeFrameNumber = 0;
                rasterEvents.Clear();
                frameRasterEvents.Clear();
                activeRasterEvents.Clear();
                lastMode4Mode5SplitLine = 192;
                hasLastMode4Mode5SplitLine = false;
                capturedFrameCounter = 0;
                previousFrameCrtcSignature = 0;
                stableCrtcSignature = 0;
                previousFrameR9 = 0;
                stableR9 = 0;
                CurrentMode = BbcScreenMode.Mode7;
                UlaControl = 0;
                screenMemoryWindow = new ScreenMemoryWindow(0x3000, 0x5000, 2, 10);
                frameScreenMemoryWindow = screenMemoryWindow;
                frameUlaControl = UlaControl;
                frameMode = CurrentMode;
                AddRasterEvent(0);
            }
        }

        /// <summary>Discards the current frame snapshot so rendering can wait for a fresh coherent frame.</summary>
        public void InvalidateFrameSnapshot()
        {
            lock (frameSnapshotLock)
            {
                hasFrameSnapshot = false;
                frameRasterEvents.Clear();
                previousFrameCrtcSignature = 0;
                stableCrtcSignature = 0;
                previousFrameR9 = 0;
                stableR9 = 0;
            }
        }

        /// <summary>Sets the BBC video RAM window used by hardware scrolling wraparound.</summary>
        /// <param name="window">The selected video RAM window and BBC hardware scroll mapping.</param>
        public void SetScreenMemoryWindow(ScreenMemoryWindow window)
        {
            if (window.Start < 0 || window.Start >= memory.Length)
                throw new ArgumentOutOfRangeException(nameof(window));

            if (window.Size <= 0 || window.Start + window.Size > 0x8000)
                throw new ArgumentOutOfRangeException(nameof(window));

            screenMemoryWindow = window;
        }

        /// <summary>Computes the frame period implied by the live CRTC programming, expressed in 1 MHz peripheral cycles.</summary>
        /// <remarks>
        /// The 6845 generates one frame every:
        ///   characters_per_line * total_lines characters
        /// where total_lines = (R4 + 1) * (R9 + 1) + R5.
        /// The Video ULA's "high clock" bit selects 2 MHz (1 byte per char) vs 1 MHz (2 bytes per char) character rate.
        /// On the BBC, the 6845 is clocked at the character rate so one CRTC character equals 1 (1 MHz mode)
        /// or 0.5 (2 MHz mode) microseconds at the peripheral 1 MHz clock.
        /// Returns 0 if the registers are clearly unprogrammed or out of range, so callers fall back to default 50 Hz.
        /// </remarks>
        public int GetCrtcFramePeriodPeripheralCycles()
        {
            int horizontalTotal = crtcRegisters[CrtcHorizontalTotalRegister];   // R0: characters per scanline minus 1
            int verticalTotal = crtcRegisters[CrtcVerticalTotalRegister];       // R4: character rows minus 1
            int scanlinesPerRow = GetCrtcScanlinesPerCharacter(crtcRegisters); // R9 + 1
            int totalScanlines = GetCrtcTotalScanlines(crtcRegisters);

            if (horizontalTotal <= 0 || verticalTotal <= 0 || totalScanlines <= 0 || scanlinesPerRow <= 0)
                return 0;

            // Stability gate: do not push a CRTC-derived period unless the current programming
            // matches what we saw at the end of the previous frame. Mid-frame ruptures that
            // briefly retarget R0/R4/R5/R9 (Tricky's Frogger trick) must not steer vsync.
            if (stableCrtcSignature == 0 || ComputeCrtcSignature() != stableCrtcSignature)
                return 0;

            int charactersPerScanline = horizontalTotal + 1;
            int totalCharacters = charactersPerScanline * totalScanlines;

            // Determine character clock: high clock bit on the ULA selects 2 MHz character rate.
            bool highClock = (UlaControl & UlaClockHigh) != 0;
            // peripheral cycles per CRTC character: 1 at 1 MHz character clock, 0.5 at 2 MHz.
            // Express the result in integer 1 MHz cycles: high-clock => totalCharacters / 2 (rounded).
            int peripheralCycles = highClock ? (totalCharacters + 1) / 2 : totalCharacters;
            return peripheralCycles;
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
                frameScreenMemoryWindow = screenMemoryWindow;
                frameSawMode4 = sawMode4ThisFrame;
                frameSawMode5 = sawMode5ThisFrame;
                frameNumber = capturedFrameCounter++;
                frameInterlaceFieldOdd = (frameNumber & 1) != 0;
                frameRasterEvents.Clear();
                frameRasterEvents.AddRange(rasterEvents);
                rasterEvents.Clear();
                AddRasterEvent(0);
                sawMode4ThisFrame = CurrentMode == BbcScreenMode.Mode4;
                sawMode5ThisFrame = CurrentMode == BbcScreenMode.Mode5;
                hasFrameSnapshot = true;

                // Stability gating: only treat the CRTC programming as "settled" when the same
                // signature has been observed for two consecutive frames. This prevents mid-frame
                // ruptures (e.g. Tricky's per-row R12/R13 writes in Frogger) from being mistaken
                // for a permanent reprogramming.
                int currentSignature = ComputeCrtcSignature();
                if (currentSignature == previousFrameCrtcSignature)
                    stableCrtcSignature = currentSignature;
                previousFrameCrtcSignature = currentSignature;

                int currentR9 = (crtcRegisters[CrtcScanLinesPerCharacterRegister] & 0x1F) + 1;
                if (currentR9 == previousFrameR9)
                    stableR9 = currentR9;
                previousFrameR9 = currentR9;
            }
        }

        private int ComputeCrtcSignature()
        {
            // 32-bit packing of the CRTC registers that determine frame timing and layout.
            // R0 (horizontal total), R4 (vertical total), R5 (vertical adjust), R6 (vertical displayed),
            // R9 (scanlines per row), plus the ULA high-clock bit. Anything else can change without
            // invalidating the period or gapped-text decision.
            int sig = crtcRegisters[CrtcHorizontalTotalRegister];
            sig |= crtcRegisters[CrtcVerticalTotalRegister] << 8;
            sig |= (crtcRegisters[CrtcVerticalAdjustRegister] & 0x1F) << 16;
            sig |= (crtcRegisters[CrtcVerticalDisplayedRegister] & 0x7F) << 21;
            sig |= ((crtcRegisters[CrtcScanLinesPerCharacterRegister] & 0x1F) << 28);
            sig ^= (UlaControl & UlaClockHigh) != 0 ? unchecked((int)0x80000000) : 0;
            return sig;
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
                    {
                        int regIndex = selectedCrtcRegister & 0x1F;
                        crtcRegisters[regIndex] = value;
                        if (regIndex is CrtcCursorHighRegister or CrtcCursorLowRegister)
                            crtcCursorAddressWritten = true;
                        // Mid-frame writes to display-start (R12/R13), scanline-per-row (R9),
                        // vertical-displayed (R6), or
                        // horizontal-displayed (R1) change rendering partway down the screen; capture
                        // a raster event so the renderer can split correctly. R1 mid-frame is what
                        // enables Tricky's per-character-row "vertical rupture" trick (used in Frogger).
                        // R6/R9 writes are flagged as well because games can use them to alter
                        // the effective visible height or reorder character rows.
                        if (regIndex is CrtcDisplayStartHighRegister or CrtcDisplayStartLowRegister
                            or CrtcScanLinesPerCharacterRegister or CrtcHorizontalDisplayedRegister
                            or CrtcVerticalDisplayedRegister)
                            AddRasterEvent(frameCpuCycle, crtcAddressLatch: true);
                    }
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
                    activeScreenMemoryWindow = frameScreenMemoryWindow;
                    activeSawMode4 = frameSawMode4;
                    activeSawMode5 = frameSawMode5;
                    activeInterlaceFieldOdd = frameInterlaceFieldOdd;
                    activeFrameNumber = frameNumber;
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
                    activeScreenMemoryWindow = screenMemoryWindow;
                    activeSawMode4 = sawMode4ThisFrame;
                    activeSawMode5 = sawMode5ThisFrame;
                    activeInterlaceFieldOdd = false;
                    activeFrameNumber = capturedFrameCounter;
                    activeRasterEvents.Clear();
                    activeRasterEvents.AddRange(rasterEvents);
                }

                if (IsDisplayDisabledByCrtcSkew())
                {
                    Array.Fill(display.FrameBuffer, Background);
                    return;
                }

                if (!IsDisplayProgrammed())
                {
                    Array.Fill(display.FrameBuffer, Background);
                    return;
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
                        if (IsGappedTextRow())
                            RenderBitmapMode3(display);
                        else
                            RenderBitmapMode0(display);
                        break;

                    case BbcScreenMode.Mode1:
                        RenderBitmapMode1(display);
                        break;

                    case BbcScreenMode.Mode2:
                        RenderBitmapMode2(display);
                        break;

                    case BbcScreenMode.Mode4:
                        if (IsGappedTextRow())
                            RenderBitmapMode6(display);
                        else
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
            if (IsTeletextOutputSuppressed())
                return;

            int xOffset = 0;
            bool flashVisible = (Environment.TickCount64 / 320 & 1) == 0;

            for (int row = 0; row < Mode7Rows; row++)
            {
                int cellY = row * cellHeight;
                TeletextState state = new TeletextState();

                for (int column = 0; column < Mode7Columns; column++)
                {
                    byte character = ReadMode7DisplayCharacter(row, column);
                    int cellX = xOffset + (column * cellWidth);

                    int control = character & 0x7F;
                    bool isControl = control < 0x20;

                    // Save the rendering attributes that apply to THIS cell (Set-After semantics:
                    // most attributes change AFTER this cell; the cell itself is rendered as a
                    // space using the attributes that were active before the control code).
                    bool wasGraphicsMode = state.GraphicsMode;
                    bool wasSeparated = state.SeparatedGraphics;
                    bool wasHoldGraphics = state.HoldGraphics;
                    bool wasDoubleHeight = state.DoubleHeight;
                    bool wasConcealed = state.Concealed;
                    uint cellForeground = state.ForegroundColour;
                    uint cellBackground = state.BackgroundColour;
                    byte? cellHeldMosaic = state.HeldMosaic;

                    if (isControl)
                    {
                        // Update state for following cells. NEW-BACKGROUND (0x1D) is "Set-At" and
                        // takes effect on the current cell — handled inside ApplyTeletextControl.
                        ApplyTeletextControl(control, state);

                        // Background colour is Set-At — re-read it for this cell.
                        if (control == 0x1C || control == 0x1D)
                            cellBackground = state.BackgroundColour;
                    }

                    // Fill the cell with current background colour.
                    if (cellBackground != Background)
                        FillRect(pixels, display.Width, display.Height, cellX, cellY, cellX + cellWidth, cellY + cellHeight, cellBackground);

                    if (state.Flashing && !flashVisible)
                        continue;
                    if (cellConcealedForRender(state) || wasConcealed)
                        continue;

                    if (isControl)
                    {
                        // Hold-Graphics: draw the most recent mosaic in graphics mode using the
                        // attributes that were active *before* this control code.
                        if (wasGraphicsMode && wasHoldGraphics && cellHeldMosaic.HasValue)
                            DrawTeletextMosaic(pixels, display.Width, display.Height, cellX, cellY, cellWidth, cellHeight, cellHeldMosaic.Value, cellForeground, wasSeparated);
                        continue;
                    }

                    if (wasGraphicsMode && IsTeletextMosaicCharacter(character))
                    {
                        state.HeldMosaic = character;
                        DrawTeletextMosaic(pixels, display.Width, display.Height, cellX, cellY, cellWidth, cellHeight, character, cellForeground, wasSeparated);
                    }
                    else
                    {
                        bool doubleHeightBottom = wasDoubleHeight && IsDoubleHeightBottomRow(row, column, character);
                        Saa5050Font.Draw(pixels, display.Width, display.Height, cellX, cellY, cellHeight, character, cellForeground, wasDoubleHeight, doubleHeightBottom);
                    }
                }
            }

            RenderMode7Cursor(display);

            // Conceal helper: only conceal once a Conceal (0x18) code has been seen on this row.
            // Wrapped here as a local fn so we can use it above without leaking it from the class.
            static bool cellConcealedForRender(TeletextState s) => s.Concealed;
        }

        private static void ApplyTeletextControl(int control, TeletextState state)
        {
            switch (control)
            {
                case >= 0x01 and <= 0x07:
                    state.GraphicsMode = false;
                    state.Concealed = false;
                    state.ForegroundColour = BbcColours[control & 0x07];
                    state.HeldMosaic = null;
                    break;

                case 0x08:
                    state.Flashing = true;
                    break;

                case 0x09:
                    state.Flashing = false;
                    break;

                case 0x0C:
                    state.DoubleHeight = false;
                    break;

                case 0x0D:
                    state.DoubleHeight = true;
                    break;

                case >= 0x11 and <= 0x17:
                    state.GraphicsMode = true;
                    state.Concealed = false;
                    state.ForegroundColour = BbcColours[control & 0x07];
                    break;

                case 0x10:
                    // 0x10 is reserved/black-graphics on some teletext variants; treat as switch
                    // to graphics mode with the current foreground colour.
                    state.GraphicsMode = true;
                    break;

                case 0x18:
                    state.Concealed = true;
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
        }

        private bool TryApplyTeletextControl(byte character, TeletextState state)
        {
            int control = character & 0x7F;
            if (control >= 0x20)
                return false;

            ApplyTeletextControl(control, state);
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
            for (int sourceY = 0; sourceY < 10; sourceY++)
            {
                for (int sourceX = 0; sourceX < 6; sourceX++)
                {
                    int block = (sourceY < 3 ? 0 : sourceY < 7 ? 2 : 4) + (sourceX < 3 ? 0 : 1);
                    if ((pattern & (1 << block)) == 0)
                        continue;

                    if (separated)
                    {
                        int blockStartX = sourceX < 3 ? 0 : 3;
                        int blockStartY = sourceY < 3 ? 0 : sourceY < 7 ? 3 : 7;
                        int blockEndY = sourceY < 3 ? 2 : sourceY < 7 ? 6 : 9;
                        if (sourceX == blockStartX || sourceY == blockEndY)
                            continue;
                    }

                    int x0 = cellX + (sourceX * cellWidth / 6);
                    int x1 = cellX + ((sourceX + 1) * cellWidth / 6);
                    int y0 = cellY + (sourceY * cellHeight / 10);
                    int y1 = cellY + ((sourceY + 1) * cellHeight / 10);
                    FillRect(pixels, width, height, x0, y0, x1, y1, colour);
                }
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
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int defaultBytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);
            int yOffset = GetBitmapYOffset(height);
            int defaultStart = GetBitmapDisplayStart();
            int characterRows = GetEffectiveCharacterRows((int)activeCrtcRegisters[CrtcVerticalDisplayedRegister], 8);
            var snapshots = BuildCharacterRowSnapshots(characterRows, 8, defaultStart, defaultBytesPerRow);

            for (int charRow = 0; charRow < characterRows; charRow++)
            {
                int bytesPerRow = Math.Clamp(snapshots[charRow].BytesPerRow, 1, BitmapBytesPerRow20K);
                int rowCrtcStart = snapshots[charRow].CrtcStart;

                for (int rasterLine = 0; rasterLine < 8; rasterLine++)
                {
                    int y = (charRow * 8) + rasterLine;
                    if (y >= height)
                        return;
                    int targetY = yOffset + (y * 2);

                    for (int byteX = 0; byteX < bytesPerRow; byteX++)
                    {
                        byte value = activeMemory[GetCharacterRowBitmapAddress(charRow, rasterLine, byteX, bytesPerRow, rowCrtcStart)];

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
        }

        private void RenderBitmapMode1(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int defaultBytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);
            int yOffset = GetBitmapYOffset(height);
            int defaultStart = GetBitmapDisplayStart();
            int characterRows = GetEffectiveCharacterRows((int)activeCrtcRegisters[CrtcVerticalDisplayedRegister], 8);
            var snapshots = BuildCharacterRowSnapshots(characterRows, 8, defaultStart, defaultBytesPerRow);

            for (int charRow = 0; charRow < characterRows; charRow++)
            {
                int bytesPerRow = Math.Clamp(snapshots[charRow].BytesPerRow, 1, BitmapBytesPerRow20K);
                int rowCrtcStart = snapshots[charRow].CrtcStart;

                for (int rasterLine = 0; rasterLine < 8; rasterLine++)
                {
                    int y = (charRow * 8) + rasterLine;
                    if (y >= height)
                        return;
                    int targetY = yOffset + (y * 2);

                    for (int byteX = 0; byteX < bytesPerRow; byteX++)
                    {
                        byte value = activeMemory[GetCharacterRowBitmapAddress(charRow, rasterLine, byteX, bytesPerRow, rowCrtcStart)];

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
        }

        private void RenderBitmapMode2(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);
            int yOffset = GetBitmapYOffset(height);

            for (int y = 0; y < height; y++)
            {
                int targetY = yOffset + (y * 2);

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
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int defaultBytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);
            int yOffset = GetBitmapYOffset(height);
            int defaultStart = GetBitmapDisplayStart();
            int characterRows = GetEffectiveCharacterRows((int)activeCrtcRegisters[CrtcVerticalDisplayedRegister], 8);
            var snapshots = BuildCharacterRowSnapshots(characterRows, 8, defaultStart, defaultBytesPerRow);
            int eventIndex = 0;
            byte[] currentPalette = activePaletteRegisters;

            for (int charRow = 0; charRow < characterRows; charRow++)
            {
                int bytesPerRow = Math.Clamp(snapshots[charRow].BytesPerRow, 1, BitmapBytesPerRow10K);
                int rowCrtcStart = snapshots[charRow].CrtcStart;

                for (int rasterLine = 0; rasterLine < 8; rasterLine++)
                {
                    int y = (charRow * 8) + rasterLine;
                    if (y >= height)
                        return;
                    currentPalette = GetPaletteForVisibleLine(y, ref eventIndex, currentPalette);
                    int targetY = yOffset + (y * 2);

                    for (int byteX = 0; byteX < bytesPerRow; byteX++)
                    {
                        byte value = activeMemory[GetCharacterRowBitmapAddress(charRow, rasterLine, byteX, bytesPerRow, rowCrtcStart)];

                        for (int bit = 0; bit < 8; bit++)
                        {
                            int logicalColour = (value >> (7 - bit)) & 0x01;
                            int targetX = xOffset + (((byteX * 8) + bit) * 2);
                            uint colour = GetPaletteColour(BbcScreenMode.Mode4, currentPalette, logicalColour);

                            WriteScaledPixel2x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                        }
                    }
                }
            }
        }

        private void RenderBitmapMode5(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int defaultBytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);
            int yOffset = GetBitmapYOffset(height);
            int defaultStart = GetBitmapDisplayStart();
            int characterRows = GetEffectiveCharacterRows((int)activeCrtcRegisters[CrtcVerticalDisplayedRegister], 8);
            var snapshots = BuildCharacterRowSnapshots(characterRows, 8, defaultStart, defaultBytesPerRow);
            int eventIndex = 0;
            byte[] currentPalette = activePaletteRegisters;
            for (int charRow = 0; charRow < characterRows; charRow++)
            {
                int bytesPerRow = Math.Clamp(snapshots[charRow].BytesPerRow, 1, BitmapBytesPerRow10K);
                int rowCrtcStart = snapshots[charRow].CrtcStart;

                for (int rasterLine = 0; rasterLine < 8; rasterLine++)
                {
                    int y = (charRow * 8) + rasterLine;
                    if (y >= height)
                        return;
                    currentPalette = GetPaletteForVisibleLine(y, ref eventIndex, currentPalette);
                    int targetY = yOffset + (y * 2);

                    for (int byteX = 0; byteX < bytesPerRow; byteX++)
                    {
                        byte value = activeMemory[GetCharacterRowBitmapAddress(charRow, rasterLine, byteX, bytesPerRow, rowCrtcStart)];

                        for (int pixel = 0; pixel < 4; pixel++)
                        {
                            int logicalColour = DecodeTwoBitPixel(value, pixel);
                            int targetX = xOffset + (((byteX * 4) + pixel) * 4);
                            uint colour = GetPaletteColour(BbcScreenMode.Mode5, currentPalette, logicalColour);

                            WriteScaledPixel4x2(pixels, display.Width, display.Height, targetX, targetY, colour);
                        }
                    }
                }
            }
        }

        /// <summary>True when the current CRTC programming describes the BBC's gapped text modes
        /// (Modes 3 or 6): the ULA selects a Mode 0/4 character clock but R9 selects 9 (so each
        /// character row spans 10 scanlines instead of 8), and only 25 character rows are
        /// displayed. The last two scanlines of each row are blanked, producing the characteristic
        /// gappy text look.
        /// Stability gate: R9 must be ≥ 9 for two consecutive frames before we dispatch as Mode 3/6,
        /// otherwise a single mid-frame R9 rupture (used as a CRTC trick by some games such as
        /// Tricky's Frogger) would briefly retarget rendering and corrupt the screen.</summary>
        private bool IsGappedTextRow()
        {
            int displayedRows = activeCrtcRegisters[CrtcVerticalDisplayedRegister];
            return stableR9 >= 9 && displayedRows > 0 && displayedRows <= 32;
        }

        private void RenderBitmapMode3(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow20K);
            int displayedRows = Math.Max(1, (int)activeCrtcRegisters[CrtcVerticalDisplayedRegister]);
            int scanlinesPerRow = (activeCrtcRegisters[CrtcScanLinesPerCharacterRegister] & 0x1F) + 1;
            int height = Math.Clamp(displayedRows * scanlinesPerRow, 1, BitmapHeight);
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow20K, 8);
            int yOffset = GetBitmapYOffset(height);
            int defaultStart = GetBitmapDisplayStart();
            int eventCursor = 0;

            // Mode 3: each character cell holds 8 pixel rows but the CRTC reserves
            // scanlinesPerRow lines per character row, leaving (scanlinesPerRow - 8) blank.
            for (int charRow = 0; charRow < displayedRows; charRow++)
            {
                for (int rasterLine = 0; rasterLine < 8; rasterLine++)
                {
                    int y = (charRow * 8) + rasterLine;
                    if (y >= BitmapHeight)
                        return;

                    int displayLine = (charRow * scanlinesPerRow) + rasterLine;
                    int crtcStart = GetCrtcStartForScanline(displayLine, scanlinesPerRow, ref eventCursor, defaultStart);
                    int targetY = yOffset + (displayLine * 2);

                    for (int byteX = 0; byteX < bytesPerRow; byteX++)
                    {
                        byte value = activeMemory[GetGappedBitmapAddress(charRow, rasterLine, byteX, bytesPerRow, crtcStart)];

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
        }

        private void RenderBitmapMode6(Display display)
        {
            uint[] pixels = display.FrameBuffer;
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int displayedRows = Math.Max(1, (int)activeCrtcRegisters[CrtcVerticalDisplayedRegister]);
            int scanlinesPerRow = (activeCrtcRegisters[CrtcScanLinesPerCharacterRegister] & 0x1F) + 1;
            int height = Math.Clamp(displayedRows * scanlinesPerRow, 1, BitmapHeight);
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);
            int yOffset = GetBitmapYOffset(height);
            int defaultStart = GetBitmapDisplayStart();
            int eventCursor = 0;

            for (int charRow = 0; charRow < displayedRows; charRow++)
            {
                for (int rasterLine = 0; rasterLine < 8; rasterLine++)
                {
                    int y = (charRow * 8) + rasterLine;
                    if (y >= BitmapHeight)
                        return;

                    int displayLine = (charRow * scanlinesPerRow) + rasterLine;
                    int crtcStart = GetCrtcStartForScanline(displayLine, scanlinesPerRow, ref eventCursor, defaultStart);
                    int targetY = yOffset + (displayLine * 2);

                    for (int byteX = 0; byteX < bytesPerRow; byteX++)
                    {
                        byte value = activeMemory[GetGappedBitmapAddress(charRow, rasterLine, byteX, bytesPerRow, crtcStart)];

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
        }

        /// <summary>Address fetch for gapped text modes (Modes 3 and 6). Memory layout matches
        /// Modes 0/4 but only the first 8 raster lines of each character row contain pixel data.</summary>
        private int GetGappedBitmapAddress(int characterRow, int rasterLine, int byteX, int bytesPerRow, int crtcStart)
        {
            int ma = crtcStart + (characterRow * bytesPerRow) + byteX;
            return TranslateBitmapAddress(ma, rasterLine);
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
            ClearBitmapFrameBuffer(pixels, display.Width, display.Height);
            int bytesPerRow = GetBitmapBytesPerRow(BitmapBytesPerRow10K);
            int height = GetBitmapHeight();
            int xOffset = GetBitmapXOffset(BitmapBytesPerRow10K, 16);

            if (TryRenderCarriedOverMode5Split(display, height, bytesPerRow, xOffset))
                return;

            int eventIndex = 0;
            int initialCrtcStart = ((activeCrtcRegisters[CrtcDisplayStartHighRegister] & 0x3F) << 8)
                | activeCrtcRegisters[CrtcDisplayStartLowRegister];
            VideoRasterEvent state = activeRasterEvents.Count > 0
                ? activeRasterEvents[0]
                : new VideoRasterEvent(0, 0, 0, activeMode, activeUlaControl, activePaletteRegisters, initialCrtcStart, activeCrtcRegisters[CrtcHorizontalDisplayedRegister], crtcAddressLatch: false);

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

        private byte[] GetPaletteForVisibleLine(int visibleLine, ref int eventIndex, byte[] currentPalette)
        {
            while (eventIndex < activeRasterEvents.Count && activeRasterEvents[eventIndex].VisibleLine <= visibleLine)
            {
                if (activeRasterEvents[eventIndex].VisibleLine >= 0)
                    currentPalette = activeRasterEvents[eventIndex].Palette;

                eventIndex++;
            }

            return currentPalette;
        }

        private void RenderMode4BitmapRow(Display display, int y, int bytesPerRow, int xOffset, byte[] palette)
        {
            uint[] pixels = display.FrameBuffer;
            int targetY = y * 2;
            int crtcStart = GetBitmapDisplayStart();

            for (int byteX = 0; byteX < bytesPerRow; byteX++)
            {
                byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow, crtcStart)];

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
            int crtcStart = GetBitmapDisplayStart();

            for (int byteX = 0; byteX < bytesPerRow; byteX++)
            {
                byte value = activeMemory[GetBitmapAddress(y, byteX, bytesPerRow, crtcStart)];

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
            int xOffset = 0;

            int column = cursorOffset % Mode7Columns;
            int row = cursorOffset / Mode7Columns;
            (int shapeStart, int shapeEnd) = GetCursorShape(crtcScanlinesPerCell);

            if (shapeEnd < shapeStart)
                return;

            int startX = xOffset + (column * cellWidth);
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
            int cursorMode = (cursorStart >> 5) & 0x03;

            if (cursorMode == 0)
                return true;

            // 6845 cursor mode 1 disables the cursor. Modes 2 and 3 flash at
            // frame-derived rates; jsbeeb uses frame masks 0x08 and 0x10.
            int flashMask = cursorMode switch
            {
                2 => 0x08,
                3 => 0x10,
                _ => 0
            };

            return flashMask != 0 && (activeFrameNumber & flashMask) != 0;
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
            int crtcAddress = GetMode7DisplayStartAddress()
                + (row * GetMode7BytesPerRow())
                + column;
            return activeMemory[TranslateMode7Address(crtcAddress)];
        }

        private int GetMode7DisplayStartOffset()
        {
            return GetMode7DisplayStartAddress() & (Mode7ScreenBytes - 1);
        }

        private int GetMode7DisplayStartAddress()
        {
            int crtcStart = ((activeCrtcRegisters[CrtcDisplayStartHighRegister] & 0x3F) << 8)
                | activeCrtcRegisters[CrtcDisplayStartLowRegister];
            return crtcStart;
        }

        private int GetMode7BytesPerRow()
        {
            int displayed = activeCrtcRegisters[CrtcHorizontalDisplayedRegister];
            if (displayed <= 0)
                return Mode7Columns;

            return Math.Clamp(displayed, 1, 128);
        }

        private int TranslateMode7Address(int crtcMemoryAddress)
        {
            int ma = crtcMemoryAddress & 0x3FFF;

            if ((ma & 0x2000) != 0)
            {
                // BBC Model B teletext "chunky" addressing: when MA13 is set, the
                // 1K offset comes from MA9..MA0. MA11 selects &7C00 when set, but
                // on a Model B the clear case maps to &3C00 rather than &7C00.
                int offset = ma & (Mode7ScreenBytes - 1);
                int baseAddress = (ma & 0x0800) != 0 ? Mode7ScreenStart : 0x3C00;
                return baseAddress + offset;
            }

            return TranslateBitmapAddress(ma, 0);
        }

        private int GetBitmapAddress(int y, int byteX, int bytesPerRow)
        {
            int crtcStart = GetBitmapDisplayStart();
            int characterRow = y >> 3;
            int rasterLine = y & 0x07;
            int ma = crtcStart + (characterRow * bytesPerRow) + byteX;
            return TranslateBitmapAddress(ma, rasterLine);
        }

        /// <summary>Address overload that allows the caller to supply a per-scanline CRTC start address
        /// (derived from raster events captured during the frame). This is what enables hardware
        /// vertical scrolling and split screens that re-program R12/R13 mid-frame.</summary>
        private int GetBitmapAddress(int y, int byteX, int bytesPerRow, int crtcStart)
        {
            int characterRow = y >> 3;
            int rasterLine = y & 0x07;
            int ma = crtcStart + (characterRow * bytesPerRow) + byteX;
            return TranslateBitmapAddress(ma, rasterLine);
        }

        /// <summary>Address fetch addressed per character row using a row-specific CRTC start
        /// (R12:R13) and bytes-per-row (R1). The supplied <paramref name="rowCrtcStart"/> already
        /// reflects either the explicit per-row latched value or the natural sequential address
        /// (computed by <see cref="BuildCharacterRowSnapshots"/>), so we never apply an additional
        /// per-row stride here. This is the address pattern produced by the real 6845 when a game
        /// performs per-character-row "vertical rupture" reprogramming or hardware scrolling.</summary>
        private int GetCharacterRowBitmapAddress(int characterRow, int rasterLine, int byteX, int bytesPerRow, int rowCrtcStart)
        {
            _ = characterRow;
            _ = bytesPerRow;
            int ma = rowCrtcStart + byteX;
            return TranslateBitmapAddress(ma, rasterLine);
        }

        /// <summary>Returns the CRTC start address (R12:R13) effective for the given visible scanline.
        /// Walks the captured raster events and snaps to the most recent event whose visible-line
        /// position is at or before <paramref name="y"/>. The caller may supply <paramref name="eventCursor"/>
        /// initialised to 0 and re-use it across scanlines for O(N+R) total cost.</summary>
        private int GetCrtcStartForScanline(int y, int scanlinesPerRow, ref int eventCursor, int defaultStart)
        {
            int start = defaultStart;
            // activeRasterEvents is already in time-order; walk forward while events apply to y or earlier.
            // Snap each event's effective scanline to the next character-row boundary, matching real
            // 6845 behaviour: R12/R13 latches only at the start of a character row, not mid-row.
            // Only events caused by actual R12/R13 writes are honoured; mode/ULA/palette events
            // carry an incidental CRTC snapshot only and must not retarget addressing.
            while (eventCursor < activeRasterEvents.Count)
            {
                int eventVisibleLine = activeRasterEvents[eventCursor].VisibleLine;
                int snappedLine = SnapToNextCharacterRow(eventVisibleLine, scanlinesPerRow);
                if (snappedLine > y)
                    break;
                if (activeRasterEvents[eventCursor].CrtcAddressLatch)
                    start = activeRasterEvents[eventCursor].CrtcStartAddress;
                eventCursor++;
            }
            return start;
        }

        /// <summary>Snaps a write-time visible scanline up to the start of the next character row,
        /// matching the 6845 latch behaviour for R12/R13/R1. Writes that occur within a character
        /// row only take effect at the start of the following row, never partway through. Writes
        /// that occur exactly on a row boundary are treated as latching at the start of that row,
        /// because games typically issue the write just before vsync/HBL crosses the boundary and
        /// our cycle-based scanline estimate is coarse enough that exact-boundary writes should
        /// be honoured by the row that is just beginning.</summary>
        private static int SnapToNextCharacterRow(int visibleLine, int scanlinesPerRow)
        {
            scanlinesPerRow = Math.Max(1, scanlinesPerRow);

            // Writes before the first visible row programme the very first character row.
            if (visibleLine <= 0)
                return 0;
            // Standard "round up to next character row". Boundary values map to themselves so a
            // write timed at the start of row N latches into row N (not row N+1).
            return ((visibleLine + scanlinesPerRow - 1) / scanlinesPerRow) * scanlinesPerRow;
        }

        /// <summary>Returns the effective number of character rows that the CRTC actually
        /// displays this frame. Normally this is just R6 (vertical displayed), but Tricky's
        /// Frogger trick programs R6 = 1 and instead repaints the screen by rewriting R12:R13
        /// at every character-row boundary, relying on a recurring vertical rupture rather than
        /// on R6. In that case R6 alone would clip the display down to a single character row,
        /// so we additionally count how far down the visible region the CRTC address-latch events
        /// reach and use whichever is greater. The result is clamped to the maximum bitmap
        /// height so we never render more rows than the screen can show.</summary>
        /// <param name="defaultRows">The starting estimate (typically R6 from the frame snapshot).</param>
        /// <param name="scanlinesPerRow">The CRTC scanlines per character row (R9 + 1).</param>
        private int GetEffectiveCharacterRows(int defaultRows, int scanlinesPerRow)
        {
            int maxRows = Math.Max(1, BitmapHeight / Math.Max(1, scanlinesPerRow));
            int rows = Math.Clamp(defaultRows, 1, maxRows);

            if (activeRasterEvents.Count == 0 || scanlinesPerRow <= 0)
                return rows;

            // Find the deepest visible scanline that is targeted by an actual R12/R13/R1/R9
            // address-latch event. Latches advance the rendered character-row index, so the
            // effective row count must be at least one more than that latch's row index.
            int highestLatchedRow = -1;
            for (int i = 0; i < activeRasterEvents.Count; i++)
            {
                if (!activeRasterEvents[i].CrtcAddressLatch)
                    continue;
                int visibleLine = activeRasterEvents[i].VisibleLine;
                if (visibleLine < 0 || visibleLine >= BitmapHeight)
                    continue;
                int rowIndex = visibleLine / scanlinesPerRow;
                if (rowIndex > highestLatchedRow)
                    highestLatchedRow = rowIndex;
            }

            if (highestLatchedRow + 1 > rows)
                rows = Math.Min(maxRows, highestLatchedRow + 1);

            return rows;
        }

        /// <summary>Builds a per-character-row map of (crtcStart, bytesPerRow) snapshots derived
        /// from the time-ordered raster events. Each entry describes what R12:R13 and R1 looked
        /// like at the moment the CRTC latched a new character row, with the natural memory stride
        /// applied between rows that have no explicit event of their own. This is what enables the
        /// per-character-row "vertical rupture" trick used by games such as Tricky's Frogger, where
        /// R12:R13 (and sometimes R1) are reprogrammed once for every character row.
        /// The default start/bytes-per-row from the frame snapshot are always used as the starting
        /// state, and explicit raster events only override when they target a visible scanline.
        /// Pre-visible events (e.g. the synthetic frame-start event captured at vsync) are ignored
        /// here because their captured state may be transient and not representative of what the
        /// frame actually uses.</summary>
        /// <param name="characterRowCount">The number of character rows to populate (typically R6).</param>
        /// <param name="scanlinesPerRow">The CRTC scanlines per character row (R9 + 1).</param>
        /// <param name="defaultStart">Fallback R12:R13 to use before the first event.</param>
        /// <param name="defaultBytesPerRow">Fallback R1 to use before the first event.</param>
        private (int CrtcStart, int BytesPerRow)[] BuildCharacterRowSnapshots(int characterRowCount, int scanlinesPerRow, int defaultStart, int defaultBytesPerRow)
        {
            var snapshots = new (int CrtcStart, int BytesPerRow)[characterRowCount];
            int currentStart = defaultStart;
            int currentBytesPerRow = defaultBytesPerRow;

            // Process any pre-visible (VisibleLine <= 0) events that occurred between vsync and
            // the start of the active region. We honour real address-latch events (R12/R13/R1/R9
            // writes) because games such as Tricky's Frogger reprogram R12/R13 during VBL to
            // point at the first playfield row before scan 0. We skip non-latch events because
            // their captured CRTC state is incidental (palette/ULA writes that just snapshot
            // the current registers, which may carry the previous frame's end-of-frame values).
            int eventIndex = 0;
            while (eventIndex < activeRasterEvents.Count && activeRasterEvents[eventIndex].VisibleLine <= 0)
            {
                if (activeRasterEvents[eventIndex].CrtcAddressLatch)
                {
                    currentStart = activeRasterEvents[eventIndex].CrtcStartAddress;
                    if (activeRasterEvents[eventIndex].HorizontalDisplayed > 0)
                        currentBytesPerRow = activeRasterEvents[eventIndex].HorizontalDisplayed;
                }
                eventIndex++;
            }

            for (int row = 0; row < characterRowCount; row++)
            {
                int rowFirstScanline = row * scanlinesPerRow;
                bool rowExplicitlyLatched = false;

                // Walk every event whose snapped scanline matches the start of this character row.
                // The 6845 latches at character-row boundaries, so writes within a row only take
                // effect at the start of the next row. Only events flagged as CrtcAddressLatch (i.e.
                // writes to R12/R13/R1/R9 themselves) may change addressing; mode/ULA/palette events
                // are skipped here because their CRTC state snapshot is incidental, not authoritative.
                while (eventIndex < activeRasterEvents.Count)
                {
                    int snappedLine = SnapToNextCharacterRow(activeRasterEvents[eventIndex].VisibleLine, scanlinesPerRow);
                    if (snappedLine > rowFirstScanline)
                        break;
                    if (activeRasterEvents[eventIndex].CrtcAddressLatch)
                    {
                        currentStart = activeRasterEvents[eventIndex].CrtcStartAddress;
                        if (activeRasterEvents[eventIndex].HorizontalDisplayed > 0)
                            currentBytesPerRow = activeRasterEvents[eventIndex].HorizontalDisplayed;
                        rowExplicitlyLatched = true;
                    }
                    eventIndex++;
                }

                if (rowExplicitlyLatched || row == 0)
                {
                    // Either an explicit R12/R13 write landed on this row, or we are at the first
                    // row of the frame: use the currently latched start address directly.
                    snapshots[row] = (currentStart, currentBytesPerRow);
                }
                else
                {
                    // No explicit event for this row: advance naturally from the previous row by
                    // its bytes-per-row stride, mirroring the 6845 address generator.
                    int previousStart = snapshots[row - 1].CrtcStart;
                    int previousStride = snapshots[row - 1].BytesPerRow;
                    snapshots[row] = (previousStart + previousStride, currentBytesPerRow);
                }
            }

            return snapshots;
        }

        private int GetBitmapDisplayStart()
        {
            return ((activeCrtcRegisters[CrtcDisplayStartHighRegister] & 0x3F) << 8)
                | activeCrtcRegisters[CrtcDisplayStartLowRegister];
        }

        private int TranslateBitmapAddress(int crtcMemoryAddress, int rasterLine)
        {
            // BBC bitmap addressing routes CRTC MA and RA through IC32/IC39 rather than
            // using a simple linear CRTC-address << 3 window wrap.
            int ma = crtcMemoryAddress & 0x1FFF;
            int adjustedHigh = (ma >> 8) & 0x0F;

            if ((ma & 0x1000) != 0)
                adjustedHigh = (adjustedHigh - activeScreenMemoryWindow.AddressSubtract) & 0x0F;

            return ((adjustedHigh << 11) | ((ma & 0xFF) << 3) | (rasterLine & 0x07)) & 0x7FFF;
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
            // Honour Tricky's Frogger trick: when the game programs R6 = 1 but uses mid-frame
            // R12/R13 latches to repaint additional rows, the effective number of displayed rows
            // is greater than R6. Use the same heuristic as the per-row snapshot builder so the
            // renderer's `y >= height` early-out matches the actual painted height.
            int effectiveRows = GetEffectiveCharacterRows(displayedRows, scanlinesPerCharacter);
            int height = effectiveRows * scanlinesPerCharacter;

            if (height <= 0)
                return BitmapHeight;

            return Math.Clamp(height, 1, BitmapHeight);
        }

        private int GetBitmapXOffset(int defaultBytesPerRow, int displayPixelsPerByte)
        {
            int bytesPerRow = GetBitmapBytesPerRow(defaultBytesPerRow);
            int unusedBytes = defaultBytesPerRow - bytesPerRow;
            int offset = unusedBytes > 0 ? unusedBytes * displayPixelsPerByte / 2 : 0;

            return offset + GetDisplayEnableSkewPixels(displayPixelsPerByte);
        }

        private int GetBitmapYOffset(int activeBitmapHeight)
        {
            if (HasVisibleRasterLayoutEvents())
                return 0;

            int unusedLines = BitmapHeight - activeBitmapHeight;
            return unusedLines > 0 ? unusedLines : 0;
        }

        private bool HasVisibleRasterLayoutEvents()
        {
            foreach (VideoRasterEvent rasterEvent in activeRasterEvents)
            {
                if (rasterEvent.CrtcAddressLatch && rasterEvent.VisibleLine >= 0)
                    return true;
            }

            return false;
        }

        private bool IsDisplayDisabledByCrtcSkew()
        {
            return GetCrtcDisplayEnableSkew() == 3;
        }

        private bool IsDisplayProgrammed()
        {
            return activeCrtcRegisters[CrtcHorizontalDisplayedRegister] > 0
                && activeCrtcRegisters[CrtcVerticalDisplayedRegister] > 0;
        }

        private int GetDisplayEnableSkewPixels(int pixelsPerCharacter)
        {
            int skew = GetCrtcDisplayEnableSkew();
            if (skew >= 3)
                return 0;

            return skew * pixelsPerCharacter;
        }

        private bool IsTeletextOutputSuppressed()
        {
            // The SAA5050 is still fed by the video bus, but with the teletext bit set
            // in a 2 MHz ULA mode (the "TTX trick") its display enable is forced off.
            return activeMode == BbcScreenMode.Mode7 && (activeUlaControl & UlaClockHigh) != 0;
        }

        private int GetCrtcDisplayEnableSkew()
        {
            return (activeCrtcRegisters[CrtcInterlaceAndSkewRegister] >> 4) & 0x03;
        }

        private bool IsCrtcInterlacedSyncAndVideo()
        {
            return (activeCrtcRegisters[CrtcInterlaceAndSkewRegister] & 0x03) == 0x03;
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

        private void ClearBitmapFrameBuffer(uint[] pixels, int width, int height)
        {
            if (!IsCrtcInterlacedSyncAndVideo())
            {
                Array.Fill(pixels, Background);
                return;
            }

            int field = activeInterlaceFieldOdd ? 1 : 0;
            for (int y = field; y < height; y += 2)
                Array.Fill(pixels, Background, y * width, width);
        }

        private void WriteScaledPixel1x2(uint[] pixels, int width, int height, int x, int y, uint colour)
        {
            if (IsCrtcInterlacedSyncAndVideo())
            {
                WriteInterlacedPixelRun(pixels, width, height, x, y, 1, colour);
                return;
            }

            if ((uint)x >= (uint)width || (uint)(y + 1) >= (uint)height)
                return;

            int offset = y * width + x;
            pixels[offset] = colour;
            pixels[offset + width] = colour;
        }

        private void WriteScaledPixel2x2(uint[] pixels, int width, int height, int x, int y, uint colour)
        {
            if (IsCrtcInterlacedSyncAndVideo())
            {
                WriteInterlacedPixelRun(pixels, width, height, x, y, 2, colour);
                return;
            }

            if ((uint)(x + 1) >= (uint)width || (uint)(y + 1) >= (uint)height)
                return;

            int offset = y * width + x;
            pixels[offset] = colour;
            pixels[offset + 1] = colour;
            pixels[offset + width] = colour;
            pixels[offset + width + 1] = colour;
        }

        private void WriteScaledPixel4x2(uint[] pixels, int width, int height, int x, int y, uint colour)
        {
            if (IsCrtcInterlacedSyncAndVideo())
            {
                WriteInterlacedPixelRun(pixels, width, height, x, y, 4, colour);
                return;
            }

            if ((uint)(x + 3) >= (uint)width || (uint)(y + 1) >= (uint)height)
                return;

            int offset = y * width + x;
            for (int i = 0; i < 4; i++)
            {
                pixels[offset + i] = colour;
                pixels[offset + width + i] = colour;
            }
        }

        private void WriteInterlacedPixelRun(uint[] pixels, int width, int height, int x, int y, int runWidth, uint colour)
        {
            int targetY = y + (activeInterlaceFieldOdd ? 1 : 0);
            if ((uint)x >= (uint)width || (uint)targetY >= (uint)height)
                return;

            int visibleRunWidth = Math.Min(runWidth, width - x);
            int offset = targetY * width + x;
            for (int i = 0; i < visibleRunWidth; i++)
                pixels[offset + i] = colour;
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

        private void AddRasterEvent(int frameCpuCycle, bool crtcAddressLatch = false)
        {
            int scanline = Math.Clamp(frameCpuCycle, 0, VideoFrameCpuCycles - 1) * VideoFrameScanlines / VideoFrameCpuCycles;
            int visibleLine = scanline - VisibleStartScanline;
            int crtcStart = ((crtcRegisters[CrtcDisplayStartHighRegister] & 0x3F) << 8)
                | crtcRegisters[CrtcDisplayStartLowRegister];
            int horizontalDisplayed = crtcRegisters[CrtcHorizontalDisplayedRegister];
            rasterEvents.Add(new VideoRasterEvent(frameCpuCycle, scanline, visibleLine, CurrentMode, UlaControl, paletteRegisters, crtcStart, horizontalDisplayed, crtcAddressLatch));
        }

        private static int GetCrtcScanlinesPerCharacter(byte[] registers)
        {
            return (registers[CrtcScanLinesPerCharacterRegister] & 0x1F) + 1;
        }

        private static int GetCrtcTotalScanlines(byte[] registers)
        {
            int verticalTotal = registers[CrtcVerticalTotalRegister];
            int verticalAdjust = registers[CrtcVerticalAdjustRegister] & 0x1F;
            int scanlinesPerRow = GetCrtcScanlinesPerCharacter(registers);
            int totalScanlines = ((verticalTotal + 1) * scanlinesPerRow) + verticalAdjust;

            if (totalScanlines < 200 || totalScanlines > 400)
                return VideoFrameScanlines;

            return totalScanlines;
        }

        private readonly struct VideoRasterEvent
        {
            public VideoRasterEvent(int frameCpuCycle, int scanline, int visibleLine, BbcScreenMode mode, byte ulaControl, byte[] palette, int crtcStartAddress, int horizontalDisplayed, bool crtcAddressLatch)
            {
                FrameCpuCycle = frameCpuCycle;
                Scanline = scanline;
                VisibleLine = visibleLine;
                Mode = mode;
                UlaControl = ulaControl;
                CrtcStartAddress = crtcStartAddress;
                HorizontalDisplayed = horizontalDisplayed;
                CrtcAddressLatch = crtcAddressLatch;
                Palette = new byte[PaletteRegisterCount];
                Array.Copy(palette, Palette, Palette.Length);
            }

            public int FrameCpuCycle { get; }

            public int Scanline { get; }

            public int VisibleLine { get; }

            public BbcScreenMode Mode { get; }

            public int CrtcStartAddress { get; }

            /// <summary>R1 (horizontal displayed = characters per row, i.e. memory bytes per row).</summary>
            public int HorizontalDisplayed { get; }

            /// <summary>True when this event was emitted by a write to R12/R13/R1 (display-start
            /// high/low or horizontal-displayed). Other events (mode/ULA/palette writes) snapshot
            /// these values for context only and must not be treated as a per-row CRTC latch.</summary>
            public bool CrtcAddressLatch { get; }

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
            public bool Concealed { get; set; }
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
