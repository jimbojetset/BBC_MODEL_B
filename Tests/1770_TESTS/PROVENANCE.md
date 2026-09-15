# WD1770 test evidence audit

[Original WD tests](https://github.com/mattgodbolt/jsbeeb/blob/d74d9043e83b981a4a3fe1d621f309e8bf083a9c/tests/unit/test-wd-fdc.js).

| Local suffix (Unverified/jsbeeb/1770/) | Original case |
|---|---|
| ForceInterrupt/D0 | does not interrupt for &D0 |
| ForceInterrupt/D8 | interrupts immediately for &D8 |
| ForceInterrupt/AbortSeek | aborts a seek in progress and interrupts for &D8 |
| ForceInterrupt/D4Index | interrupts on the next index pulse for &D4 |
| ForceInterrupt/DCImmediateAndIndex | interrupts immediately and on the next index pulse for &DC |
| ForceInterrupt/DisarmIndex | stops interrupting on index pulses once &D0 disarms it |
| ForceInterrupt/AllFlags/Idle | accepts every combination of flags when idle |
| ForceInterrupt/AllFlags/Seeking | accepts every combination of flags during a seek |
| StepRate/0–3 | variants / step rate: WD1770 rows only, 6/12/20/30 ms |

## Adapter audit

Target is the **WD1770 with Acorn Model B register mapping**, not the WD1772,
Master register mapping or Opus Challenger. Upstream default offsets 0/4/5/6/7
map to $FE80/$FE84/$FE85/$FE86/$FE87. Control $21 releases reset and selects
drive 0 in MFM. Original command bytes, status masks, 1,000-cycle short waits,
16-cycle polling and step-time windows are preserved. Controller NmiLineAsserted
replaces the upstream fake CPU's NMI latch; neither executes CPU interrupt code.

Upstream blankDisc() is an unformatted pulse surface. Seek-only cases here
mount a zero-filled SSD because our controller accepts sector images. Those
commands disable verification and do not read sector data. This difference
must be revisited for any future index, read-address or CRC adaptation.

Empty-drive cases stay empty; do not mount a disc or advance a revolution just
to make D4/DC pass. Upstream explicitly models an empty drive's index as held
asserted and sees its first edge on the first pulse callback. Our failure to
match this is an **external drive-model comparison**, not independently verified
electrical behaviour. AllFlags cases retain the original acceptance-only
assertion and do not claim to verify all flag semantics.

The four seek comparisons retain the WD1770 duration plus 64 completion cycles
and a less-than-32-cycle observation allowance. Ours completes at the nominal
step duration; no extra tick or relaxed bound is inserted into the adapter.
The read-data poll in upstream runCommand is retained during seeks too, even
though Type I status bit 1 is an index indicator rather than a data request.

Address-mark, read-track, head-settle and bit-rotation tests are not ported to
SSD approximations: doing so would discard what their original inputs test.
All WD1772 and Opus-specific cases are excluded rather than renamed WD1770.

## External provenance and reproducibility

Original authors: jsbeeb contributors. Pinned revision:
`d74d9043e83b981a4a3fe1d621f309e8bf083a9c`. Unmodified tests and GPL-3.0 licence
are retained in ../External/jsbeeb-fdc; hashes are in ../External/sources.json.
These cases have external code provenance but no established per-case physical
BBC result. Every adaptation is labelled **Unverified** in output.

On 15 September 2026 both full original files passed **41/41** tests under
Node 24.15.0. Reproduce in a separate checkout of the pinned jsbeeb revision:

```sh
npm ci --ignore-scripts
node node_modules/vitest/vitest.mjs run tests/unit/test-intel-fdc.js tests/unit/test-wd-fdc.js
```

From this repository, `python3 Tests/External/verify_sources.py` verifies the
retained files. The normal C# test run needs neither Node nor network access.

## Physical evidence not yet executable

Chris Evans' beebjit HFE experiments at revision
`c977309448a5622c4cecf2a29f8678d0fb3aeee8` are retained in ../External/beebjit-fdc
with their licence. These exercise sync and clock patterns that cannot be
represented by our decoded sector-image input. None are counted as passing,
failing or sourced cases here. Their reported DFS errors also require the
original command/error context rather than assuming raw FDC status equivalence.

## Local software regression checks

`Unverified/SoftwareRegression/SectorRoundTrip/Track0` and `Track39` use a
40-track, ten-sector, 256-byte-sector in-memory SSD. Payload bytes are i XOR $A5;
the replacement payload is their bitwise inverse. Each case checks initial
read, full write, successful completion and readback through public controller
registers, with an exact byte count. These data are generated test inputs, not
external BBC recordings. Success and round-trip equality are software-contract
assertions with **no independent hardware provenance**.

The transfer poll interval is 16 CPU cycles and the watchdog is 4,000,000 cycles.
Neither is asserted as a physical completion time. Images have no backing path;
controller writeback cannot change user files. See README.md for results and
coverage exclusions. The initial test addition did not change production controller behaviour.

## Follow-up hardware investigation

See [the controller failure audit](../DISC_CONTROLLER_RESEARCH.md) for the
chip-ROM evidence supporting 8271 mode readback, the result-ready/NMI distinction,
and why the WD1770 index and completion-delay comparisons do not yet establish
physical defects. Existing expectations and Unverified classifications remain
unchanged; no WD1770 controller code has been modified. The separately authorized
8271 corrections and regression results are recorded in the linked audit.
