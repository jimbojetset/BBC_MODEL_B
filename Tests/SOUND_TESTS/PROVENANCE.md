# Sound evidence and adaptation

## Source

All `SourcedBBC/Kortink/*` cases use John Kortink's
[SN76489 hardware research and captured bitstream](https://www.zeridajh.org/articles/me_sn76489_sound_chip_details/index.html).
The unchanged 32,767-byte capture, attribution, permission statement and hash are
retained in `../External/kortink`. It was acquired from the author's download,
not generated using this emulator or another emulator.

Kortink reports measuring an SN76489 in his Acorn BBC using tone channel 3 at
5 kHz as a synchronous reference. The machine model and chip revision are not
specified. His reset-state experiment is reported with some uncertainty; we
have not repeated it on physical hardware. A failure against that alignment
must therefore be investigated rather than automatically called a hardware bug.

## Exact mapping

The capture stores one decoded output bit per byte. The reported reset offset
is 20862; the first bit after a shift is at offset 20863. Comparisons use
`capture[(20863 + shiftIndex) % 32767]`. That indexing interprets the reset
position as the state before the first shift. No run-time alignment search is
allowed. An off-by-one discrepancy is a test-adaptation question, not a reason
to silently rotate the data until a pass appears.

The probe observes the raw production shift-register low bit and inverts it to
match the capture's output-bit convention. It does not inspect mixed PCM or
verify the emulator's analogue/output polarity. This conversion is explicit
and fixed, not selected from observed results.

## Scenario provenance

| Case | External expectation | Local adaptation |
|---|---|---|
| WhiteNoiseFullCycle | Original captured bitstream and reported repetition | Compare one full cycle and 32 further bits at the fixed offset. |
| WhiteNoiseRegisterRestart | Reported reset on a noise-frequency-register write, then captured sequence | After 513 shifts, rewrite the same control byte and compare 256 bits from restart. |
| WhiteNoiseRestartAfterPeriodicMode | The same reported register-write reset and captured sequence | Run 37 periodic shifts, switch to white noise and compare 256 bits. No periodic output values are asserted. |

The latter two scenarios are our adaptations of the reported reset experiment,
not separately supplied upstream test programs. They share one capture and one
reset claim. Their disturbance lengths are arbitrary coverage choices.

## Probe boundaries

Each case creates a fresh SN76489 object. It programs channel 3's divisor to 25
and selects noise control `$E7` (or `$E3` during periodic setup) through public
`WriteData` calls. `Tick(64)` permits queued writes to apply; this is setup
allowance, not evidence for a particular write latency.

Channel attenuation remains muted so setup ticks do not consume noise samples.
The probe then invokes production `GenerateChipSample` directly, observing
`noiseShiftRegister` through reflection until its state changes. It compares one
bit at each observed shift. This checks the generated sequence, but deliberately
does not assert an elapsed clock interval. A generous bound prevents a stopped
generator hanging the test.

This bypasses host audio, resampling, mixing and public Tick's normal audio
scheduling. It does not prove muted-channel timing, clock selection, output
polarity or the complete BBC sound path. Those areas have no measured expected
data integrated here and remain coverage gaps. Production emulation is unchanged.

Missing or altered capture data fails the case. The source hash check is an
integrity requirement, not an extra hardware test counted in the result totals.

## Baseline — 14 September 2026

All three sourced scenarios pass with the unchanged captured data. This is
33,824 bit comparisons across three scenarios, with repeated use of the same
recording (32,799 + 769 + 256). It is not 33,824 independent hardware experiments.
