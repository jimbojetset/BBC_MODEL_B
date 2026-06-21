// ============================================================================
// Project:     BBC
// File:        SN76489_Sound.cs
// Description: BBC Model B SN76489 sound path: VIA slow-bus writes, PSG tone
//              counters, noise generation, and the small internal speaker.
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
    /// The BBC drives the SN76489 through the system 6522 VIA slow data bus. MOS turns
    /// SOUND and ENVELOPE commands into latched PSG bytes; the chip itself only
    /// sees tone, noise, and attenuation register writes.
    /// </summary>
    public sealed class SN76489_Sound : IDisposable
    {
        private const int ClockHz = 4_000_000;
        private const int CpuClockHz = 2_000_000;
        private const int ChipSampleRate = ClockHz / 8;
        private const int SampleRate = 48_000;
        private const int SamplesPerBuffer = 512;
        private const int DeviceBufferSamples = 1024;
        private const int MaxQueuedSamples = SamplesPerBuffer * 2;
        private const int GeneratedQueueSamples = SampleRate / 2;
        private const int GeneratedQueueHighWaterSamples = SamplesPerBuffer * 3;
        private const int PowerOnBeepFrequencyHz = 155;
        private const int PowerOnBeepDurationMilliseconds = 355;
        private const int PowerOnBeepAttackMilliseconds = 15;
        private const int PowerOnBeepReleaseMilliseconds = 10;
        private const double PowerOnBeepAmplitude = 0.8;
        private const double PowerOnBeepSaturationDrive = 6.0;
        private const double PowerOnBeepSaturationBias = -0.035;
        private const double PowerOnBeepSaturationLevel = 0.78;
        private const int PsgWriteEnableDelayCycles = 14;
        private const ushort AudioFormatS16 = 0x8010;
        private readonly object syncRoot = new object();
        private readonly int[] tonePeriods = [0, 0, 0];
        private readonly int[] volumes = [15, 15, 15, 15];
        private readonly double[] toneCounters = new double[3];
        private readonly int[] tonePolarity = [-1, -1, -1];
        private readonly short[] sampleBuffer = new short[SamplesPerBuffer];
        private readonly short[] generatedSamples = new short[GeneratedQueueSamples];
        private readonly Queue<ScheduledPsgEvent> scheduledEvents = new Queue<ScheduledPsgEvent>();
        private static readonly double[] VolumeTable = CreateVolumeTable();
        private double sampleCycleRemainder;
        private double chipSampleRemainder;
        private long emulatedCycle;
        private byte slowDataBus;
        private bool writeEnableActive;
        private bool writeEnableSampleScheduled;
        private int generatedReadIndex;
        private int generatedWriteIndex;
        private int generatedCount;
        private short lastGeneratedSample;
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

        public bool ThrottleToPlayback { get; set; } = true;

        public SN76489_Sound()
        {
            Reset();
        }

        public void Reset()
        {
            lock (syncRoot)
            {
                Array.Fill(tonePeriods, 0);
                Array.Fill(volumes, 15);
                Array.Clear(toneCounters);
                Array.Fill(tonePolarity, -1);
                Array.Clear(generatedSamples);
                scheduledEvents.Clear();
                sampleCycleRemainder = 0;
                chipSampleRemainder = 0;
                emulatedCycle = 0;
                slowDataBus = 0;
                writeEnableActive = false;
                writeEnableSampleScheduled = false;
                generatedReadIndex = 0;
                generatedWriteIndex = 0;
                generatedCount = 0;
                lastGeneratedSample = 0;
                noiseControl = 0;
                noiseCounter = 0;
                noisePolarity = 0;
                noiseShiftRegister = 0x4000;
                latchedChannel = 0;
                latchedVolume = false;
            }
        }

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
                Samples = DeviceBufferSamples
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

        /// <summary>The real machine gives a short speaker thump before MOS has programmed the PSG.</summary>
        public void QueuePowerOnBeep()
        {
            lock (syncRoot)
            {
                int samples = SampleRate * PowerOnBeepDurationMilliseconds / 1000;
                double phase = 0;
                double phaseStep = PowerOnBeepFrequencyHz / (double)SampleRate;

                for (int i = 0; i < samples; i++)
                {
                    double carrier = Math.Sin(phase * Math.Tau) >= 0 ? 1.0 : -1.0;
                    double envelope = GetPowerOnBeepEnvelope(i, samples);
                    double signal = PowerOnBeepAmplitude * envelope * carrier;
                    short sample = (short)(short.MaxValue * ApplyPowerOnSpeakerSaturation(signal));

                    EnqueueGeneratedSample(sample);
                    phase += phaseStep;
                    if (phase >= 1.0)
                        phase -= 1.0;
                }
            }
        }

        private static double GetPowerOnBeepEnvelope(int sampleIndex, int sampleCount)
        {
            int attackSamples = SampleRate * PowerOnBeepAttackMilliseconds / 1000;
            int releaseSamples = SampleRate * PowerOnBeepReleaseMilliseconds / 1000;
            double envelope = 1.0;

            if (attackSamples > 0 && sampleIndex < attackSamples)
                envelope = sampleIndex / (double)attackSamples;

            int releaseStart = sampleCount - releaseSamples;
            if (releaseSamples > 0 && sampleIndex >= releaseStart)
                envelope = Math.Min(envelope, (sampleCount - sampleIndex) / (double)releaseSamples);

            return envelope;
        }

        private static double ApplyPowerOnSpeakerSaturation(double signal)
        {
            double biased = signal + PowerOnBeepSaturationBias;
            double zero = Math.Tanh(PowerOnBeepSaturationBias * PowerOnBeepSaturationDrive);
            return PowerOnBeepSaturationLevel * (Math.Tanh(biased * PowerOnBeepSaturationDrive) - zero) / PowerOnBeepSaturationDrive;
        }

        /// <summary>SN76489 writes are latched by WE after the VIA slow-bus delay, not at the CPU write edge.</summary>
        public void WriteData(byte value)
        {
            lock (syncRoot)
            {
                scheduledEvents.Enqueue(ScheduledPsgEvent.ForLatchedValue(emulatedCycle + PsgWriteEnableDelayCycles, value));
            }
        }

        /// <summary>IC32 controls the PSG write-enable line while VIA port A carries the eight data bits.</summary>
        public void UpdateSlowDataBus(byte value, bool active)
        {
            lock (syncRoot)
            {
                slowDataBus = value;
                writeEnableActive = active;

                if (active && !writeEnableSampleScheduled)
                {
                    scheduledEvents.Enqueue(ScheduledPsgEvent.ForSlowBusSample(emulatedCycle + PsgWriteEnableDelayCycles));
                    writeEnableSampleScheduled = true;
                }
            }
        }

        /// <summary>The PSG tone counters advance with emulated CPU time, while host audio drains independently.</summary>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            WaitForGeneratedHeadroom();

            lock (syncRoot)
            {
                long targetCycle = emulatedCycle + cycles;
                while (scheduledEvents.Count > 0 && scheduledEvents.Peek().Cycle <= targetCycle)
                {
                    ScheduledPsgEvent scheduledEvent = scheduledEvents.Dequeue();
                    GenerateSamplesForCycles((int)(scheduledEvent.Cycle - emulatedCycle));
                    emulatedCycle = scheduledEvent.Cycle;
                    ApplyScheduledEvent(scheduledEvent);
                }

                GenerateSamplesForCycles((int)(targetCycle - emulatedCycle));
                emulatedCycle = targetCycle;
            }
        }

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

                Thread.Sleep(1);
            }
        }

        private void FillSamples(short[] samples)
        {
            lock (syncRoot)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    if (generatedCount > 0)
                    {
                        samples[i] = generatedSamples[generatedReadIndex];
                        lastGeneratedSample = samples[i];
                        generatedReadIndex = (generatedReadIndex + 1) % generatedSamples.Length;
                        generatedCount--;
                        Monitor.PulseAll(syncRoot);
                    }
                    else
                    {
                        samples[i] = lastGeneratedSample;
                    }
                }
            }
        }

        private short GenerateSample()
        {
            double exactChipSamples = (ChipSampleRate / (double)SampleRate) + chipSampleRemainder;
            int chipSamples = Math.Max(1, (int)exactChipSamples);
            chipSampleRemainder = exactChipSamples - chipSamples;

            double mixed = 0;
            for (int i = 0; i < chipSamples; i++)
                mixed += GenerateChipSample();

            mixed /= chipSamples;
            return (short)Math.Clamp(mixed * short.MaxValue, short.MinValue, short.MaxValue);
        }

        private void GenerateSamplesForCycles(int cycles)
        {
            if (cycles <= 0)
                return;

            double exactSamples = ((cycles * (double)SampleRate) / CpuClockHz) + sampleCycleRemainder;
            int samplesToGenerate = (int)exactSamples;
            sampleCycleRemainder = exactSamples - samplesToGenerate;

            for (int i = 0; i < samplesToGenerate; i++)
                EnqueueGeneratedSample(GenerateSample());
        }

        private void ApplyWriteData(byte value)
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

        private void ApplyScheduledEvent(ScheduledPsgEvent scheduledEvent)
        {
            if (scheduledEvent.SampleSlowBus)
            {
                writeEnableSampleScheduled = false;
                if (writeEnableActive)
                    ApplyWriteData(slowDataBus);

                return;
            }

            ApplyWriteData(scheduledEvent.Value);
        }

        private double GenerateChipSample()
        {
            double mixed = 0;

            for (int channel = 0; channel < 3; channel++)
                mixed += AdvanceTone(channel, tonePeriods[channel]) * GetVolume(volumes[channel]);

            mixed += AdvanceNoise(noiseControl, tonePeriods[2]) * GetVolume(volumes[3]);
            return mixed;
        }

        private void EnqueueGeneratedSample(short sample)
        {
            if (generatedCount >= generatedSamples.Length)
                return;

            lastGeneratedSample = sample;
            generatedSamples[generatedWriteIndex] = sample;
            generatedWriteIndex = (generatedWriteIndex + 1) % generatedSamples.Length;
            generatedCount++;
        }

        private void WaitForGeneratedHeadroom()
        {
            if (!ThrottleToPlayback || audioDevice == 0 || !Volatile.Read(ref running))
                return;

            lock (syncRoot)
            {
                while (Volatile.Read(ref running) && generatedCount >= GeneratedQueueHighWaterSamples)
                    Monitor.Wait(syncRoot, 20);
            }
        }

        private double AdvanceTone(int channel, int period)
        {
            int effectivePeriod = period == 0 ? 1024 : period;
            toneCounters[channel] -= ClockHz / (16.0 * ChipSampleRate);

            while (toneCounters[channel] <= 0)
            {
                toneCounters[channel] += effectivePeriod;
                tonePolarity[channel] = -tonePolarity[channel];
            }

            return tonePolarity[channel] > 0 ? 1.0 : 0.0;
        }

        private double AdvanceNoise(byte control, int tone2Period)
        {
            int period = GetNoisePeriod(control, tone2Period);
            noiseCounter -= ClockHz / (16.0 * ChipSampleRate);

            while (noiseCounter <= 0)
            {
                noiseCounter += period;
                StepNoiseShiftRegister(control);
                noisePolarity = noiseShiftRegister & 0x01;
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
            noisePolarity = 0;
            noiseShiftRegister = 0x4000;
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

        private readonly record struct ScheduledPsgEvent(long Cycle, byte Value, bool SampleSlowBus)
        {

            public static ScheduledPsgEvent ForLatchedValue(long cycle, byte value)
            {
                return new ScheduledPsgEvent(cycle, value, SampleSlowBus: false);
            }

            public static ScheduledPsgEvent ForSlowBusSample(long cycle)
            {
                return new ScheduledPsgEvent(cycle, 0, SampleSlowBus: true);
            }
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
