# 6502 instruction tests

Copied from James Booth's `C64/CPU_TESTS(6502)` project and adapted to reference
the current BBC CPU core. It uses the NMOS 6502 vectors from
[SingleStepTests/65x02](https://github.com/SingleStepTests/65x02/tree/main/6502/v1).
No BBC ROMs or SDL window are needed.

Run all 256 opcodes (downloads their JSON files when no local directory is supplied):

```sh
dotnet run --project Tests/6502_CPU_TESTS/6502_CPU_TESTS.csproj -c Release
```

Run with an existing local `6502/v1` dataset:

```sh
dotnet run --project Tests/6502_CPU_TESTS/6502_CPU_TESTS.csproj -c Release -- --test-dir /path/to/65x02/6502/v1
```

A local directory must contain every requested file; missing files fail without
falling back to the network. Keep a known dataset revision for repeatable runs.
Downloaded vectors are not bundled with this project. `Tests/TestData/` is ignored
if you choose to keep a local dataset there.

For an individual opcode, append `--opcode ea` (hexadecimal). The runner reports
pass/fail totals and failing opcodes, and exits with code 1 when comparisons fail.
It retains the original checks of final PC, A, X, Y, stack pointer, flags and RAM.
It does **not** check cycle counts, bus-access sequences, BBC hardware timing or
the Tube's 65C02. Each vector runs on a fresh CPU through `StepInstruction()`, including the
instruction-completion updates for SEI, CLI and PLP. The original runner
bypassed those updates and omitted JAM opcode `$32`; both are corrected here.

Undocumented opcodes are included. No hardware-dependent exceptions are silently
excluded; differences in unstable opcodes must be reviewed against the chosen
silicon model. Historical results are not a claim that the current core passes.
