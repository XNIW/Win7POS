# Win7POS post-merge completion ledger

## Source and status rules

- Previous software baseline: `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`;
  PR `#4` remains already merged and must not be merged again.
- PR-A software merge: `DONE_MERGED`; PR `#5` was integrated by fast-forward at
  `607e1f15fce64fed48e84e5fe680d40741ef6031`, without force push.
- PR-A main CI: run `29600645459`, `completed/success` on exact `607e1f1`.
- PR-A main Release Pack: run `29600645440`, `completed/success` on exact
  `607e1f1`; installer and release ZIP were downloaded and matched the embedded
  clean-main provenance.
- PR #7 receipt/recovery/hardware work: `DONE_MERGED` by normal merge commit
  `db623a5bf61c026662fe967b905b62940bec52e9`; exact-head and post-merge CI and
  Release Pack completed successfully.
- Production certification remains `OPEN`. The authoritative 25-row status and
  evidence live only in `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`.
- A structural item becomes `DONE_MERGED` only after an explicit later merge.
  Publishing a green PR leaves it `READY_FOR_REVIEW`.

## A. External certification

Overall status: `OPEN`; authoritative PASS count: `10/25` (rows 15 and 17–25).
The Windows 11 Epson evidence closes only those tested receipt/drawer scenarios;
physical Windows 7 remains open. Static checks, tests, synthetic databases, CI
and packaging cannot promote any external row. Do not duplicate the row matrix
here; use `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`.

## B-H. Structural hardening sequence

| Order | Structural item | PR | Status | Evidence / next action |
| --- | --- | --- | --- | --- |
| B | Persistence foundation | PR-A / GitHub `#5` | `DONE_MERGED` | Fast-forward head `607e1f1`; PR/main CI and Release Pack green on the exact SHA. |
| C | Versioned migrations | PR-B | `READY_FOR_REVIEW` | Seven immutable checksummed migrations, including the append-only PR7 receipt snapshot migration; verified legacy bootstrap/backup/rollback and seven sanitized fixtures. |
| D | Startup coordinator | PR-C | `WAITING` | Wait for PR-B review and explicit merge decision. |
| E | Catalog state-machine/performance split | PR-D | `NOT_STARTED` | Wait for preceding item. |
| F | ProductRepository split | PR-E | `NOT_STARTED` | Wait for preceding item. |
| G | SaleRepository split | PR-F | `NOT_STARTED` | Wait for preceding item. |
| H | Reproducible/signable release chain | PR-G | `PARTIAL` | PR #7 closed fail-closed installer generation, exact clean provenance/manifests, privacy rejection and runtime closure; locks, SBOM, signing/timestamp, attestation and reproducibility comparison remain. |

PR-A remains `DONE_MERGED`. PR-B is implemented on its independent branch and is
left `READY_FOR_REVIEW`; this status does not imply merge. PR-C remains
`WAITING`. External certification remains `OPEN 10/25`, with physical Windows 7
still not run.
