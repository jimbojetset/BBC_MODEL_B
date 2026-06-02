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
        private readonly int[] tonePeriods = [1, 1, 1];
        private readonly int[] volumes = [15, 15, 15, 15];
        private readonly double[] tonePhases = new double[3];
        private readonly short[] sampleBuffer = new short[SamplesPerBuffer];
        private readonly Random noise = new Random(1);

        private byte noiseControl;
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
                Array.Fill(tonePeriods, 1);
                Array.Fill(volumes, 15);
                Array.Clear(tonePhases);
                noiseControl = 0;
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
                        noiseControl = (byte)(value & 0x0F);
                    }
                    else
                    {
                        tonePeriods[latchedChannel] = Math.Max(1, (tonePeriods[latchedChannel] & 0x3F0) | (value & 0x0F));
                    }

                    return;
                }

                if (latchedVolume)
                {
                    volumes[latchedChannel] = value & 0x0F;
                }
                else if (latchedChannel == 3)
                {
                    noiseControl = (byte)(value & 0x0F);
                }
                else
                {
                    tonePeriods[latchedChannel] = Math.Max(1, (tonePeriods[latchedChannel] & 0x0F) | ((value & 0x3F) << 4));
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
            int[] periods = new int[3];
            int[] attenuations = new int[4];
            byte currentNoiseControl;

            lock (syncRoot)
            {
                tonePeriods.CopyTo(periods, 0);
                volumes.CopyTo(attenuations, 0);
                currentNoiseControl = noiseControl;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                double mixed = 0;

                for (int channel = 0; channel < 3; channel++)
                {
                    double frequency = ClockHz / (32.0 * Math.Max(1, periods[channel]));
                    tonePhases[channel] += frequency / SampleRate;
                    tonePhases[channel] -= Math.Floor(tonePhases[channel]);
                    mixed += (tonePhases[channel] < 0.5 ? 1.0 : -1.0) * GetLinearVolume(attenuations[channel]);
                }

                mixed += GetNoiseSample(currentNoiseControl) * GetLinearVolume(attenuations[3]);
                samples[i] = (short)Math.Clamp(mixed * 8192, short.MinValue, short.MaxValue);
            }
        }

        private double GetNoiseSample(byte control)
        {
            int rate = control & 0x03;
            int gate = rate == 0 ? 4 : rate == 1 ? 8 : 16;
            return noise.Next(gate) == 0 ? 1.0 : -1.0;
        }

        private static double GetLinearVolume(int attenuation)
        {
            if (attenuation >= 15)
                return 0;

            return (15 - attenuation) / 15.0 / 4.0;
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
