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
- PR #6 versioned migrations: `DONE_MERGED` by normal merge commit
  `ea85d91b018ae90f81bead531bcca42253dd64ff`; exact-head CI and release evidence
  are recorded in the worklog and PR body.
- SYNC-1 correctness: `DONE_MERGED` through PR #8 at
  `6d9a9e0a863e3cb8310960ca08d395897b23c36c`.
- SYNC-2 supervisor: `DONE_MERGED` through PR #9 at normal merge commit
  `1be70172cab56895f66389a99b4a6fa92352c7e2`; exact-head and post-merge CI and
  Release Pack completed successfully.
- PERF-1 transport/apply optimization: `DONE_MERGED` through PR #10 at normal
  merge commit `0ad16bd2c40454c07d0ffb6e4908e23b89b4a5a1`, without force push.
  Exact-head CI run `29874926694` and Catalog Performance run `29874958148`
  completed successfully on `92e1c323d55ea7b00f3f8bfa35f32fe2fb28e391`;
  post-merge CI run `29875846523` and Release Pack run `29875846496` completed
  successfully on the final main SHA.
- Production certification remains `OPEN`. The authoritative 25-row status and
  evidence live only in `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`.
- Admin backend/authenticated staging validation, physical Windows 7, PERF-2,
  `P1-REL-01` and production/hardware certification remain `OPEN`.
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
| C | Versioned migrations | PR-B / GitHub `#6` | `DONE_MERGED` | Normal merge `ea85d91`; seven immutable checksummed migrations, verified legacy bootstrap/backup/rollback and seven sanitized fixtures. |
| D | Startup coordinator | PR-C | `NOT_STARTED` | PR-B prerequisite is merged; reassess this older structural item after the independent SYNC-2 delivery. |
| E | Catalog state-machine/performance split | PR-D / GitHub `#10` | `DONE_MERGED` | PERF-1 normal merge `0ad16bd`; exact-head CI/performance and post-merge CI/Release Pack are green. PERF-2 remains a separate open follow-up. |
| F | ProductRepository split | PR-E | `NOT_STARTED` | Wait for preceding item. |
| G | SaleRepository split | PR-F | `NOT_STARTED` | Wait for preceding item. |
| H | Reproducible/signable release chain | PR-G | `PARTIAL` | PR #7 closed fail-closed installer generation, exact clean provenance/manifests, privacy rejection and runtime closure; locks, SBOM, signing/timestamp, attestation and reproducibility comparison remain. |

PR-A, PR-B and PERF-1 remain `DONE_MERGED`. The older PR-C/PR-D structural labels
do not replace the explicitly separated SYNC-1, SYNC-2, PERF-1 and PERF-2
sequence. External certification remains `OPEN 10/25`, with physical Windows 7
still not run.
