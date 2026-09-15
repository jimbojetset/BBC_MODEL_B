# BBC video tests

A standalone .NET 10 console project exercising the production **HD6845 CRTC
and Video ULA**, without ROMs, a window or runtime downloads.

```sh
dotnet run --project Tests/VIDEO_TESTS -c Release
```

The suite has **nine cases** with explicitly different evidence:

- One CRTC case whose upstream author reports real-hardware confirmation.
- Two ULA clock-phase scenarios using a measurement from **one BBC Master**.
- Three external CRTC regression cases without a hardware-validation claim,
  clearly labelled `Unverified/`.
- Three local save/restore regression cases, also labelled `Unverified/`.

Read [PROVENANCE.md](PROVENANCE.md) before interpreting passes or failures as
Model B hardware evidence. These are adapted cases, not a full original disc
run or a complete video conformance suite.

```sh
dotnet run --project Tests/VIDEO_TESTS -c Release -- --list
dotnet run --project Tests/VIDEO_TESTS -c Release -- --filter CRTC
dotnet run --project Tests/VIDEO_TESTS -c Release -- --filter ULA
dotnet run --project Tests/VIDEO_TESTS -c Release -- --filter SourcedBBC/
```

Filters use a case-insensitive substring. Exit codes: **0** all selected cases
pass; **1** one or more failures; **2** invalid arguments or no matching cases.
The runner continues after failures and reports sourced and unverified totals
separately. Master-specific evidence is also identified in the case name.

## Coverage

CRTC: disabling interlace near the dummy raster, zero VSYNC width, VSYNC in
vertical adjustment and selecting interlaced VSYNC during a line.

Video ULA: replay T8's six teletext-select writes, 11 CPU cycles apart, at both
phases of a 1 MHz character. Measure the three boxes and intervening red gaps
in the raw rendered scanline against the reported width of 5.5 cells. No
emulator-generated reference PNG is treated as a photograph of hardware.

The fixture programs public CRTC/ULA registers and advances public `Tick`.
Reflection only observes internal beam state and the framebuffer, because no
headless frame/beam inspection API is exposed.

## Not covered

The full T8 raster program and its CPU/VIA synchronisation, complete SAA5050
character/control-code behaviour, palette/flash correctness, cursor behaviour,
all bitmap modes, every CRTC variant and analogue/composite output are not
verified here. No new expectations for these areas were invented from the
emulator or datasheets.

## Validation — 15 September 2026

**9 cases: 9 passed, 0 failed.** Release build: 0 warnings and 0 errors.

- Sourced: 3/3 pass, including both qualified Master ULA scenarios.
- Unverified: 6/6 pass, comprising three upstream CRTC comparisons and three
  local save/restore checks.

The original six cases initially produced two passes and four failures. After
validating their adapters against pinned upstream engines, the emulator was
corrected for sixteen-line VSYNC pulses, interlace pulse selection on R8 writes
and ULA switching between the halves of a 1 MHz character. Expected values and
source schedules were preserved. See [PROVENANCE.md](PROVENANCE.md) and the
[upstream validation instructions](Validation/README.md).

Save-state version 40 retains the separate VSYNC pulses and cached character
output. Versions 32–39 remain loadable; their missing sub-character state is
reconstructed approximately until subsequent character/pulse edges. The three
local checks compare uninterrupted execution with save/restore during a ULA
half-cell, between interlace pulse edges and before the sixteenth VSYNC line.
These are software regression checks, not hardware conformance evidence.

Related suites: SOUND_TESTS 3/3 pass; MEMORY_TESTS 66/66 pass;
VIA_TESTS 65/76 pass; BBC_TIMING_TESTS 77/89 pass. VIA's eleven failures and
BBC timing's twelve failures have identical diagnostics to the pre-change
baselines. Those outstanding failures are not resolved by this video change.

On 15 September 2026 the user reported correct displays in Yie Ar Kung Fu,
Pole Position, Phoenix, Repton, Elite and Snapper. This is user-reported visual
regression testing, separate from automated tests and physical BBC evidence.
