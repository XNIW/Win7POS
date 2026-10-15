# Owned catalog QA collector and replay

These scripts version the demonstrated collector identity/departure repair and
apply replay protocol. They contain no product changes, database setup, fixture
DDL, installer, WPF launcher, host lease or provider deployment. The existing
catalog wire parser replay remains separate.

## Collector

`Collect-Win7PosOwnedResources.ps1` preserves the accepted collector body. Supply
`OwnershipPath`, `OutputPath`, `PhaseFile`, `StopFile` and optional additional
phase files. The ownership manifest (`maximum-uniform-owned-processes-v1`)
configures runRoot, nonce, worker PID/creation/executable/script, optional clients,
PostgreSQL PID/creation/executable/data-directory/port and source commit/tree/SQL
hash. The actor SID comes from the current Windows identity. There are no frozen
machine paths, user SIDs or source commits.

Paths are absolute, normalized and compared with OrdinalIgnoreCase, with exact
executable and data-directory identities. Paths must stay in the owned runRoot;
reparse paths are refused. Command argv, nonce, postmaster identity, owner SID and
creation time remain required. No broad process termination is implemented.

Only a typed owner CIM NotFound or process departure on an admitted PostgreSQL
descendant permits a successful fresh exact-PID absence recheck. A missing-image
candidate requires live admitted-parent PID/creation lineage and successful
fresh absence. It remains `unregistered_departed_candidate`, with image/SID
unverified, null metrics and no lineage promotion. Live/reused PID, duplicate
results, changed parent and failed queries fail. Sampling cannot prove every
short-lived child, simultaneous aggregate lifetime peaks or native-owner release.

## Apply and terminal replay

`catalog-import-replay-policy.json` names the shared protocol. The controller
computes and supplies `qaScriptBindings.workerSha256`, `terminalSha256` and
`protocolSha256`; these bind the actual selected versioned files. It also supplies
the existing worker config (`maximum-uniform-worker-config-v2`), including
`dependencyRoot` containing the reviewed TypeScript/pg dependencies, sourceRoot,
sourceCommit/tree, owned loopback endpoints/data directory, immutable input and
fixture manifests, finalPosInputBinding, logicalRows, nonce, `replayEachRequest:false` and
`qaReplayPolicy`. Source SHA/tree/loaded Git bytes and real handler/auth/RPC
boundaries remain checked. No provider credentials belong in repository files.
Stage the selected worker, terminal and shared module adjacent to each other
inside the owned runRoot, with the recorded file hashes. The collector requires
the worker script there; the versioned repository is not the runtime runRoot.

The worker delivers one FIRST apply response. It keeps successful nonapply POSTs
as blocking byte-identical inband retries. Request cap 524288, part cap1000,
schedule2, complete actor/plan/part ACK checks and the product deadline remain.
No asynchronous replay or background queue is introduced.

`catalog-import-terminal-replay.mjs --config CONFIG` reuses exactly N original
request bodies and awaits every response. Its required configured
`economicSnapshotCommand` has an absolute `scriptPath`, SHA256 and string
`arguments`; the same Node executable invokes that reviewed owned-fixture reader
with `--economic-stage before|after`. The adapter must emit the complete existing
`maximum-uniform-db-postcheck-v2` JSON on stdout, including source/input/fixture
bindings, exact counts, membership/history/stock checks and both full keyset page
digests. The current external controller's saved-file postcheck reader needs an
explicit adapter for that stdout contract; do not silently treat its short
console summary as a complete snapshot. No such adapter or run is fabricated here.
Alternatively import `executeTerminalReplay` and supply the existing controller's
`captureEconomics` and `postApply` callbacks directly.

Economic-before is captured before one awaited terminal loop; economic-after is
captured afterwards. Only the phase label may differ. All other fields, including
additional counters and full page digests, must match. N FIRST/N terminal/2N
external apply frames, exact original prefix, K nonapply witnesses, raw SHA/bytes,
immutable persisted ACK JSON and parent progress/complete context are validated
before writing PASS. Apply retry files are prohibited. Artifacts use CreateNew;
a failed or previously completed terminal run cannot be overwritten by a rerun.
The schedule2 schema does not itself record HTTP method; method evidence comes
from the pinned worker branch. This does not qualify deployed Cloudflare or Win7.

## Synthetic regression checks

```
pwsh -NoProfile -File tests/qa/Test-Win7PosOwnedProcessIdentity.ps1
node --test tests/qa/catalog-import-replay-protocol.test.mjs
pwsh -NoProfile -File scripts/check-owned-catalog-qa.ps1
```

The PowerShell test extracts only identity functions via AST and uses in-memory
CIM/owner stubs. It never starts the collector or calls a provider/native process.
The Node tests use toy bodies/snapshots and temporary small files; no HTTP/PG/WPF
or large SQLite is opened. They reject path/reuse/query errors, hidden apply
replay, wrong source bindings, raw mutation, actor/receipt/context changes,
missing terminal parts, invalid economic counts and changed page digests.
The shared required gate also runs the source-index fixture toy suite. It requires
Node18+ and PowerShell on the development/CI host, fails if either is missing, and
does not introduce those tools as runtime requirements for a Win7 cash register.

Historical accepted external sources: collector035c1554, worker232747ba and
terminale118d8a8. Their runtime receipts retain their original source pins.
Publication of this portable ancillary QA revision does not reattribute old
5000/MAX results to a new QA commit and does not modify a frozen running executor.
