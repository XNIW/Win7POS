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
- RELEASE1-A: `DONE_MERGED` through PR #13 at normal merge commit
  `1832dcca8cc95054590b776c1741c61cc3821a7a`. RELEASE1-B repository-local
  supply-chain work is `DONE_MERGED` through PR #20 at normal merge commit
  `5313ff36365fc021fa323338eac6523049debebe`. Real production certificate,
  protected-tag signing and RFC3161 timestamp verification remain
  `BLOCKED_EXTERNAL`, so composite `P1-REL-01` is
  `PARTIAL_EXTERNAL_SIGNING`, not fully done.
- PERF-2: `DONE_MERGED` through three independent normal merges: PR #21
  (`81acd479c187469fe0dc31f9b0fb3a162312c1cc`), PR #22
  (`63152222a5dfcb2d3cce07cf550d928ccbadfd26`) and PR #23
  (`0c5052f3d32ead9a02b35367e554f918d4e44fd2`). The final exact-main CI,
  Security Supply Chain, Release Pack and Catalog Performance runs are
  `29908121321`, `29908121286`, `29908121289` and `29908134313`, all
  `completed/success` on `0c5052f3`.
- Production certification remains `OPEN`. The authoritative 25-row status and
  evidence live only in `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`.
- The Admin backend repository is available at
  `XNIW/merchandise-control-admin-web`, branch `main`, SHA `9406da338691e70e627c26867122499f944de897`.
  Its catalog-v2 change is not deployed to the configured staging environment,
  and no synthetic authenticated Win7POS session/fixture run exists; staging
  validation therefore remains `BLOCKED_EXTERNAL`.
- Physical Windows 7, real release signing/timestamp and production/hardware
  certification remain `BLOCKED_EXTERNAL`. No external backlog row was changed.
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
| D | Startup coordinator | PR-C | `PARTIAL` | PRs #8, #9 and #11 extracted scheduler/supervisor/trust policy, but `MainWindow.OnLoadedAsync` still directly owns DB, session, recovery and online-host sequencing. Use small C1/C2/C3 extraction PRs only if the regression surface is accepted. |
| E | Catalog state-machine/performance split | PR-D / GitHub `#10`, `#21`–`#23` | `DONE_MERGED` | PERF-1 plus durable authoritative staging, keyset paging and bounded logging passed exact-head and post-merge specialist workflows. |
| F | ProductRepository split | PR-E | `OPEN` | PERF-1/PERF-2 removed measured hot paths, but the public repository still owns query, local write, remote identity and price-history concerns. No decomposition is claimed. |
| G | SaleRepository split | PR-F | `OPEN` | Safety/CAS/reversal prerequisites are merged, but sale, reporting, reversal, stock and outbox responsibilities remain together. No decomposition is claimed. |
| H | Reproducible/signable release chain | PR-G / GitHub `#13`, `#20` | `PARTIAL_EXTERNAL_SIGNING` | Actions/toolchain/locks/version, SBOM/security gates, reproducibility, checksums, provenance and unsigned attestation are merged. A real protected-tag certificate plus RFC3161 timestamp remains external. |

PR-A, PR-B, SYNC-1, SYNC-2, PERF-1, RELEASE1-A, the repository-local portion of
RELEASE1-B and PERF2-A/B/C are normally merged. PR-C remains `PARTIAL`; PR-E and
PR-F remain `OPEN` maintainability work and are not mislabeled as superseded.
The historical nine-P2 reassessment leaves ARCH-003, ARCH-005 and PERF-05
`OPEN`, SQLITE-DURABILITY-001 `PARTIAL`, five items `DONE_MERGED` and none
`SUPERSEDED`.
External certification remains `OPEN 10/25`, with authenticated staging,
production signing and physical Windows 7 still unverified.
