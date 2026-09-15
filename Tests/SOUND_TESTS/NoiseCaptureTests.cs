using BBC;
using System.Reflection;
using System.Security.Cryptography;

internal static class NoiseCaptureTests
{
    private const int CaptureLength = 32767;
    private const string CaptureHash = "AE43BC97F5C6D9524C56D7F1E8925242D6AC8A0BD40B33052C8EADD68BCBD6DF";

    public static void Add(List<(string Name, Action Body)> tests)
    {
        tests.Add(("SourcedBBC/Kortink/WhiteNoiseFullCycle", () =>
        {
            byte[] capture = ReadCapture();
            using var noise = new NoiseProbe();
            noise.WriteNoiseControl(0xE7);
            // Include the join back to the beginning of the hardware capture.
            Compare(noise, capture, CaptureLength + 32);
        }));
        tests.Add(("SourcedBBC/Kortink/WhiteNoiseRegisterRestart", () =>
        {
            byte[] capture = ReadCapture();
            using var noise = new NoiseProbe();
            noise.WriteNoiseControl(0xE7);
            Compare(noise, capture, 513);
            noise.WriteNoiseControl(0xE7);
            Compare(noise, capture, 256);
        }));
        tests.Add(("SourcedBBC/Kortink/WhiteNoiseRestartAfterPeriodicMode", () =>
        {
            byte[] capture = ReadCapture();
            using var noise = new NoiseProbe();
            noise.WriteNoiseControl(0xE3);
            for (int i = 0; i < 37; i++) noise.NextNoiseBit();
            noise.WriteNoiseControl(0xE7);
            Compare(noise, capture, 256);
        }));
    }

    private static byte[] ReadCapture()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "External", "white_noise.bin"));
        if (bytes.Length != CaptureLength || Convert.ToHexString(SHA256.HashData(bytes)) != CaptureHash)
            throw new InvalidOperationException("Kortink hardware capture length/hash mismatch; restore the original file.");
        return bytes;
    }

    private static void Compare(NoiseProbe noise, byte[] capture, int count)
    {
        // The recorded sequence starts at an arbitrary phase. Kortink identifies
        // offset 20862 as the reset state, so the first post-shift bit is 20863.
        // No search for a phase or polarity that makes the emulator pass is used.
        for (int i = 0; i < count; i++)
        {
            int offset = (20863 + i) % capture.Length;
            byte actual = noise.NextNoiseBit();
            if (actual != capture[offset])
                throw new InvalidOperationException(
                    $"Noise shift {i + 1}, capture offset {offset}: expected {capture[offset]}, actual {actual}");
        }
    }

    private sealed class NoiseProbe : IDisposable
    {
        private readonly SN76489_Sound sound = new();
        private readonly Func<double> chipSample;
        private readonly FieldInfo shiftRegister;

        public NoiseProbe()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            chipSample = typeof(SN76489_Sound).GetMethod("GenerateChipSample", flags)!
                .CreateDelegate<Func<double>>(sound);
            shiftRegister = typeof(SN76489_Sound).GetField("noiseShiftRegister", flags)!;
            // Tone channel 3, divisor 25, as used for Kortink's capture. Attenuation
            // remains muted so public Tick applies writes without advancing audio.
            sound.WriteData(0xC9);
            sound.WriteData(0x01);
            sound.Tick(64);
        }

        public void WriteNoiseControl(byte value)
        {
            sound.WriteData(value);
            // Setup allowance only, not a hardware assertion about write latency.
            sound.Tick(64);
        }

        public byte NextNoiseBit()
        {
            ushort before = (ushort)shiftRegister.GetValue(sound)!;
            for (int samples = 0; samples < 4096; samples++)
            {
                chipSample();
                ushort after = (ushort)shiftRegister.GetValue(sound)!;
                if (after != before)
                {
                    // The capture is the inverted LFSR bit. This observes the raw
                    // register; it does not assert the emulator's mixed PCM polarity.
                    return (byte)(1 - (after & 1));
                }
            }
            throw new InvalidOperationException("Noise generator did not advance within 4096 chip samples");
        }

        public void Dispose() => sound.Dispose();
    }
}
