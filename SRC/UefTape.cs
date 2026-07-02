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
        private const ushort OriginChunk = 0x0000;
        private const ushort ImplicitDataChunk = 0x0100;
        private const ushort DefinedDataChunk = 0x0104;
        private const ushort CarrierToneChunk = 0x0110;
        private const ushort CarrierToneChunkNoDummy = 0x0111;
        private const ushort IntegerGapChunk = 0x0112;
        private const ushort FloatingGapChunk = 0x0116;

        private readonly SerialACIA serialAcia;
        private readonly object sync = new object();
        private readonly List<TapeEvent> events = new List<TapeEvent>();
        private string? mountedPath;
        private string? mountedFileName;
        private int eventIndex;
        private int delayCyclesRemaining;
        private int characterCyclesRemaining;
        private bool playbackStarted;
        private bool reachedEnd;
        private bool paused;

        public UefTape(SerialACIA serialAcia)
        {
            this.serialAcia = serialAcia;
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
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                paused = false;
            }

            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
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
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                paused = false;
            }

            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
        }

        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            lock (sync)
            {
                if (events.Count == 0 || reachedEnd)
                {
                    serialAcia.SetTapePlaying(false);
                    return;
                }

                if (paused)
                {
                    serialAcia.SetTapePlaying(false);
                    return;
                }

                if (!playbackStarted && !serialAcia.MotorRunning)
                {
                    serialAcia.SetTapePlaying(false);
                    return;
                }

                playbackStarted = true;
                serialAcia.SetTapePlaying(true);

                int remainingCycles = cycles;
                while (remainingCycles > 0 && eventIndex < events.Count)
                {
                    if (delayCyclesRemaining > 0)
                    {
                        int consumed = Math.Min(remainingCycles, delayCyclesRemaining);
                        delayCyclesRemaining -= consumed;
                        remainingCycles -= consumed;
                        if (delayCyclesRemaining > 0)
                            return;
                    }

                    if (characterCyclesRemaining > 0)
                    {
                        int consumed = Math.Min(remainingCycles, characterCyclesRemaining);
                        characterCyclesRemaining -= consumed;
                        remainingCycles -= consumed;
                        if (characterCyclesRemaining > 0)
                            return;
                    }

                    TapeEvent tapeEvent = events[eventIndex];
                    if (tapeEvent.Kind == TapeEventKind.Carrier)
                    {
                        serialAcia.SetCarrierPresent(true);
                        delayCyclesRemaining = tapeEvent.Cycles;
                        eventIndex++;
                        return;
                    }

                    if (tapeEvent.Kind == TapeEventKind.CarrierDetect)
                    {
                        serialAcia.PulseTapeCarrierDetect();
                        eventIndex++;
                        return;
                    }

                    if (tapeEvent.Kind == TapeEventKind.Gap)
                    {
                        serialAcia.SetCarrierPresent(true);
                        delayCyclesRemaining = tapeEvent.Cycles;
                        eventIndex++;
                        continue;
                    }

                    if (tapeEvent.Cycles > 0)
                    {
                        delayCyclesRemaining = tapeEvent.Cycles;
                        eventIndex++;
                        continue;
                    }

                    if (!serialAcia.CanReceiveByte)
                        return;

                    if (tapeEvent.Byte != 0xDC)
                        serialAcia.ClearTapeCarrierDetect();
                    serialAcia.QueueTapeByte(tapeEvent.Byte);
                    eventIndex++;
                    characterCyclesRemaining = CharacterCycles(tapeEvent.BitCount);
                }

                if (eventIndex >= events.Count)
                {
                    reachedEnd = true;
                    playbackStarted = false;
                    serialAcia.SetTapePlaying(false);
                }
            }
        }

        public void ResetPlayback()
        {
            lock (sync)
            {
                eventIndex = 0;
                delayCyclesRemaining = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                paused = false;
            }

            serialAcia.ClearTapeReadRequest();
            serialAcia.SetCarrierPresent(true);
            serialAcia.SetTapePlaying(false);
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
                writer.Write(characterCyclesRemaining);
                writer.Write(playbackStarted);
                writer.Write(reachedEnd);
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
            int savedCharacterCycles = reader.ReadInt32();
            bool savedPlaybackStarted = reader.ReadBoolean();
            bool savedReachedEnd = reader.ReadBoolean();
            bool savedPaused = reader.ReadBoolean();

            lock (sync)
            {
                events.Clear();
                mountedPath = null;
                mountedFileName = null;
                eventIndex = 0;
                delayCyclesRemaining = 0;
                characterCyclesRemaining = 0;
                playbackStarted = false;
                reachedEnd = false;
                paused = false;
            }

            if (!hadTape || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                serialAcia.SetTapePlaying(false);
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
                characterCyclesRemaining = Math.Max(0, savedCharacterCycles);
                playbackStarted = savedPlaybackStarted;
                reachedEnd = savedReachedEnd || eventIndex >= events.Count;
                paused = savedPaused;
            }

            serialAcia.ClearTapeReadRequest();
            serialAcia.SetTapePlaying(false);
        }

        public bool TogglePaused()
        {
            lock (sync)
            {
                if (events.Count == 0 || reachedEnd)
                    return false;

                paused = !paused;
                if (paused)
                    serialAcia.SetTapePlaying(false);
                return paused;
            }
        }

        private static int CharacterCycles(int bits)
        {
            return Math.Max(1, BbcClockHz * Math.Max(1, bits) / UefCarrierCyclesPerSecond);
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
                events.Add(TapeEvent.ForGap(cycles));
        }

        private static void AddFloatingGap(List<TapeEvent> events, ReadOnlySpan<byte> data)
        {
            if (data.Length < 4)
                return;

            float seconds = BitConverter.ToSingle(data[..4]);
            if (seconds > 0)
                events.Add(TapeEvent.ForGap((int)Math.Min(int.MaxValue, seconds * BbcClockHz)));
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

            events.Add(TapeEvent.ForCarrierDetect());
            events.Add(TapeEvent.ForCarrier(cycles));
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
            Gap
        }

        private readonly record struct TapeEvent(TapeEventKind Kind, byte Byte, int Cycles, int BitCount)
        {
            public static TapeEvent ForByte(byte value, int bitCount = 10)
            {
                return new TapeEvent(TapeEventKind.Byte, value, 0, bitCount);
            }

            public static TapeEvent ForCarrier(int cycles)
            {
                return new TapeEvent(TapeEventKind.Carrier, 0, cycles, 0);
            }

            public static TapeEvent ForCarrierDetect()
            {
                return new TapeEvent(TapeEventKind.CarrierDetect, 0, 0, 0);
            }

            public static TapeEvent ForGap(int cycles)
            {
                return new TapeEvent(TapeEventKind.Gap, 0, cycles, 0);
            }
        }
    }
}
