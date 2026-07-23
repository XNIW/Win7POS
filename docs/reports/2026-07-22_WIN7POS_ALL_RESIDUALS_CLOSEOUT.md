# Win7POS all-residuals closeout — 2026-07-22

> Historical snapshot. The subsequent repository-local closeout and its unchanged
> external blockers are recorded in
> [`2026-07-23_WIN7POS_FINAL_REPO_LOCAL_CLOSEOUT.md`](2026-07-23_WIN7POS_FINAL_REPO_LOCAL_CLOSEOUT.md).

## Executive status

- Initial main: `0ad16bd2c40454c07d0ffb6e4908e23b89b4a5a1`.
- Final repo-local implementation main before this documentation-only closeout:
  `0c5052f3d32ead9a02b35367e554f918d4e44fd2`.
- Merge policy: every PR below used a normal merge commit. Squash, rebase,
  branch-protection bypass, direct main commits and force push were not used.
- Repo-local delivery: CLOSEOUT-DOCS, RELEASE1-A, repository-local RELEASE1-B,
  PERF2-A, PERF2-B and PERF2-C are `DONE_MERGED`.
- Composite release status: `P1-REL-01=PARTIAL_EXTERNAL_SIGNING` because a real
  production certificate/protected tag/RFC3161 timestamp was not available.
- Performance status: `P1-PERF-02=DONE_MERGED`.
- External status: authenticated staging is configured but unvalidated on the
  current backend SHA; physical Windows 7 and production/hardware certification
  are not available. The authoritative backlog remains `10/25 PASS`.
- Structural status: Startup coordinator `PARTIAL`; ProductRepository and
  SaleRepository splits `OPEN`. Four historical P2 slices remain unresolved.
- Final classification: `NOT_DONE`. It would be false to use `ALL_DONE` or
  `REPO_LOCAL_DONE_EXTERNAL_BLOCKERS` while repo-local structural/P2 items are
  still explicitly open or partial.

The exact head and merge SHA of this report's own documentation PR are recorded
in GitHub and in the orchestrator's final output; embedding them here would be
self-referential and would change the value being recorded.

## PR and workflow ledger

| Stage | PR | Exact head | Normal merge | Exact-head runs | Post-merge runs |
| --- | --- | --- | --- | --- | --- |
| CLOSEOUT-DOCS | [#12](https://github.com/XNIW/Win7POS/pull/12) | `cfdb72b1864331ada9f8129f1d09bdd13d59d8bb` | `939ca84373b5f4ead7b13471a1d80728731ff498` | CI `29879271908` | CI `29882440608`; Release Pack `29882440561` |
| RELEASE1-A | [#13](https://github.com/XNIW/Win7POS/pull/13) | `ac5f05bad8629b1ad0365d823bb79e6a53b89863` | `1832dcca8cc95054590b776c1741c61cc3821a7a` | CI `29884298268`; Release Pack `29884309978` | CI `29884648847`; Release Pack `29884648852` |
| RELEASE1-B | [#20](https://github.com/XNIW/Win7POS/pull/20) | `442983acf3e0f358123481fc46f550bd21d5cf8e` | `5313ff36365fc021fa323338eac6523049debebe` | CI `29891734802`; Security `29891734800`; Release Pack `29891745928` | CI `29892217666`; Security `29892217582`; Release Pack `29892217683` |
| PERF2-A | [#21](https://github.com/XNIW/Win7POS/pull/21) | `338e7e21b72f1cc0a5779ad80f18657c3d266574` | `81acd479c187469fe0dc31f9b0fb3a162312c1cc` | CI `29897459876`; Security `29897459871`; Release Pack `29897473349`; Catalog `29897474665` | CI `29898416385`; Security `29898416372`; Release Pack `29898416440`; Catalog `29898431793` |
| PERF2-B | [#22](https://github.com/XNIW/Win7POS/pull/22) | `e7c191225ae74e4ea1c08033c7deada5d92d586d` | `63152222a5dfcb2d3cce07cf550d928ccbadfd26` | CI `29901964195`; Security `29901964220`; Release Pack `29901973934`; Catalog `29901975897` | CI `29903154730`; Security `29903154751`; Release Pack `29903154981`; Catalog `29903177785` |
| PERF2-C | [#23](https://github.com/XNIW/Win7POS/pull/23) | `5e253c5cffacaac840de49e5672640fbd3d288aa` | `0c5052f3d32ead9a02b35367e554f918d4e44fd2` | CI `29906780759`; Security `29906780732`; Release Pack `29906806859`; Catalog `29906804341` | CI `29908121321`; Security `29908121286`; Release Pack `29908121289`; Catalog `29908134313` |

Every listed run was queried against its exact head/merge SHA and completed
successfully. No run from a different SHA was reused as acceptance evidence.

## Release foundation and supply chain

| Control | Final evidence |
| --- | --- |
| GitHub Actions | Six workflows, 32 `uses:` references, 32/32 pinned to full 40-character SHAs. |
| Checkout credentials | Eight checkout uses, 8/8 set `persist-credentials: false`. |
| Workflow permissions | Explicit least-privilege permissions; canonical release-foundation negative vectors pass. |
| SDK | `global.json` pins exactly `10.0.301` with roll-forward disabled. |
| NuGet | Seven committed `packages.lock.json` files; solution/release restore uses `--locked-mode`; later build/test steps use `--no-restore`. |
| Version | `Directory.Build.props` is authoritative at `1.0.0`; assembly/file/informational version, `VERSION.txt`, Inno metadata and artifact names are resolved from it. Protected tags must match `vMAJOR.MINOR.PATCH`; PR builds use deterministic development metadata. |
| Inno Setup | Official `6.7.3` asset; expected SHA-256 `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`; version/hash/signature checks fail closed. |
| SBOM | CycloneDX 1.6 from pinned CycloneDX `6.2.0`; 99 direct/transitive components. |
| Dependency policy | Zero known vulnerable packages, zero deprecated packages and 99/99 license inventory entries accepted by the versioned policy. |
| Secret scan | Pinned Gitleaks `8.30.1`; current worktree and complete history reports contain zero findings. A local pre-publication scan covered 407 commits. |
| Static security | CodeQL C# and the repository security gates completed successfully. |
| Reproducibility | Two clean net48/x86 builds of `0c5052f3` produced identical 37-file normalized manifests, SHA-256 `c5344dbcbf12aee44193b6a160a3132387c5ce32d69a507e9d350dbff4a55d4c`, with zero differences. Only `VERSION.txt:BuildTimestampUtc` is normalized; outer ZIP/installer and derived checksum lists are explicitly excluded from payload comparison. |
| Provenance/attestation | Exact-commit checksums, provenance and in-toto unsigned attestation are present and validation/tamper negative tests fail closed. |
| PR signing | Unsigned by design and requires no signing secret. |
| Production signing | `BLOCKED_EXTERNAL`: protected workflow and ephemeral self-signed fixture validate wiring only. No real certificate, protected release tag or RFC3161 timestamp PASS is claimed. |

### Exact-main release artifacts

The following were downloaded from post-merge Release Pack run `29908121289`
for exact implementation main `0c5052f3`:

| Artifact | SHA-256 |
| --- | --- |
| `Win7POS-1.0.0-dev.0c5052f3d32e-Setup.exe` | `e370352d5cbf4a2134140e550cb4c836db52b3b244b16757ae4532b6df49605f` |
| `Win7POS-1.0.0-dev.0c5052f3d32e-x86.zip` | `0cf146f923e6c3e200a3da6fc5be39f8905f9d5c248c815c678f7261bc36a4b2` |
| CycloneDX SBOM | `08d4cb004017bcc08a8b274f39492737a4815d1cbf355b08013b2962cb4c8888` |
| unsigned payload manifest | `c5344dbcbf12aee44193b6a160a3132387c5ce32d69a507e9d350dbff4a55d4c` |
| release checksums | `75bee2c3303728f6a7537af9b26771818efe7c817f67ce602cd836cf1079c11a` |
| release provenance | `c93a66481255e748e4e2759f8a92c4c50131bfd645282a39bb39a2e4d3150da0` |
| unsigned in-toto attestation | `c9811ef4bd4538d20552162f83c1190c7e5c4fcdb523c4bbdfc838c0a340c429` |

These are unsigned development artifacts, not a production-signed release.

## Migration and PERF2-A

- Migration before: `0008-online-sync-generation`.
- Migration after: `0009-catalog-authoritative-id-stage`.
- Canonical 0009 checksum:
  `68d57cd65b2d56456d5b2ab5eee83237477aefc85f93aa2d81e5f64699fae659`.
- Migrations 0001–0008 were not changed.
- The durable table is scoped by shop identity, trusted generation/full-run and
  remote product identity. Page staging shares the guarded page transaction;
  stale/cancelled/ambiguous runs cannot publish exactness or reconciliation.
- Fresh DB, 0008→0009 upgrade, historical fixtures, ledgerless baseline,
  malformed/tampered schema, checksum mismatch, rollback, cancellation,
  restart/resume, stale generation, duplicate/tombstone/identity conflicts,
  incomplete pagination and backup/restore coverage passed. PERF2-A exact-head
  full Core/Data count was 496; final cumulative count is 534.

## PERF2-B keyset paging

- Ordinary next/previous navigation uses a filter/revision-bound
  `{barcode,id}` cursor and bounded anchor cache.
- OFFSET remains only as the explicit, tested fallback for an arbitrary page
  jump without an anchor.
- Exact barcode precedence, duplicate barcodes ordered by ID, insert/delete
  mutation, filter mismatch, backward navigation, query-plan/index use and UI
  selection tests pass.
- Same-host 100,000-row repository p95: keyset `40.587 ms`, OFFSET `86.186 ms`,
  ratio `2.12x`.
- Raw SQLite p95: keyset `0.302 ms`, OFFSET `17.638 ms`, ratio `58.42x`.
- The 15% same-host performance guard passed.

## PERF2-C bounded asynchronous logging

- One process-wide bounded Core queue and one background writer own batch append
  and rotation; the WPF logger and DB initializer use the same process writer.
- Redaction and length bounds run before enqueue. INFO is dropped first under
  saturation; reserved WARN/ERROR capacity, drop/high-water metrics, writer
  failure isolation and bounded shutdown are covered by tests.
- x86 saturation smoke: producer p95 `708.60 us`, true maximum `6.243 ms`, queue
  high-water `256/256`, accepted `256`, deliberately dropped INFO `102,817`,
  private-byte high-water delta `6,287,360`, peak private bytes `28,389,376`,
  peak working set `37,584,896`, and worker stopped after bounded shutdown.
- Final Core/Data suite: 534 passed, 0 failed, 0 skipped.

## Final catalog workflow evidence

Post-merge Catalog Performance run `29908134313` completed successfully on
`0c5052f3`:

| Scenario | Result |
| --- | --- |
| 19,763 net10/x64 | 3/3 exact `Verified`; 20 requests; authoritative stage rows after run `0`; peak working set `119,816,192`; peak private bytes `64,471,040`. |
| 19,763 net48/x86 | 3/3 exact `Verified`; 20 requests; authoritative stage rows after run `0`; median elapsed `28,298.418 ms`; peak working set `56,999,936`; peak private bytes `41,689,088`; max dispatcher delay `89.555 ms`. |
| 100,000 net10/x64 | Exact `Verified`; 100 requests; `174,243.585 ms`; peak working set `94,789,632`; peak private bytes `56,356,864`; stage rows after run `0`. |
| 100,000 net48/x86 | Exact `Verified`; 100 requests; `170,232.793 ms`; peak working set `58,626,048`; peak private bytes `44,298,240`; max dispatcher delay `28.229 ms`; stage rows after run `0`. |
| Incremental 10/100/1,000 | `93.284 / 169.402 / 520.648 ms`; one request each; no full reconciliation; stage rows after run `0`. |

The final specialist workflow accepted all elapsed/memory regression guards.
Host-to-host timing differences are not treated as a regression comparison, and
these runner measurements are not physical Windows 7 evidence.

## Local and CI validation summary

- Canonical required gates: `37/37 PASS`.
- Locked restore: seven projects, PASS.
- Release solution: zero warnings, zero errors.
- Core/Data: `534 passed`, `0 failed`, `0 skipped`.
- CLI selftest: PASS.
- WPF and UI smoke harness: Release `net48/x86`, zero warnings, zero errors.
- Dialog standards and architecture boundaries: PASS.
- Release Pack completeness, Win7 runtime closure and fresh Inno installer:
  PASS on exact implementation SHA.
- Added-line secret/private-path scan: zero findings.
- Physical effects and production data used by residual implementation: none.

## Structural and historical P2 reassessment

| Item | Status | Current decision |
| --- | --- | --- |
| Startup coordinator / PR-C | `PARTIAL` | Supervisor/scheduler/trust policies are extracted, but `MainWindow.OnLoadedAsync` still owns DB/session/recovery/online-host sequencing. Use small C1/C2/C3 extraction PRs only if justified. |
| ProductRepository split / PR-E / ARCH-003 | `OPEN` | Hot paths improved, but query/local write/remote identity/price-history responsibilities remain together. |
| SaleRepository split / PR-F | `OPEN` | Safety/CAS/reversal prerequisites are merged, but sale/reporting/reversal/stock/outbox remain together. |

The nine historical P2 findings resolve to:

- `DONE_MERGED` (5): ARCH-006, SYNC-03, SYNC-05, PERF-02, E-CI-003.
- `OPEN` (3): ARCH-003, ARCH-005, PERF-05.
- `PARTIAL` (1): SQLITE-DURABILITY-001.
- `SUPERSEDED` (0).

No new correctness defect was proven. The unresolved items are bounded
maintainability/policy/performance debt; no large refactor was started merely to
change a ledger status.

## Admin, staging and physical certification

- Admin repository: `AVAILABLE` at
  `https://github.com/XNIW/merchandise-control-admin-web`, branch `main`, SHA
  `9406da338691e70e627c26867122499f944de897`. Catalog-v2 PR #4 was normally
  merged there.
- Staging access: `CONFIGURED_BUT_NOT_VALIDATED`. The Supabase project is visible
  and healthy, and the Cloudflare staging environment exists, but the latest
  successful staging deployment is older SHA
  `eb6a2c572e228f7dc1938715525923299dec6253`; current TASK-139 main is not
  deployed. The active staging E2E workflow has zero runs and no isolated
  19,763-row fixture/device session is available in this execution.
- Required staging evidence: deploy current Admin main plus its migration to the
  non-production environment; provision isolated shop/staff/device credentials
  and deterministic 19,763-product/incremental fixtures; exercise first-login
  and catalog-pull full/incremental/interruption/recovery contracts; retain
  sanitized request IDs and cleanup proof.
- Current host: Windows 11 Home Single Language `10.0.26200`, x64, SP0. It is not
  an actual Windows 7 SP1 machine and cannot produce physical Win7 evidence.
- External backlog: unchanged at `10/25 PASS`; no row was edited.
- Production/hardware certification: `BLOCKED_EXTERNAL` for authenticated
  staging, real signing/timestamp, physical Win7, installer/runtime, mixed and
  recovery scenarios, monitor/display/scanner/printer and locale/DPI evidence.

## Final P0/P1/P2 and next stage

- P0 open: `0`.
- Delivered P1: SYNC-01, SYNC-02, PERF-01 and PERF-02 are `DONE_MERGED`.
- Remaining P1/structural: `P1-REL-01=PARTIAL_EXTERNAL_SIGNING`; Startup
  coordinator `PARTIAL`; SaleRepository split `OPEN`; authenticated staging and
  physical certification are external.
- Remaining historical P2: three `OPEN`, one `PARTIAL`.
- Final status: `NOT_DONE`.
- NEXT_STAGE: `ADMIN-STAGING-DEPLOY-AND-E2E` (`BLOCKED_EXTERNAL` until current
  backend deployment/migration and a safe authenticated fixture/session exist).
- Recommendation: first deploy and validate catalog-v2 on isolated staging;
  independently obtain real protected-tag signing material and an authorized
  physical Win7/hardware rig. Schedule structural/P2 extraction only as small,
  measured PRs after those evidence gates or when concrete maintenance pain
  justifies the regression surface.
