# 8271_TESTS

Standalone .NET 10 console tests for the production Intel 8271.
No ROMs, physical drives, SDL window or downloaded disc images are required.
All media is generated in memory; no user image files are modified.

```sh
dotnet run --project Tests/8271_TESTS -c Release
dotnet run --project Tests/8271_TESTS -c Release -- --list
dotnet run --project Tests/8271_TESTS -c Release -- --filter jsbeeb
dotnet run --project Tests/8271_TESTS -c Release -- --filter SectorRoundTrip
```

Case-insensitive substring filters; exit 0 for all passing, 1 for failures,
and 2 for invalid arguments or no matching cases. All cases run despite failures.

**No independently verified BBC hardware cases are currently executable here.**
All 10 cases are explicitly **Unverified**. The jsbeeb comparisons preserve
external expectations but lack per-case hardware evidence. The five local software checks (two sector round trips and three result-lifecycle
checks) are software invariants, not physical-controller results.
See [PROVENANCE.md](PROVENANCE.md) for individual mappings and limitations.

## Coverage

Five upstream status/register comparisons: idle, mode-write status, mode
readback, command busy/full and parameter busy/full behaviour.

Two local round trips read a known 256-byte payload from sector 9 on tracks 0
and 39, replace it through the controller's data register and read it back.
They exercise single-sector transfers on an in-memory 40-track SSD. Polling
and watchdog limits are harness choices, not hardware timing measurements.

## Initial baseline — 15 September 2026

**7 cases: 5 passed, 2 failed.** Build: zero warnings/errors.
All are Unverified; sourced hardware count is zero. Expectations were not
relaxed and production controller code was not changed.

Failures:

- ModeWriteStatus: expected result-ready status $10 after read drive status,
  observed $00.
- ModeReadback: expected $81, observed $C1.

The original upstream files pass 41/41 tests. Differences here remain external
model comparisons, not proof of physical defects. Review the documented adapter
and fixture limitations before changing controller behaviour.

## Not covered

Flux/clock-bit recognition, malformed or deleted marks, CRC generation/errors,
copy protection, index/ready electrical behaviour on a real drive, write-track
formatting, multi-sector commands, DSD side selection, MFM/ADFS data transfers,
write protection and complete MOS/DFS/NMI integration are not verified by this
initial suite. The round trips use direct controller calls, not a running 6502.
The beebjit HFE hardware results are retained as future evidence, not counted
as tests or replaced with decoded-sector approximations.

## Follow-up hardware investigation

See [the controller failure audit](../DISC_CONTROLLER_RESEARCH.md) for the
chip-ROM evidence supporting 8271 mode readback, the result-ready/NMI distinction,
and why the WD1770 index and completion-delay comparisons do not yet establish
physical defects. Existing expectations and Unverified classifications remain
unchanged. The two 8271 functional corrections below have now been implemented.

## After the 8271 corrections — 15 September 2026

**10 cases: 10 passed, 0 failed**, with zero build warnings/errors. The original
seven expectations remain unchanged. Result-full is now independent of NMI,
while deferred completion remains unreadable until its existing delay expires.
Reset, SPECIFY and WRITE SPECIAL REGISTER do not manufacture a result phase.
Command acceptance clears CMD_FULL ($40) in special register $17, so the
original mode-readback comparison returns $81 rather than $C1.

Three added Unverified software checks cover acknowledgement of a polled
result and reset with an unread result, SPECIFY's no-result completion, and
preservation of deferred read completion. They preserve software interface
behaviour; they are not new physical timing evidence. All ten cases remain
Unverified because the exact adapters lack independent hardware recordings.

Regression results: MEMORY_TESTS 66/66 pass; 1770_TESTS remains 8/14 with the
same six failures; BBC_TIMING_TESTS remains 77/89 with the same twelve failures.
The WD1770 implementation has not been changed.
