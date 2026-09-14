# VIA tests

A standalone console test project for the BBC's System and User 6522 VIAs.
Run from the repository root:

```sh
dotnet run --project Tests/VIA_TESTS/VIA_TESTS.csproj -c Release
```

No ROMs, downloaded vectors, emulator window, audio device or live services are
needed. The System VIA uses a sound-chip object without starting host audio.
NuGet restore follows the normal project dependency and audit settings.

## Selecting tests

```sh
# List all test names without executing them.
dotnet run --project Tests/VIA_TESTS/VIA_TESTS.csproj -c Release -- --list

# Run one VIA or a specific area (case-insensitive substring match).
dotnet run --project Tests/VIA_TESTS/VIA_TESTS.csproj -c Release -- --filter System/
dotnet run --project Tests/VIA_TESTS/VIA_TESTS.csproj -c Release -- --filter Timer2/
dotnet run --project Tests/VIA_TESTS/VIA_TESTS.csproj -c Release -- --filter User/Timer1/FreeRunPeriod
```

Every selected test runs, including after failures. Exit codes are **0** for all
passed, **1** for any failed assertion or unexpected test exception, and **2** for
invalid arguments or an empty selection. Each failure includes its test name and
expected/actual values. There are no skipped or expected-failure exceptions.

## Coverage

`CommonTests.cs` exercises both implementations through their public registers:

- Reset, register mirrors, DDRs, output latches and input/output mixing.
- IER set/clear semantics, masked pending interrupts, IFR summary and selective acknowledgement.
- T1/T2 clock rate, initial timeout, one-shot rearming and read/write acknowledgement.
- T1 latch/counter separation, free-running period and PB7 output modes.
- Save/load continuation across an odd CPU-cycle boundary.

`SystemTests.cs` covers address decoding, external VSYNC, ORA handshake versus its
non-handshake alias, keyboard scanning, IC32 screen latches and ADC completion.
`UserTests.cs` covers address decoding, external port inputs, output readback,
T2 PB6 pulse counting, manual printer strobes and CA1 acknowledgement.

Each case starts with a fresh VIA. Time advances in emulated CPU cycles, not wall
clock time. At the BBC's 2MHz CPU rate, two cycles represent one 1MHz VIA clock.
Timer checks use two-cycle increments to avoid skipping underflows. The initial
timeout check allows one VIA clock of API phase uncertainty beyond N+2 clocks;
the free-running period check measures the interval between observed underflows.
The System VIA's synthetic VSYNC is disabled for isolated timer tests. User VIA
timer-offset environment overrides are cleared in the test process only.

## Reference and limits

Register and timer expectations use the common 6522 behaviour documented in the
[WDC W65C22 datasheet](https://www.westerndesigncenter.com/wdc/documentation/w65c22.pdf),
particularly peripheral ports, handshake control, timers and interrupt operation
(sections 2.1–2.10 and 2.14). This is a reference for shared VIA functionality,
not a claim that the BBC's NMOS part has every WDC electrical characteristic.
BBC-specific checks exercise the emulator's keyboard, IC32, VSYNC and ADC wiring.
The save/load test is a regression check, not an independent hardware oracle.

This initial suite does not cover the shift-register modes, general input
latching, every CA/CB pin mode, same-edge interrupt races, electrical behaviour,
or CPU bus wait-state integration. It uses original C# assertions rather than
ported jsbeeb tests or captured real-machine traces. Passing it alone would not
establish complete VIA accuracy.

## Initial baseline — 14 September 2026

**66 tests: 57 passed, 9 failed.** The following failures remain visible and return
exit code 1; this project addition does not change the emulator implementation.

| Failing test | Observed gap |
|---|---|
| `System/Timer1/FirstTimeout` | T1 has not expired by N+3 VIA clocks; its load offset is 256 CPU cycles. |
| `System/Timer1/PB7OneShot` | Loading T1 does not drive PB7 low. |
| `System/Timer1/PB7FreeRun` | Underflow does not toggle PB7. |
| `System/Vsync/WriteHandshake` | ORA writes do not acknowledge CA1. |
| `System/ADC/InterruptLatchesUntilAcknowledged` | Returning EOC inactive clears the pending CB1 interrupt. |
| `User/Ports/PortBOutputReadback` | External input bits override ORB output-latch readback. |
| `User/Timer2/PulseModeIgnoresElapsedTime` | T2 counts elapsed clocks in pulse-counting mode. |
| `User/Timer2/PB6FallingEdges` | PB6 falling edges do not produce additional decrements. |
| `User/Handshake/PortAAcknowledgesCA1` | ORA reads do not acknowledge CA1. |
