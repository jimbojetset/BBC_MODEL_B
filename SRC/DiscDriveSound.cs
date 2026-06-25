// ============================================================================
// Project:     BBC
// File:        DiscDriveSound.cs
// Description: 5.25 inch floppy drive noise sampler for motor and head movement.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{
    public sealed class DiscDriveSound
    {
        private const int OutputSampleRate = 48_000;
        private const double Volume = 0.44;
        private readonly object syncRoot = new object();
        private readonly WavSample motorOn;
        private readonly WavSample motorLoop;
        private readonly WavSample motorOff;
        private readonly WavSample step;
        private readonly WavSample seekShort;
        private readonly WavSample seekMedium;
        private readonly WavSample seekLong;
        private WavSample? activeMotor;
        private WavSample? activeSeek;
        private double motorPosition;
        private double seekPosition;
        private bool motorRunning;

        private DiscDriveSound(string directory)
        {
            motorOn = WavSample.Load(Path.Combine(directory, "motoron.wav"));
            motorLoop = WavSample.Load(Path.Combine(directory, "motor.wav"));
            motorOff = WavSample.Load(Path.Combine(directory, "motoroff.wav"));
            step = WavSample.Load(Path.Combine(directory, "step.wav"));
            seekShort = WavSample.Load(Path.Combine(directory, "seek.wav"));
            seekMedium = WavSample.Load(Path.Combine(directory, "seek3.wav"));
            seekLong = WavSample.Load(Path.Combine(directory, "seek2.wav"));
        }

        public static DiscDriveSound? TryLoadDefault()
        {
            foreach (string directory in GetDefaultDirectories())
            {
                if (Directory.Exists(directory))
                    return new DiscDriveSound(directory);
            }

            return null;
        }

        public void Reset()
        {
            lock (syncRoot)
            {
                activeMotor = null;
                activeSeek = null;
                motorPosition = 0;
                seekPosition = 0;
                motorRunning = false;
            }
        }

        public void MotorStarted()
        {
            lock (syncRoot)
            {
                if (motorRunning)
                    return;

                activeMotor = motorOn;
                motorPosition = 0;
                motorRunning = true;
            }
        }

        public void MotorStopped()
        {
            lock (syncRoot)
            {
                if (!motorRunning && activeMotor is null)
                    return;

                activeMotor = motorOff;
                motorPosition = 0;
                motorRunning = false;
            }
        }

        public void Seek(int trackDelta)
        {
            int tracks = Math.Abs(trackDelta);
            if (tracks == 0)
                return;

            WavSample sample = tracks == 1
                ? step
                : tracks < 7
                    ? seekShort
                    : tracks < 30
                        ? seekMedium
                        : seekLong;

            lock (syncRoot)
            {
                activeSeek = sample;
                seekPosition = 0;
            }
        }

        public double GenerateSample()
        {
            lock (syncRoot)
            {
                double mixed = 0;

                if (activeMotor is not null)
                    mixed += NextMotorSample();

                if (activeSeek is not null)
                    mixed += NextSeekSample();

                return mixed * Volume;
            }
        }

        private double NextMotorSample()
        {
            if (activeMotor is null)
                return 0;

            double sample = activeMotor.Read(motorPosition);
            motorPosition += activeMotor.Step;

            if (motorPosition < activeMotor.Length)
                return sample;

            motorPosition = 0;
            if (ReferenceEquals(activeMotor, motorOn))
            {
                activeMotor = motorLoop;
            }
            else if (ReferenceEquals(activeMotor, motorLoop))
            {
                activeMotor = motorLoop;
            }
            else
            {
                activeMotor = null;
            }

            return sample;
        }

        private double NextSeekSample()
        {
            if (activeSeek is null)
                return 0;

            double sample = activeSeek.Read(seekPosition);
            seekPosition += activeSeek.Step;

            if (seekPosition >= activeSeek.Length)
            {
                activeSeek = null;
                seekPosition = 0;
            }

            return sample;
        }

        private static IEnumerable<string> GetDefaultDirectories()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "Assets");
            yield return Path.Combine(Environment.CurrentDirectory, "Assets");
        }

        private sealed class WavSample
        {
            private WavSample(double[] samples, int sampleRate)
            {
                Samples = samples;
                Step = sampleRate / (double)OutputSampleRate;
            }

            public double[] Samples { get; }

            public int Length => Samples.Length;

            public double Step { get; }

            public double Read(double position)
            {
                int index = (int)position;
                return index >= 0 && index < Samples.Length ? Samples[index] : 0;
            }

            public static WavSample Load(string path)
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 44
                    || data[0] != 'R'
                    || data[1] != 'I'
                    || data[2] != 'F'
                    || data[3] != 'F'
                    || data[8] != 'W'
                    || data[9] != 'A'
                    || data[10] != 'V'
                    || data[11] != 'E')
                {
                    throw new InvalidOperationException($"'{path}' is not a RIFF/WAVE file.");
                }

                int offset = 12;
                int sampleRate = 0;
                int channels = 0;
                int bitsPerSample = 0;
                int dataOffset = -1;
                int dataLength = 0;

                while (offset + 8 <= data.Length)
                {
                    string chunk = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                    int chunkLength = BitConverter.ToInt32(data, offset + 4);
                    int chunkData = offset + 8;

                    if (chunkData + chunkLength > data.Length)
                        break;

                    if (chunk == "fmt ")
                    {
                        ushort format = BitConverter.ToUInt16(data, chunkData);
                        channels = BitConverter.ToUInt16(data, chunkData + 2);
                        sampleRate = BitConverter.ToInt32(data, chunkData + 4);
                        bitsPerSample = BitConverter.ToUInt16(data, chunkData + 14);
                        if (format != 1 || channels != 1 || bitsPerSample != 16)
                            throw new InvalidOperationException($"'{path}' must be 16-bit mono PCM.");
                    }
                    else if (chunk == "data")
                    {
                        dataOffset = chunkData;
                        dataLength = chunkLength;
                    }

                    offset = chunkData + chunkLength + (chunkLength & 1);
                }

                if (sampleRate <= 0 || dataOffset < 0)
                    throw new InvalidOperationException($"'{path}' is missing PCM format or data chunks.");

                int sampleCount = dataLength / 2;
                double[] samples = new double[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = BitConverter.ToInt16(data, dataOffset + (i * 2)) / 32768.0;

                return new WavSample(samples, sampleRate);
            }
        }
    }
}
