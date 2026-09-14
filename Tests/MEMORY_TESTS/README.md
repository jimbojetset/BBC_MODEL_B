# BBC memory tests

> **Provenance audit:** See [PROVENANCE.md](PROVENANCE.md). Original local
> assertions are now labelled `Unverified/`; they are not independently verified
> BBC hardware tests. Sourced cases, where present, are reported separately.


A standalone .NET 10 console project exercising the production BBC memory map.
No ROM downloads, disc images, SDL window or test framework are required.

Run from the repository root:

```sh
dotnet run --project Tests/MEMORY_TESTS -c Release
```

List cases or select a case/group (case-insensitive substring):

```sh
dotnet run --project Tests/MEMORY_TESTS -c Release -- --list
dotnet run --project Tests/MEMORY_TESTS -c Release -- --filter Banks
dotnet run --project Tests/MEMORY_TESTS -c Release -- --filter OS/ResetVector
```

Exit codes: **0** all selected tests pass, **1** a test fails, **2** invalid
arguments or no matching tests. A failure reports the case and assertion; the
remaining cases still run.

## Coverage

- All 32 KB of main RAM, fixed OS ROM protection and the reset vector.
- CPU load/store and instruction fetching through the mapped memory bus.
- Word reads across the sideways/OS boundary and wrapping at `$FFFF`.
- All 16 sideways ROM banks, checking every byte of each 16 KB window and write protection.
- ROMSEL's low four bits through every `$FE30–$FE3F` alias and all 256 byte values.
- Sideways RAM bank isolation, switching, CPU writes, moving banks, occupied-target
  rejection, clearing RAM without clearing ROM, and empty-socket write rejection.
- Loading synthetic 8 KB and 16 KB ROM files, including 8 KB mirroring.
- FRED, JIM and SHEILA writes hiding the underlying ROM; unmapped I/O reads
  remaining independent of that ROM.
- Device chip-select predicates across the entire 64 KB address space, plus VIA,
  ADC, CRTC and ULA register mirrors through the production memory map.

## Scope and fixtures

Each case creates a fresh `Emulator`. The fixture installs its private memory-map
hooks through reflection and supplies synthetic RAM/ROM contents instead of
booting MOS. Bank-management tests invoke the existing private bank operations;
ROM-loading cases create temporary files and delete them after use. Production
APIs and emulation behaviour are unchanged.

The suite targets the configured Model B map with the emulator's 16-bank sideways
expansion. Bank management and empty sockets returning `$FF` are emulator
regressions, not claims about every physical expansion or floating bus. Unmapped
I/O checks deliberately avoid requiring a particular open-bus byte.

CRTC/ULA readback cases preserve the emulator's diagnostic behaviour. They do not
claim physical write-only registers provide that readback. These mirror checks
adapt relevant cases from the repository's earlier `SheilaAddressDecodeTests.cs`
(commit `38305d5`). Address ranges follow the
[BBC Microcomputer Service Manual](https://acorn.huininga.nl/pub/unsorted/manuals/BBC%20Microcomputer%20Service%20Manual-HTML/BBCServiceManual.html).

This is a functional memory suite. Device register semantics, cycle stretching,
interrupt timing and CPU opcode vectors belong to the separate VIA, BBC timing
and CPU projects. It does not verify every optional peripheral configuration or
analogue bus behaviour.

## Baseline

14 September 2026: **66 tests passed, 0 failed**. Release build completed with
**0 warnings and 0 errors**.
