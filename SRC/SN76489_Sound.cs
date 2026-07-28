// ============================================================================
// Project:     BBC
// File:        SN76489_Sound.cs
// Description: BBC Model B SN76489 sound path: VIA slow-bus writes, PSG tone
//              counters, noise generation, and the small internal speaker.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.InteropServices;
using System.Threading;

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
        private const int PowerOnBeepDurationMilliseconds = 400;
        private const int PowerOnBeepAttackMilliseconds = 15;
        private const int PowerOnBeepReleaseMilliseconds = 10;
        private const double PowerOnBeepAmplitude = 0.8;
        private const double PowerOnBeepSaturationDrive = 6.0;
        private const double PowerOnBeepSaturationBias = -0.035;
        private const double PowerOnBeepSaturationLevel = 0.78;
        private const int ModemDialToneLowHz = 350;
        private const int ModemDialToneHighHz = 440;
        private const int ModemDialToneDurationMilliseconds = 700;
        private const int ModemDtmfToneMilliseconds = 100;
        private const int ModemDtmfGapMilliseconds = 35;
        private const int ModemToneEnvelopeMilliseconds = 4;
        private const double ModemToneAmplitude = 0.04;
        private const double ModemConnectionSequenceDurationScale = 1;
        private const int ModemPostDialSilenceMilliseconds = 1000;
        private const int ModemRingbackLowHz = 400;
        private const int ModemRingbackHighHz = 450;
        private const int ModemV25AnswerToneHz = 2100;
        private const int ModemV25AnswerToneDurationMilliseconds = 3200;
        private const int ModemV25AnswerToneReversalMilliseconds = 450;
        private const int ModemV25PostAnswerSilenceMilliseconds = 75;
        private const int V32BisCarrierHz = 1800;
        private const int V32BisSymbolRate = 2400;
        private const int V32BisAnswerAcMilliseconds = 120;
        private const int V32BisAnswerCaMilliseconds = 50;
        private const int V32BisSSequenceMilliseconds = 107;
        private const int V32BisSBarSequenceMilliseconds = 7;
        private const int V32BisTrainingMilliseconds = 650;
        private const int V32BisRateSignalMilliseconds = 320;
        private const int V32BisScrambledOnesMilliseconds = 180;
        private const int ModemFinalNoiseMilliseconds = 1000;
        private const double CassetteToneAmplitude = 0.055;
        private const double PrinterClickDecay = 0.96;
        private const double PrinterClickAmplitude = 0.18;
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
        private readonly Queue<PrinterHeadEvent> printerHeadEvents = new Queue<PrinterHeadEvent>();
        private static readonly double[] VolumeTable = CreateVolumeTable();
        private double sampleCycleRemainder;
        private double chipSampleRemainder;
        private long emulatedCycle;
        private int scheduledEventCount;
        private int emulatedOutputActive;
        private int printerOutputActive;
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
        private int cassetteToneHz;
        private double cassetteTonePhase;
        private int printerClickSamplesUntilNext;
        private double printerClickEnvelope;
        private uint printerClickNoise = 0x6D2B79F5;
        private double printerClickFastNoise;
        private double printerClickSlowNoise;
        private double printerClickResonancePhase;
        private uint audioDevice;
        private Thread? audioThread;
        private bool running;
        private bool hostOutputPaused;
        private bool hostOutputMuted;
        private bool disposed;
        private DiscDriveSound? discDriveSound;

        public bool HostOutputMuted => Volatile.Read(ref hostOutputMuted);

        public DiscDriveSound? DiscDriveSound
        {
            get
            {
                lock (syncRoot)
                    return discDriveSound;
            }
            set
            {
                lock (syncRoot)
                    discDriveSound = value;
            }
        }

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
                Volatile.Write(ref scheduledEventCount, 0);
                sampleCycleRemainder = 0;
                chipSampleRemainder = 0;
                emulatedCycle = 0;
                Volatile.Write(ref emulatedOutputActive, 0);
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
                cassetteToneHz = 0;
                cassetteTonePhase = 0;
                printerHeadEvents.Clear();
                printerClickSamplesUntilNext = 0;
                printerClickEnvelope = 0;
                printerClickNoise = 0x6D2B79F5;
                printerClickFastNoise = 0;
                printerClickSlowNoise = 0;
                printerClickResonancePhase = 0;
                Volatile.Write(ref printerOutputActive, 0);
                discDriveSound?.Reset();
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            lock (syncRoot)
            {
                writer.Write(tonePeriods.Length);
                foreach (int period in tonePeriods)
                    writer.Write(period);

                writer.Write(volumes.Length);
                foreach (int volume in volumes)
                    writer.Write(volume);

                writer.Write(toneCounters.Length);
                foreach (double counter in toneCounters)
                    writer.Write(counter);

                writer.Write(tonePolarity.Length);
                foreach (int polarity in tonePolarity)
                    writer.Write(polarity);

                writer.Write(sampleCycleRemainder);
                writer.Write(chipSampleRemainder);
                writer.Write(emulatedCycle);
                writer.Write(slowDataBus);
                writer.Write(writeEnableActive);
                writer.Write(writeEnableSampleScheduled);
                writer.Write(noiseControl);
                writer.Write(noiseCounter);
                writer.Write(noisePolarity);
                writer.Write(noiseShiftRegister);
                writer.Write(latchedChannel);
                writer.Write(latchedVolume);
            }
        }

        public void LoadState(BinaryReader reader)
        {
            lock (syncRoot)
            {
                ReadIntArray(reader, tonePeriods, "PSG tone period");
                ReadIntArray(reader, volumes, "PSG volume");
                ReadDoubleArray(reader, toneCounters, "PSG tone counter");
                ReadIntArray(reader, tonePolarity, "PSG tone polarity");

                sampleCycleRemainder = reader.ReadDouble();
                chipSampleRemainder = reader.ReadDouble();
                emulatedCycle = reader.ReadInt64();
                slowDataBus = reader.ReadByte();
                writeEnableActive = reader.ReadBoolean();
                writeEnableSampleScheduled = reader.ReadBoolean();
                noiseControl = reader.ReadByte();
                noiseCounter = reader.ReadDouble();
                noisePolarity = reader.ReadInt32();
                noiseShiftRegister = reader.ReadUInt16();
                latchedChannel = reader.ReadInt32();
                latchedVolume = reader.ReadBoolean();

                scheduledEvents.Clear();
                Volatile.Write(ref scheduledEventCount, 0);
                generatedReadIndex = 0;
                generatedWriteIndex = 0;
                generatedCount = 0;
                lastGeneratedSample = 0;
                cassetteToneHz = 0;
                cassetteTonePhase = 0;
                printerHeadEvents.Clear();
                printerClickSamplesUntilNext = 0;
                printerClickEnvelope = 0;
                printerClickNoise = 0x6D2B79F5;
                printerClickFastNoise = 0;
                printerClickSlowNoise = 0;
                printerClickResonancePhase = 0;
                Volatile.Write(ref printerOutputActive, 0);
                Volatile.Write(ref emulatedOutputActive, 0);
                discDriveSound?.Reset();
            }

            if (audioDevice != 0)
                SDL_ClearQueuedAudio(audioDevice);
        }

        private static void ReadIntArray(BinaryReader reader, int[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            for (int i = 0; i < destination.Length; i++)
                destination[i] = reader.ReadInt32();
        }

        private static void ReadDoubleArray(BinaryReader reader, double[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            for (int i = 0; i < destination.Length; i++)
                destination[i] = reader.ReadDouble();
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
                 Thread.Sleep(250); // Allow the beep to be heard before the PSG takes over
       }

        public void PlayModemDialSequence(string digits)
        {
            int totalMilliseconds = ModemDialToneDurationMilliseconds
                + (digits.Length * (ModemDtmfToneMilliseconds + ModemDtmfGapMilliseconds));

            if (audioDevice == 0 || Volatile.Read(ref hostOutputPaused) || !Volatile.Read(ref running))
            {
                Thread.Sleep(totalMilliseconds);
                return;
            }

            QueueModemTonePair(ModemDialToneLowHz, ModemDialToneHighHz, ModemDialToneDurationMilliseconds);

            foreach (char digit in digits)
            {
                if (!TryGetDtmfFrequencies(digit, out int lowHz, out int highHz))
                    continue;

                QueueModemTonePair(lowHz, highHz, ModemDtmfToneMilliseconds);
                QueueModemSilence(ModemDtmfGapMilliseconds);
            }

            WaitForGeneratedDrain();
        }

        public void QueuePrinterHeadEvent(int pinCount, double durationSeconds)
        {
            if (pinCount < 0 || durationSeconds <= 0)
                return;

            lock (syncRoot)
            {
                if (printerHeadEvents.Count < 4096)
                {
                    int durationSamples = Math.Max(1, (int)Math.Round(durationSeconds * SampleRate));
                    printerHeadEvents.Enqueue(new PrinterHeadEvent(Math.Min(pinCount, 9), durationSamples));
                }

                Volatile.Write(ref printerOutputActive, 1);
            }
        }

        public void CancelPrinterOutput()
        {
            lock (syncRoot)
            {
                printerHeadEvents.Clear();
                printerClickSamplesUntilNext = 0;
                printerClickEnvelope = 0;
                printerClickFastNoise = 0;
                printerClickSlowNoise = 0;
                Volatile.Write(ref printerOutputActive, 0);
            }
        }

        public void PlayModemConnectionSequence(Action? remoteAnswered = null)
        {
            int totalMilliseconds = ScaleModemConnectionDuration(
                ModemPostDialSilenceMilliseconds
                + 1000
                + ModemV25AnswerToneDurationMilliseconds
                + ModemV25PostAnswerSilenceMilliseconds
                + V32BisAnswerAcMilliseconds
                + V32BisAnswerCaMilliseconds
                + V32BisAnswerAcMilliseconds
                + V32BisSSequenceMilliseconds
                + V32BisSBarSequenceMilliseconds
                + V32BisTrainingMilliseconds
                + V32BisRateSignalMilliseconds
                + V32BisSSequenceMilliseconds
                + V32BisSBarSequenceMilliseconds
                + V32BisTrainingMilliseconds
                + V32BisRateSignalMilliseconds
                + V32BisScrambledOnesMilliseconds
                + ModemFinalNoiseMilliseconds);

            if (audioDevice == 0 || Volatile.Read(ref hostOutputPaused) || !Volatile.Read(ref running))
            {
                int silenceMilliseconds = ScaleModemConnectionDuration(ModemPostDialSilenceMilliseconds);
                Thread.Sleep(silenceMilliseconds);
                remoteAnswered?.Invoke();
                Thread.Sleep(Math.Max(0, totalMilliseconds - silenceMilliseconds));
                return;
            }

            QueueModemSilence(ScaleModemConnectionDuration(ModemPostDialSilenceMilliseconds));
            remoteAnswered?.Invoke();

            QueueModemTonePair(ModemRingbackLowHz, ModemRingbackHighHz, ScaleModemConnectionDuration(400));
            QueueModemSilence(ScaleModemConnectionDuration(200));
            QueueModemTonePair(ModemRingbackLowHz, ModemRingbackHighHz, ScaleModemConnectionDuration(400));

            QueueModemV25AnswerTone(ScaleModemConnectionDuration(ModemV25AnswerToneDurationMilliseconds));
            QueueModemSilence(ScaleModemConnectionDuration(ModemV25PostAnswerSilenceMilliseconds));
            QueueV32BisStates(new[] { 0, 2 }, V32BisAnswerAcMilliseconds, ModemToneAmplitude * 0.75);
            QueueV32BisStates(new[] { 2, 0 }, V32BisAnswerCaMilliseconds, ModemToneAmplitude * 0.75);
            QueueV32BisStates(new[] { 0, 2 }, V32BisAnswerAcMilliseconds, ModemToneAmplitude * 0.75);
            QueueV32BisReceiverConditioning(answerMode: true);
            QueueV32BisRateSignal(V32BisRateSignalMilliseconds, ModemToneAmplitude * 0.75);
            QueueV32BisReceiverConditioning(answerMode: true);
            QueueV32BisRateSignal(V32BisRateSignalMilliseconds, ModemToneAmplitude * 0.8);
            QueueV32BisTraining(V32BisScrambledOnesMilliseconds, ModemToneAmplitude * 0.7);
            QueueModemNoise(ScaleModemConnectionDuration(ModemFinalNoiseMilliseconds), ModemToneAmplitude * 0.8);

            WaitForGeneratedDrain();
        }

        public void SetHostOutputPaused(bool paused)
        {
            Volatile.Write(ref hostOutputPaused, paused);
            if (paused)
                SilenceHostOutput();
        }

        public void SetHostOutputMuted(bool muted)
        {
            Volatile.Write(ref hostOutputMuted, muted);
            if (muted)
                SilenceHostOutput();
        }

        private void SilenceHostOutput()
        {
            lock (syncRoot)
            {
                generatedReadIndex = 0;
                generatedWriteIndex = 0;
                generatedCount = 0;
                lastGeneratedSample = 0;
                Monitor.PulseAll(syncRoot);
            }

            if (audioDevice != 0)
                SDL_ClearQueuedAudio(audioDevice);
        }

        public void SetCassetteTone(int frequencyHz)
        {
            lock (syncRoot)
            {
                int clampedFrequency = Math.Max(0, frequencyHz);
                if (cassetteToneHz == clampedFrequency)
                    return;

                cassetteToneHz = clampedFrequency;
                if (cassetteToneHz == 0)
                    cassetteTonePhase = 0;
                UpdateEmulatedOutputActiveLocked();
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

        private void QueueModemTonePair(int requestedLowHz, int requestedHighHz, int durationMilliseconds)
        {
            int sampleCount = SampleRate * durationMilliseconds / 1000;
            double lowPhase = 0;
            double highPhase = 0;
            double lowStep = GetSn76489ApproximateFrequency(requestedLowHz) / SampleRate;
            double highStep = GetSn76489ApproximateFrequency(requestedHighHz) / SampleRate;

            for (int i = 0; i < sampleCount; i++)
            {
                double low = lowPhase < 0.5 ? 1.0 : -1.0;
                double high = highPhase < 0.5 ? 1.0 : -1.0;
                double envelope = GetModemToneEnvelope(i, sampleCount);
                short sample = (short)(short.MaxValue * ModemToneAmplitude * envelope * (low + high) * 0.5);

                if (!EnqueueGeneratedSampleBlocking(sample))
                    return;

                lowPhase += lowStep;
                if (lowPhase >= 1.0)
                    lowPhase -= 1.0;

                highPhase += highStep;
                if (highPhase >= 1.0)
                    highPhase -= 1.0;
            }
        }

        private void QueueModemSilence(int durationMilliseconds)
        {
            int sampleCount = SampleRate * durationMilliseconds / 1000;
            for (int i = 0; i < sampleCount; i++)
            {
                if (!EnqueueGeneratedSampleBlocking(0))
                    return;
            }
        }

        private void QueueModemV25AnswerTone(int durationMilliseconds)
        {
            int sampleCount = SampleRate * durationMilliseconds / 1000;
            int reversalSamples = Math.Max(1, SampleRate * ModemV25AnswerToneReversalMilliseconds / 1000);
            double phase = 0;
            double phaseStep = GetSn76489ApproximateFrequency(ModemV25AnswerToneHz) / SampleRate;

            for (int i = 0; i < sampleCount; i++)
            {
                double carrier = phase < 0.5 ? 1.0 : -1.0;
                double envelope = GetModemToneEnvelope(i, sampleCount);
                short sample = (short)(short.MaxValue * ModemToneAmplitude * 0.9 * envelope * carrier);

                if (!EnqueueGeneratedSampleBlocking(sample))
                    return;

                phase += phaseStep;
                if (i > 0 && i % reversalSamples == 0)
                    phase += 0.5;

                if (phase >= 1.0)
                    phase -= 1.0;
            }
        }

        private void QueueV32BisReceiverConditioning(bool answerMode)
        {
            QueueV32BisStates(new[] { 2, 3 }, V32BisSSequenceMilliseconds, amplitude: ModemToneAmplitude * 0.75);
            QueueV32BisStates(new[] { 0, 1 }, V32BisSBarSequenceMilliseconds, amplitude: ModemToneAmplitude * 0.75);
            QueueV32BisTraining(V32BisTrainingMilliseconds, answerMode ? ModemToneAmplitude * 0.78 : ModemToneAmplitude * 0.72);
        }

        private void QueueV32BisStates(int[] stateQuadrants, int durationMilliseconds, double amplitude)
        {
            QueueV32BisSymbols(durationMilliseconds, amplitude, symbol =>
            {
                return stateQuadrants[symbol % stateQuadrants.Length];
            });
        }

        private void QueueV32BisTraining(int durationMilliseconds, double amplitude)
        {
            QueueV32BisSymbols(durationMilliseconds, amplitude, symbol =>
            {
                if (symbol < 256)
                    return ((symbol * 1103515245 + 12345) & 0x40000000) == 0 ? 2 : 0;

                return ((symbol * 1103515245 + 12345) >> 29) & 0x03;
            });
        }

        private void QueueV32BisRateSignal(int durationMilliseconds, double amplitude)
        {
            int[] ratePattern = { 2, 0, 0, 3, 1, 2, 3, 0, 2, 1, 0, 3, 3, 1, 2, 0 };
            QueueV32BisSymbols(durationMilliseconds, amplitude, symbol => ratePattern[symbol % ratePattern.Length]);
        }

        private void QueueV32BisSymbols(int durationMilliseconds, double amplitude, Func<int, int> getQuadrant)
        {
            int sampleCount = SampleRate * durationMilliseconds / 1000;
            int symbolSamples = Math.Max(1, SampleRate / V32BisSymbolRate);
            double phase = 0;
            double phaseStep = GetSn76489ApproximateFrequency(V32BisCarrierHz) / SampleRate;

            for (int i = 0; i < sampleCount; i++)
            {
                int symbol = i / symbolSamples;
                double quadrantPhase = (getQuadrant(symbol) & 0x03) * 0.25;
                double carrier = ((phase + quadrantPhase) % 1.0) < 0.5 ? 1.0 : -1.0;
                double envelope = GetModemToneEnvelope(i, sampleCount);
                short sample = (short)(short.MaxValue * amplitude * envelope * carrier);

                if (!EnqueueGeneratedSampleBlocking(sample))
                    return;

                phase += phaseStep;
                if (phase >= 1.0)
                    phase -= 1.0;
            }
        }

        private void QueueModemNoise(int durationMilliseconds, double amplitude)
        {
            int sampleCount = SampleRate * durationMilliseconds / 1000;
            ushort shiftRegister = 0x4000;
            int samplesPerStep = Math.Max(1, SampleRate / 16000);
            int polarity = 1;

            for (int i = 0; i < sampleCount; i++)
            {
                if (i % samplesPerStep == 0)
                {
                    int feedback = (shiftRegister ^ (shiftRegister >> 1)) & 0x01;
                    shiftRegister = (ushort)((shiftRegister >> 1) | (feedback << 14));
                    if (shiftRegister == 0)
                        shiftRegister = 0x4000;

                    polarity = (shiftRegister & 0x01) == 0 ? -1 : 1;
                }

                double envelope = GetModemToneEnvelope(i, sampleCount);
                short sample = (short)(short.MaxValue * amplitude * envelope * polarity);

                if (!EnqueueGeneratedSampleBlocking(sample))
                    return;
            }
        }

        private static int ScaleModemConnectionDuration(int milliseconds)
        {
            return Math.Max(1, (int)Math.Round(milliseconds * ModemConnectionSequenceDurationScale));
        }

        private bool EnqueueGeneratedSampleBlocking(short sample)
        {
            lock (syncRoot)
            {
                while (generatedCount >= generatedSamples.Length
                    && !Volatile.Read(ref hostOutputPaused)
                    && Volatile.Read(ref running)
                    && !disposed)
                {
                    Monitor.Wait(syncRoot, 20);
                }

                if (Volatile.Read(ref hostOutputPaused) || !Volatile.Read(ref running) || disposed)
                    return false;

                EnqueueGeneratedSample(sample);
                return true;
            }
        }

        private void WaitForGeneratedDrain()
        {
            while (!Volatile.Read(ref hostOutputPaused) && Volatile.Read(ref running) && !disposed)
            {
                lock (syncRoot)
                {
                    if (generatedCount == 0)
                        break;

                    Monitor.Wait(syncRoot, 20);
                }
            }

            if (audioDevice == 0)
                return;

            uint queuedBytes = SDL_GetQueuedAudioSize(audioDevice);
            int queuedSamples = (int)(queuedBytes / sizeof(short));
            if (queuedSamples > 0)
                Thread.Sleep(Math.Min(1000, (queuedSamples * 1000 / SampleRate) + 10));
        }

        private static double GetModemToneEnvelope(int sampleIndex, int sampleCount)
        {
            int envelopeSamples = SampleRate * ModemToneEnvelopeMilliseconds / 1000;
            if (envelopeSamples <= 0)
                return 1.0;

            double envelope = 1.0;
            if (sampleIndex < envelopeSamples)
                envelope = sampleIndex / (double)envelopeSamples;

            int releaseStart = sampleCount - envelopeSamples;
            if (sampleIndex >= releaseStart)
                envelope = Math.Min(envelope, (sampleCount - sampleIndex) / (double)envelopeSamples);

            return Math.Clamp(envelope, 0.0, 1.0);
        }

        private static double GetSn76489ApproximateFrequency(int requestedHz)
        {
            int period = Math.Clamp((int)Math.Round(ClockHz / (32.0 * requestedHz)), 1, 1023);
            return ClockHz / (32.0 * period);
        }

        private static bool TryGetDtmfFrequencies(char digit, out int lowHz, out int highHz)
        {
            switch (digit)
            {
                case '1':
                    lowHz = 697;
                    highHz = 1209;
                    return true;
                case '2':
                    lowHz = 697;
                    highHz = 1336;
                    return true;
                case '3':
                    lowHz = 697;
                    highHz = 1477;
                    return true;
                case '4':
                    lowHz = 770;
                    highHz = 1209;
                    return true;
                case '5':
                    lowHz = 770;
                    highHz = 1336;
                    return true;
                case '6':
                    lowHz = 770;
                    highHz = 1477;
                    return true;
                case '7':
                    lowHz = 852;
                    highHz = 1209;
                    return true;
                case '8':
                    lowHz = 852;
                    highHz = 1336;
                    return true;
                case '9':
                    lowHz = 852;
                    highHz = 1477;
                    return true;
                case '0':
                    lowHz = 941;
                    highHz = 1336;
                    return true;
                default:
                    lowHz = 0;
                    highHz = 0;
                    return false;
            }
        }

        /// <summary>SN76489 writes are latched by WE after the VIA slow-bus delay, not at the CPU write edge.</summary>
        public void WriteData(byte value)
        {
            lock (syncRoot)
            {
                scheduledEvents.Enqueue(ScheduledPsgEvent.ForLatchedValue(emulatedCycle + PsgWriteEnableDelayCycles, value));
                Volatile.Write(ref scheduledEventCount, scheduledEvents.Count);
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
                    Volatile.Write(ref scheduledEventCount, scheduledEvents.Count);
                    writeEnableSampleScheduled = true;
                }
            }
        }

        /// <summary>The PSG tone counters advance with emulated CPU time, while host audio drains independently.</summary>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            if (Volatile.Read(ref hostOutputPaused) || Volatile.Read(ref hostOutputMuted))
            {
                lock (syncRoot)
                {
                    long targetCycle = emulatedCycle + cycles;
                    while (scheduledEvents.Count > 0 && scheduledEvents.Peek().Cycle <= targetCycle)
                    {
                        ScheduledPsgEvent scheduledEvent = scheduledEvents.Dequeue();
                        Volatile.Write(ref scheduledEventCount, scheduledEvents.Count);
                        emulatedCycle = scheduledEvent.Cycle;
                        ApplyScheduledEvent(scheduledEvent);
                    }

                    sampleCycleRemainder = 0;
                    chipSampleRemainder = 0;
                    emulatedCycle = targetCycle;
                    printerHeadEvents.Clear();
                    printerClickEnvelope = 0;
                    printerClickSamplesUntilNext = 0;
                    Volatile.Write(ref printerOutputActive, 0);
                }

                return;
            }

            if (Volatile.Read(ref scheduledEventCount) == 0
                && Volatile.Read(ref emulatedOutputActive) == 0
                && Volatile.Read(ref printerOutputActive) == 0
                && discDriveSound?.HasActiveOutput != true)
            {
                emulatedCycle += cycles;
                sampleCycleRemainder = 0;
                chipSampleRemainder = 0;
                return;
            }

            WaitForGeneratedHeadroom();

            lock (syncRoot)
            {
                long targetCycle = emulatedCycle + cycles;
                while (scheduledEvents.Count > 0 && scheduledEvents.Peek().Cycle <= targetCycle)
                {
                    ScheduledPsgEvent scheduledEvent = scheduledEvents.Dequeue();
                    Volatile.Write(ref scheduledEventCount, scheduledEvents.Count);
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
            mixed += discDriveSound?.GenerateSample() ?? 0;
            mixed += GenerateCassetteToneSample();
            mixed += GeneratePrinterClickSample();
            return (short)Math.Clamp(mixed * short.MaxValue, short.MinValue, short.MaxValue);
        }

        private double GeneratePrinterClickSample()
        {
            if (printerClickSamplesUntilNext > 0)
                printerClickSamplesUntilNext--;

            if (printerClickSamplesUntilNext == 0 && printerHeadEvents.Count > 0)
            {
                PrinterHeadEvent headEvent = printerHeadEvents.Dequeue();
                if (headEvent.PinCount > 0)
                {
                    printerClickEnvelope = Math.Min(
                        0.36,
                        printerClickEnvelope + PrinterClickAmplitude * Math.Sqrt(headEvent.PinCount / 9.0));
                }

                printerClickSamplesUntilNext = headEvent.DurationSamples;
            }

            printerClickNoise ^= printerClickNoise << 13;
            printerClickNoise ^= printerClickNoise >> 17;
            printerClickNoise ^= printerClickNoise << 5;
            double noise = ((printerClickNoise & 0xFFFF) / 32767.5) - 1.0;
            printerClickFastNoise += (noise - printerClickFastNoise) * 0.62;
            printerClickSlowNoise += (noise - printerClickSlowNoise) * 0.10;
            double paperImpact = printerClickFastNoise - printerClickSlowNoise;
            double headResonance = Math.Sin(printerClickResonancePhase);
            printerClickResonancePhase += Math.Tau * 3200 / SampleRate;
            if (printerClickResonancePhase >= Math.Tau)
                printerClickResonancePhase -= Math.Tau;

            double sample = printerClickEnvelope * (paperImpact * 0.8 + headResonance * 0.2);
            printerClickEnvelope *= PrinterClickDecay;

            if (printerHeadEvents.Count == 0
                && printerClickSamplesUntilNext == 0
                && printerClickEnvelope < 0.0001)
            {
                printerClickEnvelope = 0;
                Volatile.Write(ref printerOutputActive, 0);
                return 0;
            }

            return sample;
        }

        private double GenerateCassetteToneSample()
        {
            if (cassetteToneHz <= 0)
                return 0;

            double sample = Math.Sin(cassetteTonePhase) * CassetteToneAmplitude;
            cassetteTonePhase += Math.Tau * cassetteToneHz / SampleRate;
            while (cassetteTonePhase >= Math.Tau)
                cassetteTonePhase -= Math.Tau;
            return sample;
        }

        private void GenerateSamplesForCycles(int cycles)
        {
            if (cycles <= 0)
                return;

            double exactSamples = ((cycles * (double)SampleRate) / CpuClockHz) + sampleCycleRemainder;
            int samplesToGenerate = (int)exactSamples;
            sampleCycleRemainder = exactSamples - samplesToGenerate;
            if (samplesToGenerate <= 0)
                return;

            if (!IsEmulatedOutputActive())
            {
                chipSampleRemainder = 0;
                return;
            }

            for (int i = 0; i < samplesToGenerate; i++)
                EnqueueGeneratedSample(GenerateSample());
        }

        private bool IsEmulatedOutputActive()
        {
            return cassetteToneHz > 0
                || volumes[0] < 15
                || volumes[1] < 15
                || volumes[2] < 15
                || volumes[3] < 15
                || Volatile.Read(ref printerOutputActive) != 0
                || discDriveSound?.HasActiveOutput == true;
        }

        private void UpdateEmulatedOutputActiveLocked()
        {
            bool active = cassetteToneHz > 0
                || volumes[0] < 15
                || volumes[1] < 15
                || volumes[2] < 15
                || volumes[3] < 15;

            Volatile.Write(ref emulatedOutputActive, active ? 1 : 0);
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

                UpdateEmulatedOutputActiveLocked();
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

            UpdateEmulatedOutputActiveLocked();
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
            if (audioDevice == 0 || !Volatile.Read(ref running))
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

        private readonly record struct PrinterHeadEvent(int PinCount, int DurationSamples);

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
