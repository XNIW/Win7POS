$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$failed = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:failed = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-RepoText([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "$relativePath missing"
        return ""
    }
    return [System.IO.File]::ReadAllText($path)
}

function Require-Text(
    [string]$text,
    [string]$pattern,
    [string]$description) {
    if ($text -notmatch $pattern) {
        Fail $description
    }
    else {
        Pass $description
    }
}

$migrationFiles = @(
    "src/Win7POS.Data/Migrations/SchemaMigration.cs",
    "src/Win7POS.Data/Migrations/SchemaMigrationRegistry.cs",
    "src/Win7POS.Data/Migrations/SchemaMigrationRunner.cs",
    "src/Win7POS.Data/Migrations/LegacySchemaDetector.cs"
)
$testFiles = @(
    "tests/Win7POS.Core.Tests/Data/MigrationRunnerTests.cs",
    "tests/Win7POS.Core.Tests/Data/LegacyMigrationFixtureTests.cs",
    "tests/Win7POS.Core.Tests/Data/MigrationBackupRestoreTests.cs"
)
$fixtureFiles = @(
    "legacy_initial_minimal.sql",
    "legacy_pre_refund_void.sql",
    "legacy_pre_outbox.sql",
    "legacy_pre_shop_binding.sql",
    "legacy_pre_catalog_exactness.sql",
    "legacy_current_main_unversioned.sql",
    "legacy_post_pr7_unversioned.sql"
)

foreach ($file in @($migrationFiles + $testFiles)) {
    [void](Read-RepoText $file)
}
foreach ($fixture in $fixtureFiles) {
    [void](Read-RepoText ("tests/fixtures/migrations/" + $fixture))
}
if ($failed) { exit 1 }

$runner = Read-RepoText "src/Win7POS.Data/Migrations/SchemaMigrationRunner.cs"
$registry = Read-RepoText "src/Win7POS.Data/Migrations/SchemaMigrationRegistry.cs"
$migration = Read-RepoText "src/Win7POS.Data/Migrations/SchemaMigration.cs"
$detector = Read-RepoText "src/Win7POS.Data/Migrations/LegacySchemaDetector.cs"
$initializer = Read-RepoText "src/Win7POS.Data/DbInitializer.cs"
$runnerTests = Read-RepoText "tests/Win7POS.Core.Tests/Data/MigrationRunnerTests.cs"
$fixtureTests = Read-RepoText "tests/Win7POS.Core.Tests/Data/LegacyMigrationFixtureTests.cs"
$backupTests = Read-RepoText "tests/Win7POS.Core.Tests/Data/MigrationBackupRestoreTests.cs"

Require-Text $runner 'CREATE TABLE schema_migrations\s*\(\s*migration_id TEXT PRIMARY KEY,\s*checksum TEXT NOT NULL,\s*description TEXT NOT NULL,\s*applied_at TEXT NOT NULL,\s*app_version TEXT NULL' `
    "migration ledger has the required immutable metadata shape"
Require-Text $migration 'SHA256\.Create\(\)' "migration checksums use SHA-256"
Require-Text $migration 'Published checksum pin does not match migration material' "published migrations enforce runtime checksum pins"
Require-Text $migration '^[\s\S]*MigrationIdPattern[\s\S]*\^\[0-9\]\{4\}' "migration IDs require a four-digit ordered prefix"
Require-Text $detector 'PRAGMA table_info' "legacy detection inspects real SQLite metadata"
Require-Text $detector 'PRAGMA foreign_key_list' "legacy detection inspects foreign keys"
Require-Text $detector 'HasAllColumnDefinitions' "legacy detection validates column type, nullability and defaults"
Require-Text $detector 'HasCanonicalTableDefinitions' "legacy detection compares canonical table definitions"
Require-Text $detector 'HasKnownTableDefinitions' "legacy detection rejects unknown structural additions"
Require-Text $detector 'PRAGMA index_xinfo' "legacy detection validates UNIQUE collation and sort order"
Require-Text $detector 'ReadForeignKeySignatures' "legacy detection rejects unknown foreign keys"
Require-Text $detector 'ReadTriggerDefinitions' "legacy detection rejects unknown triggers"
Require-Text $detector 'ReadExplicitIndexDefinitions' "legacy detection rejects unknown explicit indexes"
Require-Text $detector '\\bCHECK\\s\*\\\(' "legacy detection rejects unknown CHECK constraints"
Require-Text $detector 'HasAllIndexDefinitions' "legacy detection validates full index definitions"
Require-Text $registry 'DbInitializer\.CoreSchemaFingerprintSql' "core bootstrap uses the exact legacy structural fingerprint"
Require-Text $registry 'DbInitializer\.DependentSchemaSql' "dependent bootstrap uses exact table definitions"
Require-Text $registry 'DbInitializer\.PrBKnownSchemaSql' "ledgerless bootstrap is bounded to the frozen PR-B schema whitelist"
Require-Text $registry 'IsRecognizedPostPr7LedgerlessBaseline' "post-PR7 ledgerless bootstrap uses a dedicated bounded baseline recognizer"
Require-Text $registry 'IsCanonicalRegistry' "special ledgerless recognition is restricted to the canonical registry"
Require-Text $migration 'RecognizesLedgerlessBaseline' "ledgerless baseline recognition is explicit migration metadata"
Require-Text $initializer 'PostPr7LedgerlessKnownSchemaSql' "post-PR7 receipt schema has a separate frozen whitelist"
Require-Text $initializer 'ReceiptShopSnapshotColumn' "receipt snapshot is owned by a dedicated additive migration"
Require-Text $runner 'BeginTransaction\(deferred:\s*false\)' "ledger and migrations use immediate SQLite transactions"
Require-Text $runner 'Checksum mismatch' "applied checksum mismatches fail closed"
Require-Text $runner 'ledger contains a gap' "ledger gaps fail closed"
Require-Text $runner 'Automatic downgrade is not supported' "unknown future migrations block downgrade"
Require-Text $runner 'Path\.GetFileName\(backupPath\)' "migration logging exposes only the backup filename"
Require-Text $initializer 'new SchemaMigrationRunner\(\s*factory' "startup delegates versioning to the migration runner"
Require-Text $initializer 'ReconcileMutableInvariants' "repeatable mutable invariants are separate from one-shot migrations"

$backupIndex = $runner.IndexOf("if (needsBackup)", [StringComparison]::Ordinal)
$ledgerIndex = $runner.IndexOf("if (!inspection.LedgerExists)", [StringComparison]::Ordinal)
if ($backupIndex -lt 0 -or $ledgerIndex -lt 0 -or $backupIndex -ge $ledgerIndex) {
    Fail "verified backup must run before any ledger creation or bootstrap"
}
else {
    Pass "verified backup runs before any ledger creation or bootstrap"
}

$ids = @([regex]::Matches($registry, 'new SchemaMigration\(\s*"(?<id>[0-9]{4}-[a-z0-9-]+)"') |
    ForEach-Object { $_.Groups['id'].Value })
$expectedIds = @(
    "0001-core-pos-schema",
    "0002-supported-legacy-columns",
    "0003-outbox-catalog-evidence",
    "0004-shop-bound-outbox-backfill",
    "0005-canonical-query-indexes",
    "0006-system-role-permissions",
    "0007-receipt-shop-snapshot"
)
if (($ids -join "|") -ne ($expectedIds -join "|")) {
    Fail "registry is not the expected append-only ordered migration sequence"
}
else {
    Pass "registry is the expected append-only ordered migration sequence"
}

$expectedChecksums = @(
    "bd7f3e733cdf867b40816757687e34a654ceee39a2d60ea6923dda6cb98591c6",
    "93008b229176205ed7c8d9c631739fb78e2166504012b4b9f277e1338d125d47",
    "dbc5dae94d81d82fd9043020712471731cb34c1e1d961e00348fcc5cec29eacd",
    "649f49fbe75acf86ecfd354269df305fcece6b81a21e45a5de224f2377992a66",
    "44afcce1cee8d87f0d68f1de472c18f0b5fb6ca474ee94c592d43cf71234da1a",
    "ade7405f309f563d6734bf5eaafd36df1f2ef6da8bd42ac9b910d1c51b783b8e",
    "a1d12cca8bbfeb57872ee854e18cc32bf98258937d1f7be4be91d925f2ef6462"
)
foreach ($checksum in $expectedChecksums) {
    if ($registry -notmatch [regex]::Escape($checksum) -or
        $runnerTests -notmatch [regex]::Escape($checksum)) {
        Fail "published checksum is not pinned in registry and tests: $checksum"
    }
}
if (-not $failed) { Pass "published ID-to-checksum mapping is pinned in registry and tests" }

$trueBeforeApplyCount = [regex]::Matches(
    $registry,
    'true,\s*\r?\n\s*DbInitializer\.').Count
if ($trueBeforeApplyCount -ne $expectedIds.Count) {
    Fail "every published PR-B migration must require a pre-migration backup"
}
else {
    Pass "every published PR-B migration requires a pre-migration backup"
}

foreach ($token in @(
    "FailureBeforeDdl",
    "FailureAfterDdl",
    "FailureDuringBackfill",
    "ChecksumMismatch",
    "LedgerGap",
    "ConcurrentEnsureCreated",
    "SameNamedWrongIndexDefinition",
    "CanonicalIndexBatch_IgnoresTrailingWhitespace",
    "WrongColumnTypeNullabilityOrDefault",
    "WrongPrimaryKeyOrUniqueConstraint",
    "UnknownColumnOrUniqueCollation",
    "UnknownCheckTriggerForeignKeyOrIndex",
    "CommentSeparatedCheckConstraint",
    "LedgerThrough0006_AppliesOnlyReceiptSnapshotMigrationAndBacksUpFirst",
    "LedgerlessPostPr7WithUnknownColumn_FailsClosedWithoutLedgerRows",
    "LedgerlessPostPr7WithMissingOwnershipBackfill_IsNotFalselyBootstrapped",
    "AuthenticLatestLedgerWithoutReceiptSnapshotColumn_FailsCurrentSchemaValidation",
    "CanonicalBaselineRecognizer_CannotBootstrapUnsatisfiedCustomPredecessor",
    "MalformedSchemaWithLedgerThrough0006_DoesNotCommit0007")) {
    if ($runnerTests -notmatch [regex]::Escape($token)) {
        Fail "migration runner regression scenario missing: $token"
    }
}
if (-not $failed) { Pass "migration runner rollback, tamper and concurrency scenarios are present" }

foreach ($fixture in $fixtureFiles) {
    if ($fixtureTests -notmatch [regex]::Escape($fixture)) {
        Fail "fixture is not exercised by tests: $fixture"
    }
}
Require-Text $fixtureTests 'ReadSemanticSchema' "fixture tests compare upgraded and fresh semantic schema"
Require-Text $fixtureTests 'ReadLedgerTimestamps' "fixture tests verify reopen idempotence"
Require-Text $backupTests 'BackupFailure_LeavesExistingDatabaseAndLedgerUntouched' "backup failure is tested before ledger mutation"
Require-Text $backupTests 'VerifiedPreMigrationBackup_CanBeRestoredAndUpgradedToLatest' "pre-migration restore and re-upgrade are tested"
Require-Text $backupTests 'RestoreWithAuthenticLatestLedgerButMissingReceiptColumn_RollsBackLiveDatabase' "restore rejects an authentic latest ledger with missing current schema"
Require-Text $backupTests 'InvalidForeignKeySource_IsRejectedBeforeLedgerMutation' "invalid backup sources are tested fail-closed"
Require-Text $backupTests 'BackupIntegrityValidationFailure_LeavesLedgerUntouched' "invalid backup integrity results are tested fail-closed"
Require-Text $fixtureTests 'ReadFixtureDomainEvidence' "fixture tests preserve seeded domain rows"
Require-Text $fixtureTests 'ReadBlockedOutboxTimestamps' "repeatable outbox reconciliation does not churn canonical blocked rows"
Require-Text $fixtureTests 'AssertPostPr7MainWasBootstrappedWithoutReapplying' "post-PR7 ledgerless fixture bootstraps without replay"

$coreSchemaStart = $initializer.IndexOf('internal static string CoreSchemaSql', [StringComparison]::Ordinal)
$coreSchemaEnd = $initializer.IndexOf('internal static void CreateBaseTables', [StringComparison]::Ordinal)
$supportedStart = $initializer.IndexOf('internal static SchemaColumnDefinition[] SupportedLegacyColumns', [StringComparison]::Ordinal)
$supportedEnd = $initializer.IndexOf('internal static string SupportedLegacyColumnMaterial', [StringComparison]::Ordinal)
$prBStart = $initializer.IndexOf('internal static string PrBKnownSchemaSql', [StringComparison]::Ordinal)
$prBEnd = $initializer.IndexOf('internal static string PostPr7LedgerlessKnownSchemaSql', [StringComparison]::Ordinal)
if ($coreSchemaStart -lt 0 -or $coreSchemaEnd -le $coreSchemaStart -or
    $supportedStart -lt 0 -or $supportedEnd -le $supportedStart -or
    $prBStart -lt 0 -or $prBEnd -le $prBStart) {
    Fail "published PR-B schema material boundaries are missing"
}
else {
    $publishedMaterial =
        $initializer.Substring($coreSchemaStart, $coreSchemaEnd - $coreSchemaStart) +
        $initializer.Substring($supportedStart, $supportedEnd - $supportedStart) +
        $initializer.Substring($prBStart, $prBEnd - $prBStart)
    if ($publishedMaterial -match 'receipt_shop_snapshot') {
        Fail "receipt snapshot mutated published 0001-0003 schema material"
    }
    else {
        Pass "receipt snapshot is excluded from published 0001-0003 schema material"
    }
}

$migrationSource = @($migrationFiles + "src/Win7POS.Data/DbInitializer.cs") |
    ForEach-Object { Read-RepoText $_ } |
    Out-String
if ($migrationSource -match '(?i)PRAGMA\s+journal_mode\s*=\s*WAL') {
    Fail "PR-B must not enable WAL"
}
else {
    Pass "PR-B does not enable WAL"
}
if ($migrationSource -match '(?i)DROP\s+TABLE|ALTER\s+TABLE\s+\S+\s+DROP\s+COLUMN') {
    Fail "destructive table or column migration detected"
}
else {
    Pass "PR-B migrations are additive and non-destructive"
}

$fixtureDirectory = Join-Path $repoRoot "tests/fixtures/migrations"
$unexpectedBinary = @(Get-ChildItem -LiteralPath $fixtureDirectory -File |
    Where-Object { $_.Extension -ne ".sql" })
if ($unexpectedBinary.Count -gt 0) {
    Fail "fixture directory contains non-SQL artifacts"
}
else {
    Pass "fixture directory contains SQL only"
}
$fixtureText = Get-ChildItem -LiteralPath $fixtureDirectory -Filter "*.sql" -File |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String
if ($fixtureText -match '(?i)[A-Z]:\\Users\\|/home/|/Users/|github_pat_|ghp_|sessionToken\s*=|deviceToken\s*=|password\s*=') {
    Fail "fixture SQL contains a local path or credential-shaped value"
}
else {
    Pass "fixture SQL is sanitized and contains no local paths or credentials"
}

if ($failed) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
