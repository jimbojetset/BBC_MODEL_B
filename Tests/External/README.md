# Retained external BBC test evidence

`sources.json` records exact upstream paths, pinned revisions and SHA-256 hashes
of the unmodified source files. Both upstream projects supply GPL-3.0 licences,
retained beside their material. No live network fetch is required to run tests.

- `jsbeeb/via.js`: Scarybeasts' programs as adapted by jsbeeb, with real-BBC result
  statements. `via-cases.json` is our generated subset, not an upstream file.
- `seddon/TIMINGS`: Tom Seddon's tokenised BBC BASIC program. Its retained README
  reports physical BBC B and Master 128 testing. The C# adapter uses NMOS results.

From the repository root:

```sh
python3 Tests/External/extract_via.py
python3 Tests/External/verify_seddon.py
python3 Tests/External/verify_sources.py
```

The first command regenerates ten selected instruction programs and unchanged
expected arrays. The second checks all thirty timing adaptations against the
retained source. The third checks source hashes. Scripts fail on mismatches or
unsupported selected syntax. These are reproducibility/translation checks, not
new physical-machine validation.

See the VIA and BBC timing `PROVENANCE.md` files for the exact adaptations,
hardware qualifications, omitted upstream cases and test baselines. Do not copy
Master-only or upstream-unverified expectations into Model B conformance tests.
