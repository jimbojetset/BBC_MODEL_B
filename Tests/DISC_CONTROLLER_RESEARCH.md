# Disc controller failure investigation — 15 September 2026

This audit distinguishes chip evidence, manufacturer specifications and upstream
emulator policy. The investigation itself did not change controllers or expectations;
the subsequently authorized correction is recorded at the end.
The eight current failures must not be described collectively as hardware bugs.

| Failing cases | Finding | Confidence / action |
|---|---|---|
| 8271 ModeReadback | The recovered 8271 ROM clears bit $40 of internal R23 ($17) when accepting a command. READ SPECIAL REGISTER reads that internal register. This supports $81 during the read command, rather than treating the previously written $C1 as immutable. | Strong chip-ROM evidence for the functional correction. The zero-delay local adapter is still not a physical timing test. |
| 8271 ModeWriteStatus | Intel defines result-register-full separately from interrupt-request. READ DRIVE STATUS returns a result without a completion interrupt. Our status reader hides result-full whenever NMI is not asserted. | Manufacturer-supported functional defect. Exact immediate $10 observation has no independent hardware capture; retain Unverified. |
| 1770 D4Index / DCImmediateAndIndex | The expected event comes from jsbeeb asserting index for an empty drive and sampling its first edge on a drive callback. This is not a universal WD1770 property. | Drive-dependent; do not alter ours to assume every empty drive generates this edge. |
| 1770 StepRate/0–3 | The base 6/12/20/30 ms rates match the WD1770 target. The extra 32 microseconds is an explicit command-completion timer in upstream emulation. | No matching physical measurement found establishing that exact extra delay. Neither exact total is proven by these tests; do not add 64 cycles solely to match jsbeeb. |

## 8271: recovered silicon ROM and real-BBC software

Chris Evans' [8271 reverse-engineering report](https://scarybeastsecurity.blogspot.com/2020/11/reverse-engineering-forgotten-1970s.html)
includes ROM instruction addresses $014–$017 (command acceptance) and
$32E–$335 (READ SPECIAL REGISTER). Together these support the mode-readback
conclusion above. This is an inference from recovered chip code, not a new
capture of our exact register sequence. The report also describes black-box
hardware checks of its ROM interpretation.

Intel's [8271/8271-6 datasheet](https://pulsar-cad.com/datasheets/54/05/00000000554.pdf),
printed page 8-118 (PDF page 4), distinguishes status bits D4 and D3. Table 2 on
printed page 8-126 (PDF page 12) records no completion interrupt for READ DRIVE
STATUS and READ SPECIAL REGISTER. These scanned pages were visually inspected.
That supports exposing a completed polled result without requiring NMI.
This is specification evidence, not an independent BBC test result.

The [original 8271 test software](https://github.com/scarybeasts/misc/tree/master/8271)
linked by Evans includes command-full, parameter-full and busy polling in
`test.asm`, and a VIA-timed measurement in `latency.bas`. These files were
inspected as research leads; they have not been integrated or given fabricated
expected results. Their repository links are moving references, not pinned
executable fixtures in our source manifest.

The pinned beebjit `intel_fdc_update_external_status` explicitly acknowledges
instant command handling as inaccurate and reports BBC latencies including
188 microseconds for READ DRIVE STATUS (including setup). Our jsbeeb adapters
write parameters and observe results without advancing time. They can compare
the two emulators' completed-state shortcuts, but cannot establish when a real
8271 reaches those states. All existing cases retain their Unverified labels.

## 1770: index input belongs to the drive interface

The [WD1770 pin description in Commodore's service manual](https://www.devili.iki.fi/Computers/Commodore/C1581/Service_Manual/Page_09.html)
identifies IP as an external active-low input. The controller does not decide
whether an empty drive asserts it.

TEAC's [FD-55BV-06 specification, revision C](https://forum.maxiol.com/index.php?act=attach&id=9753&type=post),
section 1-8-3(10), printed page 119, gates index output with hole detection,
drive selection and READY, with a different qualification for hard-sector
configuration. This is a concrete manufacturer counterexample to treating a
single empty-drive model as universal. It does not establish the output of every
BBC drive, nor prove our entire index implementation correct.

In pinned jsbeeb, `DiscDrive.indexPulse` returns true without a disc; the first
callback provides the transition assumed by D4/DC. The local controller fixture
has no explicit equivalent electrical drive profile. Therefore these failures
are unsuitable as unconditional controller-conformance assertions. They remain
visible, unchanged, as Unverified external-model comparisons.

## 1770: nominal step rate versus completion latency

Avery Lee's [Altirra Hardware Reference Manual](https://www.virtualdub.org/downloads/Altirra%20Hardware%20Reference%20Manual.pdf),
chapter 10, table 57 (printed page 300 in the inspected edition), lists
6/12/20/30 ms for WD1770 at 8 MHz. Its XF551 discussion uses a different clock;
it is not a BBC timing capture and has not been substituted into our tests.
No evidence found there establishes the tested extra 32 microseconds.

The pinned [jsbeeb WD engine](https://github.com/mattgodbolt/jsbeeb/blob/d74d9043e83b981a4a3fe1d621f309e8bf083a9c/src/wd-fdc.js)
adds that delay explicitly in `_commandDone`. The pinned
[beebjit WD engine](https://github.com/scarybeasts/beebjit/blob/c977309448a5622c4cecf2a29f8678d0fb3aeee8/wd_fdc.c)
has the same 32-microsecond completion timer. Agreement between related emulator
implementations is not an independent measurement. No hardware result or
citation accompanies that constant in these functions.

## Evidence needed to settle the outstanding timing and drive questions

- For WD1770 seek completion: capture command-write, STEP, INTRQ and busy reads
  on an identified WD1770 at a measured clock rate, with spin-up disabled and
  verification off, for all four rates. Record the exact interval endpoints.
- For index: identify the drive model and straps, record IP while empty and
  selected, then record D4/DC interrupt responses to the observed transitions.
  Test a known injected index transition separately from the drive's behaviour.
- For the exact 8271 adapters: replay the sequences on a real BBC with the
  command/parameter/busy handshakes, recording status and result independently.
  Measure latency separately instead of asserting immediate completion.

No physical apparatus or new hardware captures were available for this audit.
No test was promoted to SourcedBBC and no failure was hidden or weakened.
The justified next functional work is the 8271 status/register handling;
the six WD1770 failures do not currently justify changing timing or empty-drive
signalling merely to make the upstream comparisons pass.

## Subsequent authorized implementation — 15 September 2026

The two 8271 functional corrections have now been applied. Result-ready no
longer depends on NMI, result-free commands/reset leave it clear, and command
acceptance clears CMD_FULL in mode register $17. Existing deferred result timing
is retained. All seven original 8271 cases and three additional Unverified
software regressions pass. The exact hardware timing remains unverified.

No WD1770 change was made. Its six discrepancies remain visible; the audit's
cautions about completion latency and drive-dependent index behaviour still
apply. Existing upstream expectations and all evidence labels are unchanged.
