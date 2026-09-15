# beebjit physical floppy-controller results — not yet executable here

Chris Evans' [HFE test notes](https://github.com/scarybeasts/beebjit/blob/c977309448a5622c4cecf2a29f8678d0fb3aeee8/test/hfe/README)
at revision `c977309448a5622c4cecf2a29f8678d0fb3aeee8` are retained unchanged
as README.upstream, with the original GPL-3.0 COPYING notice and SHA-256 manifest.

These notes describe generating exact FM data/clock patterns for a
Gotek/FlashFloppy and report different 8271 and 1770 results for sync lengths,
missing data marks, sector gaps and packed headers. They are relevant physical
controller evidence, but are **not counted as running tests** in either project.

Our controllers consume decoded sector images, without an input for raw
clock/data marks. Converting these patterns to an SSD would discard the very
conditions being tested. Implementing them faithfully requires a flux/bitstream
media path and an adapter preserving the original command and result context;
the reported DFS error bytes must not be treated as raw WD1770 status bytes.
Exact BBC board/chip revisions are not specified by these notes.
