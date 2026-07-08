// ============================================================================
// Project:     BBC
// File:        HD6845_Video.cs
// Description: BBC Model B video hardware: 6845 CRTC timing, Video ULA colour
//              decoding, SAA5050 teletext, and screen-memory wrap.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{

    /// <summary>
    /// The BBC combines a 6845-style CRTC address generator with a Video ULA
    /// that interprets RAM differently in bitmap modes and Mode 7 teletext.
    /// </summary>
    public sealed class HD6845_Video
    {
        private enum CrtcInterlaceMode
        {
            NonInterlace,
            InterlaceSync,
            InterlaceSyncAndVideo
        }

        public const ushort Mode7ScreenStart = 0x7C00;
        public const int Mode7Columns = 40;
        public const int Mode7Rows = 25;
        public const int Mode7ScreenBytes = 1024;

        private const uint Background = 0xFF000000;
        private const int TeletextCharacterWidth = 12;
        private const int TeletextDisplayCharacterWidth = 16;
        private const int BeamFramebufferWidth = 1024;
        private const int BeamFramebufferHeight = 625;
        private const int VDisplayEnable = 1 << 0;
        private const int HDisplayEnable = 1 << 1;
        private const int SkewDisplayEnable = 1 << 2;
        private const int ScanlineDisplayEnable = 1 << 3;
        private const int UserDisplayEnable = 1 << 4;
        private const int FrameSkipEnable = 1 << 5;
        private const int EverythingEnabled = VDisplayEnable | HDisplayEnable | SkewDisplayEnable | ScanlineDisplayEnable | UserDisplayEnable | FrameSkipEnable;
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
        private const int CrtcLightPenHighRegister = 16;
        private const int CrtcLightPenLowRegister = 17;
        private const int PaletteRegisterCount = 16;
        private const byte UlaTeletext = 0x02;
        private const byte UlaCharactersPerLineMask = 0x0C;
        private const byte UlaClockHigh = 0x10;
        private static readonly uint[] BbcColours =
        [
            0xFF000000,
            0xFFFF0000,
            0xFF00FF00,
            0xFFFFFF00,
            0xFF0000FF,
            0xFFFF00FF,
            0xFF00FFFF,
            0xFFFFFFFF
        ];
        private static readonly byte[] CrtcRegisterMasks =
        [
            0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x1F, 0x7F, 0x7F,
            0xF3, 0x1F, 0x7F, 0x1F, 0x3F, 0xFF, 0x3F, 0xFF,
            0x3F, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        private readonly byte[] memory;
        private readonly byte[] crtcRegisters = new byte[CrtcRegisterCount];
        private readonly byte[] paletteRegisters = new byte[PaletteRegisterCount];
        private readonly byte[] pendingPaletteRegisters = new byte[PaletteRegisterCount];
        private readonly bool[] pendingPaletteWrites = new bool[PaletteRegisterCount];
        private readonly object beamFrameLock = new object();
        private readonly uint[] beamRenderFrame = new uint[BeamFramebufferWidth * BeamFramebufferHeight];
        private readonly uint[] beamCompletedFrame = new uint[BeamFramebufferWidth * BeamFramebufferHeight];
        private ScreenMemoryWindow screenMemoryWindow = new ScreenMemoryWindow(0x3000, 0x5000, 2, 10);
        private byte selectedCrtcRegister;
        private byte lastPaletteWrite;
        private bool beamHasCompletedFrame;
        private int beamActiveMinX;
        private int beamActiveMinY;
        private int beamActiveMaxX;
        private int beamActiveMaxY;
        private int beamCompletedMinX;
        private int beamCompletedMinY;
        private int beamCompletedMaxX;
        private int beamCompletedMaxY;
        private bool displayFrameRectValid;
        private int displayFrameX;
        private int displayFrameY;
        private int displayFrameWidth;
        private int displayFrameHeight;
        private bool beamCompletedVisibleRuptureTimingActive;
        private int beamMode4To5X;
        private int beamMode4To5Y;
        private int beamMode4To5HorizontalCounter;
        private int beamMode4To5VerticalCounter;
        private int beamPendingDisplayStartRuptureAddress;
        private bool beamPendingDisplayStartRupture;
        private bool beamDisplayStartRuptureThisRow;
        private bool beamOddClock;
        private bool beamHalfClock = true;
        private bool beamFirstScanline = true;
        private bool beamInHSync;
        private bool beamInVSync;
        private bool beamHadVSyncThisRow;
        private bool beamCheckVertAdjust;
        private bool beamEndOfMainLatched;
        private bool beamEndOfVertAdjustLatched;
        private bool beamEndOfFrameLatched;
        private bool beamInVertAdjust;
        private bool beamInDummyRaster;
        private CrtcInterlaceMode beamInterlaceMode;
        private bool beamInterlacedSyncAndVideo;
        private bool beamDoEvenFrameLogic;
        private bool beamIsEvenRender = true;
        private bool beamLastRenderWasEven;
        private int beamBitmapX;
        private int beamBitmapY;
        private int beamFrameCount;
        private int beamCompletedFrameCount;
        private int beamStableVerticalTotal;
        private int beamStableVerticalAdjust;
        private int beamStableVerticalSync;
        private bool beamStableVerticalTimingValid;
        private int beamHpulseWidth;
        private int beamVpulseWidth;
        private int beamHpulseCounter;
        private int beamVpulseCounter;
        private int beamDisplayEnabled = FrameSkipEnable | UserDisplayEnable;
        private int beamHorizontalCounter;
        private int beamVerticalCounter;
        private int beamScanlineCounter;
        private int beamVerticalAdjustCounter;
        private int beamAddress;
        private int beamLineStartAddress;
        private int beamNextLineStartAddress;
        private byte beamUlaControl;
        private byte beamPixelUlaControlOverride;
        private bool beamPixelUlaControlOverrideValid;
        private readonly TeletextChip beamTeletext = new TeletextChip(BbcColours);
        private int beamPixelsPerCharacter = 16;
        private int beamDisplayEnableSkew;
        private int beamCursorDisplaySkew;
        private int beamCursorPos;
        private int beamCursorDrawIndex;
        private bool beamCursorDisplayEnabled = true;
        private bool beamCursorOnThisFrame = true;

        public BbcScreenMode CurrentMode { get; private set; } = BbcScreenMode.Mode7;

        public byte UlaControl { get; private set; }

        /// <summary>Raised when the CRTC VSYNC output changes before it reaches the system 6522 VIA.</summary>
        public event Action<bool>? VsyncChanged;

        public static bool IsSheilaAddress(ushort address)
        {
            return address is >= 0xFE00 and <= 0xFE01
                or >= 0xFE20 and <= 0xFE23;
        }

        public HD6845_Video(byte[] memory)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
            ResetBeamState();
        }

        public void Reset()
        {
            Array.Clear(crtcRegisters);
            ResetPalette();
            Array.Clear(pendingPaletteWrites);
            selectedCrtcRegister = 0;
            CurrentMode = BbcScreenMode.Mode7;
            UlaControl = 0;
            screenMemoryWindow = new ScreenMemoryWindow(0x3000, 0x5000, 2, 10);
            ResetBeamState();
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(crtcRegisters.Length);
            writer.Write(crtcRegisters);
            writer.Write(paletteRegisters.Length);
            writer.Write(paletteRegisters);
            writer.Write(selectedCrtcRegister);
            writer.Write(lastPaletteWrite);
            writer.Write(UlaControl);
            writer.Write((int)CurrentMode);
            writer.Write(screenMemoryWindow.Start);
            writer.Write(screenMemoryWindow.Size);
            writer.Write(screenMemoryWindow.HardwareScroll);
            writer.Write(screenMemoryWindow.AddressSubtract);
            writer.Write(pendingPaletteRegisters.Length);
            writer.Write(pendingPaletteRegisters);
            WriteBoolArray(writer, pendingPaletteWrites);
            writer.Write(beamHasCompletedFrame);
            writer.Write(beamActiveMinX);
            writer.Write(beamActiveMinY);
            writer.Write(beamActiveMaxX);
            writer.Write(beamActiveMaxY);
            writer.Write(beamCompletedMinX);
            writer.Write(beamCompletedMinY);
            writer.Write(beamCompletedMaxX);
            writer.Write(beamCompletedMaxY);
            writer.Write(beamCompletedVisibleRuptureTimingActive);
            writer.Write(beamMode4To5X);
            writer.Write(beamMode4To5Y);
            writer.Write(beamMode4To5HorizontalCounter);
            writer.Write(beamMode4To5VerticalCounter);
            writer.Write(beamPendingDisplayStartRuptureAddress);
            writer.Write(beamPendingDisplayStartRupture);
            writer.Write(beamDisplayStartRuptureThisRow);
            writer.Write(beamOddClock);
            writer.Write(beamHalfClock);
            writer.Write(beamFirstScanline);
            writer.Write(beamInHSync);
            writer.Write(beamInVSync);
            writer.Write(beamHadVSyncThisRow);
            writer.Write(beamCheckVertAdjust);
            writer.Write(beamEndOfMainLatched);
            writer.Write(beamEndOfVertAdjustLatched);
            writer.Write(beamEndOfFrameLatched);
            writer.Write(beamInVertAdjust);
            writer.Write(beamInDummyRaster);
            writer.Write((int)beamInterlaceMode);
            writer.Write(beamInterlacedSyncAndVideo);
            writer.Write(beamDoEvenFrameLogic);
            writer.Write(beamIsEvenRender);
            writer.Write(beamLastRenderWasEven);
            writer.Write(beamBitmapX);
            writer.Write(beamBitmapY);
            writer.Write(beamFrameCount);
            writer.Write(beamCompletedFrameCount);
            writer.Write(beamStableVerticalTotal);
            writer.Write(beamStableVerticalAdjust);
            writer.Write(beamStableVerticalSync);
            writer.Write(beamStableVerticalTimingValid);
            writer.Write(beamHpulseWidth);
            writer.Write(beamVpulseWidth);
            writer.Write(beamHpulseCounter);
            writer.Write(beamVpulseCounter);
            writer.Write(beamDisplayEnabled);
            writer.Write(beamHorizontalCounter);
            writer.Write(beamVerticalCounter);
            writer.Write(beamScanlineCounter);
            writer.Write(beamVerticalAdjustCounter);
            writer.Write(beamAddress);
            writer.Write(beamLineStartAddress);
            writer.Write(beamNextLineStartAddress);
            writer.Write(beamUlaControl);
            writer.Write(beamPixelUlaControlOverride);
            writer.Write(beamPixelUlaControlOverrideValid);
            beamTeletext.SaveState(writer);
            writer.Write(beamPixelsPerCharacter);
            writer.Write(beamDisplayEnableSkew);
            writer.Write(beamCursorDisplaySkew);
            writer.Write(beamCursorPos);
            writer.Write(beamCursorDrawIndex);
            writer.Write(beamCursorDisplayEnabled);
            writer.Write(beamCursorOnThisFrame);
            lock (beamFrameLock)
            {
                WriteUintArray(writer, beamRenderFrame);
                WriteUintArray(writer, beamCompletedFrame);
            }
        }

        public void LoadState(BinaryReader reader)
        {
            ReadBytes(reader, crtcRegisters, "CRTC register");
            ReadBytes(reader, paletteRegisters, "Video ULA palette");
            selectedCrtcRegister = reader.ReadByte();
            lastPaletteWrite = reader.ReadByte();
            UlaControl = reader.ReadByte();
            CurrentMode = (BbcScreenMode)reader.ReadInt32();
            screenMemoryWindow = new ScreenMemoryWindow(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());

            if (reader.BaseStream.Position >= reader.BaseStream.Length)
            {
                Array.Copy(paletteRegisters, pendingPaletteRegisters, paletteRegisters.Length);
                Array.Clear(pendingPaletteWrites);
                RebuildCompletedFrameFromRegisters();
                return;
            }

            ReadBytes(reader, pendingPaletteRegisters, "pending Video ULA palette");
            ReadBoolArray(reader, pendingPaletteWrites, "pending Video ULA palette write");
            beamHasCompletedFrame = reader.ReadBoolean();
            beamActiveMinX = reader.ReadInt32();
            beamActiveMinY = reader.ReadInt32();
            beamActiveMaxX = reader.ReadInt32();
            beamActiveMaxY = reader.ReadInt32();
            beamCompletedMinX = reader.ReadInt32();
            beamCompletedMinY = reader.ReadInt32();
            beamCompletedMaxX = reader.ReadInt32();
            beamCompletedMaxY = reader.ReadInt32();
            beamCompletedVisibleRuptureTimingActive = reader.ReadBoolean();
            beamMode4To5X = reader.ReadInt32();
            beamMode4To5Y = reader.ReadInt32();
            beamMode4To5HorizontalCounter = reader.ReadInt32();
            beamMode4To5VerticalCounter = reader.ReadInt32();
            beamPendingDisplayStartRuptureAddress = reader.ReadInt32();
            beamPendingDisplayStartRupture = reader.ReadBoolean();
            beamDisplayStartRuptureThisRow = reader.ReadBoolean();
            beamOddClock = reader.ReadBoolean();
            beamHalfClock = reader.ReadBoolean();
            beamFirstScanline = reader.ReadBoolean();
            beamInHSync = reader.ReadBoolean();
            beamInVSync = reader.ReadBoolean();
            beamHadVSyncThisRow = reader.ReadBoolean();
            beamCheckVertAdjust = reader.ReadBoolean();
            beamEndOfMainLatched = reader.ReadBoolean();
            beamEndOfVertAdjustLatched = reader.ReadBoolean();
            beamEndOfFrameLatched = reader.ReadBoolean();
            beamInVertAdjust = reader.ReadBoolean();
            beamInDummyRaster = reader.ReadBoolean();
            beamInterlaceMode = (CrtcInterlaceMode)reader.ReadInt32();
            beamInterlacedSyncAndVideo = reader.ReadBoolean();
            beamDoEvenFrameLogic = reader.ReadBoolean();
            beamIsEvenRender = reader.ReadBoolean();
            beamLastRenderWasEven = reader.ReadBoolean();
            beamBitmapX = reader.ReadInt32();
            beamBitmapY = reader.ReadInt32();
            beamFrameCount = reader.ReadInt32();
            beamCompletedFrameCount = reader.ReadInt32();
            beamStableVerticalTotal = reader.ReadInt32();
            beamStableVerticalAdjust = reader.ReadInt32();
            beamStableVerticalSync = reader.ReadInt32();
            beamStableVerticalTimingValid = reader.ReadBoolean();
            beamHpulseWidth = reader.ReadInt32();
            beamVpulseWidth = reader.ReadInt32();
            beamHpulseCounter = reader.ReadInt32();
            beamVpulseCounter = reader.ReadInt32();
            beamDisplayEnabled = reader.ReadInt32();
            beamHorizontalCounter = reader.ReadInt32();
            beamVerticalCounter = reader.ReadInt32();
            beamScanlineCounter = reader.ReadInt32();
            beamVerticalAdjustCounter = reader.ReadInt32();
            beamAddress = reader.ReadInt32();
            beamLineStartAddress = reader.ReadInt32();
            beamNextLineStartAddress = reader.ReadInt32();
            beamUlaControl = reader.ReadByte();
            beamPixelUlaControlOverride = reader.ReadByte();
            beamPixelUlaControlOverrideValid = reader.ReadBoolean();
            beamTeletext.LoadState(reader);
            beamPixelsPerCharacter = reader.ReadInt32();
            beamDisplayEnableSkew = reader.ReadInt32();
            beamCursorDisplaySkew = reader.ReadInt32();
            beamCursorPos = reader.ReadInt32();
            beamCursorDrawIndex = reader.ReadInt32();
            beamCursorDisplayEnabled = reader.ReadBoolean();
            beamCursorOnThisFrame = reader.ReadBoolean();
            lock (beamFrameLock)
            {
                ReadUintArray(reader, beamRenderFrame, "beam render frame");
                ReadUintArray(reader, beamCompletedFrame, "beam completed frame");
                displayFrameRectValid = false;
            }

            VsyncChanged?.Invoke(beamInVSync);
        }

        private static void ReadBytes(BinaryReader reader, byte[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            bytes.CopyTo(destination, 0);
        }

        private static void WriteBoolArray(BinaryWriter writer, bool[] values)
        {
            writer.Write(values.Length);
            foreach (bool value in values)
                writer.Write(value);
        }

        private static void ReadBoolArray(BinaryReader reader, bool[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            for (int i = 0; i < destination.Length; i++)
                destination[i] = reader.ReadBoolean();
        }

        private static void WriteUintArray(BinaryWriter writer, uint[] values)
        {
            writer.Write(values.Length);
            foreach (uint value in values)
                writer.Write(value);
        }

        private static void ReadUintArray(BinaryReader reader, uint[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            for (int i = 0; i < destination.Length; i++)
                destination[i] = reader.ReadUInt32();
        }

        private void RebuildCompletedFrameFromRegisters()
        {
            ResetBeamState();
            UpdateBeamUlaControl(UlaControl);

            int cycles = 0;
            while (!beamHasCompletedFrame && cycles < 200_000)
            {
                TickBeamClock();
                cycles++;
            }
        }

        public void SetScreenMemoryWindow(ScreenMemoryWindow window)
        {
            if (window.Start < 0 || window.Start >= memory.Length)
                throw new ArgumentOutOfRangeException(nameof(window));

            if (window.Size <= 0 || window.Start + window.Size > 0x8000)
                throw new ArgumentOutOfRangeException(nameof(window));

            screenMemoryWindow = window;
        }

        /// <summary>The CRTC advances with CPU time so mid-frame ULA and CRTC changes take effect on the beam.</summary>
        public void Tick(int cycles)
        {
            for (int i = 0; i < cycles; i++)
                TickBeamClock();
        }

        private void ResetBeamState()
        {
            lock (beamFrameLock)
            {
                Array.Fill(beamRenderFrame, Background);
                Array.Fill(beamCompletedFrame, Background);
                beamHasCompletedFrame = false;
            }

            ResetBeamActiveBounds();
            beamCompletedMinX = 0;
            beamCompletedMinY = 0;
            beamCompletedMaxX = 0;
            beamCompletedMaxY = 0;
            displayFrameRectValid = false;
            displayFrameX = 0;
            displayFrameY = 0;
            displayFrameWidth = 0;
            displayFrameHeight = 0;
            beamCompletedVisibleRuptureTimingActive = false;
            beamMode4To5X = -1;
            beamMode4To5Y = -1;
            beamMode4To5HorizontalCounter = -1;
            beamMode4To5VerticalCounter = -1;
            beamPendingDisplayStartRuptureAddress = 0;
            beamPendingDisplayStartRupture = false;
            beamDisplayStartRuptureThisRow = false;
            beamOddClock = false;
            beamHalfClock = true;
            beamFirstScanline = true;
            beamInHSync = false;
            beamInVSync = false;
            beamHadVSyncThisRow = false;
            beamCheckVertAdjust = false;
            beamEndOfMainLatched = false;
            beamEndOfVertAdjustLatched = false;
            beamEndOfFrameLatched = false;
            beamInVertAdjust = false;
            beamInDummyRaster = false;
            beamInterlaceMode = CrtcInterlaceMode.NonInterlace;
            beamInterlacedSyncAndVideo = false;
            beamDoEvenFrameLogic = false;
            beamIsEvenRender = true;
            beamLastRenderWasEven = false;
            beamBitmapX = 0;
            beamBitmapY = 0;
            beamFrameCount = 0;
            beamStableVerticalTotal = 0;
            beamStableVerticalAdjust = 0;
            beamStableVerticalSync = 0;
            beamStableVerticalTimingValid = false;
            beamHpulseWidth = 0;
            beamVpulseWidth = 0;
            beamHpulseCounter = 0;
            beamVpulseCounter = 0;
            beamDisplayEnabled = FrameSkipEnable | UserDisplayEnable | HDisplayEnable | VDisplayEnable | ScanlineDisplayEnable;
            beamHorizontalCounter = 0;
            beamVerticalCounter = 0;
            beamScanlineCounter = 0;
            beamVerticalAdjustCounter = 0;
            beamAddress = 0;
            beamLineStartAddress = 0;
            beamNextLineStartAddress = 0;
            beamUlaControl = 0;
            beamPixelUlaControlOverride = 0;
            beamPixelUlaControlOverrideValid = false;
            Array.Clear(pendingPaletteWrites);
            beamTeletext.Reset();
            beamPixelsPerCharacter = 16;
            beamDisplayEnableSkew = 0;
            beamCursorDisplaySkew = 0;
            beamCursorPos = 0;
            beamCursorDrawIndex = 0;
            beamCursorDisplayEnabled = true;
            beamCursorOnThisFrame = true;
            VsyncChanged?.Invoke(false);
        }

        private void UpdateBeamUlaControl(byte control)
        {
            BbcScreenMode previousBeamMode = DecodeModeFromUlaControl(beamUlaControl);
            BbcScreenMode nextBeamMode = DecodeModeFromUlaControl(control);

            if (ShouldHoldPreviousPixelUlaControl(previousBeamMode, nextBeamMode))
            {
                beamPixelUlaControlOverride = beamUlaControl;
                beamPixelUlaControlOverrideValid = true;
            }

            beamUlaControl = control;
            beamPixelsPerCharacter = nextBeamMode == BbcScreenMode.Mode7
                ? TeletextDisplayCharacterWidth
                : (control & UlaClockHigh) != 0 ? 8 : 16;
            beamHalfClock = (control & UlaClockHigh) == 0;

            if (previousBeamMode == BbcScreenMode.Mode4
                && nextBeamMode == BbcScreenMode.Mode5
                && beamMode4To5Y < 0
                && !beamPixelUlaControlOverrideValid)
            {
                beamMode4To5X = beamBitmapX;
                beamMode4To5Y = beamBitmapY;
                beamMode4To5HorizontalCounter = beamHorizontalCounter;
                beamMode4To5VerticalCounter = beamVerticalCounter;
            }
        }

        private bool ShouldHoldPreviousPixelUlaControl(BbcScreenMode previousBeamMode, BbcScreenMode nextBeamMode)
        {
            if (previousBeamMode != BbcScreenMode.Mode4 || nextBeamMode != BbcScreenMode.Mode5)
                return false;

            if (!BeamVerticalDisplayEnabled)
                return false;

            return beamScanlineCounter != 0;
        }

        private byte GetBeamPixelUlaControl()
        {
            return beamPixelUlaControlOverrideValid ? beamPixelUlaControlOverride : beamUlaControl;
        }

        private void UpdateBeamCrtcDerivedState(int register, byte value)
        {
            switch (register)
            {
                case 3:
                {
                    int horizontalSyncWidth = value & 0x0F;
                    int verticalSyncWidth = (value >> 4) & 0x0F;

                    beamHpulseWidth = Math.Max(1, horizontalSyncWidth);
                    beamVpulseWidth = verticalSyncWidth == 0 ? 16 : verticalSyncWidth;
                    break;
                }

                case CrtcInterlaceAndSkewRegister:
                {
                    beamInterlaceMode = (value & 0x03) switch
                    {
                        0x01 => CrtcInterlaceMode.InterlaceSync,
                        0x03 => CrtcInterlaceMode.InterlaceSyncAndVideo,
                        _ => CrtcInterlaceMode.NonInterlace
                    };
                    beamInterlacedSyncAndVideo = beamInterlaceMode == CrtcInterlaceMode.InterlaceSyncAndVideo;
                    int displaySkew = (value >> 4) & 0x03;
                    int cursorSkew = (value >> 6) & 0x03;

                    if (displaySkew < 3)
                    {
                        beamDisplayEnableSkew = displaySkew;
                        BeamDisplayEnableSet(UserDisplayEnable);
                    }
                    else
                    {
                        BeamDisplayEnableClear(UserDisplayEnable);
                    }

                    beamCursorDisplaySkew = cursorSkew;
                    beamCursorDisplayEnabled = cursorSkew < 3;
                    break;
                }

                case CrtcCursorHighRegister:
                case CrtcCursorLowRegister:
                    beamCursorPos = (crtcRegisters[CrtcCursorLowRegister]
                        | (crtcRegisters[CrtcCursorHighRegister] << 8)) & 0x3FFF;
                    break;
            }
        }

        private void TickBeamClock()
        {
            _ = beamInDummyRaster;
            beamOddClock = !beamOddClock;
            beamBitmapX += 8;

            if (beamHalfClock && !beamOddClock)
                return;

            if (beamInHSync)
                HandleBeamHSync();

            int displayEnablePos = GetBeamDisplayEnablePosition();
            if (beamHorizontalCounter == displayEnablePos)
                BeamDisplayEnableSet(SkewDisplayEnable);

            if (beamHorizontalCounter == crtcRegisters[CrtcHorizontalDisplayedRegister])
                beamNextLineStartAddress = beamAddress;

            if (beamHorizontalCounter == crtcRegisters[CrtcHorizontalDisplayedRegister] + displayEnablePos
                || beamHorizontalCounter == crtcRegisters[CrtcHorizontalTotalRegister] + displayEnablePos)
                BeamDisplayEnableClear(HDisplayEnable | SkewDisplayEnable);

            if (beamHorizontalCounter == crtcRegisters[2] && !beamInHSync)
            {
                beamInHSync = true;
                beamHpulseCounter = 0;
            }

            TickBeamVSync();
            RenderBeamCharacter();

            if (!BeamHorizontalDisplayEnabled && BeamVerticalDisplayEnabled)
            {
                beamTeletext.FetchData((byte)(ReadBeamVideoMemory() | 0x40));
            }

            beamAddress = (beamAddress + 1) & 0x3FFF;
            TickBeamVerticalAdjust();
            LatchBeamEndOfMainFrame();

            if (beamHorizontalCounter == crtcRegisters[CrtcHorizontalTotalRegister])
            {
                EndBeamScanline();
                beamHorizontalCounter = 0;
                BeamDisplayEnableSet(HDisplayEnable);
            }
            else
            {
                beamHorizontalCounter = (beamHorizontalCounter + 1) & 0xFF;
            }

            bool r6Hit = beamVerticalCounter == GetBeamVerticalDisplayed();
            if (r6Hit && !beamFirstScanline && BeamVerticalDisplayEnabled && !beamDisplayStartRuptureThisRow)
            {
                BeamDisplayEnableClear(VDisplayEnable);
                beamFrameCount++;
            }

            bool r7Hit = beamVerticalCounter == GetBeamVerticalSync();
            if (r6Hit || r7Hit)
                beamDoEvenFrameLogic = (beamFrameCount & 1) != 0;
        }

        private void TickBeamVSync()
        {
            bool isInterlace = beamInterlaceMode != CrtcInterlaceMode.NonInterlace;
            bool halfR0Hit = beamHorizontalCounter == (crtcRegisters[CrtcHorizontalTotalRegister] >> 1);
            bool isVsyncPoint = !isInterlace || !beamDoEvenFrameLogic || halfR0Hit;
            bool vSyncEnding = false;
            bool vSyncStarting = false;

            if (beamInVSync && beamVpulseCounter == beamVpulseWidth && isVsyncPoint)
            {
                vSyncEnding = true;
                beamInVSync = false;
            }

            if (beamVerticalCounter == GetBeamVerticalSync()
                && !beamInVSync
                && !beamHadVSyncThisRow
                && isVsyncPoint)
            {
                vSyncStarting = true;
                beamInVSync = true;
            }

            if (vSyncStarting && !vSyncEnding)
            {
                beamHadVSyncThisRow = true;
                beamVpulseCounter = 0;
                if (crtcRegisters[CrtcHorizontalTotalRegister] != 0 && GetBeamVerticalTotal() != 0)
                    PaintAndClearBeamFrame();
            }

            if (vSyncStarting || vSyncEnding)
            {
                VsyncChanged?.Invoke(beamInVSync);
                beamTeletext.SetDEW(beamInVSync);
            }
        }

        private void RenderBeamCharacter()
        {
            if ((uint)beamBitmapX >= BeamFramebufferWidth || (uint)beamBitmapY >= BeamFramebufferHeight)
                return;
                
            bool insideBorder = BeamHorizontalDisplayEnabled && BeamVerticalDisplayEnabled;
            if (!insideBorder && beamCursorDrawIndex == 0)
                return;

            byte data = ReadBeamVideoMemory();
            if (insideBorder)
                beamTeletext.FetchData(data);

            bool renderDisplayEnabled = (beamDisplayEnabled & EverythingEnabled) == EverythingEnabled;
            bool cursorDisplayTimingEnabled = insideBorder && (beamDisplayEnabled & UserDisplayEnable) != 0;
            if (cursorDisplayTimingEnabled
                && beamCursorDisplayEnabled
                && beamAddress == beamCursorPos
                && IsBeamCursorRasterActive()
                && beamHorizontalCounter < crtcRegisters[CrtcHorizontalDisplayedRegister])
            {
                beamCursorDrawIndex = 3 - beamCursorDisplaySkew;
            }

            //if ((uint)beamBitmapX >= BeamFramebufferWidth || (uint)beamBitmapY >= BeamFramebufferHeight)
            //    return;

            bool doubledLines = false;
            int y = beamBitmapY;
            if ((!beamInterlacedSyncAndVideo) || beamIsEvenRender == beamLastRenderWasEven)
            {
                doubledLines = true;
                y &= ~1;
            }

            int offset = (y * BeamFramebufferWidth) + beamBitmapX;
            if (renderDisplayEnabled)
            {
                if (IsBeamTeletextMode)
                {
                    RenderBeamTeletextCharacter(offset, y);
                }
                else
                {
                    RecordBeamActiveRun(beamBitmapX, y, beamPixelsPerCharacter, doubledLines);
                    BlitBeamBitmap(data, offset, beamPixelsPerCharacter);
                }

                if (doubledLines && y + 1 < BeamFramebufferHeight)
                    Array.Copy(beamRenderFrame, offset, beamRenderFrame, offset + BeamFramebufferWidth, Math.Min(beamPixelsPerCharacter, BeamFramebufferWidth - beamBitmapX));
            }

            if (beamCursorDrawIndex != 0)
                HandleBeamCursor(offset, doubledLines);
        }

        private int GetBeamDisplayEnablePosition()
        {
            return beamDisplayEnableSkew + (IsBeamTeletextMode ? 2 : 0);
        }

        private void RenderBeamTeletextCharacter(int offset, int y)
        {
            beamTeletext.Render(beamRenderFrame, offset, BeamFramebufferWidth);
            RecordBeamActiveRun(beamBitmapX, y, beamPixelsPerCharacter, doubledLines: false);
        }

        private byte ReadBeamVideoMemory()
        {
            if ((beamAddress & 0x2000) != 0)
            {
                int memoryAddress = beamAddress & 0x03FF;
                memoryAddress |= (beamAddress & 0x0800) != 0 ? Mode7ScreenStart : 0x3C00;
                return memory[memoryAddress & 0xFFFF];
            }

            int ma = beamAddress & 0x1FFF;
            int adjustedHigh = (ma >> 8) & 0x0F;
            if ((ma & 0x1000) != 0)
                adjustedHigh = (adjustedHigh - screenMemoryWindow.AddressSubtract) & 0x0F;

            int address = ((adjustedHigh << 11) | ((ma & 0xFF) << 3) | (GetBeamRasterAddress() & 0x07)) & 0x7FFF;
            return memory[address];
        }

        private void BlitBeamBitmap(byte data, int offset, int pixelCount)
        {
            int visiblePixels = Math.Min(pixelCount, BeamFramebufferWidth - beamBitmapX);
            for (int i = 0; i < visiblePixels; i++)
            {
                int paletteIndex = DecodeBeamPaletteIndex(data, i);
                beamRenderFrame[offset + i] = ResolveBeamPhysicalColour(paletteRegisters[paletteIndex & 0x0F]);
            }
        }

        private int DecodeBeamPaletteIndex(byte value, int pixel)
        {
            int ulaMode = (GetBeamPixelUlaControl() >> 2) & 0x03;
            int sample = ulaMode switch
            {
                3 => pixel,
                2 => pixel >> 1,
                1 => pixel >> 2,
                _ => pixel >> 3
            };

            int shifted = ((value << sample) | ((1 << Math.Min(sample, 8)) - 1)) & 0xFF;
            int index = 0;
            if ((shifted & 0x02) != 0) index |= 0x01;
            if ((shifted & 0x08) != 0) index |= 0x02;
            if ((shifted & 0x20) != 0) index |= 0x04;
            if ((shifted & 0x80) != 0) index |= 0x08;
            return index;
        }

        private uint ResolveBeamPhysicalColour(byte physicalColour)
        {
            int colour = physicalColour & 0x0F;
            if (colour >= 8 && (GetBeamPixelUlaControl() & 0x01) != 0)
                colour &= 0x07;
            else
                colour &= 0x07;

            return BbcColours[colour];
        }

        private void HandleBeamCursor(int offset, bool doubledLines)
        {
            if (beamCursorOnThisFrame && (beamUlaControl & GetBeamCursorMask()) != 0)
            {
                int visiblePixels = Math.Min(beamPixelsPerCharacter, BeamFramebufferWidth - beamBitmapX);
                for (int i = 0; i < visiblePixels; i++)
                    beamRenderFrame[offset + i] ^= 0x00FFFFFF;

                if (doubledLines && !beamInterlacedSyncAndVideo && offset + BeamFramebufferWidth < beamRenderFrame.Length)
                {
                    for (int i = 0; i < visiblePixels; i++)
                        beamRenderFrame[offset + BeamFramebufferWidth + i] ^= 0x00FFFFFF;
                }
            }

            if (++beamCursorDrawIndex == 7)
                beamCursorDrawIndex = 0;
        }

        private int GetBeamCursorMask()
        {
            return beamCursorDrawIndex switch
            {
                3 => 0x80,
                4 => 0x40,
                5 or 6 => 0x20,
                _ => 0x00
            };
        }

        private bool IsBeamCursorRasterActive()
        {
            int cursorMode = (crtcRegisters[CrtcCursorStartRegister] >> 5) & 0x03;
            if (cursorMode == 0x01 || !beamCursorOnThisFrame)
                return false;

            int cursorStart = crtcRegisters[CrtcCursorStartRegister] & 0x1F;
            int cursorEnd = crtcRegisters[CrtcCursorEndRegister] & 0x1F;
            int rasterAddress = GetBeamRasterAddress();

            return cursorStart <= cursorEnd
                && rasterAddress >= cursorStart
                && rasterAddress <= cursorEnd;
        }

        private void HandleBeamHSync()
        {
            beamHpulseCounter = (beamHpulseCounter + 1) & 0x0F;
            if (beamHpulseCounter == (beamHpulseWidth >> 1))
            {
                beamBitmapX = -8;
                if ((beamHpulseWidth & 1) != 0)
                    beamBitmapX -= 4;

                beamBitmapY += 2;
                if (beamBitmapY >= 768 && !BeamVisibleRuptureTimingActive)
                    PaintAndClearBeamFrame();
            }
            else if (beamHpulseCounter == beamHpulseWidth)
            {
                beamInHSync = false;
            }
        }

        private void EndBeamScanline()
        {
            beamFirstScanline = false;
            beamVpulseCounter = (beamVpulseCounter + 1) & 0x0F;
            bool r9Hit = beamScanlineCounter == GetBeamMaximumRasterAddress();
            if (r9Hit)
                beamLineStartAddress = beamNextLineStartAddress;

            if (beamInterlacedSyncAndVideo)
                beamScanlineCounter = (beamScanlineCounter + 2) & 0x1E;
            else
                beamScanlineCounter = (beamScanlineCounter + 1) & 0x1F;

            if (!IsBeamTeletextMode)
            {
                if (((GetBeamRasterAddress() >> 3) & 1) != 0)
                    BeamDisplayEnableClear(ScanlineDisplayEnable);
                else
                    BeamDisplayEnableSet(ScanlineDisplayEnable);
            }

            if (!beamInVertAdjust && r9Hit)
                EndBeamCharacterLine();

            if (beamEndOfMainLatched && !beamEndOfVertAdjustLatched)
                beamInVertAdjust = true;

            bool endOfFrame = beamEndOfFrameLatched;
            if (beamEndOfVertAdjustLatched)
            {
                beamInVertAdjust = false;
                if (beamInterlaceMode != CrtcInterlaceMode.NonInterlace && beamDoEvenFrameLogic)
                {
                    beamInDummyRaster = true;
                    beamEndOfFrameLatched = true;
                }
                else
                {
                    endOfFrame = true;
                }
            }

            if (endOfFrame)
            {
                beamEndOfMainLatched = false;
                beamEndOfVertAdjustLatched = false;
                beamEndOfFrameLatched = false;
                beamInDummyRaster = false;
                EndBeamCharacterLine();
                EndBeamFrame();
            }

            beamAddress = beamLineStartAddress;
            beamTeletext.SetRA0((GetBeamRasterAddress() & 1) != 0);
        }

        private int GetBeamRasterAddress()
        {
            int rasterAddress = beamScanlineCounter;
            if (beamInterlacedSyncAndVideo && (beamFrameCount & 1) != 0)
                rasterAddress++;

            return rasterAddress & 0x1F;
        }

        private int GetBeamMaximumRasterAddress() => crtcRegisters[CrtcScanLinesPerCharacterRegister] & 0x1F;

        private void EndBeamCharacterLine()
        {
            beamVerticalCounter = (beamVerticalCounter + 1) & 0x7F;
            beamScanlineCounter = 0;
            beamDisplayStartRuptureThisRow = false;
            if (beamPendingDisplayStartRupture)
            {
                beamLineStartAddress = beamPendingDisplayStartRuptureAddress;
                beamNextLineStartAddress = beamPendingDisplayStartRuptureAddress;
                BeamDisplayEnableSet(VDisplayEnable | ScanlineDisplayEnable);
                beamPendingDisplayStartRupture = false;
                beamDisplayStartRuptureThisRow = true;
            }
            else if (BeamVisibleRuptureTimingActive)
            {
                BeamDisplayEnableClear(VDisplayEnable);
            }

            if (beamPixelUlaControlOverrideValid)
            {
                beamPixelUlaControlOverrideValid = false;
                if (DecodeModeFromUlaControl(beamUlaControl) == BbcScreenMode.Mode5 && beamMode4To5Y < 0)
                {
                    beamMode4To5X = beamBitmapX;
                    beamMode4To5Y = beamBitmapY;
                    beamMode4To5HorizontalCounter = beamHorizontalCounter;
                    beamMode4To5VerticalCounter = beamVerticalCounter;
                }
            }
            ApplyPendingPaletteWrites();
            beamHadVSyncThisRow = false;
            BeamDisplayEnableSet(ScanlineDisplayEnable);
        }

        private void EndBeamFrame()
        {
            beamVerticalCounter = 0;
            beamFirstScanline = true;
            beamDisplayStartRuptureThisRow = false;
            beamNextLineStartAddress = (crtcRegisters[CrtcDisplayStartLowRegister]
                | (crtcRegisters[CrtcDisplayStartHighRegister] << 8)) & 0x3FFF;
            beamLineStartAddress = beamNextLineStartAddress;
            BeamDisplayEnableSet(VDisplayEnable);

            int cursorFlash = (crtcRegisters[CrtcCursorStartRegister] >> 5) & 0x03;
            int flashMask = cursorFlash switch { 2 => 0x08, 3 => 0x10, _ => 0x00 };
            beamCursorOnThisFrame = cursorFlash == 0 || (flashMask != 0 && (beamFrameCount & flashMask) != 0);
            beamLastRenderWasEven = beamIsEvenRender;
            beamIsEvenRender = (beamFrameCount & 1) == 0;
            if (!beamInVSync)
                beamDoEvenFrameLogic = false;
        }

        private void TickBeamVerticalAdjust()
        {
            if (!beamCheckVertAdjust)
                return;

            beamCheckVertAdjust = false;
            if (!beamEndOfMainLatched)
                return;

            if (beamVerticalAdjustCounter == (GetBeamVerticalAdjust() & 0x1F))
                beamEndOfVertAdjustLatched = true;
            beamVerticalAdjustCounter = (beamVerticalAdjustCounter + 1) & 0x1F;
        }

        private void LatchBeamEndOfMainFrame()
        {
            if (beamHorizontalCounter != 1)
                return;

            if (beamVerticalCounter == GetBeamVerticalTotal()
                && beamScanlineCounter == GetBeamMaximumRasterAddress())
            {
                beamEndOfMainLatched = true;
                beamVerticalAdjustCounter = 0;
            }

            beamCheckVertAdjust = true;
        }

        private void PaintAndClearBeamFrame()
        {
            lock (beamFrameLock)
            {
                Array.Copy(beamRenderFrame, beamCompletedFrame, beamCompletedFrame.Length);
                beamCompletedMinX = beamActiveMinX;
                beamCompletedMinY = beamActiveMinY;
                beamCompletedMaxX = beamActiveMaxX;
                beamCompletedMaxY = beamActiveMaxY;
                beamCompletedVisibleRuptureTimingActive = BeamVisibleRuptureTimingActive;
                beamCompletedFrameCount = beamFrameCount;
                beamHasCompletedFrame = true;
                ClearBeamRenderFrame();
                ResetBeamActiveBounds();
                beamMode4To5X = -1;
                beamMode4To5Y = -1;
                beamMode4To5HorizontalCounter = -1;
                beamMode4To5VerticalCounter = -1;
                beamPixelUlaControlOverrideValid = false;
                Array.Clear(pendingPaletteWrites);
            }

            beamDisplayEnabled &= ~FrameSkipEnable;
            beamDisplayEnabled |= FrameSkipEnable;
            beamBitmapY = 0;
            if (beamInterlaceMode != CrtcInterlaceMode.NonInterlace && (beamFrameCount & 1) != 0)
                beamBitmapY = -1;
        }

        private void ClearBeamRenderFrame()
        {
            if (beamInterlacedSyncAndVideo)
            {
                int line = beamFrameCount & 1;
                while (line < BeamFramebufferHeight)
                {
                    Array.Fill(beamRenderFrame, Background, line * BeamFramebufferWidth, BeamFramebufferWidth);
                    line += 2;
                }
            }
            else
            {
                Array.Fill(beamRenderFrame, Background);
            }
        }

        private void BeamDisplayEnableSet(int flags)
        {
            beamDisplayEnabled |= flags;
            UpdateBeamTeletextDisplayTiming();
        }

        private void BeamDisplayEnableClear(int flags)
        {
            beamDisplayEnabled &= ~flags;
            UpdateBeamTeletextDisplayTiming();
        }

        private void UpdateBeamTeletextDisplayTiming()
        {
            const int displayTimingMask = HDisplayEnable | VDisplayEnable | UserDisplayEnable;
            beamTeletext.SetDISPTMG((beamDisplayEnabled & displayTimingMask) == displayTimingMask);
        }

        private void ResetBeamActiveBounds()
        {
            beamActiveMinX = BeamFramebufferWidth;
            beamActiveMinY = BeamFramebufferHeight;
            beamActiveMaxX = 0;
            beamActiveMaxY = 0;
        }

        private void RecordBeamActiveRun(int x, int y, int width, bool doubledLines)
        {
            if (x >= BeamFramebufferWidth || y >= BeamFramebufferHeight)
                return;

            int x0 = Math.Clamp(x, 0, BeamFramebufferWidth);
            int x1 = Math.Clamp(x + width, 0, BeamFramebufferWidth);
            int y0 = Math.Clamp(y, 0, BeamFramebufferHeight);
            int y1 = Math.Clamp(y + (doubledLines ? 2 : 1), 0, BeamFramebufferHeight);

            if (x1 <= x0 || y1 <= y0)
                return;

            beamActiveMinX = Math.Min(beamActiveMinX, x0);
            beamActiveMinY = Math.Min(beamActiveMinY, y0);
            beamActiveMaxX = Math.Max(beamActiveMaxX, x1);
            beamActiveMaxY = Math.Max(beamActiveMaxY, y1);
        }

        private bool BeamHorizontalDisplayEnabled => (beamDisplayEnabled & HDisplayEnable) != 0;

        private bool BeamVerticalDisplayEnabled => (beamDisplayEnabled & VDisplayEnable) != 0;

        private bool IsBeamTeletextMode => (beamUlaControl & UlaTeletext) != 0;

        /// <summary>Reads the CRTC register or Video ULA latch exposed in the FE00-FE23 SHEILA range.</summary>
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

        /// <summary>BBC software often changes CRTC start, ULA mode, and palette registers during display.</summary>
        public void WriteSheila(ushort address, byte value)
        {
            switch (address)
            {
                case 0xFE00:
                    selectedCrtcRegister = (byte)(value & 0x1F);
                    break;

                case 0xFE01:
                    {
                        int regIndex = selectedCrtcRegister & 0x1F;
                        if (regIndex >= 18
                            || regIndex is CrtcLightPenHighRegister or CrtcLightPenLowRegister)
                            break;

                        value = (byte)(value & CrtcRegisterMasks[regIndex]);
                        crtcRegisters[regIndex] = value;
                        UpdateBeamCrtcDerivedState(regIndex, value);
                        ValidateBeamCrtcProgramming(regIndex);
                        UpdateBeamStableVerticalTiming();
                        HandleBeamDisplayStartRupture(regIndex);
                    }
                    break;

                case 0xFE20:
                case 0xFE22:
                    UlaControl = value;
                    CurrentMode = DecodeModeFromUlaControl(value);
                    UpdateBeamUlaControl(value);
                    break;

                case 0xFE21:
                case 0xFE23:
                    lastPaletteWrite = value;
                    int paletteIndex = (value >> 4) & 0x0F;
                    byte physicalColour = DecodePhysicalColour(value);
                    if (ShouldDeferPaletteWrite())
                    {
                        pendingPaletteRegisters[paletteIndex] = physicalColour;
                        pendingPaletteWrites[paletteIndex] = true;
                    }
                    else
                    {
                        paletteRegisters[paletteIndex] = physicalColour;
                    }
                    break;
            }
        }

        private bool ShouldDeferPaletteWrite()
        {
            if (beamPixelUlaControlOverrideValid)
                return true;

            return DecodeModeFromUlaControl(beamUlaControl) == BbcScreenMode.Mode4
                && BeamVerticalDisplayEnabled
                && beamScanlineCounter != 0;
        }

        private void ApplyPendingPaletteWrites()
        {
            for (int i = 0; i < PaletteRegisterCount; i++)
            {
                if (!pendingPaletteWrites[i])
                    continue;

                paletteRegisters[i] = pendingPaletteRegisters[i];
                pendingPaletteWrites[i] = false;
            }
        }

        public void Render(Display display)
        {
            if (!TryCopyBeamFrameToDisplay(display))
            {
                Array.Fill(display.FrameBuffer, Background);
                display.MarkFrameDirty();
                displayFrameRectValid = false;
            }
        }

        private bool TryCopyBeamFrameToDisplay(Display display)
        {
            lock (beamFrameLock)
            {
                if (!beamHasCompletedFrame)
                    return false;

                uint[] destination = display.FrameBuffer;

                if (beamCompletedMaxX <= beamCompletedMinX || beamCompletedMaxY <= beamCompletedMinY)
                {
                    if (displayFrameRectValid)
                    {
                        FillDisplayRect(destination, display.Width, displayFrameX, displayFrameY, displayFrameWidth, displayFrameHeight, Background);
                        display.MarkFrameDirty(displayFrameX, displayFrameY, displayFrameWidth, displayFrameHeight);
                        displayFrameRectValid = false;
                    }

                    return true;
                }

                int sourceMinY = beamCompletedMinY & ~1;
                int sourceMaxY = Math.Min(BeamFramebufferHeight, (beamCompletedMaxY + 1) & ~1);
                int sourceWidth = beamCompletedMaxX - beamCompletedMinX;
                int sourceHeight = sourceMaxY - sourceMinY;
                int copyWidth = Math.Min(display.Width, sourceWidth);
                int copyHeight = Math.Min(display.Height, sourceHeight);
                int destinationX = Math.Max(0, (display.Width - copyWidth) / 2);
                int destinationY = Math.Max(0, (display.Height - copyHeight) / 2);
                int dirtyX = destinationX;
                int dirtyY = destinationY;
                int dirtyRight = destinationX + copyWidth;
                int dirtyBottom = destinationY + copyHeight;
                if (displayFrameRectValid)
                {
                    dirtyX = Math.Min(dirtyX, displayFrameX);
                    dirtyY = Math.Min(dirtyY, displayFrameY);
                    dirtyRight = Math.Max(dirtyRight, displayFrameX + displayFrameWidth);
                    dirtyBottom = Math.Max(dirtyBottom, displayFrameY + displayFrameHeight);
                }

                int dirtyWidth = dirtyRight - dirtyX;
                int dirtyHeight = dirtyBottom - dirtyY;
                FillDisplayRect(destination, display.Width, dirtyX, dirtyY, dirtyWidth, dirtyHeight, Background);

                for (int y = 0; y < copyHeight; y++)
                {
                    int sourceY = sourceMinY + y;
                    int sourceRow = sourceY * BeamFramebufferWidth;
                    int destinationRow = ((y + destinationY) * display.Width) + destinationX;

                    Array.Copy(beamCompletedFrame, sourceRow + beamCompletedMinX, destination, destinationRow, copyWidth);
                }

                display.MarkFrameDirty(dirtyX, dirtyY, dirtyWidth, dirtyHeight);
                displayFrameX = destinationX;
                displayFrameY = destinationY;
                displayFrameWidth = copyWidth;
                displayFrameHeight = copyHeight;
                displayFrameRectValid = true;
                return true;
            }
        }

        private static void FillDisplayRect(uint[] destination, int stride, int x, int y, int width, int height, uint colour)
        {
            for (int row = 0; row < height; row++)
                Array.Fill(destination, colour, ((y + row) * stride) + x, width);
        }

        private void HandleBeamDisplayStartRupture(int register)
        {
            if (register != CrtcDisplayStartHighRegister)
                return;

            // Phoenix.ssd, a modern BBC conversion, abuses late R12/R13 display-start
            // writes for its playfield/score split. A trial that stopped the late
            // bitmap R12/R13 value becoming the next frame's start fixed the displaced
            // playfield and duplicate top score, but also suppressed the lower score
            // panel. The eventual fix needs to separate frame-start latching from the
            // lower-screen split rather than simply blocking or accepting the write.
            if (beamInVSync || beamFirstScanline)
                return;

            if (crtcRegisters[CrtcVerticalDisplayedRegister] > 1)
                return;

            beamPendingDisplayStartRuptureAddress = (crtcRegisters[CrtcDisplayStartLowRegister]
                | (crtcRegisters[CrtcDisplayStartHighRegister] << 8)) & 0x3FFF;
            beamPendingDisplayStartRupture = true;
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void ValidateBeamCrtcProgramming(int changedRegister)
        {
            int horizontalTotal = crtcRegisters[CrtcHorizontalTotalRegister] + 1;
            int horizontalDisplayed = crtcRegisters[CrtcHorizontalDisplayedRegister];
            int horizontalSync = crtcRegisters[2];
            int horizontalSyncWidth = crtcRegisters[3] & 0x0F;

            if ((changedRegister is CrtcHorizontalTotalRegister or CrtcHorizontalDisplayedRegister)
                && horizontalDisplayed != 0
                && horizontalDisplayed >= horizontalTotal)
            {
                TraceBeamCrtcDiagnostic($"R1 horizontal displayed ({horizontalDisplayed}) must be less than R0+1 ({horizontalTotal}).");
            }

            if ((changedRegister is CrtcHorizontalTotalRegister or 2)
                && horizontalSync > crtcRegisters[CrtcHorizontalTotalRegister])
            {
                TraceBeamCrtcDiagnostic($"R2 horizontal sync position ({horizontalSync}) is beyond R0 horizontal total ({crtcRegisters[CrtcHorizontalTotalRegister]}).");
            }

            if (changedRegister == 3 && horizontalSyncWidth == 0)
                TraceBeamCrtcDiagnostic("R3 horizontal sync width is 0; HD6845 documents HSW=0 as invalid.");

            int verticalTotal = GetBeamVerticalTotal() + 1;
            int verticalDisplayed = GetBeamVerticalDisplayed();
            int verticalSync = GetBeamVerticalSync();

            if ((changedRegister is CrtcVerticalTotalRegister or CrtcVerticalDisplayedRegister)
                && verticalDisplayed != 0
                && verticalDisplayed >= verticalTotal)
            {
                TraceBeamCrtcDiagnostic($"R6 vertical displayed ({verticalDisplayed}) must be less than R4+1 ({verticalTotal}).");
            }

            if ((changedRegister is CrtcVerticalTotalRegister or CrtcVerticalSyncRegister)
                && verticalSync > GetBeamVerticalTotal())
            {
                TraceBeamCrtcDiagnostic($"R7 vertical sync position ({verticalSync}) is beyond R4 vertical total ({GetBeamVerticalTotal()}).");
            }

            int cursorStart = crtcRegisters[CrtcCursorStartRegister] & 0x1F;
            int cursorEnd = crtcRegisters[CrtcCursorEndRegister] & 0x1F;
            int maxRaster = GetBeamMaximumRasterAddress();

            if ((changedRegister is CrtcCursorStartRegister or CrtcCursorEndRegister or CrtcScanLinesPerCharacterRegister)
                && cursorStart <= cursorEnd
                && (cursorStart > maxRaster || cursorEnd > maxRaster))
            {
                TraceBeamCrtcDiagnostic($"Cursor raster range R10/R11 ({cursorStart}-{cursorEnd}) exceeds R9 maximum raster ({maxRaster}).");
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void TraceBeamCrtcDiagnostic(string message)
        {
            System.Diagnostics.Debug.WriteLine($"CRTC diagnostic: {message}");
        }

        private void UpdateBeamStableVerticalTiming()
        {
            if (crtcRegisters[CrtcVerticalTotalRegister] < 0x10
                || crtcRegisters[CrtcVerticalDisplayedRegister] < 0x10
                || crtcRegisters[CrtcVerticalSyncRegister] < 0x10)
            {
                return;
            }

            beamStableVerticalTotal = crtcRegisters[CrtcVerticalTotalRegister];
            beamStableVerticalAdjust = crtcRegisters[CrtcVerticalAdjustRegister] & 0x1F;
            beamStableVerticalSync = crtcRegisters[CrtcVerticalSyncRegister];
            beamStableVerticalTimingValid = true;
        }

        private bool BeamVisibleRuptureTimingActive =>
            beamStableVerticalTimingValid
            && crtcRegisters[CrtcVerticalTotalRegister] <= 0x07
            && crtcRegisters[CrtcVerticalDisplayedRegister] <= 0x01
            && crtcRegisters[CrtcVerticalSyncRegister] <= 0x03;

        private int GetBeamVerticalTotal() =>
            BeamVisibleRuptureTimingActive ? beamStableVerticalTotal : crtcRegisters[CrtcVerticalTotalRegister];

        private int GetBeamVerticalAdjust() =>
            BeamVisibleRuptureTimingActive ? beamStableVerticalAdjust : crtcRegisters[CrtcVerticalAdjustRegister];

        private int GetBeamVerticalDisplayed() => crtcRegisters[CrtcVerticalDisplayedRegister];

        private int GetBeamVerticalSync() =>
            BeamVisibleRuptureTimingActive ? beamStableVerticalSync : crtcRegisters[CrtcVerticalSyncRegister];

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

        public string[] ReadMode7TextRows()
        {
            string[] rows = new string[Mode7Rows];

            for (int row = 0; row < Mode7Rows; row++)
            {
                char[] line = new char[Mode7Columns];
                int baseAddress = Mode7ScreenStart + row * Mode7Columns;

                for (int column = 0; column < Mode7Columns; column++)
                {
                    byte value = memory[baseAddress + column];
                    value &= 0x7F;
                    line[column] = value >= 32 && value < 127 ? (char)value : ' ';
                }

                rows[row] = new string(line).TrimEnd();
            }

            return rows;
        }

        private void ResetPalette()
        {
            ResetPaletteArray(paletteRegisters);
            lastPaletteWrite = 0;
        }

        private static void ResetPaletteArray(byte[] palette)
        {
            for (int i = 0; i < palette.Length; i++)
                palette[i] = (byte)(i & 0x07);
        }

        private static byte DecodePhysicalColour(byte paletteRegisterValue)
        {
            return (byte)((paletteRegisterValue & 0x0F) ^ 0x07);
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

        private sealed class TeletextChip
        {
            private enum GlyphSet
            {
                Normal,
                Graphics,
                Separated
            }

            private readonly uint[] colours;
            private readonly byte[] dataQueue = new byte[4];
            private int previousColour;
            private int foregroundColour;
            private int backgroundColour;
            private bool separatedGraphics;
            private bool doubleHeight;
            private bool previousDoubleHeight;
            private bool secondHalfOfDoubleHeight;
            private bool sawDoubleHeight;
            private bool graphicsMode;
            private bool flash;
            private bool flashOn;
            private int flashTime;
            private byte heldCharacter;
            private bool holdCharacter;
            private int scanlineCounter;
            private bool dewLevel;
            private bool displayTimingLevel;
            private bool rowAddressBit0;
            private GlyphSet nextGlyphSet;
            private GlyphSet currentGlyphSet;
            private GlyphSet heldGlyphSet;
            private const int TeletextCellWidth = TeletextDisplayCharacterWidth;

            public TeletextChip(uint[] colours)
            {
                this.colours = colours;
                Reset();
            }

            public void Reset()
            {
                Array.Clear(dataQueue);
                previousColour = 0;
                foregroundColour = 7;
                backgroundColour = 0;
                separatedGraphics = false;
                doubleHeight = false;
                previousDoubleHeight = false;
                secondHalfOfDoubleHeight = false;
                sawDoubleHeight = false;
                graphicsMode = false;
                flash = false;
                flashOn = false;
                flashTime = 0;
                heldCharacter = 0x20;
                holdCharacter = false;
                scanlineCounter = 0;
                dewLevel = false;
                displayTimingLevel = false;
                rowAddressBit0 = false;
                nextGlyphSet = GlyphSet.Normal;
                currentGlyphSet = GlyphSet.Normal;
                heldGlyphSet = GlyphSet.Normal;
            }

            public void SaveState(BinaryWriter writer)
            {
                writer.Write(dataQueue.Length);
                writer.Write(dataQueue);
                writer.Write(previousColour);
                writer.Write(foregroundColour);
                writer.Write(backgroundColour);
                writer.Write(separatedGraphics);
                writer.Write(doubleHeight);
                writer.Write(previousDoubleHeight);
                writer.Write(secondHalfOfDoubleHeight);
                writer.Write(sawDoubleHeight);
                writer.Write(graphicsMode);
                writer.Write(flash);
                writer.Write(flashOn);
                writer.Write(flashTime);
                writer.Write(heldCharacter);
                writer.Write(holdCharacter);
                writer.Write(scanlineCounter);
                writer.Write(dewLevel);
                writer.Write(displayTimingLevel);
                writer.Write(rowAddressBit0);
                writer.Write((int)nextGlyphSet);
                writer.Write((int)currentGlyphSet);
                writer.Write((int)heldGlyphSet);
            }

            public void LoadState(BinaryReader reader)
            {
                int queueLength = reader.ReadInt32();
                if (queueLength != dataQueue.Length)
                    throw new InvalidDataException("Save state has an incompatible teletext data queue.");

                byte[] queue = reader.ReadBytes(queueLength);
                if (queue.Length != queueLength)
                    throw new EndOfStreamException();

                queue.CopyTo(dataQueue, 0);
                previousColour = reader.ReadInt32();
                foregroundColour = reader.ReadInt32();
                backgroundColour = reader.ReadInt32();
                separatedGraphics = reader.ReadBoolean();
                doubleHeight = reader.ReadBoolean();
                previousDoubleHeight = reader.ReadBoolean();
                secondHalfOfDoubleHeight = reader.ReadBoolean();
                sawDoubleHeight = reader.ReadBoolean();
                graphicsMode = reader.ReadBoolean();
                flash = reader.ReadBoolean();
                flashOn = reader.ReadBoolean();
                flashTime = reader.ReadInt32();
                heldCharacter = reader.ReadByte();
                holdCharacter = reader.ReadBoolean();
                scanlineCounter = reader.ReadInt32();
                dewLevel = reader.ReadBoolean();
                displayTimingLevel = reader.ReadBoolean();
                rowAddressBit0 = reader.ReadBoolean();
                nextGlyphSet = (GlyphSet)reader.ReadInt32();
                currentGlyphSet = (GlyphSet)reader.ReadInt32();
                heldGlyphSet = (GlyphSet)reader.ReadInt32();
            }

            /// <summary>The SAA5050 sees a delayed character stream, so control codes affect following cells.</summary>
            public void FetchData(byte data)
            {
                dataQueue[0] = dataQueue[1];
                dataQueue[1] = dataQueue[2];
                dataQueue[2] = dataQueue[3];
                dataQueue[3] = (byte)(data & 0x7F);
            }

            /// <summary>DEW falling marks the end of the teletext data entry window for a row.</summary>
            public void SetDEW(bool level)
            {
                bool oldLevel = dewLevel;
                dewLevel = level;
                if (!oldLevel || level)
                    return;

                scanlineCounter = 0;
                secondHalfOfDoubleHeight = false;

                flashTime = (flashTime + 1) & 0x3F;
                flashOn = flashTime < 16;
            }

            /// <summary>DISPTMG falling ends a teletext character row and advances double-height state.</summary>
            public void SetDISPTMG(bool level)
            {
                bool oldLevel = displayTimingLevel;
                displayTimingLevel = level;
                if (!oldLevel || level)
                    return;

                foregroundColour = 7;
                backgroundColour = 0;
                holdCharacter = false;
                heldCharacter = 0x20;
                nextGlyphSet = GlyphSet.Normal;
                heldGlyphSet = GlyphSet.Normal;
                flash = false;
                separatedGraphics = false;
                graphicsMode = false;
                doubleHeight = false;

                scanlineCounter++;
                if (scanlineCounter == 10)
                {
                    scanlineCounter = 0;
                    if (secondHalfOfDoubleHeight)
                        secondHalfOfDoubleHeight = false;
                    else
                        secondHalfOfDoubleHeight = sawDoubleHeight;
                }

                sawDoubleHeight = false;
            }

            /// <summary>RA0 selects the upper or lower half of separated mosaic pixels within the scanline pair.</summary>
            public void SetRA0(bool level)
            {
                rowAddressBit0 = level;
            }

            public void Render(uint[] buffer, int offset, int width)
            {
                if (offset < 0 || offset >= buffer.Length)
                    return;

                byte data = dataQueue[0];
                int scanline = scanlineCounter << 1;
                if (rowAddressBit0)
                    scanline++;

                previousDoubleHeight = doubleHeight;
                previousColour = foregroundColour;
                currentGlyphSet = nextGlyphSet;

                bool flashThisCell = flash;
                if (data < 0x20)
                {
                    data = HandleControlCode(data);
                }
                else if (graphicsMode)
                {
                    if ((data & 0x20) != 0)
                    {
                        heldCharacter = data;
                        heldGlyphSet = currentGlyphSet;
                    }
                }
                else
                {
                    heldCharacter = 0x20;
                }

                if (previousDoubleHeight)
                {
                    scanline >>= 1;
                    if (secondHalfOfDoubleHeight)
                        scanline += 10;
                }

                if (flashThisCell && !flash)
                    flashThisCell = false;

                uint background = colours[backgroundColour & 0x07];
                if ((flashThisCell && flashOn) || (secondHalfOfDoubleHeight && !doubleHeight))
                {
                    FillRun(buffer, offset, width, background);
                    return;
                }

                uint foreground = colours[previousColour & 0x07];
                int rowStart = offset - (offset % width);
                int maxOffset = Math.Min(rowStart + width, buffer.Length);

                ushort mask = GetRowMask(data, scanline, currentGlyphSet);
                for (int pixel = 0; pixel < TeletextCellWidth && offset + pixel < maxOffset; pixel++)
                {
                    int sourcePixel = pixel * TeletextCharacterWidth / TeletextCellWidth;
                    buffer[offset + pixel] = (mask & (1 << (15 - sourcePixel))) != 0 ? foreground : background;
                }
            }

            private byte HandleControlCode(byte data)
            {
                bool wasGraphics = graphicsMode;
                bool wasHoldCharacter = holdCharacter;

                switch (data)
                {
                    case >= 1 and <= 7:
                        graphicsMode = false;
                        foregroundColour = data;
                        SetNextGlyphSet();
                        break;

                    case 8:
                        flash = true;
                        break;

                    case 9:
                        flash = false;
                        break;

                    case 12:
                    case 13:
                        doubleHeight = (data & 1) != 0;
                        if (doubleHeight)
                            sawDoubleHeight = true;
                        break;

                    case >= 17 and <= 23:
                        graphicsMode = true;
                        foregroundColour = data & 7;
                        SetNextGlyphSet();
                        break;

                    case 24:
                        foregroundColour = previousColour = backgroundColour;
                        break;

                    case 25:
                        separatedGraphics = false;
                        SetNextGlyphSet();
                        break;

                    case 26:
                        separatedGraphics = true;
                        SetNextGlyphSet();
                        break;

                    case 28:
                        backgroundColour = 0;
                        break;

                    case 29:
                        backgroundColour = foregroundColour;
                        break;

                    case 30:
                        holdCharacter = true;
                        break;

                    case 31:
                        holdCharacter = false;
                        break;
                }

                if (wasGraphics && (wasHoldCharacter || holdCharacter) && doubleHeight == previousDoubleHeight)
                {
                    data = heldCharacter;
                    if (data >= 0x40 && data < 0x60)
                        data = 0x20;
                    currentGlyphSet = heldGlyphSet;
                }
                else
                {
                    heldCharacter = 0x20;
                    data = 0x20;
                }

                return data;
            }

            private void SetNextGlyphSet()
            {
                nextGlyphSet = graphicsMode
                    ? separatedGraphics ? GlyphSet.Separated : GlyphSet.Graphics
                    : GlyphSet.Normal;
            }

            private static ushort GetRowMask(byte data, int scanline, GlyphSet glyphSet)
            {
                if ((uint)scanline >= 20)
                    return 0;

                return glyphSet switch
                {
                    GlyphSet.Graphics => SAA5050_Font.GetMosaicRowMask(data, scanline, separated: false),
                    GlyphSet.Separated => SAA5050_Font.GetMosaicRowMask(data, scanline, separated: true),
                    _ => SAA5050_Font.GetAlphanumericRowMask(data, scanline)
                };
            }

            private static void FillRun(uint[] buffer, int offset, int width, uint colour)
            {
                int rowStart = offset - (offset % width);
                int maxOffset = Math.Min(rowStart + width, buffer.Length);
                for (int pixel = 0; pixel < TeletextCellWidth && offset + pixel < maxOffset; pixel++)
                    buffer[offset + pixel] = colour;
            }
        }
    }

    /// <summary>The BBC Video ULA mode bits map to these MOS display modes.</summary>
    public enum BbcScreenMode
    {

        /// <summary>Used when a ULA control value does not map cleanly to a supported BBC mode.</summary>
        Unknown = -1,

        /// <summary>Mode 0: 640 x 256, 2 logical colours.</summary>
        Mode0 = 0,

        /// <summary>Mode 1: 320 x 256, 4 logical colours.</summary>
        Mode1 = 1,

        /// <summary>Mode 2: 160 x 256, 16 logical colours.</summary>
        Mode2 = 2,

        /// <summary>Mode 3 uses the Mode 0 ULA group with a different CRTC character layout.</summary>
        Mode3 = 3,

        /// <summary>Mode 4: 320 x 256, 2 logical colours.</summary>
        Mode4 = 4,

        /// <summary>Mode 5: 160 x 256, 4 logical colours.</summary>
        Mode5 = 5,

        /// <summary>Mode 6 uses the Mode 4 ULA group with a different CRTC character layout.</summary>
        Mode6 = 6,

        /// <summary>Mode 7 bypasses bitmap pixel decoding and uses the SAA5050 teletext character generator.</summary>
        Mode7 = 7
    }
}
