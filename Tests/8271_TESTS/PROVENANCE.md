# Intel 8271 test evidence audit

[Original Intel tests](https://github.com/mattgodbolt/jsbeeb/blob/d74d9043e83b981a4a3fe1d621f309e8bf083a9c/tests/unit/test-intel-fdc.js).

| Local suffix (Unverified/jsbeeb/8271/) | Original case |
|---|---|
| Idle | should construct and start out idle |
| ModeWriteStatus | does not report command or parameter register full after the mode register is written |
| ModeReadback | keeps the mode bits in the shared internal byte where read special register sees them |
| CommandBusyWithoutFull | shows busy but not command register full once a command taking parameters is written |
| ParametersWithoutFull | does not report parameter register full after any parameter write |

## Adapter audit

The original status cases use an IntelFdc with a FakeDrive, fake CPU and
scheduler. Ours uses a fresh production Intel8271_Disk with no mounted medium;
the selected tests do not advance a drive or inspect the drive-status result
byte. Command/parameter/status/result offsets 0/1 map to $FE80/$FE81. All
command bytes, parameter writes, observation order, masks and expected status
values are unchanged. There are no inserted waits to hide status differences.

Idle observes public status rather than upstream internalStatus and omits the
upstream scheduler-headroom assertion. Fake-drive motor and physical stepping
checks are not ported: our media implementation has no equivalent FakeDrive
injection. The duplicate internal-busy assertion is covered by the public
CommandBusyWithoutFull case. No fake-drive track result is reinterpreted as a
logical sector track number.

The two status failures reproduce discrepancies at the same public register
operations as upstream; this does not independently establish hardware truth.
The round-trip commands use the BBC 8271 drive-zero selection, variable-length
read/write commands, track/sector parameters and a one-sector 256-byte transfer.
They are separately classified as local software checks.

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
unchanged. The subsequent 8271 corrections are recorded below.

## Implemented correction and regression checks

The subsequent 8271 correction follows the chip-ROM and manufacturer evidence
in the linked audit, without changing the five jsbeeb comparisons or relabelling
them as hardware-verified. The initial seven cases now pass.

ResultTests adds three explicitly Unverified local software checks:

- PolledResultAcknowledgement: result-ready can be observed without NMI;
  reading the result acknowledges it, and reset discards an unread result.
- SpecifyHasNoResult: completing SPECIFY does not leave a result or NMI,
  including when an earlier polled result had not been read.
- DeferredReadCompletion: immediately after consuming the last sector byte,
  the emulator's existing completion delay still withholds the result and NMI.

These expectations cover the software lifecycle affected by the change. They
are not externally recorded BBC results. In particular, DeferredReadCompletion
does not measure or certify the real 8271's byte/completion latency. The original
sector round trips check eventual successful completion. Total: 10/10 pass.
