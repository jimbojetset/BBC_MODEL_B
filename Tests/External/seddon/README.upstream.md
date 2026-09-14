Tests that exercise a few specific annoying 6502/65c02 timing cases on
the BBC Micro/BBC Master.

Intended for checking emulator CPU timing accuracy, in conjunction
with (the far more comprehensive)
https://github.com/dp111/6502Timing - as these tests exercise a few
cases that `6502Timing` currently doesn't.

# how to use

1. Download latest .ssd from the releases page:
   https://github.com/beeb_6502_timing_tests/releases
   
2. Load into emulator and press SHIFT+BREAK

You can also run the tests on real hardware. (Tested on BBC B and
Master 128.) **Please disconnect anything from the 1 MHz bus if you do
this**: the test writes to addresses in pages $FC and $FD.

On success, the program will finish without any errors and with no
failure cases listed.

If there are failures, the program will print a list of the failing
cases and finish with a `STOP` error. The cases (and diagrams of
expected timings) can be found in
[./docs/Annoying6502Cases.xlsx](./docs/Annoying6502Cases.xlsx).

For more about the PFA/FFA terminology, see
[./docs/6502DeadCycles.pdf](./docs/6502DeadCycles.pdf).

For more about the 1 MHz access timing on the BBC B (which applies to
the Master too), see p9 of
[./docs/BBCServiceManual.pdf](./docs/BBCServiceManual.pdf).


# licence

GPL v3. See [./gpl-3.0.txt](./gpl-3.0.txt).
