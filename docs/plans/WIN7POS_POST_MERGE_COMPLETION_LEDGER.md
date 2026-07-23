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
- 2026-07-23 repo-local residual closure: SQLite durability is `DONE_MERGED`
  through PR #25 (`3887262`), ARCH-005 through PR #26 (`56b3803`), PR-C through
  PR #27 (`d6751576`), ProductRepository/ARCH-003 through PRs #28-#31
  (`6556a85`, `f2175ab`, `bc52904`, `38cf284`), and SaleRepository/PR-F through
  PRs #32-#38 (`0918437`, `0c95204`, `e2d8e24`, `93100e2`, `8bcbaea`,
  `f20030c`, `cc9f02c`). All were normal merges; their individual linked PR
  records retain the exact-head and post-merge evidence. Post-merge CI on PR #33
  merge `0c952047` failed in run `29985031154`; PR #34 remediated that failure
  and completed its post-merge workflows successfully.
- PERF-05 is `DONE_MERGED` through PR #39 at normal merge commit
  `93a5e4afa819f0de14513ddb7603091433d917ba` from exact head
  `777931aba66727779480fd6774d8c5c2548d5a3a`. Exact-head CI, Security Supply
  Chain and Release Pack runs `30005059126`, `30005059146` and `30005117027`,
  and post-merge runs `30005966795`, `30005966771` and `30005966770`, all
  completed successfully on their respective exact SHAs.
- This closeout's derived production-certification disposition is
  `BLOCKED_EXTERNAL`. The authoritative 25-row backlog status remains `OPEN` and
  its evidence lives only in `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`;
  no external backlog row was changed by this repository-local closure.
- The Admin backend repository is available at
  `XNIW/merchandise-control-admin-web`, branch `main`, SHA `9406da338691e70e627c26867122499f944de897`.
  Its catalog-v2 change is not deployed to the configured staging environment,
  and no synthetic authenticated Win7POS session/fixture run exists; staging
  validation therefore remains `BLOCKED_EXTERNAL`.
- Supabase staging reconciliation is `BLOCKED_EXTERNAL` in this closeout: the
  source has 72 migration IDs, staging history has 79 and 71 are common. No
  authoritative source-provenance/recovery reconciliation manifest and verified
  backup for the eight remote-only Supabase staging migrations was supplied:
  `20260707183000`, `20260707200500`, `20260708003000`, `20260713010000`,
  `20260713020000`, `20260718120000`, `20260718235345`, `20260719090000`.
  Source-only `20260719170600` is recorded as observed evidence, not a repair
  instruction. No migration was repaired, rebased, baselined, reverted or
  deployed in this execution.
- Physical Windows 7 is `DEFERRED_BY_USER`. Real release signing/timestamp and
  production/hardware certification remain `BLOCKED_EXTERNAL`.
- A structural item becomes `DONE_MERGED` only after an explicit later merge.
  Publishing a green PR leaves it `READY_FOR_REVIEW`.

## A. External certification

This closeout's derived disposition: `BLOCKED_EXTERNAL`; authoritative backlog
status: `OPEN`; historical PASS count: `10/25` (rows 15 and 17–25).
The Windows 11 Epson evidence closes only those tested receipt/drawer scenarios;
physical Windows 7 remains open. Static checks, tests, synthetic databases, CI
and packaging cannot promote any external row. Do not duplicate the row matrix
here; use `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`.

## B-H. Structural hardening sequence

| Order | Structural item | PR | Status | Evidence / next action |
| --- | --- | --- | --- | --- |
| B | Persistence foundation | PR-A / GitHub `#5` | `DONE_MERGED` | Fast-forward head `607e1f1`; PR/main CI and Release Pack green on the exact SHA. |
| C | Versioned migrations | PR-B / GitHub `#6` | `DONE_MERGED` | Normal merge `ea85d91`; seven immutable checksummed migrations, verified legacy bootstrap/backup/rollback and seven sanitized fixtures. |
| D | Startup coordinator | PR-C / GitHub `#27` | `DONE_MERGED` | Normal merge `d6751576`; the startup orchestration responsibility was extracted behind its existing façade and post-merge acceptance workflows passed. |
| E | Catalog state-machine/performance split | PR-D / GitHub `#10`, `#21`–`#23`, `#39` | `DONE_MERGED` | PERF-1/PERF-2 plus PERF-05 post-commit remote-price diagnostics passed exact-head and post-merge specialist workflows. PERF-05 is observational, not a set-based rewrite or timing threshold. |
| F | ProductRepository split | PR-E / GitHub `#28`–`#31` | `DONE_MERGED` | Façade-preserving query, remote-price-history, local-writer and remote-product-writer extractions were normally merged and validated post-merge. |
| G | SaleRepository split | PR-F / GitHub `#32`–`#38` | `DONE_MERGED` | Façade-preserving read, line, stock, outbox, reversal and transaction writer extractions were normally merged. PR #33 post-merge CI `29985031154` failed; PR #34 remediated it, and final integration evidence is green in PR #39. |
| H | Reproducible/signable release chain | PR-G / GitHub `#13`, `#20` | `PARTIAL_EXTERNAL_SIGNING` | Actions/toolchain/locks/version, SBOM/security gates, reproducibility, checksums, provenance and unsigned attestation are merged. A real protected-tag certificate plus RFC3161 timestamp remains external. |

PR-A, PR-B, SYNC-1, SYNC-2, PERF-1, RELEASE1-A, the repository-local portion of
RELEASE1-B, PERF2-A/B/C, PR-C, PR-E, PR-F, ARCH-005, PERF-05 and
SQLITE-DURABILITY-001 are normally merged. No completed structural item is
mislabelled as superseded.
The historical nine-P2 reassessment is now nine `DONE_MERGED`, zero `OPEN`,
zero `PARTIAL` and zero `SUPERSEDED`.
The authoritative external-certification backlog remains `OPEN` at the
historical 10/25 snapshot. This closeout's derived disposition is
`BLOCKED_EXTERNAL`: authenticated staging reconciliation, production signing and
physical Windows 7 remain unverified.
