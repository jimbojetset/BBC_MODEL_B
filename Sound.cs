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
    /// Emulates the BBC Micro SN76489 sound chip. BBC MOS SOUND/ENVELOPE queues are
    /// handled by the OS ROM; this class only consumes bytes written through the VIA slow bus.
    /// </summary>
    public sealed class Sound : IDisposable
    {
        private const int ClockHz = 4_000_000;
        private const int SampleRate = 48_000;
        private const int SamplesPerBuffer = 1024;
        private const int MaxQueuedSamples = SampleRate / 10;
        private const ushort AudioFormatS16 = 0x8010;
        private const double PowerOnToneFrequencyHz = 120.0;
        private const double PowerOnToneDurationSeconds = 0.35;
        private const double PowerOnToneAmplitude = 0.1;
        private readonly Lock syncRoot = new Lock();
        private readonly int[] tonePeriods = [0, 0, 0];
        private readonly int[] volumes = [15, 15, 15, 15];
        private readonly double[] toneCounters = new double[3];
        private readonly int[] tonePolarity = [1, 1, 1];
        private readonly double[] smoothedChannelGains = new double[4];
        private readonly short[] sampleBuffer = new short[SamplesPerBuffer];
        private static readonly double[] VolumeTable = CreateVolumeTable();
        private byte noiseControl;
        private double noiseCounter;
        private int noisePolarity = 1;
        private ushort noiseShiftRegister = 0x4000;
        private int latchedChannel;
        private bool latchedVolume;
        private int powerOnToneSamplesRemaining;
        private int powerOnToneTotalSamples;
        private double powerOnTonePhase;
        private bool powerOnToneQueued;
        private uint audioDevice;
        private Thread? audioThread;
        private bool running;
        private bool disposed;

        /// <summary>Initializes a new sound generator.</summary>
        public Sound()
        {
            Reset();
        }

        /// <summary>Gets the length of the emulated hardware power-on tone.</summary>
        public TimeSpan PowerOnToneDuration => TimeSpan.FromSeconds(PowerOnToneDurationSeconds);

        /// <summary>Resets all tone/noise registers to silence.</summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                Array.Fill(tonePeriods, 0);
                Array.Fill(volumes, 15);
                Array.Clear(toneCounters);
                Array.Fill(tonePolarity, 1);
                Array.Clear(smoothedChannelGains);
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
            QueuePowerOnTone();
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
            lock (syncRoot)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    double mixed = 0;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        double targetGain = GetVolume(volumes[channel]);
                        mixed += AdvanceTone(channel, tonePeriods[channel]) * SlewChannelGain(channel, targetGain);
                    }

                    double noiseTargetGain = GetVolume(volumes[3]);
                    mixed += AdvanceNoise(noiseControl, tonePeriods[2]) * SlewChannelGain(3, noiseTargetGain);
                    mixed += AdvancePowerOnTone();
                    samples[i] = (short)Math.Clamp(mixed * 8192, short.MinValue, short.MaxValue);
                }
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

        private void QueuePowerOnTone()
        {
            lock (syncRoot)
            {
                if (powerOnToneQueued)
                    return;

                powerOnToneTotalSamples = (int)(PowerOnToneDurationSeconds * SampleRate);
                powerOnToneSamplesRemaining = powerOnToneTotalSamples;
                powerOnTonePhase = 0;
                powerOnToneQueued = true;
            }
        }

        private double AdvancePowerOnTone()
        {
            if (powerOnToneSamplesRemaining <= 0 || powerOnToneTotalSamples <= 0)
                return 0;

            double age = 1.0 - (powerOnToneSamplesRemaining / (double)powerOnToneTotalSamples);
            double envelope = Math.Min(1.0, age / 0.08);
            double sample = (powerOnTonePhase < 0.5 ? 1.0 : -1.0) * PowerOnToneAmplitude * envelope;

            powerOnTonePhase += PowerOnToneFrequencyHz / SampleRate;
            if (powerOnTonePhase >= 1.0)
                powerOnTonePhase -= Math.Floor(powerOnTonePhase);

            powerOnToneSamplesRemaining--;
            return sample;
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

        private double SlewChannelGain(int channel, double targetGain)
        {
            const double attackCoefficient = 0.25;
            const double releaseCoefficient = 0.0025;

            double currentGain = smoothedChannelGains[channel];
            double coefficient = targetGain > currentGain ? attackCoefficient : releaseCoefficient;
            currentGain += (targetGain - currentGain) * coefficient;
            smoothedChannelGains[channel] = currentGain;
            return currentGain;
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
