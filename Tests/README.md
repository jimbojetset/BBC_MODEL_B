# BBC test projects and evidence policy

Keep one console project per major test group. **Hardware correctness claims must
be rooted in externally verifiable BBC test programs, recorded hardware results,
or behaviour demonstrated on the relevant physical machine.** An assertion
written from our emulator or a datasheet alone is not independent BBC evidence.

For every new hardware test:

1. Identify the original author, source URL, pinned revision and original case ID.
2. Retain the source, licence and expected results, with file hashes where practical.
3. Record the evidence for hardware validation and the machine/CPU variant. A
   Master result is not automatically an NMOS Model B result.
4. Preserve expected values. Explain relocation, setup and harness changes; do
   not change expectations to make our emulator pass.
5. Where this evidence is missing, label the case **Unverified** in output and
   documentation. Software-only checks may be retained as regression tests, but
   must never be presented as physical hardware verification.
6. Report sourced and unverified results separately. Treat failures as
   discrepancies requiring investigation of both the adapter and emulator,
   not automatic proof that the emulator needs changing.

`SourcedBBC/` means an adaptation of an identified external BBC test with hardware
validation reported upstream. It does not mean our adaptation has itself been
rerun on a physical BBC. `Unverified/` means no independent BBC result has been
established for that case, even if its expectation is plausible or documented.
Use `--filter SourcedBBC/` to run only the sourced cases in VIA, BBC timing, sound or video.

## Current evidence

| Project | Independent evidence currently integrated |
|---|---|
| 6502_CPU_TESTS | External SingleStepTests vectors; see its README for source and CPU target. CPU state vectors do not verify BBC peripherals. |
| 65C02_CPU_TESTS | External Rockwell SingleStepTests vectors; not a BBC Master 65C12 test suite. |
| VIA_TESTS | Ten Scarybeasts/jsbeeb programs carrying upstream real-BBC result arrays. Original local cases remain Unverified. |
| BBC_TIMING_TESTS | Thirty NMOS cases adapted from Tom Seddon's program, reported tested on BBC B and Master. Original local cases remain Unverified. |
| SOUND_TESTS | John Kortink's BBC white-noise capture: three sequence/restart scenarios. No complete audio-path verification claimed. |
| VIDEO_TESTS | One hardware-confirmed beebjit CRTC case; two ULA scenarios based on one Master measurement. Three further upstream CRTC cases and three local save/restore checks remain Unverified. |
| MEMORY_TESTS | **No independently verified BBC test data integrated.** All current cases are Unverified supplementary checks. |

See each project's `PROVENANCE.md` for scope, adaptations and exclusions. Pinned
upstream material and reproducibility scripts are in `External/`.

This policy applies to all future groups, including video, disk, sound, Tube and
other peripherals. Missing evidence must be reported rather than replaced with
new assertions inferred solely from our implementation or documentation.
