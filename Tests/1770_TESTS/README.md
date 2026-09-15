# 1770_TESTS

Standalone .NET 10 console tests for the production WD1770 on the Acorn Model B interface.
No ROMs, physical drives, SDL window or downloaded disc images are required.
All media is generated in memory; no user image files are modified.

```sh
dotnet run --project Tests/1770_TESTS -c Release
dotnet run --project Tests/1770_TESTS -c Release -- --list
dotnet run --project Tests/1770_TESTS -c Release -- --filter jsbeeb
dotnet run --project Tests/1770_TESTS -c Release -- --filter SectorRoundTrip
```

Case-insensitive substring filters; exit 0 for all passing, 1 for failures,
and 2 for invalid arguments or no matching cases. All cases run despite failures.

**No independently verified BBC hardware cases are currently executable here.**
All 14 cases are explicitly **Unverified**. The jsbeeb comparisons preserve
external expectations but lack per-case hardware evidence. The two sector
round trips are local software invariants, not physical-controller results.
See [PROVENANCE.md](PROVENANCE.md) for individual mappings and limitations.

## Coverage

Twelve upstream comparisons: force interrupt (D0, D8, D4, DC, seek abort,
disarming index and acceptance of all sixteen flag combinations while idle
and seeking), plus four WD1770 step rates. WD1772 and Opus variants are excluded.

Two local round trips read a known 256-byte payload from sector 9 on tracks 0
and 39, replace it through the controller's data register and read it back.
They exercise single-sector transfers on an in-memory 40-track SSD. Polling
and watchdog limits are harness choices, not hardware timing measurements.

## Baseline — 15 September 2026

**14 cases: 8 passed, 6 failed.** Build: zero warnings/errors.
All are Unverified; sourced hardware count is zero. Expectations were not
relaxed and WD1770 controller code was not changed.

Failures:

- D4Index and DCImmediateAndIndex: the upstream empty-drive fixture expects an
  index interrupt within 1,000 CPU cycles; our model does not raise it.
- Step rates 0–3: observed 12,000 / 24,000 / 40,000 / 60,000 cycles. Upstream
  expects each step time plus 64 completion cycles, within a 32-cycle polling
  window. These are WD1770 expectations, not WD1772 expectations.

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
unchanged; the WD1770 controller has not been modified. The separate 8271
correction was followed by a rerun: the same six WD1770 comparisons still fail
and both sector round trips still pass.
