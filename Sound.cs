// ============================================================================
// Project:     BBC
// File:        Sound.cs
// Description: BBC Model B SN76489 sound generator and SDL audio output.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.InteropServices;

namespace BBC
{
    /// <summary>
    /// Emulates the BBC Micro's SN76489 programmable sound generator.
    /// </summary>
    public sealed class Sound : IDisposable
    {
        private const int ClockHz = 4_000_000;
        private const int SampleRate = 48_000;
        private const int SamplesPerBuffer = 1024;
        private const int MaxQueuedSamples = SampleRate / 10;
        private const ushort AudioFormatS16 = 0x8010;

        private readonly object syncRoot = new object();
        private readonly int[] tonePeriods = [0, 0, 0];
        private readonly int[] volumes = [15, 15, 15, 15];
        private readonly int[] durationRemainingSamples = [0, 0, 0, 0];
        private readonly double[] toneCounters = new double[3];
        private readonly int[] tonePolarity = [1, 1, 1];
        private readonly short[] sampleBuffer = new short[SamplesPerBuffer];
        private readonly SoundEnvelope[] envelopes = new SoundEnvelope[16];
        private static readonly double[] VolumeTable = CreateVolumeTable();

        private byte noiseControl;
        private double noiseCounter;
        private int noisePolarity = 1;
        private ushort noiseShiftRegister = 0x4000;
        private int latchedChannel;
        private bool latchedVolume;
        private uint audioDevice;
        private Thread? audioThread;
        private bool running;
        private bool disposed;

        /// <summary>Resets all tone/noise registers to silence.</summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                Array.Fill(tonePeriods, 0);
                Array.Fill(volumes, 15);
                Array.Fill(durationRemainingSamples, 0);
                Array.Clear(envelopes);
                Array.Clear(toneCounters);
                Array.Fill(tonePolarity, 1);
                noiseControl = 0;
                noiseCounter = 0;
                noisePolarity = 1;
                noiseShiftRegister = 0x4000;
                latchedChannel = 0;
                latchedVolume = false;
            }
        }

        /// <summary>Starts SDL audio output.</summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (audioDevice != 0)
                return;

            ThrowIfSdlFailed(SDL_InitSubSystem(SDL_INIT_AUDIO), "SDL_InitSubSystem");

            SdlAudioSpec desired = new SdlAudioSpec
            {
                Freq = SampleRate,
                Format = AudioFormatS16,
                Channels = 1,
                Samples = SamplesPerBuffer
            };

            audioDevice = SDL_OpenAudioDevice(null, 0, ref desired, out SdlAudioSpec obtained, 0);
            if (audioDevice == 0)
                throw new InvalidOperationException($"SDL_OpenAudioDevice failed: {GetSdlError()}");

            running = true;
            audioThread = new Thread(RunAudio)
            {
                IsBackground = true,
                Name = "BBC SN76489"
            };
            audioThread.Start();
            SDL_PauseAudioDevice(audioDevice, 0);
        }

        /// <summary>Accepts one byte on the SN76489 data bus.</summary>
        /// <param name="value">The latched or data byte written by the BBC slow bus.</param>
        public void WriteData(byte value)
        {
            lock (syncRoot)
            {
                if ((value & 0x80) != 0)
                {
                    latchedChannel = (value >> 5) & 0x03;
                    latchedVolume = (value & 0x10) != 0;

                    if (latchedVolume)
                    {
                        volumes[latchedChannel] = value & 0x0F;
                    }
                    else if (latchedChannel == 3)
                    {
                        SetNoiseControl((byte)(value & 0x0F));
                    }
                    else
                    {
                        tonePeriods[latchedChannel] = (tonePeriods[latchedChannel] & 0x3F0) | (value & 0x0F);
                    }

                    return;
                }

                if (latchedVolume)
                {
                    volumes[latchedChannel] = value & 0x0F;
                }
                else if (latchedChannel == 3)
                {
                    SetNoiseControl((byte)(value & 0x0F));
                }
                else
                {
                    tonePeriods[latchedChannel] = (tonePeriods[latchedChannel] & 0x0F) | ((value & 0x3F) << 4);
                }
            }
        }

        /// <summary>Stores one BBC MOS ENVELOPE definition.</summary>
        /// <param name="data">The 14-byte OSWORD &amp;08 envelope block.</param>
        public void SetEnvelope(ReadOnlySpan<byte> data)
        {
            if (data.Length < 14)
                return;

            int envelopeNumber = data[0] & 0x0F;
            if (envelopeNumber == 0)
                return;

            lock (syncRoot)
            {
                envelopes[envelopeNumber] = new SoundEnvelope(data[8], data[9], data[10], data[11], data[12], data[13]);
            }
        }

        /// <summary>Plays a BBC MOS SOUND command through the SN76489.</summary>
        /// <param name="channel">BBC SOUND channel word.</param>
        /// <param name="amplitude">BBC SOUND amplitude word.</param>
        /// <param name="pitch">BBC SOUND pitch word.</param>
        /// <param name="duration">BBC SOUND duration word.</param>
        public void PlaySoundCommand(short channel, short amplitude, short pitch, short duration)
        {
            int bbcChannel = channel & 0x03;
            int chipChannel = bbcChannel == 0 ? 3 : bbcChannel - 1;
            int attenuation = GetAttenuation(amplitude);
            int durationSamples = duration < 0 ? 0 : Math.Max(1, (int)duration) * SampleRate / 20;

            lock (syncRoot)
            {
                if (attenuation >= 15 || duration == 0)
                {
                    volumes[chipChannel] = 15;
                    durationRemainingSamples[chipChannel] = 0;
                    return;
                }

                if (chipChannel == 3)
                {
                    SetNoiseControl(GetNoiseControlForPitch(pitch));
                    volumes[3] = attenuation;
                    durationRemainingSamples[3] = durationSamples;
                    return;
                }

                int period = PitchToTonePeriod(pitch);
                tonePeriods[chipChannel] = period;
                toneCounters[chipChannel] = 0;
                volumes[chipChannel] = attenuation;
                durationRemainingSamples[chipChannel] = durationSamples;
            }
        }

        /// <summary>Releases the SDL audio device.</summary>
        public void Dispose()
        {
            if (disposed)
                return;

            running = false;

            if (audioThread is not null && audioThread.IsAlive)
                audioThread.Join(TimeSpan.FromSeconds(1));

            audioThread = null;

            if (audioDevice != 0)
            {
                SDL_ClearQueuedAudio(audioDevice);
                SDL_CloseAudioDevice(audioDevice);
                audioDevice = 0;
                SDL_QuitSubSystem(SDL_INIT_AUDIO);
            }

            disposed = true;
        }

        private void RunAudio()
        {
            while (running)
            {
                if (SDL_GetQueuedAudioSize(audioDevice) < MaxQueuedSamples * sizeof(short))
                {
                    FillSamples(sampleBuffer);
                    GCHandle handle = GCHandle.Alloc(sampleBuffer, GCHandleType.Pinned);
                    try
                    {
                        _ = SDL_QueueAudio(audioDevice, handle.AddrOfPinnedObject(), (uint)(sampleBuffer.Length * sizeof(short)));
                    }
                    finally
                    {
                        handle.Free();
                    }
                }

                Thread.Sleep(5);
            }
        }

        private void FillSamples(short[] samples)
        {
            int[] periods = new int[3];
            int[] attenuations = new int[4];
            byte currentNoiseControl;

            lock (syncRoot)
            {
                UpdateDurations(samples.Length);
                tonePeriods.CopyTo(periods, 0);
                volumes.CopyTo(attenuations, 0);
                currentNoiseControl = noiseControl;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                double mixed = 0;

                for (int channel = 0; channel < 3; channel++)
                    mixed += AdvanceTone(channel, periods[channel]) * GetVolume(attenuations[channel]);

                mixed += AdvanceNoise(currentNoiseControl, periods[2]) * GetVolume(attenuations[3]);
                samples[i] = (short)Math.Clamp(mixed * 8192, short.MinValue, short.MaxValue);
            }
        }

        private double AdvanceTone(int channel, int period)
        {
            int effectivePeriod = period == 0 ? 1 : period;
            toneCounters[channel] -= ClockHz / (16.0 * SampleRate);

            while (toneCounters[channel] <= 0)
            {
                toneCounters[channel] += effectivePeriod;
                tonePolarity[channel] = -tonePolarity[channel];
            }

            return tonePolarity[channel];
        }

        private double AdvanceNoise(byte control, int tone2Period)
        {
            int period = GetNoisePeriod(control, tone2Period);
            noiseCounter -= ClockHz / (16.0 * SampleRate);

            while (noiseCounter <= 0)
            {
                noiseCounter += period;
                StepNoiseShiftRegister(control);
                noisePolarity = (noiseShiftRegister & 0x01) == 0 ? 1 : -1;
            }

            return noisePolarity;
        }

        private void StepNoiseShiftRegister(byte control)
        {
            int feedback;

            if ((control & 0x04) != 0)
                feedback = (noiseShiftRegister ^ (noiseShiftRegister >> 1)) & 0x01;
            else
                feedback = noiseShiftRegister & 0x01;

            noiseShiftRegister = (ushort)((noiseShiftRegister >> 1) | (feedback << 14));
            if (noiseShiftRegister == 0)
                noiseShiftRegister = 0x4000;
        }

        private void SetNoiseControl(byte control)
        {
            noiseControl = control;
            noiseCounter = 0;
            noisePolarity = 1;
            noiseShiftRegister = 0x4000;
        }

        private void UpdateDurations(int sampleCount)
        {
            for (int channel = 0; channel < durationRemainingSamples.Length; channel++)
            {
                if (durationRemainingSamples[channel] <= 0)
                    continue;

                durationRemainingSamples[channel] -= sampleCount;
                if (durationRemainingSamples[channel] <= 0)
                {
                    durationRemainingSamples[channel] = 0;
                    volumes[channel] = 15;
                }
            }
        }

        private static int GetAttenuation(short amplitude)
        {
            if (amplitude == 0)
                return 15;

            int volume;
            if (amplitude < 0)
            {
                volume = Math.Clamp(-amplitude, 0, 15);
            }
            else
            {
                // Positive amplitudes select MOS envelopes. Until full envelope
                // shaping is emulated, play them at a clearly audible level.
                volume = 14;
            }

            return 15 - volume;
        }

        private static int PitchToTonePeriod(short pitch)
        {
            int clampedPitch = Math.Clamp((int)pitch, 0, 255);
            double frequency = 440.0 * Math.Pow(2.0, (clampedPitch - 88) / 48.0);
            return Math.Clamp((int)Math.Round(ClockHz / (32.0 * frequency)), 1, 1023);
        }

        private static byte GetNoiseControlForPitch(short pitch)
        {
            int clampedPitch = Math.Clamp((int)pitch, 0, 7);
            int noiseType = clampedPitch >= 4 ? 0x04 : 0x00;
            int rate = clampedPitch & 0x03;
            return (byte)(noiseType | rate);
        }

        private static int GetNoisePeriod(byte control, int tone2Period)
        {
            return (control & 0x03) switch
            {
                0 => 0x10,
                1 => 0x20,
                2 => 0x40,
                _ => Math.Max(1, tone2Period == 0 ? 1 : tone2Period)
            };
        }

        private static double GetVolume(int attenuation)
        {
            return VolumeTable[Math.Clamp(attenuation, 0, 15)];
        }

        private static double[] CreateVolumeTable()
        {
            double[] table = new double[16];

            for (int i = 0; i < table.Length - 1; i++)
                table[i] = Math.Pow(10.0, -2.0 * i / 20.0) / 4.0;

            table[15] = 0;
            return table;
        }

        private readonly struct SoundEnvelope
        {
            public SoundEnvelope(byte attackStepCount, byte decayStepCount, byte sustainStepCount, byte attackLevel, byte decayLevel, byte sustainLevel)
            {
                AttackStepCount = attackStepCount;
                DecayStepCount = decayStepCount;
                SustainStepCount = sustainStepCount;
                AttackLevel = attackLevel;
                DecayLevel = decayLevel;
                SustainLevel = sustainLevel;
            }

            public byte AttackStepCount { get; }

            public byte DecayStepCount { get; }

            public byte SustainStepCount { get; }

            public byte AttackLevel { get; }

            public byte DecayLevel { get; }

            public byte SustainLevel { get; }
        }

        private static void ThrowIfSdlFailed(int result, string operation)
        {
            if (result < 0)
                throw new InvalidOperationException($"{operation} failed: {GetSdlError()}");
        }

        private static string GetSdlError()
        {
            IntPtr error = SDL_GetError();
            return error == IntPtr.Zero ? "unknown SDL error" : Marshal.PtrToStringAnsi(error) ?? "unknown SDL error";
        }

        private const string SdlLibrary = "SDL2";
        private const uint SDL_INIT_AUDIO = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct SdlAudioSpec
        {
            public int Freq;
            public ushort Format;
            public byte Channels;
            public byte Silence;
            public ushort Samples;
            public ushort Padding;
            public uint Size;
            public IntPtr Callback;
            public IntPtr UserData;
        }

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern uint SDL_OpenAudioDevice(string? device, int iscapture, ref SdlAudioSpec desired, out SdlAudioSpec obtained, int allowedChanges);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseAudioDevice(uint dev);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_PauseAudioDevice(uint dev, int pauseOn);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_QueueAudio(uint dev, IntPtr data, uint len);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_GetQueuedAudioSize(uint dev);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_ClearQueuedAudio(uint dev);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();
    }
}
