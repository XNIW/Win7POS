# Source index fixture and catalog parity

This QA module extracts the two existing Admin expression indexes from an exact
Git commit. It does not connect to a database, execute SQL, apply migrations,
change auth, or qualify throughput, resources, Windows 7 or a deployed service.
An authorized owned runner can use the exported SELECTs and comparators.

Node 18 or later and Git are sufficient; there are no package dependencies.

## Extract canonical source

Pass the Admin checkout and reviewed 40-character commit explicitly. The module
reads canonical Git bytes, independent of the checkout's CRLF settings or local
uncommitted edits. It exports only the two literal CREATE INDEX statements;
comments, strings and function bodies cannot supply a phantom statement.
Definition drift, duplicates, partial/unique indexes and missing statements fail.

```powershell
node scripts/qa/catalog-source-index-fixture.mjs source `
  --admin-root <AdminCheckout> --admin-commit <ReviewedAdminSha> `
  --output <FreshOwnedOutputDirectory>/source-index-fixture.json
```

`--migration-path` can override the relative Git source path. Its default is
`supabase/migrations/20261001220355_wechat_010_recovery_checkpoint_performance.sql`.
The artifact records commit, tree, Git blob and SHA256 of the canonical source
and extracted SQL. `--output` uses exclusive creation and refuses an existing
file. The operator freezes this artifact's hash in the runner's reviewed manifest.
This source command grants no permission to execute its two statements.

## Compare captures without inventing historical metadata

The historical saved catalog can contain only `schemaname`, `indexname` and
`indexdef`. `assertSavedCatalogBaseline(saved, before)` compares its complete
identity set and every property actually saved. It never invents flags or owners
for an old capture. A saved 16-index subset cannot validate a 74-index full query.
Counts are derived from the captures; they are not substituted constants.

The runtime `before`, `after` and `afterReplay` captures must contain all fields
from `FULL_INDEX_CATALOG_SQL`: OIDs, schema, table, owner, definition, valid/ready/
live/unique/primary flags, access method, expression and predicate. The query's
scope is exactly public/app_private/auth/extensions, not all PostgreSQL schemas
or a selected TEST index inventory. Use the same query in every phase.

`TARGET_TABLE_CONTEXT_SQL` captures both target tables and their owner/schema/
table identities **before DDL**. New index owner must match its target table.
Missing historical properties are not supplied from guessed defaults.

`assertSourceIndexInstallation(before, after, targetTables, fixture)` allows only
the two exact source definitions. It preserves **every captured property** of
each existing index and checks all required properties of the two additions.
`assertFullIndexCatalogUnchanged(after, afterReplay)` compares the entire final
catalog again, including new OIDs and any additional captured fields.

The captures can be row arrays, `{ "rows": [...] }`, or saved query responses
with one `{ "resultSets": [{ "rowCount": N, "rows": [...] }] }`. A supplied
rowCount must match the actual array. Target-table context accepts the same forms.

```powershell
node scripts/qa/catalog-source-index-fixture.mjs verify `
  --fixture <PinnedSourceFixture.json> --saved-baseline <PinnedHistoricalCatalog.json> `
  --before <BeforeFullCatalog.json> --after <AfterFullCatalog.json> `
  --after-replay <AfterReplayFullCatalog.json> --target-tables <BeforeTargetTables.json> `
  --output <FreshOwnedOutputDirectory>/index-parity.json
```

The containing runner must bind input hashes, current source, actor, host lease,
ownership and cleanup. This comparator proves only captured metadata parity;
function/data/economic invariance, real ACKs and performance require their own
actual evidence. A failed earlier run remains failed.

## Regression checks

```powershell
node --test scripts/qa/catalog-source-index-fixture.test.mjs
```

The small saved fixture is explicitly synthetic. It expands into 74 indexes
(51 public, 22 app_private, 1 auth), then tests the exact two additions and all76
after replay. Counterexamples remove each source index while replacing it with
an unrelated index to keep count76, alter definitions/flags/owners/OIDs, retain
the old16-subset mistake, or mutate final metadata. No database or runtime
qualification is performed by these tests.
