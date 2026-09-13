# Performance documentation history

The live performance documents were compacted on 2026-09-08 so routine work no
longer requires reading more than twelve thousand historical lines. These files
are preserved byte-for-byte under
`.codex/session-backups/20260908-before-performance-live-doc-compaction/`:

| Original path | Lines | Bytes | SHA256 |
|---|---:|---:|---|
| `PERFORMANCE_STATUS.md` | 37 | 5,550 | `E9D79B90C0A688DDDEEFC1B6750D72317669D9517CD687A5D1AC09C2844FFCC5` |
| `docs/performance/AUDIT.md` | 363 | 33,654 | `6F5CD1A83393C7945CC5A87CF7CEFE710C24D1B55F83FAACB6E24EC7EAA1E8DB` |
| `docs/performance/IMPLEMENTATION_RESULTS.md` | 8,374 | 548,082 | `ADAC0C7BD867866D5F2B18117CF5541EBC7FB3D9CD6E13FC545C861886C1C5E6` |
| `scripts/performance/README.md` | 4,543 | 263,089 | `2A671B98FB7BEE51299FB7365523888325A21774FBE9424F6272FBD871AE4209` |

`PERFORMANCE_TODO.md` remains the complete live 37-ID table. The measurement
ledger `docs/performance/OPTIMIZATION_MEASUREMENTS.csv` was not compacted or
rewritten. Immutable source snapshots and validation artifacts under
`artifacts/performance/` remain unchanged.

For historical research, search one archive by ID, method or experiment and read
only the matching section. Old `next action` paragraphs are historical and must
not override `PERFORMANCE_STATUS.md`.

The 2026-09-10 native-animation continuation adds immutable N2 (successful
preflight), PILOT1 (development-only marker-schema diagnosis), N3 (five-process
native capture) and V1 (precision reverification of N3) artifacts. Their method,
results and limitations are recorded in
[ANIMATION_NATIVE_BASELINE.md](ANIMATION_NATIVE_BASELINE.md). N1's historical
permission failure and the original N3 report remain unchanged; the
optimization ledger has not been appended by this measurement-only stage.
