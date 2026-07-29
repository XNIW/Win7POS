$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$failed = $false

function Read-RepoText([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host "FAIL: missing $relativePath" -ForegroundColor Red
        $script:failed = $true
        return ""
    }
    return [IO.File]::ReadAllText($path)
}

function Require-Pattern(
    [string]$label,
    [string]$text,
    [string]$pattern
) {
    if ($text -notmatch $pattern) {
        Write-Host "FAIL: $label" -ForegroundColor Red
        $script:failed = $true
        return
    }
    Write-Host "PASS: $label" -ForegroundColor Green
}

function Reject-Pattern(
    [string]$label,
    [string]$text,
    [string]$pattern
) {
    if ($text -match $pattern) {
        Write-Host "FAIL: $label" -ForegroundColor Red
        $script:failed = $true
        return
    }
    Write-Host "PASS: $label" -ForegroundColor Green
}

$runner = Read-RepoText "scripts/qa/Invoke-Win7PosStagingAcceptance.ps1"
$staging = Read-RepoText "tests/Win7POS.Wpf.UiSmokeHarness/StagingAcceptanceWpfHarness.cs"
$article = Read-RepoText "tests/Win7POS.Wpf.UiSmokeHarness/StagingArticleMutationAcceptance.cs"
$loopback = Read-RepoText "tests/Win7POS.Wpf.UiSmokeHarness/ArticleMutationLoopbackHarness.cs"
$program = Read-RepoText "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$processRunner = Read-RepoText "scripts/qa/Win7PosAcceptanceProcessRunner.psm1"
$runnerTest = Read-RepoText "tests/qa/Test-Win7PosStagingAcceptanceRunner.ps1"
$bootstrap = Read-RepoText "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"

$acceptanceWrapperPath = Join-Path $repoRoot (
    "scripts/qa/Invoke-Win7PosStagingAcceptance.ps1")
$acceptanceWrapperTokens = $null
$acceptanceWrapperParseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $acceptanceWrapperPath,
    [ref]$acceptanceWrapperTokens,
    [ref]$acceptanceWrapperParseErrors) | Out-Null
if ($acceptanceWrapperParseErrors.Count -ne 0) {
    Write-Host (
        "FAIL: acceptance wrapper parser errors: " +
        (($acceptanceWrapperParseErrors | ForEach-Object {
            $_.Message
        }) -join " | ")
    ) -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "PASS: acceptance wrapper parses completely" `
        -ForegroundColor Green
}

$finalDataDirectory = [regex]::Escape(
    "C:\POSData\Win7POSFinalArticleSyncAcceptance")
Require-Pattern "runner uses the final isolated data directory" $runner `
    $finalDataDirectory
Reject-Pattern "obsolete article-mutation data directory is absent" `
    ($runner + $staging) "Win7POSArticleMutationAcceptance"
Require-Pattern "run IDs use the final acceptance namespace" $runner `
    "\`$runId\s*=\s*'ASUSART_FINAL_'"
Require-Pattern "evidence uses the final acceptance directory namespace" `
    $runner "win7pos-final-article-sync-"
Reject-Pattern "obsolete V1 evidence directory namespace is absent" `
    $runner "win7pos-pos-article-sync-v1-"
Require-Pattern "runner requires zero blocked conflicts" $runner `
    "articleBlockedConflicts\s*-eq\s*0"
Require-Pattern "runner requires supported conflict resolution" $runner `
    "articleConflictResolved\s*-eq\s*\`$true"
Require-Pattern "runner requires multilingual UI evidence" $runner `
    "articleUiLanguagesVerified\s*-eq\s*\`$true"
Require-Pattern "runner executes a prepare process phase" $runner `
    "'--acceptance-phase',\s*'prepare'"
Require-Pattern "runner executes a resume process phase" $runner `
    "'--acceptance-phase',\s*'resume'"
Require-Pattern "runner requires the restart checkpoint exit" $runner `
    "ExitCode\s*-ne\s*75"
Require-Pattern "runner requires bounded online recovery after restart" `
    $runner "restartOnlineRecoveryValid\s*-eq\s*\`$true"
Require-Pattern "runner requires process-scoped offline authority to clear" `
    $runner "restartOfflineAuthorityCleared\s*-eq\s*\`$true"
Require-Pattern "runner rejects every worktree change including untracked files" `
    $runner "status\s+--porcelain\)"
Reject-Pattern "runner does not hide untracked files" $runner `
    "--untracked-files=no"
Require-Pattern "repo evidence attests untracked cleanliness" $runner `
    "worktreeCleanIncludingUntracked=True"
Require-Pattern "runner consumes durable marker or final report" $processRunner `
    "run-consumed-redacted\.json[\s\S]*staging-acceptance-result\.json"
Require-Pattern "timeout-after-marker preserves logical run accounting" `
    $runnerTest "Timeout after the consumed marker incorrectly restored run budget"
Require-Pattern "pre-rename marker preserves logical run accounting" `
    $runnerTest "Flushed pre-rename marker incorrectly restored run budget"
Require-Pattern "runner test parses the complete acceptance wrapper" `
    $runnerTest "Language\.Parser\]::ParseFile"
Require-Pattern "program requires explicit acceptance phases" $program `
    "--acceptance-phase prepare\|resume"
Require-Pattern "first server response invokes the in-flight observation callback" `
    $bootstrap "FirstLoginAsync[\s\S]*requestReachedServerObserved\?\.Invoke\(\)"
Require-Pattern "staging callback writes the run-consumed marker" `
    $staging "requestReachedServerObserved:\s*\(\)\s*=>[\s\S]*WriteRunConsumedMarkerAtomically"
Require-Pattern "run-consumed marker is flushed before atomic rename" `
    $staging "stream\.Flush\(true\);[\s\S]*File\.Move\(temporaryPath, finalPath\)"
Require-Pattern "loopback holds catalog after durable server-reach marker" `
    $loopback "run_consumed_marker_not_durable_before_catalog_return"

$resourceAssignment = $program.IndexOf(
    "Application.ResourceAssembly =",
    [StringComparison]::Ordinal)
$applicationCreation = $program.IndexOf(
    "var app = new Application",
    [StringComparison]::Ordinal)
if ($resourceAssignment -lt 0 -or
    $applicationCreation -lt 0 -or
    $resourceAssignment -gt $applicationCreation) {
    Write-Host "FAIL: WPF resource assembly must be set before Application creation" `
        -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "PASS: WPF resource assembly is set before Application creation" `
        -ForegroundColor Green
}

Require-Pattern "loopback supersedes the intentional conflict" $loopback `
    "resolution_code\s*=\s*'superseded_by_correction'"
Require-Pattern "loopback final blocked count is zero" $loopback `
    "report\.BlockedConflicts\s*=\s*0"
Reject-Pattern "loopback no longer accepts a terminal blocked conflict" `
    $loopback "report\.BlockedConflicts\s*=\s*1"
Require-Pattern "staging supersedes the intentional conflict" $article `
    "intentional_conflict_not_superseded"
Require-Pattern "staging final conflict count is zero" $article `
    "counts\.BlockedConflicts\s*==\s*0"
Require-Pattern "staging records the supported resolution" $article `
    "result\.ConflictResolved\s*=\s*true"
Require-Pattern "staging performs a real process restart checkpoint" $article `
    "article-restart-checkpoint\.json"
Require-Pattern "resume uses bounded online recovery after process restart" `
    $staging "TryLoadRestartedOnlineRecoverySession"
Require-Pattern "resume preserves prepare-phase offline authority proof" `
    $staging "report\.OfflineAuthorizationValid[\s\S]*RestartOnlineRecoveryValid"
Require-Pattern "offline create is verified in one SQLite transaction" $article `
    "ReadOfflineCreateAtomicSnapshotAsync[\s\S]*BeginTransaction\(\)"
Require-Pattern "restart verifies the stable client product identity" $article `
    "restartCreateSnapshot\.ProductClientProductId[\s\S]*checkpoint\.ClientProductId"
Reject-Pattern "staging no longer recreates only the host in one process" `
    $article "initialHost\.Dispose\(\)"
Require-Pattern "staging counts every unresolved blocked row" $article `
    "WHERE state = 'failed_blocked';"
Require-Pattern "conflict correction requires an applied ACK" $article `
    "correction\.ack_status = 'applied'"
Require-Pattern "canonical product and shadow values are compared" $article `
    "RequireCanonicalProductAsync"
Require-Pattern "ACK revision is compared to catalog updatedAt" $article `
    "LatestAckAuthoritativeRevision[\s\S]*CatalogUpdatedAtRevision"
Require-Pattern "remote price and stock IDs are required" $article `
    "remote_price_history_ids_missing[\s\S]*remote_stock_movement_ids_missing"
Require-Pattern "replay ACK equality is required" $article `
    "ReplayAckMatches"
Require-Pattern "duplicate identities are independent" $article `
    "DuplicateIdentityIndependent\s*=\s*true"
Require-Pattern "lifecycle canonical readback is required" $article `
    "LifecycleCanonicalReadback\s*=\s*true"
Require-Pattern "UI exercises keyboard traversal" $article `
    "FocusNavigationDirection\.Next"
Require-Pattern "UI validates rendered localization" $article `
    "article_sync_center_locale_not_rendered_"
Require-Pattern "UI validates visible controls are unclipped" $article `
    "clippingCandidates[\s\S]*OfType<FrameworkElement>"
Require-Pattern "UI clipping has an explicit scrollable-content policy" $article `
    "IsClippingAllowedByScrollableAncestor"
Require-Pattern "UI scroll exemption is direction-aware" $article `
    "HorizontalScrollBarVisibility[\s\S]*VerticalScrollBarVisibility"
Require-Pattern "UI clipping regression uses a non-focusable label" `
    ($article + $program) "RunNonFocusableClippingRegressionAsync"
Require-Pattern "UI clipping regression covers vertical-only scrolling" `
    $article "HorizontalScrollBarVisibility\s*=\s*[\s\S]*Disabled[\s\S]*VerticalScrollBarVisibility"
Require-Pattern "UI validates non-modal conflict state" $article `
    "ArticleLastTypedCode[\s\S]*failed_conflict"
Require-Pattern "UI non-modal accumulator starts true" $article `
    "ConflictNonModal\s*=\s*true"
Reject-Pattern "UI screenshot count is not hardcoded" $article `
    "UiScreenshots\s*=\s*10"
Reject-Pattern "UI language result is not hardcoded" $article `
    "UiLanguagesVerified\s*=\s*true"

foreach ($field in @(
    "clientMutationIds",
    "remoteProductIds",
    "priceHistoryIds",
    "manualStockMovementIds",
    "mutationReceiptReferences",
    "conflictReceiptReferences",
    "syncEventIds",
    "syncEventResolution",
    "expectedCounts",
    "syntheticShopIds",
    "syntheticCategoryIds",
    "syntheticSupplierIds"
    "receiptScopeShopId"
)) {
    Require-Pattern "cleanup manifest includes $field" $article `
        ([regex]::Escape("Name = `"$field`""))
}

Require-Pattern "cleanup prompt requires one guarded transaction" $article `
    "Begin one guarded staging transaction"
Require-Pattern "cleanup prompt requires rollback on mismatch" $article `
    "Roll back on any row-count mismatch"
Require-Pattern "cleanup prompt preserves immutable audit" $article `
    "Preserve immutable.*audit_logs"
Require-Pattern "cleanup prompt covers Admin TASK-143 through TASK-147" `
    $article "TASK-143.*TASK-144.*TASK-145.*TASK-146.*TASK-147"
Require-Pattern "cleanup prompt returns the exact cross-repo status" $article `
    "DONE_CROSS_REPO_POS_ARTICLE_SYNC"
Require-Pattern "cleanup prompt forbids wildcard cleanup" $article `
    "never use a wildcard"
Require-Pattern "cleanup receipts include exact shop scope" $article `
    "Name = `"shopId`""

foreach ($fileName in @(
    "00-repo-sync.txt",
    "01-admin-handoff.txt",
    "02-profile-preflight.txt",
    "03-exact-main-build.txt",
    "04-local-gates.txt",
    "05-first-login-redacted.json",
    "06-catalog-exactness.json",
    "07-article-mutation-results-redacted.json",
    "08-outbox-state-redacted.json",
    "09-price-history-counts.txt",
    "10-stock-movement-counts.txt",
    "11-replay-conflict-results.txt",
    "12-no-echo-result.txt",
    "13-ui-smoke-result.txt",
    "14-redaction-scan.txt",
    "article-mutation-create-article-1024x768.png",
    "article-mutation-duplicate-article-1024x768.png",
    "article-mutation-sync-center-pending-1024x768.png",
    "article-mutation-sync-center-in-progress-1024x768.png",
    "article-mutation-sync-center-conflict-1024x768.png",
    "article-mutation-sync-center-clean-1024x768.png"
)) {
    Require-Pattern "required evidence declares $fileName" `
        ($runner + $staging) ([regex]::Escape($fileName))
}

Require-Pattern "evidence secret scan is recursive" $staging `
    "SearchOption\.AllDirectories"
Require-Pattern "evidence scan checks short profile values via policy" $staging `
    "profile\?\.ShopCode[\s\S]*profile\?\.StaffCode"
Require-Pattern "evidence scan checks header markers" $staging `
    "Authorization:[\s\S]*Cookie:"
Require-Pattern "log scan checks trusted session secrets" $staging `
    "LogsDoNotContainSecrets[\s\S]*trustedSession\?\.DeviceToken[\s\S]*trustedSession\?\.SessionToken"
Require-Pattern "evidence scan includes log files" $staging `
    "scannableExtensions[\s\S]*`"\.log`""
Require-Pattern "screenshots are mirrored into their evidence directory" `
    $staging "MirrorAcceptanceScreenshots"
Require-Pattern "catalog evidence includes exact price rows" $staging `
    "ManifestPriceRows[\s\S]*LocalPriceRows"
Require-Pattern "catalog evidence includes terminal and repair state" $staging `
    "RepairRequired[\s\S]*TerminalHasMore"
Require-Pattern "offline authority evidence includes both bounds" $staging `
    "OfflineAuthorityAfterServerTime[\s\S]*OfflineAuthorityWithinSessionExpiry"
Require-Pattern "logical runs start at zero" $staging `
    "LogicalRuns\s*=\s*0"
Require-Pattern "logical run begins only after server reachability" $staging `
    "report\.LogicalRuns\s*=[\s\S]*report\.RequestReachedServer\s*\?\s*1\s*:\s*0"

if ($failed) {
    exit 1
}

Write-Host "FINAL_ARTICLE_SYNC_ACCEPTANCE_GATE=PASS"
