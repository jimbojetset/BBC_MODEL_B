// ============================================================================
// Project:     BBC
// File:        TMS5220_Speech.cs
// Description: Acorn speech upgrade: TMS5220 LPC synthesiser and TMS6100
//              serial phrase ROM.
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
    /// The Model B speech upgrade hangs from the system VIA slow data bus. The
    /// TMS5220 consumes 200 8 kHz samples per LPC frame and obtains its bitstream
    /// either from its 16-byte FIFO or the serial TMS6100 phrase ROM.
    /// </summary>
    public sealed class TMS5220_Speech
    {
        private const int FifoSize = 16;
        private const int SamplesPerFrame = 200;
        private const int SamplesPerInterpolation = 25;

        // TMS5220 tables, derived from the chip ROM tables documented by TI and
        // the BSD-licensed MAME TMS52xx implementation.
        private static readonly int[] EnergyTable =
            [0, 1, 2, 3, 4, 6, 8, 11, 16, 23, 33, 47, 63, 85, 114, 0];

        private static readonly int[] PitchTable =
        [
             0, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
            30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 44, 46, 48,
            50, 52, 53, 56, 58, 60, 62, 65, 68, 70, 72, 76, 78, 80, 84, 86,
            91, 94, 98,101,105,109,114,118,122,127,132,137,142,148,153,159
        ];

        private static readonly int[][] KTable =
        [
            [-501,-498,-497,-495,-493,-491,-488,-482,-478,-474,-469,-464,-459,-452,-445,-437,
             -412,-380,-339,-288,-227,-158, -81,  -1,  80, 157, 226, 287, 337, 379, 411, 436],
            [-328,-303,-274,-244,-211,-175,-138, -99, -59, -18,  24,  64, 105, 143, 180, 215,
              248, 278, 306, 331, 354, 374, 392, 408, 422, 435, 445, 455, 463, 470, 476, 506],
            [-441,-387,-333,-279,-225,-171,-117, -63,  -9,  45,  98, 152, 206, 260, 314, 368],
            [-328,-273,-217,-161,-106, -50,   5,  61, 116, 172, 228, 283, 339, 394, 450, 506],
            [-328,-282,-235,-189,-142, -96, -50,  -3,  43,  90, 136, 182, 229, 275, 322, 368],
            [-256,-212,-168,-123, -79, -35,  10,  54,  98, 143, 187, 232, 276, 320, 365, 409],
            [-308,-260,-212,-164,-117, -69, -21,  27,  75, 122, 170, 218, 266, 314, 361, 409],
            [-256,-161, -66,  29, 124, 219, 314, 409],
            [-256,-176, -96, -15,  65, 146, 226, 307],
            [-205,-132, -59,  14,  87, 160, 234, 307]
        ];

        private static readonly int[] KBits = [5, 5, 4, 4, 4, 4, 4, 3, 3, 3];
        private static readonly int[] InterpolationShift = [3, 3, 3, 2, 2, 1, 1, 0];
        private static readonly sbyte[] ChirpTable =
        [
            0x00,0x03,0x0f,0x28,0x4c,0x6c,0x71,0x50,0x25,0x26,0x4c,0x44,0x1a,0x32,0x3b,0x13,
            0x37,0x1a,0x25,0x1f,0x1d,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
        ];

        private readonly byte[] fifo = new byte[FifoSize];
        private readonly int[] currentK = new int[10];
        private readonly int[] targetK = new int[10];
        private readonly int[] lattice = new int[10];
        private byte[]? phraseRom;
        private int fifoHead;
        private int fifoTail;
        private int fifoCount;
        private int fifoBit;
        private int phraseAddress;
        private int phraseBit;
        private bool readBytePending;
        private byte readByte;
        private bool speakExternal;
        private bool talk;
        private bool bufferEmpty = true;
        private bool bufferLow = true;
        private bool interruptAsserted;
        private int currentEnergy;
        private int targetEnergy;
        private int previousEnergy;
        private int currentPitch;
        private int targetPitch;
        private int sampleInFrame;
        private int pitchCounter;
        private ushort noise = 0x1FFF;
        private short lastSample;

        public bool Enabled { get; private set; }
        public bool IsSpeaking => Enabled && talk;
        public bool HasPhraseRom => phraseRom is not null;
        public bool Ready => Enabled && (!speakExternal || fifoCount < FifoSize);
        public bool InterruptAsserted => Enabled && interruptAsserted;

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            Reset();
        }

        public void LoadPhraseRom(string path, bool preservePosition = false)
        {
            byte[] image = File.ReadAllBytes(path);
            if (image.Length != 16 * 1024)
                throw new InvalidDataException("The TMS6100 phrase ROM must be exactly 16 KB.");

            phraseRom = image;
            if (!preservePosition)
            {
                phraseAddress = 0;
                phraseBit = 0;
            }
        }

        public void ClearPhraseRom()
        {
            phraseRom = null;
            phraseAddress = 0;
            phraseBit = 0;
        }

        public void Reset()
        {
            Array.Clear(fifo);
            Array.Clear(currentK);
            Array.Clear(targetK);
            Array.Clear(lattice);
            fifoHead = fifoTail = fifoCount = fifoBit = 0;
            phraseAddress = phraseBit = 0;
            readBytePending = false;
            readByte = 0;
            speakExternal = false;
            talk = false;
            bufferEmpty = true;
            bufferLow = true;
            interruptAsserted = false;
            currentEnergy = targetEnergy = previousEnergy = 0;
            currentPitch = targetPitch = 0;
            sampleInFrame = pitchCounter = 0;
            noise = 0x1FFF;
            lastSample = 0;
        }

        public byte Read()
        {
            if (!Enabled)
                return 0xFF;

            if (readBytePending)
            {
                readBytePending = false;
                return readByte;
            }

            interruptAsserted = false;
            return (byte)((talk ? 0x80 : 0) | (bufferLow ? 0x40 : 0) | (bufferEmpty ? 0x20 : 0));
        }

        public void Write(byte value)
        {
            if (!Enabled)
                return;

            if (speakExternal)
            {
                AddFifoByte(value);
                return;
            }

            switch ((value >> 4) & 0x07)
            {
                case 1: // Read byte
                    if (!talk)
                    {
                        readByte = (byte)ReadPhraseBits(8);
                        readBytePending = true;
                    }
                    break;

                case 3: // Read and branch
                    if (!talk)
                    {
                        phraseAddress = ReadPhraseBits(16) & 0x3FFF;
                        phraseBit = 0;
                        readBytePending = false;
                    }
                    break;

                case 4: // Load address, least-significant nibble first
                    if (!talk)
                    {
                        phraseAddress = ((phraseAddress >> 4) | ((value & 0x0F) << 16)) & 0xFFFFF;
                        phraseBit = 0;
                        readBytePending = false;
                    }
                    break;

                case 5: // Speak from the TMS6100
                    StartSpeaking(external: false);
                    break;

                case 6: // Speak external through the FIFO
                    Array.Clear(fifo);
                    fifoHead = fifoTail = fifoCount = fifoBit = 0;
                    speakExternal = true;
                    talk = false;
                    UpdateFifoFlags();
                    ClearFilter();
                    break;

                case 7:
                    Reset();
                    break;
            }
        }

        /// <summary>Returns one native 8 kHz TMS5220 output sample.</summary>
        public short GenerateSample()
        {
            if (!Enabled || !talk)
                return 0;

            if (sampleInFrame == 0 && !ParseFrame())
                return 0;

            int interpolation = sampleInFrame / SamplesPerInterpolation;
            if ((sampleInFrame % SamplesPerInterpolation) == 0)
                InterpolateParameters(InterpolationShift[interpolation]);

            int excitation;
            if (currentPitch == 0)
            {
                excitation = (noise & 1) != 0 ? -64 : 64;
            }
            else
            {
                excitation = pitchCounter < ChirpTable.Length ? ChirpTable[pitchCounter] : 0;
            }

            for (int i = 0; i < 20; i++)
            {
                int feedback = ((noise >> 12) ^ (noise >> 3) ^ (noise >> 2) ^ noise) & 1;
                noise = (ushort)(((noise << 1) | feedback) & 0x1FFF);
            }

            int sample = LatticeFilter(excitation);
            lastSample = (short)Math.Clamp(sample << 1, short.MinValue, short.MaxValue);

            if (currentPitch != 0)
            {
                pitchCounter++;
                if (pitchCounter >= currentPitch)
                    pitchCounter = 0;
            }

            sampleInFrame++;
            if (sampleInFrame >= SamplesPerFrame)
                sampleInFrame = 0;

            return lastSample;
        }

        private bool ParseFrame()
        {
            if (speakExternal && !CanReadBits(4))
            {
                StopSpeaking();
                return false;
            }

            int energyIndex = ReadBits(4);
            if (!talk)
                return false;

            if (energyIndex == 15)
            {
                targetEnergy = 0;
                currentEnergy = 0;
                StopSpeaking();
                return false;
            }

            targetEnergy = EnergyTable[energyIndex];
            if (energyIndex == 0)
            {
                targetPitch = 0;
                Array.Clear(targetK);
                return true;
            }

            int repeat = ReadBits(1);
            int pitchIndex = ReadBits(6);
            if (!talk)
                return false;

            targetPitch = PitchTable[pitchIndex];
            if (repeat != 0)
                return true;

            int coefficients = pitchIndex == 0 ? 4 : 10;
            for (int i = 0; i < coefficients; i++)
                targetK[i] = KTable[i][ReadBits(KBits[i])];
            for (int i = coefficients; i < targetK.Length; i++)
                targetK[i] = 0;

            return talk;
        }

        private void InterpolateParameters(int shift)
        {
            if (shift == 0)
            {
                currentEnergy = targetEnergy;
                currentPitch = targetPitch;
                targetK.CopyTo(currentK, 0);
                return;
            }

            currentEnergy += (targetEnergy - currentEnergy) >> shift;
            currentPitch += (targetPitch - currentPitch) >> shift;
            for (int i = 0; i < currentK.Length; i++)
                currentK[i] += (targetK[i] - currentK[i]) >> shift;
        }

        private int LatticeFilter(int excitation)
        {
            int u = MatrixMultiply(previousEnergy, excitation << 6);
            Span<int> stage = stackalloc int[11];
            stage[10] = u;
            for (int i = 9; i >= 0; i--)
                stage[i] = Wrap14(stage[i + 1] - MatrixMultiply(currentK[i], lattice[i]));

            for (int i = 9; i >= 1; i--)
                lattice[i] = Wrap14(lattice[i - 1] + MatrixMultiply(currentK[i - 1], stage[i - 1]));
            lattice[0] = stage[0];
            previousEnergy = currentEnergy;
            return Wrap14(stage[0]);
        }

        private static int MatrixMultiply(int coefficient, int value)
        {
            return (Wrap10(coefficient) * Wrap14(value)) >> 9;
        }

        private static int Wrap10(int value)
        {
            value &= 0x3FF;
            return value >= 0x200 ? value - 0x400 : value;
        }

        private static int Wrap14(int value)
        {
            value &= 0x7FFF;
            return value >= 0x4000 ? value - 0x8000 : value;
        }

        private int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                if (speakExternal)
                {
                    if (fifoCount == 0)
                    {
                        StopSpeaking();
                        return value;
                    }

                    value = (value << 1) | ((fifo[fifoHead] >> fifoBit) & 1);
                    fifoBit++;
                    if (fifoBit == 8)
                    {
                        fifoBit = 0;
                        fifoHead = (fifoHead + 1) % FifoSize;
                        fifoCount--;
                        UpdateFifoFlags();
                    }
                }
                else
                {
                    value = (value << 1) | ReadPhraseBit();
                }
            }
            return value;
        }

        private bool CanReadBits(int count)
        {
            return !speakExternal || fifoCount * 8 - fifoBit >= count;
        }

        private int ReadPhraseBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
                value = (value << 1) | ReadPhraseBit();
            return value;
        }

        private int ReadPhraseBit()
        {
            if (phraseRom is null)
                return 1; // An empty serial ROM bus reaches a stop frame.

            int address = phraseAddress & 0x3FFF;
            int bit = (phraseRom[address] >> phraseBit) & 1;
            phraseBit++;
            if (phraseBit == 8)
            {
                phraseBit = 0;
                phraseAddress = (phraseAddress + 1) & 0x3FFF;
            }
            return bit;
        }

        private void AddFifoByte(byte value)
        {
            if (fifoCount == FifoSize)
                return;

            bool wasLow = bufferLow;
            fifo[fifoTail] = value;
            fifoTail = (fifoTail + 1) % FifoSize;
            fifoCount++;
            UpdateFifoFlags();

            // Speak External starts when byte nine crosses the half-full boundary.
            if (!talk && wasLow && !bufferLow)
                StartSpeaking(external: true);
        }

        private void StartSpeaking(bool external)
        {
            speakExternal = external;
            talk = true;
            readBytePending = false;
            sampleInFrame = 0;
            ClearFilter();
        }

        private void StopSpeaking()
        {
            if (talk)
                interruptAsserted = true;
            talk = false;
            speakExternal = false;
            sampleInFrame = 0;
            currentEnergy = targetEnergy = previousEnergy = 0;
            lastSample = 0;
            UpdateFifoFlags();
        }

        private void ClearFilter()
        {
            Array.Clear(currentK);
            Array.Clear(targetK);
            Array.Clear(lattice);
            currentEnergy = targetEnergy = previousEnergy = 0;
            currentPitch = targetPitch = 0;
            pitchCounter = 0;
        }

        private void UpdateFifoFlags()
        {
            bool wasLow = bufferLow;
            bool wasEmpty = bufferEmpty;
            bufferLow = fifoCount <= 8;
            bufferEmpty = fifoCount == 0;
            if ((!wasLow && bufferLow) || (!wasEmpty && bufferEmpty))
                interruptAsserted = true;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(Enabled);
            writer.Write(fifo.Length);
            writer.Write(fifo);
            writer.Write(fifoHead);
            writer.Write(fifoTail);
            writer.Write(fifoCount);
            writer.Write(fifoBit);
            writer.Write(phraseAddress);
            writer.Write(phraseBit);
            writer.Write(readBytePending);
            writer.Write(readByte);
            writer.Write(speakExternal);
            writer.Write(talk);
            writer.Write(bufferEmpty);
            writer.Write(bufferLow);
            writer.Write(interruptAsserted);
            writer.Write(currentEnergy);
            writer.Write(targetEnergy);
            writer.Write(previousEnergy);
            writer.Write(currentPitch);
            writer.Write(targetPitch);
            WriteArray(writer, currentK);
            WriteArray(writer, targetK);
            WriteArray(writer, lattice);
            writer.Write(sampleInFrame);
            writer.Write(pitchCounter);
            writer.Write(noise);
            writer.Write(lastSample);
        }

        public void LoadState(BinaryReader reader)
        {
            Enabled = reader.ReadBoolean();
            ReadBytes(reader, fifo, "speech FIFO");
            fifoHead = reader.ReadInt32();
            fifoTail = reader.ReadInt32();
            fifoCount = reader.ReadInt32();
            fifoBit = reader.ReadInt32();
            phraseAddress = reader.ReadInt32();
            phraseBit = reader.ReadInt32();
            readBytePending = reader.ReadBoolean();
            readByte = reader.ReadByte();
            speakExternal = reader.ReadBoolean();
            talk = reader.ReadBoolean();
            bufferEmpty = reader.ReadBoolean();
            bufferLow = reader.ReadBoolean();
            interruptAsserted = reader.ReadBoolean();
            currentEnergy = reader.ReadInt32();
            targetEnergy = reader.ReadInt32();
            previousEnergy = reader.ReadInt32();
            currentPitch = reader.ReadInt32();
            targetPitch = reader.ReadInt32();
            ReadArray(reader, currentK, "speech current coefficient");
            ReadArray(reader, targetK, "speech target coefficient");
            ReadArray(reader, lattice, "speech lattice");
            sampleInFrame = reader.ReadInt32();
            pitchCounter = reader.ReadInt32();
            noise = reader.ReadUInt16();
            lastSample = reader.ReadInt16();
        }

        private static void WriteArray(BinaryWriter writer, int[] values)
        {
            writer.Write(values.Length);
            foreach (int value in values)
                writer.Write(value);
        }

        private static void ReadArray(BinaryReader reader, int[] values, string name)
        {
            int length = reader.ReadInt32();
            if (length != values.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadInt32();
        }

        private static void ReadBytes(BinaryReader reader, byte[] values, string name)
        {
            int length = reader.ReadInt32();
            if (length != values.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");
            byte[] loaded = reader.ReadBytes(length);
            if (loaded.Length != length)
                throw new EndOfStreamException();
            loaded.CopyTo(values, 0);
        }
    }
}
