// ============================================================================
// Project:     BBC
// File:        UefTape.cs
// Description: UEF cassette image player for the BBC Serial ACIA path.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.IO.Compression;
using System.Text;

namespace BBC
{
    public sealed class UefTape
    {
        public const string RecordableOrigin = "BBC_MODEL_B_RECORDABLE_TAPE";

        private const int BbcClockHz = 2_000_000;
        private const int UefCarrierCyclesPerSecond = 1200;
        private const int TapeZeroToneHz = 1200;
        private const int TapeOneToneHz = 2400;
        private const int TapeBitCycles = BbcClockHz / UefCarrierCyclesPerSecond;
        private const int CounterStepCycles = BbcClockHz * 6 / 5;
        private const int FastTransportMultiplier = 20;
        private const int RecordedBlockCarrierCycles = BbcClockHz;
        private const float BlankTapeSeconds = 10 * 60;
        private const ushort OriginChunk = 0x0000;
        private const ushort ImplicitDataChunk = 0x0100;
        private const ushort DefinedDataChunk = 0x0104;
        private const ushort CarrierToneChunk = 0x0110;
        private const ushort CarrierToneChunkNoDummy = 0x0111;
        private const ushort IntegerGapChunk = 0x0112;
        private const ushort FloatingGapChunk = 0x0116;
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_TAPE_TRACE") == "1";
        private static readonly string TracePath = Environment.GetEnvironmentVariable("BBC_TAPE_TRACE_FILE") ?? "bbc-tape-trace.log";
        private static readonly object TraceSync = new object();
        private static bool traceStarted;

        private readonly SerialACIA serialAcia;
        private readonly SN76489_Sound tapeSound;
        private readonly object sync = new object();
        private readonly List<TapeEvent> events = new List<TapeEvent>();
        private string? mountedPath;
        private string? mountedFileName;
        private int eventIndex;
        private int delayCyclesRemaining;
        private int delayToneHz;
        private int characterCyclesRemaining;
        private bool playbackStarted;
        private bool reachedEnd;
        private bool transportPlaying;
        private bool paused;
        private bool recording;
        private bool recordingDataRunActive;
        private bool recordable;
        private bool dirty;
        private FastTapeTransport fastTransport;
        private byte characterToneByte;
        private int characterToneBitCount;
        private int characterToneBitIndex;
        private int characterToneBitCyclesRemaining;
        private long tapePositionCycles;
        private long counterResetPositionCycles;
        private long totalTapeCycles;
        private long pendingRecordedCarrierCycles;
        private long recordingWritePositionCycles;
        private readonly List<byte> recordingBlockBytes = new List<byte>();
        private bool tapePositionSeekNeeded;
        private string? lastTraceState;

        public UefTape(SerialACIA serialAcia, SN76489_Sound tapeSound)
        {
            this.serialAcia = serialAcia;
            this.tapeSound = tapeSound;
        }

        public bool HasTape
        {
            get
            {
                lock (sync)
                    return mountedPath is not null;
            }
        }

        public string? MountedFileName
        {
            get
            {
                lock (sync)
                    return mountedFileName;
            }
        }

        public bool Paused
        {
            get
            {
                lock (sync)
                    return paused;
            }
        }

        public bool Recordable
        {
            get
            {
                lock (sync)
                    return recordable;
            }
        }

        public bool Recording
        {
            get
            {
                lock (sync)
                    return recording;
            }
        }

        public bool Playing
        {
            get
            {
                lock (sync)
                    return transportPlaying && !recording && fastTransport == FastTapeTransport.None && !paused && !reachedEnd;
            }
        }

        public bool FastTransportActive
        {
            get
            {
                lock (sync)
                    return fastTransport != FastTapeTransport.None;
            }
        }

        public int Counter
        {
            get
            {
                lock (sync)
                    return (int)Math.Clamp((tapePositionCycles - counterResetPositionCycles) / CounterStepCycles, 0, 999);
            }
        }

        public bool CanPause
        {
            get
            {
                lock (sync)
                    return mountedPath is not null && !reachedEnd;
            }
        }

        public static void CreateBlankTape(string path, bool overwrite = false)
        {
            string fullPath = Path.GetFullPath(path);
            if (!overwrite && File.Exists(fullPath))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            using FileStream file = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using BinaryWriter writer = new BinaryWriter(file);
            writer.Write("UEF File!\0"u8);
            writer.Write((byte)0x05);
            writer.Write((byte)0x00);

            byte[] origin = Encoding.ASCII.GetBytes(RecordableOrigin);
            writer.Write(OriginChunk);
            writer.Write(origin.Length);
            writer.Write(origin);

            writer.Write(FloatingGapChunk);
            writer.Write(sizeof(float));
            writer.Write(BlankTapeSeconds);
            writer.Flush();
            file.Flush(flushToDisk: true);
        }

        public void Mount(string path)
        {
            ParsedUefTape loadedTape = ReadTape(path);

            lock (sync)
            {
                events.Clear();
                events.AddRange(loadedTape.Events);
                mountedPath = Path.GetFullPath(path);
                mountedFileName = Path.GetFileName(path);
                recordable = loadedTape.Recordable;
                recording = false;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                dirty = false;
                eventIndex = 0;
                delayCyclesRemaining = 0;
                delayToneHz = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                transportPlaying = false;
                paused = false;
                recording = false;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                dirty = false;
                fastTransport = FastTapeTransport.None;
                tapePositionCycles = 0;
                counterResetPositionCycles = 0;
                totalTapeCycles = GetTotalCycles(events);
                pendingRecordedCarrierCycles = 0;
                recordingWritePositionCycles = 0;
                tapePositionSeekNeeded = false;
                ResetCharacterTone();
                lastTraceState = null;
            }

            Trace($"mounted {Path.GetFileName(path)} events={loadedTape.Events.Count} recordable={(loadedTape.Recordable ? 1 : 0)}");
            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();
        }

        public void Unmount()
        {
            FlushRecording();

            lock (sync)
            {
                events.Clear();
                mountedPath = null;
                mountedFileName = null;
                recordable = false;
                recording = false;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                dirty = false;
                eventIndex = 0;
                delayCyclesRemaining = 0;
                delayToneHz = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                transportPlaying = false;
                paused = false;
                fastTransport = FastTapeTransport.None;
                tapePositionCycles = 0;
                counterResetPositionCycles = 0;
                totalTapeCycles = 0;
                pendingRecordedCarrierCycles = 0;
                recordingWritePositionCycles = 0;
                tapePositionSeekNeeded = false;
                ResetCharacterTone();
                lastTraceState = null;
            }

            Trace("unmounted");
            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();
        }

        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            lock (sync)
            {
                if (events.Count == 0)
                {
                    TraceStateOnce("idle no-events-or-end");
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                    return;
                }

                if (fastTransport != FastTapeTransport.None)
                {
                    FastTransport(cycles);
                    return;
                }

                if (recording)
                {
                    if (reachedEnd)
                    {
                        recording = false;
                        recordingDataRunActive = false;
                        recordingBlockBytes.Clear();
                        transportPlaying = false;
                        serialAcia.SetTapePlaying(false);
                        SilenceTapeTone();
                        FlushRecording(reloadTape: true);
                        return;
                    }

                    if (paused || !serialAcia.MotorRunning)
                    {
                        TraceStateOnce(paused ? "record paused" : "record motor-off");
                        serialAcia.SetTapePlaying(false);
                        SilenceTapeTone();
                        if (!paused)
                        {
                            FlushRecording(reloadTape: true);
                            recordingDataRunActive = false;
                            recordingBlockBytes.Clear();
                        }
                        return;
                    }

                    TraceStateOnce("record running");
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                    if (!recordingDataRunActive)
                    {
                        pendingRecordedCarrierCycles += cycles;
                        dirty = true;
                    }
                    AdvanceTapeCounter(cycles);
                    if (tapePositionCycles >= totalTapeCycles)
                    {
                        reachedEnd = true;
                        recording = false;
                        recordingDataRunActive = false;
                        recordingBlockBytes.Clear();
                        transportPlaying = false;
                        FlushRecording(reloadTape: true);
                    }
                    return;
                }

                if (reachedEnd)
                {
                    TraceStateOnce("idle no-events-or-end");
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                    return;
                }

                if (paused || !transportPlaying)
                {
                    TraceStateOnce(paused ? "blocked paused" : "blocked transport-stopped");
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                    return;
                }

                if (!serialAcia.MotorRunning)
                {
                    TraceStateOnce("blocked motor-off");
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                    return;
                }

                playbackStarted = true;
                serialAcia.SetTapePlaying(true);
                TraceStateOnce("running");

                int remainingCycles = cycles;
                while (remainingCycles > 0 && (eventIndex < events.Count || delayCyclesRemaining > 0 || characterCyclesRemaining > 0))
                {
                    if (delayCyclesRemaining > 0)
                    {
                        SetTapeTone(delayToneHz);
                        int consumed = Math.Min(remainingCycles, delayCyclesRemaining);
                        delayCyclesRemaining -= consumed;
                        remainingCycles -= consumed;
                        AdvanceTapeCounter(consumed);
                        if (delayCyclesRemaining > 0)
                            return;
                    }

                    if (characterCyclesRemaining > 0)
                    {
                        ApplyCharacterTone();
                        int consumed = Math.Min(remainingCycles, characterCyclesRemaining);
                        characterCyclesRemaining -= consumed;
                        remainingCycles -= consumed;
                        AdvanceTapeCounter(consumed);
                        AdvanceCharacterTone(consumed);
                        if (characterCyclesRemaining > 0)
                            return;
                    }

                    if (eventIndex >= events.Count)
                        break;

                    TapeEvent tapeEvent = events[eventIndex];
                    if (tapeEvent.Kind == TapeEventKind.Trace)
                    {
                        Trace($"event {eventIndex}/{events.Count} {tapeEvent.Label}");
                        eventIndex++;
                        continue;
                    }

                    if (tapeEvent.Kind == TapeEventKind.Carrier)
                    {
                        Trace($"event {eventIndex}/{events.Count} carrier cycles={tapeEvent.Cycles} motor={(serialAcia.MotorRunning ? 1 : 0)} carrier={(serialAcia.CarrierPresent ? 1 : 0)}");
                        serialAcia.SetCarrierPresent(true);
                        SetTapeTone(TapeOneToneHz);
                        delayCyclesRemaining = tapeEvent.Cycles;
                        delayToneHz = TapeOneToneHz;
                        eventIndex++;
                        return;
                    }

                    if (tapeEvent.Kind == TapeEventKind.CarrierDetect)
                    {
                        Trace($"event {eventIndex}/{events.Count} carrier-detect pulse");
                        serialAcia.PulseTapeCarrierDetect();
                        eventIndex++;
                        return;
                    }

                    if (tapeEvent.Kind == TapeEventKind.Gap)
                    {
                        Trace($"event {eventIndex}/{events.Count} gap cycles={tapeEvent.Cycles} motor={(serialAcia.MotorRunning ? 1 : 0)} carrier={(serialAcia.CarrierPresent ? 1 : 0)}");
                        serialAcia.SetCarrierPresent(false);
                        SilenceTapeTone();
                        delayCyclesRemaining = tapeEvent.Cycles;
                        delayToneHz = 0;
                        eventIndex++;
                        continue;
                    }

                    if (tapeEvent.Cycles > 0)
                    {
                        SilenceTapeTone();
                        delayCyclesRemaining = tapeEvent.Cycles;
                        delayToneHz = 0;
                        eventIndex++;
                        continue;
                    }

                    if (!serialAcia.CanReceiveByte)
                    {
                        TraceStateOnce("blocked acia-full");
                        return;
                    }

                    if (tapeEvent.Byte != 0xDC)
                        serialAcia.ClearTapeCarrierDetect();
                    serialAcia.QueueTapeByte(tapeEvent.Byte);
                    eventIndex++;
                    characterCyclesRemaining = CharacterCycles(tapeEvent.BitCount);
                    StartCharacterTone(tapeEvent.Byte, tapeEvent.BitCount);
                }

                if (eventIndex >= events.Count && delayCyclesRemaining == 0 && characterCyclesRemaining == 0)
                {
                    Trace("reached end");
                    reachedEnd = true;
                    transportPlaying = false;
                    playbackStarted = false;
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                }
            }
        }

        public void ResetPlayback()
        {
            lock (sync)
            {
                eventIndex = 0;
                delayCyclesRemaining = 0;
                delayToneHz = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                transportPlaying = false;
                paused = false;
                fastTransport = FastTapeTransport.None;
                tapePositionCycles = 0;
                counterResetPositionCycles = 0;
                tapePositionSeekNeeded = false;
                ResetCharacterTone();
                lastTraceState = null;
            }

            Trace("reset playback");
            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();
        }

        public void SaveState(BinaryWriter writer)
        {
            lock (sync)
            {
                writer.Write(events.Count > 0);
                WriteString(writer, mountedPath);
                WriteString(writer, mountedFileName);
                writer.Write(eventIndex);
                writer.Write(delayCyclesRemaining);
                writer.Write(delayToneHz);
                writer.Write(characterCyclesRemaining);
                writer.Write(playbackStarted);
                writer.Write(reachedEnd);
                writer.Write(transportPlaying);
                writer.Write(paused);
                writer.Write(recording);
                writer.Write(recordingDataRunActive);
                writer.Write(recordable);
                writer.Write(recordingBlockBytes.Count);
                foreach (byte value in recordingBlockBytes)
                    writer.Write(value);
                writer.Write((int)fastTransport);
                writer.Write(tapePositionCycles);
                writer.Write(counterResetPositionCycles);
                writer.Write(totalTapeCycles);
                writer.Write(pendingRecordedCarrierCycles);
                writer.Write(recordingWritePositionCycles);
                writer.Write(tapePositionSeekNeeded);
            }
        }

        public void LoadState(BinaryReader reader)
        {
            bool hadTape = reader.ReadBoolean();
            string? path = ReadString(reader);
            string? fileName = ReadString(reader);
            int savedEventIndex = reader.ReadInt32();
            int savedDelayCycles = reader.ReadInt32();
            int savedDelayToneHz = reader.ReadInt32();
            int savedCharacterCycles = reader.ReadInt32();
            bool savedPlaybackStarted = reader.ReadBoolean();
            bool savedReachedEnd = reader.ReadBoolean();
            bool savedTransportPlaying = reader.ReadBoolean();
            bool savedPaused = reader.ReadBoolean();
            bool savedRecording = reader.ReadBoolean();
            bool savedRecordingDataRunActive = reader.ReadBoolean();
            bool savedRecordable = reader.ReadBoolean();
            int savedRecordingBlockByteCount = reader.ReadInt32();
            byte[] savedRecordingBlockBytes = reader.ReadBytes(Math.Max(0, savedRecordingBlockByteCount));
            FastTapeTransport savedFastTransport = (FastTapeTransport)reader.ReadInt32();
            long savedTapePositionCycles = reader.ReadInt64();
            long savedCounterResetPositionCycles = reader.ReadInt64();
            long savedTotalTapeCycles = reader.ReadInt64();
            long savedPendingRecordedCarrierCycles = reader.ReadInt64();
            long savedRecordingWritePositionCycles = reader.ReadInt64();
            bool savedTapePositionSeekNeeded = reader.ReadBoolean();

            lock (sync)
            {
                events.Clear();
                mountedPath = null;
                mountedFileName = null;
                eventIndex = 0;
                delayCyclesRemaining = 0;
                delayToneHz = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                transportPlaying = false;
                paused = false;
                recordable = false;
                recording = false;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                dirty = false;
                fastTransport = FastTapeTransport.None;
                tapePositionCycles = 0;
                counterResetPositionCycles = 0;
                totalTapeCycles = 0;
                pendingRecordedCarrierCycles = 0;
                recordingWritePositionCycles = 0;
                tapePositionSeekNeeded = false;
                ResetCharacterTone();
                lastTraceState = null;
            }

            if (!hadTape || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                serialAcia.SetTapePlaying(false);
                SilenceTapeTone();
                return;
            }

            ParsedUefTape loadedTape = ReadTape(path);
            lock (sync)
            {
                events.AddRange(loadedTape.Events);
                mountedPath = path;
                mountedFileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName;
                recordable = loadedTape.Recordable || savedRecordable;
                eventIndex = Math.Clamp(savedEventIndex, 0, events.Count);
                delayCyclesRemaining = Math.Max(0, savedDelayCycles);
                delayToneHz = Math.Max(0, savedDelayToneHz);
                characterCyclesRemaining = Math.Max(0, savedCharacterCycles);
                playbackStarted = savedPlaybackStarted;
                reachedEnd = savedReachedEnd || eventIndex >= events.Count;
                recording = savedRecording && recordable && !reachedEnd;
                recordingDataRunActive = savedRecordingDataRunActive && recording;
                recordingBlockBytes.Clear();
                if (recordingDataRunActive)
                    recordingBlockBytes.AddRange(savedRecordingBlockBytes);
                transportPlaying = savedTransportPlaying && !reachedEnd;
                paused = savedPaused;
                fastTransport = Enum.IsDefined(savedFastTransport) ? savedFastTransport : FastTapeTransport.None;
                totalTapeCycles = savedTotalTapeCycles > 0 ? savedTotalTapeCycles : GetTotalCycles(events);
                tapePositionCycles = Math.Clamp(savedTapePositionCycles, 0, totalTapeCycles);
                counterResetPositionCycles = Math.Clamp(savedCounterResetPositionCycles, 0, totalTapeCycles);
                pendingRecordedCarrierCycles = Math.Max(0, savedPendingRecordedCarrierCycles);
                recordingWritePositionCycles = Math.Clamp(savedRecordingWritePositionCycles, 0, totalTapeCycles);
                tapePositionSeekNeeded = savedTapePositionSeekNeeded;
                ResetCharacterTone();
                lastTraceState = null;
            }

            Trace($"loaded state events={loadedTape.Events.Count} index={eventIndex}");
            serialAcia.ClearTapeReadRequest();
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();
        }

        public bool TogglePaused()
        {
            lock (sync)
            {
                if (mountedPath is null || reachedEnd)
                    return false;

                AlignPlaybackToTapePosition();
                paused = !paused;
                fastTransport = FastTapeTransport.None;
                if (paused)
                {
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                }
                return paused;
            }
        }

        public bool ToggleRecording()
        {
            lock (sync)
            {
                if (mountedPath is null || !recordable || reachedEnd)
                    return false;

                if (recording)
                {
                    recording = false;
                    recordingDataRunActive = false;
                    recordingBlockBytes.Clear();
                    transportPlaying = false;
                    paused = false;
                    lastTraceState = null;
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                    FlushRecording(reloadTape: true);
                    Trace($"record stop position={tapePositionCycles}/{totalTapeCycles}");
                    return false;
                }

                AlignPlaybackToTapePosition();
                recording = true;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                recordingWritePositionCycles = tapePositionCycles;
                transportPlaying = true;
                fastTransport = FastTapeTransport.None;
                paused = false;
                lastTraceState = null;
                Trace($"record position={tapePositionCycles}/{totalTapeCycles}");
                return true;
            }
        }

        public bool Play()
        {
            lock (sync)
            {
                if (mountedPath is null || reachedEnd || recording)
                    return false;

                AlignPlaybackToTapePosition();
                transportPlaying = true;
                fastTransport = FastTapeTransport.None;
                paused = false;
                lastTraceState = null;
                Trace($"play index={eventIndex}/{events.Count}");
                return true;
            }
        }

        public bool Stop()
        {
            lock (sync)
            {
                if (mountedPath is null)
                    return false;

                AlignPlaybackToTapePosition();
                transportPlaying = false;
                paused = false;
                recording = false;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                fastTransport = FastTapeTransport.None;
                playbackStarted = false;
                lastTraceState = null;
                serialAcia.SetTapePlaying(false);
                SilenceTapeTone();
                FlushRecording(reloadTape: true);
                Trace($"stop index={eventIndex}/{events.Count}");
                return true;
            }
        }

        public bool FastForward()
        {
            return StartFastTransport(FastTapeTransport.Forward);
        }

        public bool Rewind()
        {
            return StartFastTransport(FastTapeTransport.Rewind);
        }

        public bool ResetCounter()
        {
            lock (sync)
            {
                if (mountedPath is null)
                    return false;

                counterResetPositionCycles = tapePositionCycles;
                return true;
            }
        }

        private bool StartFastTransport(FastTapeTransport mode)
        {
            lock (sync)
            {
                if (mountedPath is null)
                    return false;

                if (mode == FastTapeTransport.Forward && tapePositionCycles >= totalTapeCycles)
                    return false;

                if (mode == FastTapeTransport.Rewind && tapePositionCycles <= 0)
                    return false;

                fastTransport = mode;
                transportPlaying = false;
                paused = false;
                recording = false;
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
                playbackStarted = false;
                lastTraceState = null;
                serialAcia.SetTapePlaying(false);
                SilenceTapeTone();
                FlushRecording(reloadTape: true);
                Trace($"{(mode == FastTapeTransport.Forward ? "fast-forward" : "rewind")} position={tapePositionCycles}/{totalTapeCycles}");
                return true;
            }
        }

        private void FastTransport(int cycles)
        {
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();

            long delta = (long)cycles * FastTransportMultiplier;
            long nextPosition = fastTransport == FastTapeTransport.Forward
                ? Math.Min(totalTapeCycles, tapePositionCycles + delta)
                : Math.Max(0, tapePositionCycles - delta);

            tapePositionCycles = nextPosition;
            tapePositionSeekNeeded = true;

            if (tapePositionCycles == 0 || tapePositionCycles >= totalTapeCycles)
            {
                SetTapePosition(tapePositionCycles);
                tapePositionSeekNeeded = false;
                fastTransport = FastTapeTransport.None;
                transportPlaying = false;
                paused = false;
            }
        }

        private void AdvanceTapeCounter(int cycles)
        {
            tapePositionCycles = Math.Min(totalTapeCycles, tapePositionCycles + Math.Max(0, cycles));
        }

        private void SetTapePosition(long positionCycles)
        {
            tapePositionCycles = Math.Clamp(positionCycles, 0, totalTapeCycles);
            tapePositionSeekNeeded = false;
            eventIndex = 0;
            delayCyclesRemaining = 0;
            delayToneHz = 0;
            characterCyclesRemaining = 0;
            playbackStarted = false;
            reachedEnd = tapePositionCycles >= totalTapeCycles && events.Count > 0;
            ResetCharacterTone();

            long remaining = tapePositionCycles;
            for (int i = 0; i < events.Count; i++)
            {
                int duration = GetEventCycles(events[i]);
                if (duration == 0)
                {
                    eventIndex = i + 1;
                    continue;
                }

                if (remaining == 0)
                {
                    eventIndex = i;
                    return;
                }

                if (remaining >= duration)
                {
                    remaining -= duration;
                    eventIndex = i + 1;
                    continue;
                }

                eventIndex = i + 1;
                int eventRemaining = duration - (int)remaining;
                if (events[i].Kind is TapeEventKind.Carrier or TapeEventKind.Gap)
                {
                    delayCyclesRemaining = eventRemaining;
                    delayToneHz = events[i].Kind == TapeEventKind.Carrier ? TapeOneToneHz : 0;
                }
                else if (events[i].Kind == TapeEventKind.Byte)
                {
                    characterCyclesRemaining = eventRemaining;
                    StartCharacterTone(events[i].Byte, events[i].BitCount);
                    SilenceTapeTone();
                }
                return;
            }

            eventIndex = events.Count;
        }

        private void AlignPlaybackToTapePosition()
        {
            if (tapePositionSeekNeeded)
                SetTapePosition(tapePositionCycles);
        }

        public void RecordByte(byte value)
        {
            lock (sync)
            {
                if (!recording || !recordable || paused || reachedEnd || mountedPath is null || !serialAcia.MotorRunning)
                    return;

                if (!recordingDataRunActive)
                {
                    if (value != 0x2A)
                        return;

                    MaterializePendingRecordedCarrier();
                    recordingWritePositionCycles = tapePositionCycles;
                    recordingBlockBytes.Clear();
                    recordingDataRunActive = true;
                }

                InsertRecordedByte(recordingWritePositionCycles, value);
                recordingBlockBytes.Add(value);
                dirty = true;
                tapePositionSeekNeeded = true;
                recordingWritePositionCycles = Math.Min(totalTapeCycles, recordingWritePositionCycles + CharacterCycles(10));
                if (TryGetRecordedBlockLength(recordingBlockBytes, 0, out int blockLength)
                    && recordingBlockBytes.Count >= blockLength)
                {
                    recordingDataRunActive = false;
                    recordingBlockBytes.Clear();
                }
                Trace($"record byte ${value:X2} position={tapePositionCycles}/{totalTapeCycles}");
            }
        }

        public void CassetteMotorChanged(bool running)
        {
            if (running)
                return;

            lock (sync)
            {
                if (recording)
                {
                    FlushRecording(reloadTape: true);
                    recordingDataRunActive = false;
                    recordingBlockBytes.Clear();
                }
            }
        }

        private void InsertRecordedByte(long positionCycles, byte value)
        {
            InsertRecordedEvent(positionCycles, TapeEvent.ForByte(value));
        }

        private void InsertRecordedCarrier(long positionCycles, int cycles)
        {
            if (cycles <= 0)
                return;

            InsertRecordedEvent(positionCycles, TapeEvent.ForCarrier(cycles));
        }

        private void MaterializePendingRecordedCarrier()
        {
            if (pendingRecordedCarrierCycles <= 0)
                return;

            long carrierStartCycles = Math.Max(0, tapePositionCycles - pendingRecordedCarrierCycles);
            long remainingCycles = pendingRecordedCarrierCycles;
            while (remainingCycles > 0)
            {
                int chunkCycles = (int)Math.Min(int.MaxValue, remainingCycles);
                InsertRecordedCarrier(carrierStartCycles, chunkCycles);
                carrierStartCycles += chunkCycles;
                remainingCycles -= chunkCycles;
            }

            pendingRecordedCarrierCycles = 0;
        }

        private void InsertRecordedEvent(long positionCycles, TapeEvent recordedEvent)
        {
            int recordedCycles = GetEventCycles(recordedEvent);
            if (recordedCycles <= 0)
                return;

            List<TapeEvent> updatedEvents = new List<TapeEvent>(events.Count + 2);
            long remaining = Math.Clamp(positionCycles, 0, totalTapeCycles);
            bool inserted = false;

            foreach (TapeEvent tapeEvent in events)
            {
                int duration = GetEventCycles(tapeEvent);
                if (inserted || duration == 0)
                {
                    updatedEvents.Add(tapeEvent);
                    continue;
                }

                if (remaining >= duration)
                {
                    updatedEvents.Add(tapeEvent);
                    remaining -= duration;
                    continue;
                }

                if (tapeEvent.Kind == TapeEventKind.Gap)
                {
                    int beforeCycles = (int)remaining;
                    int afterCycles = Math.Max(0, duration - beforeCycles - recordedCycles);
                    if (beforeCycles > 0)
                        updatedEvents.Add(TapeEvent.ForGap(beforeCycles));
                    updatedEvents.Add(recordedEvent);
                    if (afterCycles > 0)
                        updatedEvents.Add(TapeEvent.ForGap(afterCycles));
                    inserted = true;
                    continue;
                }

                updatedEvents.Add(recordedEvent);
                updatedEvents.Add(tapeEvent);
                inserted = true;
            }

            if (!inserted)
            {
                long gapCycles = Math.Max(0, remaining);
                if (gapCycles > 0)
                    updatedEvents.Add(TapeEvent.ForGap((int)Math.Min(int.MaxValue, gapCycles)));
                updatedEvents.Add(recordedEvent);
            }

            events.Clear();
            CoalesceTapeEvents(updatedEvents, events);
            totalTapeCycles = Math.Max(totalTapeCycles, GetTotalCycles(events));
        }

        private static void CoalesceTapeEvents(List<TapeEvent> source, List<TapeEvent> destination)
        {
            foreach (TapeEvent tapeEvent in source)
            {
                if (destination.Count == 0)
                {
                    destination.Add(tapeEvent);
                    continue;
                }

                TapeEvent previous = destination[^1];
                if (previous.Kind == tapeEvent.Kind
                    && previous.Kind is TapeEventKind.Carrier or TapeEventKind.Gap
                    && previous.Cycles <= int.MaxValue - tapeEvent.Cycles)
                {
                    destination[^1] = previous.Kind == TapeEventKind.Carrier
                        ? TapeEvent.ForCarrier(previous.Cycles + tapeEvent.Cycles)
                        : TapeEvent.ForGap(previous.Cycles + tapeEvent.Cycles);
                    continue;
                }

                destination.Add(tapeEvent);
            }
        }

        private void FlushRecording(bool reloadTape = false)
        {
            MaterializePendingRecordedCarrier();
            if (!dirty || !recordable || mountedPath is null)
                return;

            string path = mountedPath;
            WriteTape(path, events);
            dirty = false;

            if (reloadTape)
                ReloadWrittenTape(path);
        }

        private void ReloadWrittenTape(string path)
        {
            ParsedUefTape loadedTape = ReadTape(path);
            long positionCycles = tapePositionCycles;
            bool wasRecording = recording;

            events.Clear();
            events.AddRange(loadedTape.Events);
            recordable = loadedTape.Recordable || recordable;
            totalTapeCycles = GetTotalCycles(events);
            tapePositionCycles = Math.Clamp(positionCycles, 0, totalTapeCycles);
            recordingWritePositionCycles = tapePositionCycles;
            recordingBlockBytes.Clear();
            pendingRecordedCarrierCycles = 0;
            tapePositionSeekNeeded = false;
            delayCyclesRemaining = 0;
            delayToneHz = 0;
            characterCyclesRemaining = 0;
            playbackStarted = false;
            reachedEnd = tapePositionCycles >= totalTapeCycles && events.Count > 0;
            ResetCharacterTone();
            SetTapePosition(tapePositionCycles);
            recording = wasRecording && recordable && !reachedEnd;
            if (!recording)
            {
                recordingDataRunActive = false;
                recordingBlockBytes.Clear();
            }
        }

        private void TraceStateOnce(string state)
        {
            if (!TraceEnabled)
                return;

            string next = eventIndex >= 0 && eventIndex < events.Count
                ? events[eventIndex].Describe()
                : "end";
            string line = $"{state} index={eventIndex}/{events.Count} motor={(serialAcia.MotorRunning ? 1 : 0)} tape={(serialAcia.TapePlaying ? 1 : 0)} carrier={(serialAcia.CarrierPresent ? 1 : 0)} next={next}";
            if (line == lastTraceState)
                return;

            lastTraceState = line;
            Trace(line);
        }

        private static int CharacterCycles(int bits)
        {
            return Math.Max(1, TapeBitCycles * Math.Max(1, bits));
        }

        private static long GetTotalCycles(List<TapeEvent> tapeEvents)
        {
            long total = 0;
            foreach (TapeEvent tapeEvent in tapeEvents)
                total += GetEventCycles(tapeEvent);
            return total;
        }

        private static int GetEventCycles(TapeEvent tapeEvent)
        {
            return tapeEvent.Kind switch
            {
                TapeEventKind.Byte => CharacterCycles(tapeEvent.BitCount),
                TapeEventKind.Carrier or TapeEventKind.Gap => Math.Max(0, tapeEvent.Cycles),
                _ => 0
            };
        }

        private void StartCharacterTone(byte value, int bitCount)
        {
            characterToneByte = value;
            characterToneBitCount = Math.Max(1, bitCount);
            characterToneBitIndex = 0;
            characterToneBitCyclesRemaining = TapeBitCycles;
            ApplyCharacterTone();
        }

        private void AdvanceCharacterTone(int cycles)
        {
            if (characterToneBitCount <= 0 || cycles <= 0)
                return;

            int remainingCycles = cycles;
            while (remainingCycles > 0 && characterToneBitIndex < characterToneBitCount)
            {
                int consumed = Math.Min(remainingCycles, characterToneBitCyclesRemaining);
                characterToneBitCyclesRemaining -= consumed;
                remainingCycles -= consumed;

                if (characterToneBitCyclesRemaining > 0)
                    continue;

                characterToneBitIndex++;
                characterToneBitCyclesRemaining = TapeBitCycles;
                ApplyCharacterTone();
            }
        }

        private void ApplyCharacterTone()
        {
            if (characterToneBitIndex >= characterToneBitCount)
                return;

            SetTapeTone(GetCharacterBitTone(characterToneByte, characterToneBitIndex));
        }

        private static int GetCharacterBitTone(byte value, int bitIndex)
        {
            if (bitIndex == 0)
                return TapeZeroToneHz;

            int dataBit = bitIndex - 1;
            if (dataBit < 8)
                return (value & (1 << dataBit)) != 0 ? TapeOneToneHz : TapeZeroToneHz;

            return TapeOneToneHz;
        }

        private void ResetCharacterTone()
        {
            characterToneByte = 0;
            characterToneBitCount = 0;
            characterToneBitIndex = 0;
            characterToneBitCyclesRemaining = 0;
        }

        private void SetTapeTone(int frequencyHz)
        {
            tapeSound.SetCassetteTone(frequencyHz);
        }

        private void SilenceTapeTone()
        {
            tapeSound.SetCassetteTone(0);
        }

        private static ParsedUefTape ReadTape(string path)
        {
            byte[] image = ReadUefBytes(path);
            if (image.Length < 12 || !image.AsSpan(0, 10).SequenceEqual("UEF File!\0"u8))
                throw new InvalidDataException("Not a UEF tape image.");

            List<TapeEvent> events = new List<TapeEvent>();
            bool recordable = false;
            int offset = 12;
            while (offset + 6 <= image.Length)
            {
                ushort chunk = ReadUInt16(image, offset);
                int length = ReadInt32(image, offset + 2);
                offset += 6;
                if (length < 0 || offset + length > image.Length)
                    throw new InvalidDataException("UEF chunk extends beyond the end of the file.");

                ReadOnlySpan<byte> data = image.AsSpan(offset, length);
                switch (chunk)
                {
                    case OriginChunk:
                        if (Encoding.ASCII.GetString(data).Contains(RecordableOrigin, StringComparison.Ordinal))
                            recordable = true;
                        break;

                    case ImplicitDataChunk:
                        AddBytes(events, data);
                        break;

                    case DefinedDataChunk:
                        AddDefinedBytes(events, data);
                        break;

                    case CarrierToneChunk:
                        AddCarrierDelay(events, data);
                        break;

                    case CarrierToneChunkNoDummy:
                        AddCarrierWithDummy(events, data);
                        break;

                    case IntegerGapChunk:
                        AddIntegerGap(events, data);
                        break;

                    case FloatingGapChunk:
                        AddFloatingGap(events, data);
                        break;
                }

                offset += length;
            }

            return new ParsedUefTape(events, recordable);
        }

        private static void WriteTape(string path, List<TapeEvent> tapeEvents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using BinaryWriter writer = new BinaryWriter(file);
            writer.Write("UEF File!\0"u8);
            writer.Write((byte)0x05);
            writer.Write((byte)0x00);

            byte[] origin = Encoding.ASCII.GetBytes(RecordableOrigin);
            writer.Write(OriginChunk);
            writer.Write(origin.Length);
            writer.Write(origin);

            List<byte> bytes = new List<byte>();
            foreach (TapeEvent tapeEvent in tapeEvents)
            {
                if (tapeEvent.Kind == TapeEventKind.Byte)
                {
                    bytes.Add(tapeEvent.Byte);
                    continue;
                }

                WriteImplicitData(writer, bytes);

                if (tapeEvent.Kind == TapeEventKind.Gap && tapeEvent.Cycles > 0)
                    WriteFloatingGap(writer, tapeEvent.Cycles);
                else if (tapeEvent.Kind == TapeEventKind.Carrier && tapeEvent.Cycles > 0)
                    WriteCarrier(writer, tapeEvent.Cycles);
            }

            WriteImplicitData(writer, bytes);
            writer.Flush();
            file.Flush(flushToDisk: true);
        }

        private static void WriteImplicitData(BinaryWriter writer, List<byte> bytes)
        {
            if (bytes.Count == 0)
                return;

            int offset = 0;
            bool wroteBlock = false;
            while (TryFindRecordedBlock(bytes, offset, out int blockStart, out int blockLength))
            {
                if (blockStart > offset)
                    WriteCarrier(writer, Math.Max(RecordedBlockCarrierCycles, CharacterCycles(10) * (blockStart - offset)));

                WriteSyncBytes(writer);
                WriteCarrier(writer, RecordedBlockCarrierCycles);
                WriteImplicitDataChunk(writer, bytes, blockStart, blockLength);
                offset = blockStart + blockLength;
                wroteBlock = true;
            }

            if (!wroteBlock)
                WriteImplicitDataChunk(writer, bytes, 0, bytes.Count);
            else if (offset < bytes.Count)
                WriteCarrier(writer, CharacterCycles(10) * (bytes.Count - offset));

            bytes.Clear();
        }

        private static void WriteImplicitDataChunk(BinaryWriter writer, List<byte> bytes, int offset, int count)
        {
            writer.Write(ImplicitDataChunk);
            writer.Write(count);
            for (int i = 0; i < count; i++)
                writer.Write(bytes[offset + i]);
        }

        private static bool TryFindRecordedBlock(List<byte> bytes, int offset, out int blockStart, out int blockLength)
        {
            for (int i = Math.Max(0, offset); i < bytes.Count; i++)
            {
                if (bytes[i] != 0x2A)
                    continue;

                if (TryGetRecordedBlockLength(bytes, i, out blockLength))
                {
                    blockStart = i;
                    return true;
                }
            }

            blockStart = -1;
            blockLength = 0;
            return false;
        }

        private static bool TryGetRecordedBlockLength(List<byte> bytes, int blockStart, out int blockLength)
        {
            blockLength = 0;

            int nameEnd = blockStart + 1;
            while (nameEnd < bytes.Count && bytes[nameEnd] != 0x00 && nameEnd - blockStart <= 12)
                nameEnd++;

            if (nameEnd >= bytes.Count || bytes[nameEnd] != 0x00 || nameEnd == blockStart + 1)
                return false;

            const int headerBytesAfterName = 19;
            int dataLengthOffset = nameEnd + 1 + 4 + 4 + 2;
            int dataOffset = nameEnd + 1 + headerBytesAfterName;
            if (dataLengthOffset + 1 >= bytes.Count || dataOffset + 2 > bytes.Count)
                return false;

            int dataLength = bytes[dataLengthOffset] | (bytes[dataLengthOffset + 1] << 8);
            int totalLength = dataOffset - blockStart + dataLength + 2;
            if (totalLength <= 0 || blockStart + totalLength > bytes.Count)
                return false;

            blockLength = totalLength;
            return true;
        }

        private static void WriteSyncBytes(BinaryWriter writer)
        {
            writer.Write(ImplicitDataChunk);
            writer.Write(1);
            writer.Write((byte)0xDC);
        }

        private static void WriteFloatingGap(BinaryWriter writer, int cycles)
        {
            writer.Write(FloatingGapChunk);
            writer.Write(sizeof(float));
            writer.Write(cycles / (float)BbcClockHz);
        }

        private static void WriteCarrier(BinaryWriter writer, int cycles)
        {
            long carrierCycles = Math.Max(0, (long)cycles * UefCarrierCyclesPerSecond / BbcClockHz);
            while (carrierCycles > 0)
            {
                ushort chunkCycles = (ushort)Math.Min(ushort.MaxValue, carrierCycles);
                writer.Write(CarrierToneChunk);
                writer.Write(sizeof(ushort));
                writer.Write(chunkCycles);
                carrierCycles -= chunkCycles;
            }
        }

        private static byte[] ReadUefBytes(string path)
        {
            byte[] image = File.ReadAllBytes(path);
            if (image.Length < 2 || image[0] != 0x1F || image[1] != 0x8B)
                return image;

            using MemoryStream source = new MemoryStream(image);
            using GZipStream gzip = new GZipStream(source, CompressionMode.Decompress);
            using MemoryStream decompressed = new MemoryStream();
            gzip.CopyTo(decompressed);
            return decompressed.ToArray();
        }

        private static void AddBytes(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            events.Add(TapeEvent.ForTrace(DescribeImplicitData(data)));

            if (data.Length > 0 && data[0] == 0x2A)
                events.Add(TapeEvent.ForByte(0xDC));

            foreach (byte value in data)
                events.Add(TapeEvent.ForByte(value));
        }

        private static void AddDefinedBytes(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            if (data.Length < 3)
                return;

            int dataBits = data[0];
            char parity = (char)data[1];
            int stopBits = data[2] & 0x7F;
            int bitCount = 1 + dataBits + (parity == 'N' ? 0 : 1) + stopBits;
            ReadOnlySpan<byte> bytes = data[3..];
            events.Add(TapeEvent.ForTrace($"defined-data {dataBits}{parity}{stopBits} bytes={bytes.Length}"));
            foreach (byte value in bytes)
                events.Add(TapeEvent.ForByte(dataBits == 7 ? (byte)(value & 0x7F) : value, bitCount));
        }

        private static void AddCarrierDelay(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            if (data.Length < 2)
                return;

            int cycles = CarrierCyclesToCpuCycles(ReadUInt16(data, 0));
            if (cycles > 0)
                AddCarrier(events, cycles);
        }

        private static void AddCarrierWithDummy(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            if (data.Length < 4)
                return;

            int beforeCycles = CarrierCyclesToCpuCycles(ReadUInt16(data, 0));
            if (beforeCycles > 0)
                AddCarrier(events, beforeCycles);

            events.Add(TapeEvent.ForTrace("carrier dummy byte $AA"));
            events.Add(TapeEvent.ForByte(0xAA));

            int afterCycles = CarrierCyclesToCpuCycles(ReadUInt16(data, 2));
            if (afterCycles > 0)
                AddCarrier(events, afterCycles);
        }

        private static void AddIntegerGap(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            if (data.Length < 2)
                return;

            int gap = ReadUInt16(data, 0);
            if (gap <= 0)
                return;

            int cycles = Math.Max(1, BbcClockHz / (2 * gap * UefCarrierCyclesPerSecond));
            if (cycles > 0)
            {
                events.Add(TapeEvent.ForTrace($"integer-gap raw={gap} cycles={cycles}"));
                events.Add(TapeEvent.ForGap(cycles));
            }
        }

        private static void AddFloatingGap(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            if (data.Length < 4)
                return;

            float seconds = BitConverter.ToSingle(data[..4]);
            if (seconds > 0)
            {
                int cycles = (int)Math.Min(int.MaxValue, seconds * BbcClockHz);
                events.Add(TapeEvent.ForTrace($"floating-gap seconds={seconds:0.000000} cycles={cycles}"));
                events.Add(TapeEvent.ForGap(cycles));
            }
        }

        private static int CarrierCyclesToCpuCycles(int carrierCycles)
        {
            long cycles = (long)Math.Max(0, carrierCycles) * BbcClockHz / UefCarrierCyclesPerSecond;
            return (int)Math.Min(int.MaxValue, cycles);
        }

        private static void AddCarrier(List<TapeEvent> events, int cycles)
        {
            if (cycles <= 0)
                return;

            events.Add(TapeEvent.ForTrace($"carrier cycles={cycles}"));
            events.Add(TapeEvent.ForCarrierDetect());
            events.Add(TapeEvent.ForCarrier(cycles));
        }

        private static string DescribeImplicitData(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return "implicit-data bytes=0";

            if (data.Length == 1)
                return $"implicit-data byte=${data[0]:X2}";

            if (data[0] != 0x2A)
                return $"implicit-data bytes={data.Length} first=${data[0]:X2}";

            int nameEnd = data[1..].IndexOf((byte)0x00);
            if (nameEnd < 0)
                return $"bbc-block bytes={data.Length} unterminated-name";

            nameEnd++;
            int header = nameEnd + 1;
            if (header + 19 > data.Length)
                return $"bbc-block name='{FormatTapeName(data[1..nameEnd])}' bytes={data.Length} short-header";

            uint load = ReadUInt32(data, header);
            uint exec = ReadUInt32(data, header + 4);
            ushort block = ReadUInt16(data, header + 8);
            ushort length = ReadUInt16(data, header + 10);
            byte flags = data[header + 12];
            return $"bbc-block name='{FormatTapeName(data[1..nameEnd])}' block={block} len=${length:X4} flags=${flags:X2} load=${load:X8} exec=${exec:X8} chunk-bytes={data.Length}";
        }

        private static string FormatTapeName(ReadOnlySpan<byte> name)
        {
            StringWriter writer = new StringWriter();
            foreach (byte value in name)
            {
                if (value >= 0x20 && value < 0x7F && value != '\\' && value != '\'')
                    writer.Write((char)value);
                else
                    writer.Write($"\\x{value:X2}");
            }
            return writer.ToString();
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
        {
            return data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        {
            return (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));
        }

        private static void Trace(string message)
        {
            if (!TraceEnabled)
                return;

            string line = $"[tape] {message}";
            lock (TraceSync)
            {
                if (!traceStarted)
                {
                    File.WriteAllText(TracePath, string.Empty);
                    traceStarted = true;
                }

                File.AppendAllText(TracePath, line + Environment.NewLine);
            }

            Console.WriteLine(line);
        }

        private static void WriteString(BinaryWriter writer, string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
                writer.Write(value);
        }

        private static string? ReadString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadString() : null;
        }

        private enum TapeEventKind
        {
            Byte,
            Carrier,
            CarrierDetect,
            Gap,
            Trace
        }

        private enum FastTapeTransport
        {
            None,
            Forward,
            Rewind
        }

        private readonly record struct ParsedUefTape(List<TapeEvent> Events, bool Recordable);

        private readonly record struct TapeEvent(TapeEventKind Kind, byte Byte, int Cycles, int BitCount, string? Label)
        {
            public static TapeEvent ForByte(byte value, int bitCount = 10)
            {
                return new TapeEvent(TapeEventKind.Byte, value, 0, bitCount, null);
            }

            public static TapeEvent ForCarrier(int cycles)
            {
                return new TapeEvent(TapeEventKind.Carrier, 0, cycles, 0, null);
            }

            public static TapeEvent ForCarrierDetect()
            {
                return new TapeEvent(TapeEventKind.CarrierDetect, 0, 0, 0, null);
            }

            public static TapeEvent ForGap(int cycles)
            {
                return new TapeEvent(TapeEventKind.Gap, 0, cycles, 0, null);
            }

            public static TapeEvent ForTrace(string label)
            {
                return new TapeEvent(TapeEventKind.Trace, 0, 0, 0, label);
            }

            public string Describe()
            {
                return Kind switch
                {
                    TapeEventKind.Byte => $"byte ${Byte:X2}",
                    TapeEventKind.Carrier => $"carrier {Cycles}",
                    TapeEventKind.CarrierDetect => "carrier-detect",
                    TapeEventKind.Gap => $"gap {Cycles}",
                    TapeEventKind.Trace => Label ?? "trace",
                    _ => Kind.ToString()
                };
            }
        }
    }
}
