# John Kortink's BBC SN76489 white-noise capture

Author: **John Kortink**. Original research:
[SN76489 sound chip details](https://www.zeridajh.org/articles/me_sn76489_sound_chip_details/index.html),
first published 15 March 2002.

Unmodified data:
[white_noise.bin](https://www.zeridajh.org/articles/me_sn76489_sound_chip_details/downloads/white_noise.bin).
Retrieved 14 September 2026. There is no published revision identifier; this
snapshot is pinned by SHA-256:

```text
ae43bc97f5c6d9524c56d7f1e8925242d6ac8a0bd40b33052c8eadd68bcbd6df
```

The file contains 32,767 bytes, each 0 or 1. It is a decoded noise bitstream,
not raw PCM. Kortink describes recording his BBC's sound output with a PC sound
card, using tone channel 3 as a timing reference, then extracting the noise bits.
His article reports the repeat length and experimentally investigated reset
phase. It does not identify the exact BBC model, board issue or chip marking.

## Attribution and permission

The article permits using the information to improve SN76489 emulation with
suitable credit. Credit is retained here and in SOUND_TESTS. This is the author's
stated permission, **not a GPL licence or a claim of public-domain status**. No
broader relicensing of his material is asserted.

The article itself is linked rather than reproduced. See
`../../SOUND_TESTS/PROVENANCE.md` for how this capture is used and the limits of
the comparison. The retained file is checked by `../verify_sources.py` and by
the sound runner before each case.
