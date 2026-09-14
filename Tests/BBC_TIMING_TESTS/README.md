# BBC timing tests

> **Provenance audit:** See [PROVENANCE.md](PROVENANCE.md). Original local
> assertions are now labelled `Unverified/`; they are not independently verified
> BBC hardware tests. Sourced cases, where present, are reported separately.


A separate console project for NMOS CPU timing and BBC CPU/bus/device integration.
It runs deterministic instruction sequences without ROMs, mounted media, windows,
network test data, wall-clock waits or emulator threads.

```sh
dotnet run --project Tests/BBC_TIMING_TESTS/BBC_TIMING_TESTS.csproj -c Release
```

## Selection and results

```sh
dotnet run --project Tests/BBC_TIMING_TESTS/BBC_TIMING_TESTS.csproj -c Release -- --list
dotnet run --project Tests/BBC_TIMING_TESTS/BBC_TIMING_TESTS.csproj -c Release -- --filter CPU/Interrupts/
dotnet run --project Tests/BBC_TIMING_TESTS/BBC_TIMING_TESTS.csproj -c Release -- --filter Bus/
dotnet run --project Tests/BBC_TIMING_TESTS/BBC_TIMING_TESTS.csproj -c Release -- --filter Devices/
```

Filters are case-insensitive substrings of test names. Exit codes are **0** for
all selected tests passing, **1** for any failure, and **2** for invalid arguments
or no matching tests. A failed case does not prevent remaining cases from running.
There are no skipped or expected-failure exceptions. User VIA timer environment
overrides are cleared within the runner process to make clock checks repeatable.

## Coverage

- `CpuTimingTests.cs`: instruction costs, taken/untaken branches, page crossing,
  read versus write indexing, IRQ mask latency, NMI edge/priority behaviour,
  interrupt cycle delivery and externally requested stalls.
- `BusTimingTests.cs`: absolute reads and writes at both CPU clock phases to RAM,
  ROM, FRED, JIM and representative SHEILA devices; slow-bus read/modify/write
  accesses and clock-neutral peeks.
- `DeviceTimingTests.cs`: VIA progression during RAM and stretched I/O accesses,
  shared System/User VIA clocks, timer-to-CPU IRQ propagation, wired-OR IRQ
  acknowledgement and peripheral progression while the CPU stalls.

`TimingMachine` constructs the real `Emulator` and invokes its memory-map
installation method through reflection. It obtains the two existing VIA objects
for setup and observation; the measured instructions use the production CPU,
memory map, stretch callback, device tick distribution and IRQ wiring. Firmware
hooks are disabled because these tests execute synthetic RAM programs, not MOS.
No emulation methods are copied into the fixture. Reflection is confined to setup
so no production API changes are required; renaming those members requires a test
fixture update.

## Timing expectations and limits

The BBC CPU runs at 2MHz and its VIAs at 1MHz. A slow peripheral access adds one
or two CPU cycles depending on clock phase. The bus tests check both phases
without assuming which host phase is labelled zero. NMOS read/modify/write
instructions perform a read and two writes, so each slow access must stretch.

CPU interrupt entry takes seven cycles. This emulator's `StepInstruction()` also
executes the first handler instruction in the same call; entry tests therefore
expect nine cycles with a two-cycle NOP handler. Device callbacks and the CPU's
elapsed clock must account for the entire interval. The short IRQ pulse check
injects line transitions at emulated cycle callbacks around a NOP polling point.

References:

- [A Hardware Guide for the BBC Microcomputer, circuit description](https://ftp.nvg.ntnu.no/pub/bbc/doc/A%20Hardware%20Guide%20for%20the%20BBC%20Microcomputer/bbc_hw_03.htm): BBC clock stretching and bus connections.
- [6502Timing](https://github.com/dp111/6502Timing): independent instruction timing test material for future expansion.
- [Visual6502 interrupt recognition analysis](https://www.nesdev.org/wiki/Visual6502wiki/6502_Interrupt_Recognition_Stages_and_Tolerances): transistor-level interrupt timing reference.
- [NMOS interrupt behaviour](https://www.nesdev.org/wiki/CPU_interrupts): CLI/SEI/PLP versus RTI polling and interrupt recognition.

The original group consists of local C# assertions. Thirty external Seddon
BBC timing cases are now adapted in `SourcedBBC/`; see PROVENANCE.md for the
retained source, hardware-validation claim and adapter limitations. The project does not establish complete cycle-by-cycle bus
accuracy. It does not yet test raster/video contention, protected-disc timing,
Tube synchronisation, all addressing-mode dummy accesses, every interrupt race,
or analogue clock setup/hold margins. VIA register-level behaviour is covered
separately by `VIA_TESTS`.

## Initial baseline — 14 September 2026

**59 tests: 53 passed, 6 failed.** All bus read/write phase checks and VIA clock
progression during normal/stretched accesses pass. Remaining failures are:

| Test | Observed behaviour |
|---|---|
| `CPU/Interrupts/RTIUnmasksImmediately` | One foreground instruction executes before the pending IRQ is taken. |
| `CPU/Interrupts/PolledIRQSurvivesDeassertion` | The short IRQ pulse is not retained for the next instruction boundary. |
| `CPU/Interrupts/NMICycleDelivery` | Only six cycles reach the device callback for seven-cycle entry plus a two-cycle NOP. |
| `CPU/Interrupts/NMIPriorityOverIRQ` | IRQ overrides NMI when both lines assert together. |
| `CPU/Cycles/ExternalStallReachesDevices` | Requested CPU stall cycles are absent from device callbacks. |
| `Devices/StallAdvancesVIATimer` | The VIA advances for the instruction but not the preceding CPU stall. |

These failures remain visible and return exit code 1. Emulator code is unchanged
by this test-project addition. Audit these unverified expectations before changing emulator behaviour.
They are not automatically proven hardware defects.
