# Reproduce the adapter audit

These optional runners validate the adapters against original upstream engines.
They are not dependencies of VIDEO_TESTS and do not provide new hardware results.
Use the exact revisions listed in ../PROVENANCE.md:

- beebjit: `c977309448a5622c4cecf2a29f8678d0fb3aeee8`
- jsbeeb: `d74d9043e83b981a4a3fe1d621f309e8bf083a9c`

Obtain separate checkouts or extract the GitHub archives for those commits.
The retained source files under Tests/External can be checked against the
checkouts, and `python3 Tests/External/verify_sources.py` verifies their hashes.
Preserve the upstream licences; no modified upstream engine is required.

## beebjit

Tested on macOS arm64 with clang. Set `bbc_repo` to this repository and run from
the root of the pinned beebjit checkout:

```sh
bbc_repo=/Users/jamesbooth/Repo/BBC_MODEL_B
clang -O2 -DBEEBJIT_HEADLESS -Wl,-dead_strip -I . \
  -o /tmp/validate-beebjit-video \
  "$bbc_repo/Tests/VIDEO_TESTS/Validation/beebjit.c" \
  timing.c render.c teletext.c util.c util_string.c util_container.c \
  log.c test.c os.c -lm -lpthread
/tmp/validate-beebjit-video
```

The original video.c includes test-video.c. The runner calls the three original
functions containing our selected CRTC scenarios, with a fresh upstream fixture
for each. The fixture has no attached VIA. The three VIA link stubs abort if
called, so they cannot silently fake a successful peripheral interaction.

Observed: corner cases PASS; no dummy raster PASS; VSYNC mux PASS. The upstream
sixteen-line-width diagnostic is expected.

## jsbeeb

Tested with Node 24.15.0. Copy the small runner to the root of the pinned jsbeeb
checkout so its relative import finds the original src/video.js:

```sh
cp "$bbc_repo/Tests/VIDEO_TESTS/Validation/jsbeeb-t8.mjs" ./validate-t8.mjs
node validate-t8.mjs
```

No npm packages, ROMs or browser are needed. The fixture supplies video RAM and
an unused VIA interrupt callback. Observed for each phase: three boxes of width
88 and gaps of width 88. The runner asserts these values, including the number
of boxes. This exercises the same reduced T8 waveform as our C# adapter; it
does not run the complete BASIC/6502 program.
