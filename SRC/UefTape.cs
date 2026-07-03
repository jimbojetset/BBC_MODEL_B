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

namespace BBC
{
    public sealed class UefTape
    {
        private const int BbcClockHz = 2_000_000;
        private const int UefCarrierCyclesPerSecond = 1200;
        private const int TapeZeroToneHz = 1200;
        private const int TapeOneToneHz = 2400;
        private const int TapeBitCycles = BbcClockHz / UefCarrierCyclesPerSecond;
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
        private byte characterToneByte;
        private int characterToneBitCount;
        private int characterToneBitIndex;
        private int characterToneBitCyclesRemaining;
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
                    return events.Count > 0;
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

        public bool Playing
        {
            get
            {
                lock (sync)
                    return transportPlaying && !paused && !reachedEnd;
            }
        }

        public bool CanPause
        {
            get
            {
                lock (sync)
                    return events.Count > 0 && !reachedEnd;
            }
        }

        public void Mount(string path)
        {
            List<TapeEvent> loadedEvents = ReadEvents(path);
            if (loadedEvents.Count == 0)
                throw new InvalidDataException("UEF image does not contain any supported tape data.");

            lock (sync)
            {
                events.Clear();
                events.AddRange(loadedEvents);
                mountedPath = Path.GetFullPath(path);
                mountedFileName = Path.GetFileName(path);
                eventIndex = 0;
                delayCyclesRemaining = 0;
                delayToneHz = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                transportPlaying = false;
                paused = false;
                ResetCharacterTone();
                lastTraceState = null;
            }

            Trace($"mounted {Path.GetFileName(path)} events={loadedEvents.Count}");
            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();
        }

        public void Unmount()
        {
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
                if (events.Count == 0 || reachedEnd)
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
                while (remainingCycles > 0 && eventIndex < events.Count)
                {
                    if (delayCyclesRemaining > 0)
                    {
                        SetTapeTone(delayToneHz);
                        int consumed = Math.Min(remainingCycles, delayCyclesRemaining);
                        delayCyclesRemaining -= consumed;
                        remainingCycles -= consumed;
                        if (delayCyclesRemaining > 0)
                            return;
                    }

                    if (characterCyclesRemaining > 0)
                    {
                        ApplyCharacterTone();
                        int consumed = Math.Min(remainingCycles, characterCyclesRemaining);
                        characterCyclesRemaining -= consumed;
                        remainingCycles -= consumed;
                        AdvanceCharacterTone(consumed);
                        if (characterCyclesRemaining > 0)
                            return;
                    }

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

                if (eventIndex >= events.Count)
                {
                    Trace("reached end");
                    reachedEnd = true;
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
                ResetCharacterTone();
                lastTraceState = null;
            }

            if (!hadTape || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                serialAcia.SetTapePlaying(false);
                SilenceTapeTone();
                return;
            }

            List<TapeEvent> loadedEvents = ReadEvents(path);
            lock (sync)
            {
                events.AddRange(loadedEvents);
                mountedPath = path;
                mountedFileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName;
                eventIndex = Math.Clamp(savedEventIndex, 0, events.Count);
                delayCyclesRemaining = Math.Max(0, savedDelayCycles);
                delayToneHz = Math.Max(0, savedDelayToneHz);
                characterCyclesRemaining = Math.Max(0, savedCharacterCycles);
                playbackStarted = savedPlaybackStarted;
                reachedEnd = savedReachedEnd || eventIndex >= events.Count;
                transportPlaying = savedTransportPlaying && !reachedEnd;
                paused = savedPaused;
                ResetCharacterTone();
                lastTraceState = null;
            }

            Trace($"loaded state events={loadedEvents.Count} index={eventIndex}");
            serialAcia.ClearTapeReadRequest();
            serialAcia.SetTapePlaying(false);
            SilenceTapeTone();
        }

        public bool TogglePaused()
        {
            lock (sync)
            {
                if (events.Count == 0 || reachedEnd)
                    return false;

                paused = !paused;
                if (paused)
                {
                    serialAcia.SetTapePlaying(false);
                    SilenceTapeTone();
                }
                return paused;
            }
        }

        public bool Play()
        {
            lock (sync)
            {
                if (events.Count == 0 || reachedEnd)
                    return false;

                transportPlaying = true;
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
                if (events.Count == 0)
                    return false;

                transportPlaying = false;
                paused = false;
                playbackStarted = false;
                lastTraceState = null;
                serialAcia.SetTapePlaying(false);
                SilenceTapeTone();
                Trace($"stop index={eventIndex}/{events.Count}");
                return true;
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

        private static List<TapeEvent> ReadEvents(string path)
        {
            byte[] image = ReadUefBytes(path);
            if (image.Length < 12 || !image.AsSpan(0, 10).SequenceEqual("UEF File!\0"u8))
                throw new InvalidDataException("Not a UEF tape image.");

            List<TapeEvent> events = new List<TapeEvent>();
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

            return events;
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
