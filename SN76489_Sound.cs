// ============================================================================
// Project:     BBC
// File:        SN76489_Sound.cs
// Description: BBC Model B SN76489 sound generator emulation with SDL audio
//              mixing and playback.
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

        /// <summary>Gets or sets whether generated audio may throttle emulation to real-time playback.</summary>
        public bool ThrottleToPlayback { get; set; } = true;

        /// <summary>Initializes a new sound generator.</summary>
        public SN76489_Sound()
        {
            Reset();
        }

        /// <summary>Resets all tone/noise registers to silence.</summary>
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

        /// <summary>Queues the host power-on tone before MOS-generated sound begins.</summary>
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

        /// <summary>Shapes the measured power-on tone with a fast rise and short release.</summary>
        /// <param name="sampleIndex">The sample position inside the generated tone.</param>
        /// <param name="sampleCount">The total number of samples in the generated tone.</param>
        /// <returns>The computed amplitude multiplier.</returns>
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

        /// <summary>Adds subtle speaker-style compression to the synthetic power-on tone.</summary>
        /// <param name="signal">The normalized input sample.</param>
        /// <returns>The lightly saturated sample.</returns>
        private static double ApplyPowerOnSpeakerSaturation(double signal)
        {
            double biased = signal + PowerOnBeepSaturationBias;
            double zero = Math.Tanh(PowerOnBeepSaturationBias * PowerOnBeepSaturationDrive);
            return PowerOnBeepSaturationLevel * (Math.Tanh(biased * PowerOnBeepSaturationDrive) - zero) / PowerOnBeepSaturationDrive;
        }

        /// <summary>Accepts one byte on the SN76489 data bus.</summary>
        /// <param name="value">The latched or data byte written by the BBC slow bus.</param>
        public void WriteData(byte value)
        {
            lock (syncRoot)
            {
                scheduledEvents.Enqueue(ScheduledPsgEvent.ForLatchedValue(emulatedCycle + PsgWriteEnableDelayCycles, value));
            }
        }

        /// <summary>Refreshes slow data bus after related emulator state changes.</summary>
        /// <param name="value">The input value.</param>
        /// <param name="active">Whether PSG write enable is active.</param>
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

        /// <summary>Advances scheduled PSG writes and generates audio for elapsed CPU cycles.</summary>
        /// <param name="cycles">The number of elapsed CPU cycles.</param>
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

        /// <summary>Runs the SDL audio worker loop that drains queued PSG samples to the audio device.</summary>
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

        /// <summary>Fills samples.</summary>
        /// <param name="samples">The samples value.</param>
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

        /// <summary>Generates one normalized audio sample from the current PSG state.</summary>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Generates enough audio samples to cover the supplied emulated CPU cycles.</summary>
        /// <param name="cycles">The number of emulated CPU cycles.</param>
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

        /// <summary>Applies a latched SN76489 data byte to tone, noise, or volume registers.</summary>
        /// <param name="value">The input value.</param>
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

        /// <summary>Applies a scheduled PSG write or slow-bus sample at its target emulated cycle.</summary>
        /// <param name="scheduledEvent">The scheduled event value.</param>
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

        /// <summary>Mixes the current SN76489 tone and noise channel outputs into one sample.</summary>
        /// <returns>The computed floating-point value.</returns>
        private double GenerateChipSample()
        {
            double mixed = 0;

            for (int channel = 0; channel < 3; channel++)
                mixed += AdvanceTone(channel, tonePeriods[channel]) * GetVolume(volumes[channel]);

            mixed += AdvanceNoise(noiseControl, tonePeriods[2]) * GetVolume(volumes[3]);
            return mixed;
        }

        /// <summary>Queues one generated SDL audio sample while keeping the buffer bounded.</summary>
        /// <param name="sample">The sample value.</param>
        private void EnqueueGeneratedSample(short sample)
        {
            if (generatedCount >= generatedSamples.Length)
                return;

            lastGeneratedSample = sample;
            generatedSamples[generatedWriteIndex] = sample;
            generatedWriteIndex = (generatedWriteIndex + 1) % generatedSamples.Length;
            generatedCount++;
        }

        /// <summary>Waits until the queued audio buffer has room for more generated samples.</summary>
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

        /// <summary>Advances one SN76489 tone channel and toggles output when its counter expires.</summary>
        /// <param name="channel">The channel value.</param>
        /// <param name="period">The period value.</param>
        /// <returns>The computed floating-point value.</returns>
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

        /// <summary>Advances the SN76489 noise channel according to its configured period.</summary>
        /// <param name="control">The control value.</param>
        /// <param name="tone2Period">The tone2 period value.</param>
        /// <returns>The computed floating-point value.</returns>
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

        /// <summary>Advances the SN76489 noise LFSR using white or periodic feedback.</summary>
        /// <param name="control">The control value.</param>
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

        /// <summary>Applies a SN76489 noise-control register write and resets the noise shift register.</summary>
        /// <param name="control">The control value.</param>
        private void SetNoiseControl(byte control)
        {
            noiseControl = control;
            noiseCounter = 0;
            noisePolarity = 0;
            noiseShiftRegister = 0x4000;
        }

        /// <summary>Returns the active SN76489 noise period, including tone-2-derived noise.</summary>
        /// <param name="control">The control value.</param>
        /// <param name="tone2Period">The tone2 period value.</param>
        /// <returns>The computed value.</returns>
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

        /// <summary>Converts a SN76489 volume register value into a linear sample amplitude.</summary>
        /// <param name="attenuation">The attenuation value.</param>
        /// <returns>The computed floating-point value.</returns>
        private static double GetVolume(int attenuation)
        {
            return VolumeTable[Math.Clamp(attenuation, 0, 15)];
        }

        /// <summary>Creates volume table.</summary>
        /// <returns>The resulting collection.</returns>
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

            /// <summary>Creates a scheduled PSG write event for a latched SN76489 value.</summary>
            /// <param name="cycle">The cycle value.</param>
            /// <param name="value">The input value.</param>
            /// <returns>The resulting value.</returns>
            public static ScheduledPsgEvent ForLatchedValue(long cycle, byte value)
            {
                return new ScheduledPsgEvent(cycle, value, SampleSlowBus: false);
            }

            /// <summary>Creates a scheduled event that samples the slow sound data bus.</summary>
            /// <param name="cycle">The cycle value.</param>
            /// <returns>The resulting value.</returns>
            public static ScheduledPsgEvent ForSlowBusSample(long cycle)
            {
                return new ScheduledPsgEvent(cycle, 0, SampleSlowBus: true);
            }
        }

        /// <summary>Throws an exception when an SDL call reports a failure.</summary>
        /// <param name="result">The result value.</param>
        /// <param name="operation">The operation value.</param>
        private static void ThrowIfSdlFailed(int result, string operation)
        {
            if (result < 0)
                throw new InvalidOperationException($"{operation} failed: {GetSdlError()}");
        }

        /// <summary>Reads the current SDL error string and converts it to managed text.</summary>
        /// <returns>The resulting string.</returns>
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

        /// <summary>Imports SDL_InitSubSystem for starting the SDL audio subsystem.</summary>
        /// <param name="flags">The flag mask.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);

        /// <summary>Imports SDL_QuitSubSystem for shutting down the SDL audio subsystem.</summary>
        /// <param name="flags">The flag mask.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        /// <summary>Imports SDL_OpenAudioDevice for creating the host audio device.</summary>
        /// <param name="device">The device value.</param>
        /// <param name="iscapture">The iscapture value.</param>
        /// <param name="desired">The desired value.</param>
        /// <param name="obtained">The obtained value.</param>
        /// <param name="allowedChanges">The allowed changes value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern uint SDL_OpenAudioDevice(string? device, int iscapture, ref SdlAudioSpec desired, out SdlAudioSpec obtained, int allowedChanges);

        /// <summary>Imports SDL_CloseAudioDevice for closing the host audio device.</summary>
        /// <param name="dev">The dev value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseAudioDevice(uint dev);

        /// <summary>Imports SDL_PauseAudioDevice for pausing or resuming audio playback.</summary>
        /// <param name="dev">The dev value.</param>
        /// <param name="pauseOn">The pause on value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_PauseAudioDevice(uint dev, int pauseOn);

        /// <summary>Imports SDL_QueueAudio for submitting generated samples to the host device.</summary>
        /// <param name="dev">The dev value.</param>
        /// <param name="data">The data byte or buffer.</param>
        /// <param name="len">The len value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_QueueAudio(uint dev, IntPtr data, uint len);

        /// <summary>Imports SDL_GetQueuedAudioSize for measuring queued audio bytes.</summary>
        /// <param name="dev">The dev value.</param>
        /// <returns>The resulting value.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_GetQueuedAudioSize(uint dev);

        /// <summary>Imports SDL_ClearQueuedAudio for dropping queued audio samples.</summary>
        /// <param name="dev">The dev value.</param>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_ClearQueuedAudio(uint dev);

        /// <summary>Imports SDL_GetError for retrieving native SDL failure details.</summary>
        /// <returns>The native pointer returned by the host API.</returns>
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();
    }
}
