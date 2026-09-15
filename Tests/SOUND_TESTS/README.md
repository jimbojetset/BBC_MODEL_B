# BBC sound tests

A separate .NET 10 console project checking the production SN76489 white-noise
generator against **John Kortink's captured BBC noise sequence**. This initial
suite has three sourced scenarios; it does not claim complete sound emulation
verification. See [PROVENANCE.md](PROVENANCE.md).

From the repository root:

```sh
dotnet run --project Tests/SOUND_TESTS -c Release
```

No ROMs, audio device, SDL window or runtime downloads are required. The original
capture is retained in `Tests/External/kortink` with attribution and its hash.

```sh
dotnet run --project Tests/SOUND_TESTS -c Release -- --list
dotnet run --project Tests/SOUND_TESTS -c Release -- --filter WhiteNoiseFullCycle
dotnet run --project Tests/SOUND_TESTS -c Release -- --filter Restart
```

Filtering is a case-insensitive substring match. Exit codes are **0** when all
selected cases pass, **1** for failures (including missing/altered capture data),
and **2** for invalid arguments or no matching cases. Other cases still run
after a failure. Diagnostics identify the noise shift and capture byte offset.

## Coverage

- Complete 32,767-bit white-noise cycle plus 32 bits across the wrap.
- Restart after rewriting the noise-control register during white-noise output.
- Restart into white noise after running periodic-noise mode.

All three use the same external capture, not three independent recordings.
Their sample counts are test coverage choices, not additional hardware results.
Expected bits are read from the capture; no reference LFSR is generated in the
test code and no phase/polarity search is used to obtain a pass.

The fixture uses reflection to clock the real internal chip-sample routine and
observe shifts before mixing/resampling. Register writes use the public sound
API. It neither copies the noise algorithm nor changes the production API.

## Not covered

Tone frequency, attenuation curves, periodic-noise waveform, noise clock rates,
write-enable timing, VIA/IC32 wiring, output polarity, resampling and analogue
speaker response are **not verified by this project**. Nor are speech, cassette,
modem, printer or Turtle audio. No invented assertions for those areas have been
added; further cases need their own external evidence.

## Baseline — 14 September 2026

**3 sourced scenarios passed, 0 failed; 0 unverified cases added.** Release build:
0 warnings and 0 errors. Listing/filtering and invalid/no-match exit codes were
checked. Altering the output-directory capture was rejected with exit code 1;
the original bytes were then restored. The retained source hash also passes.
