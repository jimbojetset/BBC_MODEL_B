# BBC timing evidence audit

## SourcedBBC/Seddon/* — external BBC timing programs

Original author: Tom Seddon. Source:
[beeb_6502_timing_tests](https://github.com/tom-seddon/beeb_6502_timing_tests/tree/f3dec4fddd1acce008949551ffbf2c4fea181989).
Revision: `f3dec4fddd1acce008949551ffbf2c4fea181989`.
The upstream README explicitly reports testing on **BBC B and Master 128**.
We use only the NMOS/BBC B expectations; this is an upstream validation claim,
not a new physical-machine measurement by this project.

Unchanged tokenised `TIMINGS`, README, notes and GPL-3.0 licence are in
`../External/seddon`. `SeddonTimingTests.cs` identifies each original hex case ID
and BASIC line number. Thirty cases cover absolute/indexed RAM/ROM/I/O reads
and writes, page crossings and 1 MHz bus phases. The source validation script
checks IDs, line references, instruction bytes, index values and NMOS cycle
expectations directly against the retained BASIC program:

```sh
python3 Tests/External/verify_seddon.py
```

### Adaptation

The adapter retains the upstream ten-copy instruction buffer, User VIA T2
measurement, `(cycles * 10 + 12 + 4) / 2` timer setup and expected **zero T2END
low byte**. Twelve cycles are JSR/RTS overhead and four are timer-load overhead,
as specified by upstream PROCT. T2END's high byte is not asserted upstream and
is not asserted here. The timer value is loaded before the measurement, and the
program's `BIT 0` phase changes are retained.

Code and zero-page workspace are relocated. The BASIC assembler/UI, CPU/model
detection and self-modifying register reload setup are replaced by fixed test
setup. The measured START2 body, JSR/RTS and timer-read sequence are retained;
there is no Master ACCCON path. MOS is not booted; timers are reset and System
VIA synthetic VSYNC and firmware hooks are disabled. Thus these are **adapted
cases**, not a claim that the entire original disc passed. A mismatch requires
checking this adaptation as well as CPU/bus/VIA behaviour.

## Unverified/* — all 59 original local cases

All original cases from `CpuTimingTests.cs`, `BusTimingTests.cs` and
`DeviceTimingTests.cs` retain their assertions but are now prefixed Unverified.
None previously ran imported BBC test programs or consumed captured hardware
results. Their timing-document references are explanatory, not independent
validation. The emulator-specific callback, stall and peek checks are software
regressions. The remaining local timing expectations are unverified assumptions.

The historical 53 passes/6 failures do not establish 53 verified behaviours or
six proven hardware bugs. Those assertions must be audited before changing core
behaviour to satisfy them.

## Not yet integrated

The remaining Seddon cases, dp111's timing discs and Richard Talbot-Watkins'
TestTimings IRQ program have **not** been integrated by this change. Links to
those sources are not counted as executed coverage. No raster, disk, Tube or
complete cycle-by-cycle hardware certification is claimed.

## Audited baseline — 14 September 2026

- Sourced BBC: **30 cases, 24 passed, 6 failed** (&16, &66, &17, &67, &18, &68).
- Unverified local: **59 cases, 53 passed, 6 failed**.
- Full run: 89 cases, 77 passed, 12 failed; exit code 1.

The sourced failures concern indexed reads crossing from I/O to fixed ROM or
between I/O pages. They leave T2END low at 10 (&16/&66/&18/&68) or 5 (&17/&67),
against the original zero expectation. They require adapter/CPU/bus/VIA diagnosis;
no expected value or production behaviour was changed to obtain a pass.
