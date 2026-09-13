# Rockwell 65C02 instruction tests

Copied from the 6502 runner, using the current BBC `CPU_65C02` core and
[SingleStepTests Rockwell 65C02 vectors](https://github.com/SingleStepTests/65x02/tree/main/rockwell65c02/v1).
The test plan includes all 256 opcodes, including Rockwell bit operations and CMOS NOPs.

```sh
dotnet run --project Tests/65C02_CPU_TESTS/65C02_CPU_TESTS.csproj -c Release
```

Without a local directory, the runner downloads the Rockwell JSON files. For an
offline run, use the matching dataset (not the NMOS 6502 or WDC vectors):

```sh
dotnet run --project Tests/65C02_CPU_TESTS/65C02_CPU_TESTS.csproj -c Release -- --test-dir /path/to/65x02/rockwell65c02/v1
```

Append `--opcode 32` to test an individual hexadecimal opcode. Each vector uses a
fresh CPU and flat 64 KiB RAM, executing through `StepInstruction()`. No BBC ROMs
or SDL window are needed. Final PC, A, X, Y, stack pointer, flags and RAM are
compared; cycle counts, bus-access sequences and Tube communication are not checked.

Failures are reported without exclusions and return exit code 1. A successful
runner build does not establish that the CPU conforms to all Rockwell behaviour.
Use a known dataset revision for repeatable runs. Local files must exist when
`--test-dir` is supplied; there is no network fallback. Datasets are not bundled.

## Verified result

The corrected core passed all **2,560,000** Rockwell v1 register/memory cases
across **256 opcodes**, with **zero failures and no excluded opcodes**, on
13 September 2026. Separate checks confirmed IRQ/NMI decimal-flag stacking and
RTI restoration, and a headless Tube boot completed with the existing ROMs.
This does not establish cycle-by-cycle bus accuracy or exhaustive Tube compatibility.

The corrections replace retained NMOS illegal instructions with Rockwell reserved
NOPs, implement `$9F` as BBS1, correct decimal ADC/SBC results and flags, and clear
D after stacking status on BRK/IRQ/NMI. Decimal arithmetic includes the CMOS extra
cycle. On a failure, the runner prints the first mismatching vector for that opcode
with expected/actual registers and whether RAM matched.
