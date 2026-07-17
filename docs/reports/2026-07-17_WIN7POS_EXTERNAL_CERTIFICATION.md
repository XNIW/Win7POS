# Win7POS external certification — 2026-07-17

## Decision

- External certification: `OPEN`.
- Items with real PASS evidence: `0/16`.
- Production certification: `OPEN`, not certified.
- No production account, database, credential or hardware was used.
- No visual PASS is declared; Computer Use evidence is therefore not promoted
  for any UI, monitor, DPI or language item.

The available environment does not contain an authorized non-production QA
credential, Windows 7 SP1 target, dual-monitor topology, scanner, Xprinter or cash
drawer. No credential was requested through the prompt or entered through a
terminal. The staging and physical procedures were not simulated and no synthetic
result is labeled PASS.

## Final main provenance

| Evidence | Result |
| --- | --- |
| GitHub main | `f3e779bd537d62ed0f3ddb5333149e9213e2c13f` |
| PR #4 | already `MERGED`; no old feature merge repeated |
| CI | run `29591597390`, `completed/success`, exact main SHA |
| Release Pack | run `29591597131`, `completed/success`, branch `main`, exact main SHA |
| Release version record | `CommitSHA=f3e779b...`, `Ref=main`, `Platform=x86`, `SdkVersion=10.0.301`, `TreeState=clean`, run `261` |
| Release-pack artifact | `sha256:d5c64f82df63a67d3da9c99340e3a24fe34847d560b0973e88300bdc3acef7d6` |
| Dist artifact | `sha256:5ffb73dba0980c609e505d34abc4949d87959b9934a396796bbc1348b057f378` |
| Installer artifact | `sha256:c5a502c73049f737a3089647d20c9ffd1517ea16bf18a2378183ce2297212cf3` |

The three GitHub artifacts were downloaded and inspected. The release ZIP and
`Win7POS-Setup.exe` are present, but this software provenance does not replace an
installer smoke test on Windows 7.

## External item result

| Range | Result | Reason |
| --- | --- | --- |
| Items 1-8 | `BLOCKED_CREDENTIALS` | Authenticated QA staging catalog, sales, backlog, reconciliation and business-date work cannot be executed. |
| Items 9-10 | `BLOCKED_WIN7` | No qualifying Windows 7 SP1 target is available. |
| Items 11-15 | `BLOCKED_HARDWARE` | Dual monitor, customer display, scanner, Xprinter and drawer are unavailable. |
| Item 16 | `NOT_RUN` | The full DPI/language matrix needs unavailable runtime surfaces and topology. |

The 60-sale backlog was not generated: raw SQL is prohibited and application-layer
QA services are not available without authentication. Daily totals, midnight and
businessDate were not compared to a server. The file
`docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md` is intentionally unchanged because
the task permits updating only items that actually PASS.

## Inputs required to continue

- authorized non-production QA shop/staff credential, entered only in the app;
- disposable Windows 7 SP1 POS/VM with .NET Framework 4.8;
- two physical displays in Extend mode;
- scanner, Xprinter and compatible cash drawer;
- authorization for prefixed QA catalog/sales mutations through application
  services.
