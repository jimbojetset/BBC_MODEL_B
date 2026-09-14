# Memory evidence audit

**There is currently no independently verified BBC hardware test dataset or
external BBC test program integrated into MEMORY_TESTS. All 66 cases are
labelled `Unverified/` in output.** Passing them does not establish hardware
conformance. This is a documented coverage gap, not a substitute for provenance.

| Cases / source | Origin and evidence status |
|---|---|
| `RAM/*`, `OS/*`, `Memory/*` in MemoryMapTests.cs | Original synthetic patterns and instruction sequences. Expectations were inferred from the documented map; no physical BBC observations attached. |
| `IO/*` in MemoryMapTests.cs | Original assertions about I/O masking. No independently recorded bus data; deliberately avoids a fixed floating-bus value. |
| `FlatBus/*` | Emulator memory-bus API regression. Not a physical BBC address-width measurement. |
| `Banks/*` in BankTests.cs | Original mapping/banking assertions and emulator bank-management regressions. No independently verified sideways-expansion test data. |
| `Decode/*/AddressBlock` | Original predicates against address ranges from documentation, not an external hardware test. |
| Other `Decode/*` | Adapted local regression checks from this repository's `SheilaAddressDecodeTests.cs`, commit `38305d5`. This is code history, **not independent hardware provenance**. |

All patterns above apply after the `Unverified/` prefix. In particular, CRTC/ULA
readback, empty-socket `$FF`, bank moving and clearing preserve software behaviour;
they must not be cited as proof of physical register or socket behaviour.

BBC manuals explain the intended map, but are not presented here as measured
expected test data. We have not identified and validated a suitable external
Model B memory/sideways-expansion suite during this audit. The sourced timing
cases exercise some memory bus accesses, but do not validate our entire memory
map or the particular 16-bank expansion.

Retain these cases as supplementary regression coverage. Any future promotion
to hardware evidence requires an identified external BBC program/result, pinned
source, relevant hardware configuration and a reviewed adaptation under
[the test evidence policy](../README.md).

## Audited baseline — 14 September 2026

**0 sourced BBC cases. 66 Unverified cases pass.** This is a software regression
result only, with the missing independent hardware coverage explicitly retained.
