$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    return [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

. (Join-Path $PSScriptRoot "sales-sync-outbox-gate-helpers.ps1")

function Get-MethodDeclarationCount(
    [string]$text,
    [string]$access,
    [string]$methodName) {
    $pattern = "(?m)^\s*" + [regex]::Escape($access) +
        "\s+(?:static\s+)?(?:async\s+)?Task(?:<[^\r\n]+>)?\s+" +
        [regex]::Escape($methodName) + "\s*\("
    return [regex]::Matches($text, $pattern).Count
}

function Get-MethodSlices(
    [string]$text,
    [string]$access,
    [string]$methodName) {
    return @(
        Get-CSharpMethodSlices $text $access $methodName |
            ForEach-Object { $_.Text })
}

function Get-PatternIndex([string]$text, [string]$pattern) {
    $match = [regex]::Match($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($match.Success) {
        return $match.Index
    }
    return -1
}

function Assert-StrictFacadeDelegations(
    [string]$saleRepository,
    [string]$writer,
    [object[]]$contracts) {
    $failures = New-Object System.Collections.Generic.List[string]
    $facadeLeaks = @(
        "\b(?:_factory|_stockMovementWriter|_reversalWriter|_salesSyncOutbox)\b",
        "\b(?:SalesReceiptContentPolicy|ReceiptDocumentPolicy|CatalogShopStateRepository|AuditLogRepository)\b",
        "\b(?:BuildClientSaleId|InsertSaleLineAsync)\b",
        "\bconn\s*\.\s*(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async\b",
        "\b(?:conn|tx)\s*\.\s*(?:Open|OpenAsync|BeginTransaction|Commit|Rollback|Close|CloseAsync|Dispose|DisposeAsync)\s*\(",
        "(?m)^\s*using\s+",
        "(?i)\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|UPDATE|DELETE\s+FROM)\s+(?:sales|sale_lines|sales_sync_outbox|local_stock_movements)\b",
        "\bnew\s+Sale\s*\{"
    )

    foreach ($contract in $contracts) {
        $facadeAccess = if ($contract.PSObject.Properties.Name -contains "Access") {
            $contract.Access
        } else {
            "public"
        }
        $facadeSlices = @(Get-MethodSlices $saleRepository $facadeAccess $contract.Facade)
        $facadeCount = Get-MethodDeclarationCount $saleRepository $facadeAccess $contract.Facade
        $writerCount = Get-MethodDeclarationCount $writer "internal" $contract.Writer
        if ($facadeCount -ne $contract.Forwarding.Count -or
            $facadeSlices.Count -ne $contract.Forwarding.Count -or
            $writerCount -ne $contract.Forwarding.Count) {
            $failures.Add(
                "$($contract.Facade)/$($contract.Writer) facade=$facadeCount slices=$($facadeSlices.Count) writer=$writerCount expected=$($contract.Forwarding.Count)") | Out-Null
            continue
        }

        for ($index = 0; $index -lt $facadeSlices.Count; $index++) {
            $slice = $facadeSlices[$index]
            $delegationPattern = "_transactionWriter\s*\.\s*" +
                [regex]::Escape($contract.Writer) + "\s*\("
            $forwardingPattern = "(?s)_transactionWriter\s*\.\s*" +
                [regex]::Escape($contract.Writer) + "\s*\(\s*" +
                $contract.Forwarding[$index] + "\s*\)"
            $delegated = [regex]::Matches($slice, $delegationPattern).Count
            $forwarded = [regex]::Matches($slice, $forwardingPattern).Count
            $leaks = @($facadeLeaks | Where-Object { $slice -match $_ })
            if ($delegated -ne 1 -or $forwarded -ne 1 -or $leaks.Count -gt 0) {
                $failures.Add(
                    "$($contract.Facade) overload $($index + 1) delegates=$delegated exact-forward=$forwarded leaks=$($leaks -join ', ')") | Out-Null
            }
        }
    }

    if ($failures.Count -gt 0) {
        Fail "F6 SaleRepository transaction facades must be exact, method-slice-scoped forwards: $($failures -join '; ')"
    } else {
        Pass "Every F6 transaction facade forwards its exact argument order without transaction or persistence ownership"
    }
}

function Assert-CallerOwnedSlice(
    [string]$slice,
    [string]$label,
    [string[]]$requiredMarkers,
    [switch]$AllowDelegatedDatabaseWork) {
    if ([string]::IsNullOrWhiteSpace($slice)) {
        Fail "$label method slice is missing"
        return
    }

    $missing = @($requiredMarkers | Where-Object { $slice -notmatch $_ })
    $dapper = @(Get-CSharpDapperAsyncInvocations $slice)
    $unbound = @($dapper | Where-Object {
            -not (Test-CSharpInvocationUsesTransaction $_.Text "tx")
        })
    $lifecycleLeaks = @(
        "\b_factory\b",
        "\bconn\s*\.\s*Open(?:Async)?\s*\(",
        "\b(?:conn|tx)\s*\.\s*(?:BeginTransaction|Commit|Rollback|Close|CloseAsync|Dispose|DisposeAsync)\s*\(",
        "(?m)^\s*using\s+"
    ) | Where-Object { $slice -match $_ }

    if ($missing.Count -gt 0 -or
        (-not $AllowDelegatedDatabaseWork -and $dapper.Count -eq 0) -or
        $unbound.Count -gt 0 -or $lifecycleLeaks.Count -gt 0) {
        Fail "$label must use only the supplied transaction. missing=$($missing -join ', ') dapper=$($dapper.Count) missingTx=$($unbound.Count) lifecycle=$($lifecycleLeaks -join ', ')"
    } else {
        Pass "$label propagates the supplied transaction without connection or transaction lifecycle ownership"
    }
}

function Assert-OrderedMarkers(
    [string]$slice,
    [string]$label,
    [object[]]$markers) {
    $previous = -1
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($marker in $markers) {
        $index = Get-PatternIndex $slice $marker.Pattern
        if ($index -lt 0) {
            $failures.Add("missing $($marker.Name)") | Out-Null
        } elseif ($index -le $previous) {
            $failures.Add("out-of-order $($marker.Name)") | Out-Null
        } else {
            $previous = $index
        }
    }

    if ($failures.Count -gt 0) {
        Fail "$label must retain its ordered transaction protocol: $($failures -join '; ')"
    } else {
        Pass "$label retains its ordered transaction protocol"
    }
}

function Remove-CSharpComments([string]$text) {
    $result = New-Object System.Text.StringBuilder
    for ($index = 0; $index -lt $text.Length;) {
        $end = Get-CSharpTriviaEnd $text $index
        if ($end -gt $index) {
            if ($text.Substring($index).StartsWith('//') -or $text.Substring($index).StartsWith('/*')) {
                [void]$result.Append(' ')
            } else { [void]$result.Append($text.Substring($index, $end - $index)) }
            $index = $end
        } else { [void]$result.Append($text[$index]); $index++ }
    }
    return $result.ToString()
}

function Get-CoordinatorCommitSlice([string]$text) {
    $declarations = [regex]::Matches($text, '\bpublic\s+void\s+CommitAndPublishReportRevision\s*\(')
    if ($declarations.Count -ne 1) { return '' }
    $body = Find-CSharpMethodBodyStart $text $declarations[0].Index
    if ($null -eq $body -or $body.Kind -ne 'block') { return '' }
    $end = Find-CSharpMatchingDelimiter $text $body.Index '{' '}'
    if ($end -lt $body.Index) { return '' }
    return $text.Substring($declarations[0].Index, $end - $declarations[0].Index + 1)
}

function Test-PublicationCommitProtocol([string]$helper, [string]$coordinatorCommit) {
    # Closed method shapes deliberately reject extra commits, demands, catches,
    # callbacks or early gate disposal. Whitespace/comments do not affect them.
    $helperPattern = @'
\A\s*private\s+async\s+Task\s+CommitWithPublicationAsync\s*\(
\s*SqliteConnection\s+connection\s*,\s*SqliteTransaction\s+transaction\s*,
\s*SaleAuthorizationCommitGuard\s+guard\s*,\s*Action\s+commit\s*,\s*TimeSpan\s+safety\s*\)\s*\{
\s*var\s+coordinator\s*=\s*SalesReportSnapshotCoordinator\.ForDatabase\(\s*_factory\.DbPath\s*\)\s*;
\s*using\s*\(\s*await\s+coordinator\.EnterPublicationAsync\(\s*CancellationToken\.None\s*\)\.ConfigureAwait\(\s*false\s*\)\s*\)\s*\{
\s*coordinator\.CommitAndPublishReportRevision\(\s*connection\s*,\s*transaction\s*,\s*\(\s*\)\s*=>\s*\{
\s*if\s*\(\s*guard\s*==\s*null\s*\)\s*commit\(\s*\)\s*;
\s*else\s+guard\.CommitIfStillValid\(\s*safety\s*,\s*commit\s*\)\s*;
\s*\}\s*,\s*_factory\.AuthorityStamp\s*\)\s*;\s*\}\s*\}\s*\z
'@
    $coordinatorPattern = @'
\A\s*public\s+void\s+CommitAndPublishReportRevision\s*\(
\s*SqliteConnection\s+connection\s*,\s*SqliteTransaction\s+transaction\s*,\s*Action\s+commit\s*,\s*SalesReportAuthorityStamp\s+authority\s*\)\s*\{
\s*if\s*\(\s*connection\s*==\s*null\s*\|\|\s*transaction\s*==\s*null\s*\|\|\s*commit\s*==\s*null\s*\)\s*throw\s+new\s+ArgumentNullException\(\s*\)\s*;
\s*var\s+row\s*=\s*connection\.QuerySingle<StateRow>\(\s*"SELECT[ ]dataset_id[ ]AS[ ]DatasetId,[ ]data_revision[ ]AS[ ]DataRevision[ ]FROM[ ]historical_import_dataset[ ]WHERE[ ]singleton_id=1"\s*,\s*transaction:\s*transaction\s*\)\s*;
\s*commit\(\s*\)\s*;
\s*PublishCommitted\(\s*row\.DatasetId\s*,\s*row\.DataRevision\s*,\s*authority\s*\)\s*;
\s*\}\s*\z
'@
    $options = [System.Text.RegularExpressions.RegexOptions]::IgnorePatternWhitespace
    return [regex]::IsMatch((Remove-CSharpComments $helper), $helperPattern, $options) -and
        [regex]::IsMatch((Remove-CSharpComments $coordinatorCommit), $coordinatorPattern, $options)
}

function Test-GuardedCommitRoute([string]$slice, [switch]$ExactReplay) {
    $code = Remove-CSharpComments $slice
    $delegate = 'Action\s+commitTransaction\s*=\s*\(\s*\)\s*=>\s*\{\s*tx\.Commit\(\s*\)\s*;\s*transactionCommitted\s*=\s*true\s*;\s*\}\s*;'
    $durableCall = 'await\s+CommitWithPublicationAsync\(\s*conn\s*,\s*tx\s*,\s*authorizationCommitGuard\s*,\s*commitTransaction\s*,\s*SqliteConnectionFactory\.DurableCommitSafetyBudget\s*\)\.ConfigureAwait\(false\)\s*;\s*return\s+saleId\s*;'
    $replayCall = 'if\s*\(replaySaleId\.HasValue\)\s*\{\s*await\s+CommitWithPublicationAsync\(\s*conn\s*,\s*tx\s*,\s*authorizationCommitGuard\s*,\s*commitTransaction\s*,\s*TimeSpan\.Zero\s*\)\.ConfigureAwait\(false\)\s*;\s*return\s+replaySaleId\.Value\s*;\s*\}'
    $originalVoidProof = 'if\s*\(originalSaleIdToMarkVoided\.HasValue\)\s*\{\s*await\s+_reversalWriter\.MarkSaleVoidedAsync\(\s*conn\s*,\s*tx\s*,\s*originalSaleIdToMarkVoided\.Value\s*,\s*saleId\s*,\s*DateTimeOffset\.UtcNow\.ToUnixTimeMilliseconds\(\)\s*\)\.ConfigureAwait\(false\)\s*;\s*await\s+NativeSalesOwnershipRepository\.RefreshForSaleInTransactionAsync\(\s*conn\s*,\s*tx\s*,\s*originalSaleIdToMarkVoided\.Value\s*,\s*CancellationToken\.None\s*\)\.ConfigureAwait\(false\)\s*;\s*\}'
    $calls = if ($ExactReplay) { 2 } else { 1 }
    $demands = if ($ExactReplay) { 2 } else { 1 }
    $finalCommitIndex = Get-PatternIndex $code $durableCall
    $outboxIndex = Get-PatternIndex $code '\bawait\s+EnqueueSalesSyncOutboxAsync\('
    return $code -match 'var\s+transactionCommitted\s*=\s*false\s*;' -and
        $code -match $delegate -and $code -match $durableCall -and
        (-not $ExactReplay -or $code -match $replayCall) -and
        ($ExactReplay -or $code -match $originalVoidProof) -and
        [regex]::Matches($code, '\b\w+\s*\.\s*Commit\b').Count -eq 1 -and
        [regex]::Matches($code, '\btransactionCommitted\s*=\s*true\b').Count -eq 1 -and
        [regex]::Matches($code, '\bcommitTransaction\s*=').Count -eq 1 -and
        [regex]::Matches($code, '\bcommitTransaction\b').Count -eq ($calls + 1) -and
        [regex]::Matches($code, '\bCommitWithPublicationAsync\s*\(').Count -eq $calls -and
        [regex]::Matches($code, '\bCommitIfStillValid\s*\(').Count -eq 0 -and
        $outboxIndex -ge 0 -and $finalCommitIndex -gt $outboxIndex -and
        [regex]::Matches($code, 'authorizationCommitGuard\?\.DemandStillValid\(\)').Count -eq $demands -and
        $code -match 'authorizationCommitGuard\s*==\s*null\s*\?\s*null\s*:\s*SqliteConnectionFactory\.AcquireExclusiveCommitFence\(conn\)' -and
        $code -match 'catch\s*\{\s*if\s*\(\s*!transactionCommitted\s*\)\s*\{\s*try\s*\{\s*tx\.Rollback\(\)\s*;\s*\}\s*catch\s*\{\s*\}\s*\}\s*throw\s*;\s*\}'
}

function Test-StandaloneVoidRoute([string]$slice) {
    $pattern = @'
\A\s*internal\s+async\s+Task\s+MarkSaleVoidedAsync\(\s*long\s+originalSaleId\s*,\s*long\s+refundSaleId\s*,\s*long\s+nowMs\s*\)\s*\{
\s*using\s+var\s+conn\s*=\s*_factory\.Open\(\)\s*;
\s*using\s+var\s+tx\s*=\s*conn\.BeginTransaction\(\)\s*;
\s*var\s+transactionCommitted\s*=\s*false\s*;
\s*Action\s+commitTransaction\s*=\s*\(\)\s*=>\s*\{\s*tx\.Commit\(\)\s*;\s*transactionCommitted\s*=\s*true\s*;\s*\}\s*;
\s*try\s*\{
\s*await\s+_reversalWriter\.MarkSaleVoidedAsync\(\s*conn\s*,\s*tx\s*,\s*originalSaleId\s*,\s*refundSaleId\s*,\s*nowMs\s*\)\.ConfigureAwait\(false\)\s*;
\s*await\s+NativeSalesOwnershipRepository\.RefreshForSaleInTransactionAsync\(\s*conn\s*,\s*tx\s*,\s*originalSaleId\s*,\s*CancellationToken\.None\s*\)\.ConfigureAwait\(false\)\s*;
\s*await\s+CommitWithPublicationAsync\(\s*conn\s*,\s*tx\s*,\s*null\s*,\s*commitTransaction\s*,\s*TimeSpan\.Zero\s*\)\.ConfigureAwait\(false\)\s*;
\s*\}\s*catch\s*\{\s*if\s*\(\s*!transactionCommitted\s*\)\s*\{\s*try\s*\{\s*tx\.Rollback\(\)\s*;\s*\}\s*catch\s*\{\s*\}\s*\}\s*throw\s*;\s*\}\s*\}\s*\z
'@
    return [regex]::IsMatch((Remove-CSharpComments $slice), $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnorePatternWhitespace)
}

function Assert-RejectedMutation([string]$name, [string]$original, [string]$mutation, [scriptblock]$accept) {
    if ($original -eq $mutation -or (& $accept $mutation)) {
        Fail "F6 negative fixture $name must change the source and be rejected"
    } else { Pass "F6 negative fixture rejects $name" }
}

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs",
    "src/Win7POS.Data/Repositories/SaleStockMovementWriter.cs",
    "src/Win7POS.Data/Repositories/SaleReversalWriter.cs",
    "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs",
    "src/Win7POS.Data/SqliteConnectionFactory.cs",
    "src/Win7POS.Data/Reports/SalesReportSnapshotCoordinator.cs",
    "tests/Win7POS.Core.Tests/Data/SaleTransactionWriterTests.cs",
    "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$saleRepository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$writer = Read-Text "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs"
$stockWriter = Read-Text "src/Win7POS.Data/Repositories/SaleStockMovementWriter.cs"
$reversalWriter = Read-Text "src/Win7POS.Data/Repositories/SaleReversalWriter.cs"
$salesOutbox = Read-Text "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs"
$connectionFactory = Read-Text "src/Win7POS.Data/SqliteConnectionFactory.cs"
$snapshotCoordinator = Read-Text "src/Win7POS.Data/Reports/SalesReportSnapshotCoordinator.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleTransactionWriterTests.cs"
$connectionPolicyTests = Read-Text "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs"
$commitHelpers = @(Get-MethodSlices $writer "private" "CommitWithPublicationAsync")
$coordinatorCommit = Get-CoordinatorCommitSlice $snapshotCoordinator
if ($commitHelpers.Count -ne 1 -or -not (Test-PublicationCommitProtocol $commitHelpers[0] $coordinatorCommit)) {
    Fail "F6 publication helper must hold its one gate around revision read, exact current-guard/callback commit and post-commit stamp publication without other work"
} else {
    Pass "F6 publication helper binds revision read, final guard and callback commit to its one publication gate"
}

$collaboratorMarkers = @(
    "private readonly SaleReadRepository _reads",
    "private readonly SaleLineReadRepository _lineReads",
    "private readonly SaleStockMovementWriter _stockMovementWriter",
    "private readonly SaleReversalWriter _reversalWriter",
    "private readonly SalesSyncOutboxRepository _salesSyncOutbox",
    "private readonly SaleTransactionWriter _transactionWriter",
    "new SaleTransactionWriter\(\s*factory\s*,\s*_stockMovementWriter\s*,\s*_reversalWriter\s*,\s*_salesSyncOutbox\s*\)"
)
$missingCollaborators = @($collaboratorMarkers | Where-Object { $saleRepository -notmatch $_ })
if ($missingCollaborators.Count -gt 0) {
    Fail "SaleRepository must retain F1-F5 collaborators and construct F6 from those exact instances: $($missingCollaborators -join ', ')"
} else {
    Pass "SaleRepository retains F1-F5 collaborators and constructs F6 from their existing instances"
}

if ($writer -notmatch "internal sealed class SaleTransactionWriter" -or
    $writer -notmatch "internal SaleTransactionWriter\(\s*SqliteConnectionFactory factory\s*,\s*SaleStockMovementWriter stockMovementWriter\s*,\s*SaleReversalWriter reversalWriter\s*,\s*SalesSyncOutboxRepository salesSyncOutbox\s*\)" -or
    $writer -match "\bnew\s+SaleRepository\s*\(") {
    Fail "SaleTransactionWriter must receive the F1-F5 collaborators and never reconstruct SaleRepository"
} else {
    Pass "SaleTransactionWriter receives the existing F1-F5 writers without reconstructing the facade"
}

$facadeContracts = @(
    [pscustomobject]@{ Access = "internal"; Facade = "InsertSaleAsync"; Writer = "InsertSaleAsync"; Forwarding = @("sale\s*,\s*lines") },
    [pscustomobject]@{ Access = "public"; Facade = "InsertAuthorizedSaleAsync"; Writer = "InsertSaleAsync"; Forwarding = @("sale\s*,\s*lines\s*,\s*authorizationCommitGuard") },
    [pscustomobject]@{ Facade = "MarkPdfPrintedAsync"; Writer = "MarkPdfPrintedAsync"; Forwarding = @("saleId") },
    [pscustomobject]@{ Facade = "InsertRefundSaleAsync"; Writer = "InsertRefundSaleAsync"; Forwarding = @(
            "req\s*,\s*totalMinor\s*,\s*paidCashMinor\s*,\s*paidCardMinor\s*,\s*changeMinor",
            "conn\s*,\s*tx\s*,\s*req\s*,\s*totalMinor\s*,\s*paidCashMinor\s*,\s*paidCardMinor\s*,\s*changeMinor") },
    [pscustomobject]@{ Facade = "InsertRefundOrVoidAsync"; Writer = "InsertRefundOrVoidAsync"; Forwarding = @("refundSale\s*,\s*refundLines\s*,\s*originalSaleIdToMarkVoided\s*,\s*auditAction\s*,\s*auditDetailsFactory") },
    [pscustomobject]@{ Facade = "InsertAuthorizedRefundOrVoidAsync"; Writer = "InsertRefundOrVoidAsync"; Forwarding = @("refundSale\s*,\s*refundLines\s*,\s*originalSaleIdToMarkVoided\s*,\s*auditAction\s*,\s*auditDetailsFactory\s*,\s*authorizationCommitGuard") },
    [pscustomobject]@{ Facade = "InsertSaleLinesAsync"; Writer = "InsertSaleLinesAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*lines") },
    [pscustomobject]@{ Facade = "EnsureClientSaleIdAsync"; Writer = "EnsureClientSaleIdAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*saleId") },
    [pscustomobject]@{ Facade = "ApplyLocalStockMovementsAsync"; Writer = "ApplyLocalStockMovementsAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*sale\s*,\s*lines") },
    [pscustomobject]@{ Facade = "EnqueueSalesSyncOutboxAsync"; Writer = "EnqueueSalesSyncOutboxAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*saleId\s*,\s*clientSaleId") }
)
Assert-StrictFacadeDelegations $saleRepository $writer $facadeContracts
$voidFacades = @(Get-MethodSlices $saleRepository "public" "MarkSaleVoidedAsync" | Where-Object { $_ -notmatch 'SqliteConnection conn' })
if ($voidFacades.Count -ne 1 -or
    $voidFacades[0] -notmatch '(?s)=>\s*_transactionWriter\.MarkSaleVoidedAsync\(\s*originalSaleId\s*,\s*refundSaleId\s*,\s*nowMs\s*\)\s*;\s*\z') {
    Fail "F6 standalone void facade must forward its exact arguments only to the transaction writer"
} else { Pass "F6 standalone void facade delegates its complete transaction lifecycle" }

$expectedWriterMethods = @{
    "InsertSaleAsync" = 1
    "MarkPdfPrintedAsync" = 1
    "MarkSaleVoidedAsync" = 1
    "InsertRefundSaleAsync" = 2
    "InsertRefundOrVoidAsync" = 1
    "InsertSaleLinesAsync" = 1
    "EnsureClientSaleIdAsync" = 1
    "ApplyLocalStockMovementsAsync" = 1
    "EnqueueSalesSyncOutboxAsync" = 1
}
$writerMethodPattern = '(?m)^\s*internal\s+(?:static\s+)?(?:async\s+)?Task(?:<[^\r\n]+>)?\s+([A-Za-z0-9_]+)\s*\('
$actualWriterMethods = @(
    [regex]::Matches($writer, $writerMethodPattern) |
    ForEach-Object { $_.Groups[1].Value })
$surfaceFailures = New-Object System.Collections.Generic.List[string]
foreach ($method in $expectedWriterMethods.Keys) {
    $count = @($actualWriterMethods | Where-Object { $_ -eq $method }).Count
    if ($count -ne $expectedWriterMethods[$method]) {
        $surfaceFailures.Add("$method=$count expected=$($expectedWriterMethods[$method])") | Out-Null
    }
}
$unexpectedWriterMethods = @($actualWriterMethods | Where-Object {
        -not $expectedWriterMethods.ContainsKey($_)
    } | Sort-Object -Unique)
if ($surfaceFailures.Count -gt 0 -or $unexpectedWriterMethods.Count -gt 0) {
    Fail "SaleTransactionWriter must expose exactly the F6 write surface. $($surfaceFailures -join '; ') unexpected=$($unexpectedWriterMethods -join ', ')"
} else {
    Pass "SaleTransactionWriter exposes exactly the F6 persistence surface"
}

$standaloneVoidWriters = @(Get-MethodSlices $writer "internal" "MarkSaleVoidedAsync")
if ($standaloneVoidWriters.Count -ne 1 -or -not (Test-StandaloneVoidRoute $standaloneVoidWriters[0])) {
    Fail "F6 standalone void must own exactly mark, original proof refresh and one flagged publication commit in the same transaction"
} else { Pass "F6 standalone void atomically marks, refreshes proof and publishes its flagged transaction" }

$saleWriters = @(Get-MethodSlices $writer "internal" "InsertSaleAsync")
if ($saleWriters.Count -ne 1) {
    Fail "F6 full-sale writer must have exactly one implementation"
} else {
    $saleWriter = $saleWriters[0]
    Assert-OrderedMarkers $saleWriter "F6 full-sale writer" @(
        [pscustomobject]@{ Name = "sale receipt validation"; Pattern = "SalesReceiptContentPolicy\.EnsureValid\(sale\s*,\s*lines\)" },
        [pscustomobject]@{ Name = "receipt snapshot validation"; Pattern = "ReceiptDocumentPolicy\.EnsureValidSnapshotJson\(sale\.ReceiptShopSnapshotJson\)" },
        [pscustomobject]@{ Name = "connection open"; Pattern = "_factory\.OpenAsync\(\)" },
        [pscustomobject]@{ Name = "authorized exclusive commit fence"; Pattern = "AcquireExclusiveCommitFence\(conn\)" },
        [pscustomobject]@{ Name = "transaction begin"; Pattern = "conn\.BeginTransaction\(\)" },
        [pscustomobject]@{ Name = "default-kind guard"; Pattern = "if \(sale\.Kind == 0\)" },
        [pscustomobject]@{ Name = "default sale kind"; Pattern = "sale\.Kind\s*=\s*\(int\)SaleKind\.Sale" },
        [pscustomobject]@{ Name = "ordinary-kind guard"; Pattern = "if \(sale\.Kind == \(int\)SaleKind\.Sale\)" },
        [pscustomobject]@{ Name = "ordinary sale-safe barrier"; Pattern = "RequireSaleSafeForOrdinarySaleAsync\(conn\s*,\s*tx\)" },
        [pscustomobject]@{ Name = "reversal boundary"; Pattern = "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync\(conn\s*,\s*tx\s*,\s*sale\s*,\s*lines\)" },
        [pscustomobject]@{ Name = "sale header"; Pattern = "INSERT\s+INTO\s+sales\(" },
        [pscustomobject]@{ Name = "client sale id"; Pattern = "EnsureClientSaleIdAsync\(conn\s*,\s*tx\s*,\s*saleId\)" },
        [pscustomobject]@{ Name = "sale line"; Pattern = "InsertSaleLineAsync\(conn\s*,\s*tx\s*,\s*l\.line\)" },
        [pscustomobject]@{ Name = "stock movements"; Pattern = "ApplyLocalStockMovementsAsync\(conn\s*,\s*tx\s*,\s*sale\s*,\s*lines\)" },
        [pscustomobject]@{ Name = "outbox enqueue"; Pattern = "EnqueueSalesSyncOutboxAsync\(conn\s*,\s*tx\s*,\s*saleId\s*,\s*sale\.ClientSaleId\)" },
        [pscustomobject]@{ Name = "authorized publication commit and budget"; Pattern = "CommitWithPublicationAsync\(\s*conn\s*,\s*tx\s*,\s*authorizationCommitGuard\s*,\s*commitTransaction\s*,\s*SqliteConnectionFactory\.DurableCommitSafetyBudget\s*\)" }
    )
    if (-not (Test-GuardedCommitRoute $saleWriter -ExactReplay)) {
        Fail "F6 full-sale writer must retain guarded durable commit, zero-budget exact replay and its single post-commit completion flag"
    } else {
        Pass "F6 full-sale writer preserves exact replay and guarded durable publication through its single completion delegate"
    }
    if ($saleWriter -notmatch "catch\s*\{[\s\S]*if \(!transactionCommitted\)[\s\S]*tx\.Rollback\(\)[\s\S]*throw;\s*\}" -or
        $saleWriter -notmatch "if \(sale\.Kind == \(int\)SaleKind\.Sale\)" -or
        $saleWriter -notmatch "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync" -or
        $saleWriter -notmatch "authorizationCommitGuard\s*==\s*null[\s\S]{0,180}AcquireExclusiveCommitFence") {
        Fail "F6 full-sale writer must retain sale-safe/reversal validation and rollback"
    } else {
        Pass "F6 full-sale writer retains sale-safe/reversal validation and rollback"
    }
    $saleDapper = @(Get-CSharpDapperAsyncInvocations $saleWriter)
    $saleDapperWithoutTx = @($saleDapper | Where-Object {
            -not (Test-CSharpInvocationUsesTransaction $_.Text "tx")
        })
    if ($saleDapper.Count -ne 1 -or $saleDapperWithoutTx.Count -gt 0) {
        Fail "F6 full-sale header write must be the one direct Dapper command on the active transaction. dapper=$($saleDapper.Count) missingTx=$($saleDapperWithoutTx.Count)"
    } else {
        Pass "F6 full-sale header write uses the active transaction"
    }
}

if ($connectionFactory -notmatch "ExpectedLockingMode\s*=\s*`"normal`"" -or
    $connectionFactory -notmatch "DurableCommitSafetyBudget\s*=[\s\S]{0,120}TimeSpan\.FromSeconds\(10\)" -or
    $connectionFactory -notmatch "AcquireExclusiveCommitFence\([\s\S]{0,1600}BEGIN EXCLUSIVE;[\s\S]{0,500}COMMIT;" -or
    $connectionFactory -notmatch "TryRestoreNormalLockingMode[\s\S]*SetLockingMode\(connection,\s*`"NORMAL`"\)[\s\S]*TouchMainDatabase[\s\S]*main\.sqlite_schema" -or
    $connectionFactory -notmatch "ClearPool\(connection\)[\s\S]{0,220}ClearAllPools\(\)") {
    Fail "SQLite commit fence must drain readers, retain a separate headroom budget, restore NORMAL and quarantine poisoned pools"
} else {
    Pass "SQLite commit fence drains readers and safely restores pooled connections"
}

$readerExpiryTests = @(
    Get-MethodSlices $tests "public" "AuthorizedSale_SharedReaderCannotCarryValidatedCommitPastExactExpiry")
$connectionFenceTests = @(
    Get-MethodSlices $connectionPolicyTests "public" "ExclusiveCommitFence_DrainsSharedReader_PreservesProviderTransaction_AndRestoresNormalMode")
if ($readerExpiryTests.Count -ne 1 -or
    $connectionFenceTests.Count -ne 1 -or
    $readerExpiryTests[0] -notmatch "BeginTransaction\(deferred:\s*true\)[\s\S]*WaitForPendingExclusiveLockAsync[\s\S]*guard\.Expire\(\)[\s\S]*SELECT COUNT\(1\) FROM sale_lines[\s\S]*local_stock_movements[\s\S]*sales_sync_outbox" -or
    $connectionFenceTests[0] -notmatch "AcquireExclusiveCommitFence[\s\S]*WaitForSqliteBusyAsync[\s\S]*PRAGMA main\.locking_mode[\s\S]*readerBlockedBeforeProviderTransaction[\s\S]*BeginTransaction\(\)[\s\S]*readerBlockedAfterProviderCommit") {
    Fail "reader-held expiry and provider transaction fence regressions are incomplete"
} else {
    Pass "reader-held expiry and provider transaction fence regressions are present"
}

$pdfWriters = @(Get-MethodSlices $writer "internal" "MarkPdfPrintedAsync")
if ($pdfWriters.Count -ne 1) {
    Fail "F6 PDF-printed writer must have exactly one implementation"
} else {
    $pdfWriter = $pdfWriters[0]
    $pdfDapper = @(Get-CSharpDapperAsyncInvocations $pdfWriter | Where-Object {
            $_.Method -eq "ExecuteAsync"
        })
    if ($pdfWriter -notmatch "using var conn = _factory\.Open\(\)" -or
        $pdfWriter -notmatch "await conn\.ExecuteAsync\(" -or
        $pdfWriter -notmatch "UPDATE sales SET pdf_printed = 1 WHERE id = @saleId" -or
        $pdfWriter -notmatch "\.ConfigureAwait\(false\)" -or
        $pdfWriter -match "return\s+conn\.ExecuteAsync|BeginTransaction|\.Commit\s*\(|\.Rollback\s*\(" -or
        $pdfDapper.Count -ne 1) {
        Fail "F6 PDF-printed writer must await exactly its narrow UPDATE before disposing the connection"
    } else {
        Pass "F6 PDF-printed writer awaits exactly its narrow UPDATE before disposing the connection"
    }
}

$refundOrVoidWriters = @(Get-MethodSlices $writer "internal" "InsertRefundOrVoidAsync")
if ($refundOrVoidWriters.Count -ne 1) {
    Fail "F6 refund/void writer must have exactly one implementation"
} else {
    $refundOrVoidWriter = $refundOrVoidWriters[0]
    Assert-OrderedMarkers $refundOrVoidWriter "F6 refund/void writer" @(
        [pscustomobject]@{ Name = "refund/void kind restriction"; Pattern = "if\s*\(refundSale\.Kind\s*!=\s*\(int\)SaleKind\.Refund\s*&&\s*refundSale\.Kind\s*!=\s*\(int\)SaleKind\.Void\)" },
        [pscustomobject]@{ Name = "refund receipt validation"; Pattern = "SalesReceiptContentPolicy\.EnsureValid\(refundSale\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "refund snapshot validation"; Pattern = "ReceiptDocumentPolicy\.EnsureValidSnapshotJson\(refundSale\.ReceiptShopSnapshotJson\)" },
        [pscustomobject]@{ Name = "authorization/operator binding"; Pattern = "ValidateAuthorizationBinding\(refundSale\s*,\s*authorizationCommitGuard\)" },
        [pscustomobject]@{ Name = "connection open"; Pattern = "_factory\.Open\(\)" },
        [pscustomobject]@{ Name = "authorized exclusive commit fence"; Pattern = "AcquireExclusiveCommitFence\(conn\)" },
        [pscustomobject]@{ Name = "transaction begin"; Pattern = "conn\.BeginTransaction\(\)" },
        [pscustomobject]@{ Name = "initial authorization demand"; Pattern = "authorizationCommitGuard\?\.DemandStillValid\(\)" },
        [pscustomobject]@{ Name = "reversal boundary"; Pattern = "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync\(conn\s*,\s*tx\s*,\s*refundSale\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "refund header"; Pattern = "INSERT\s+INTO\s+sales\(" },
        [pscustomobject]@{ Name = "client sale id"; Pattern = "EnsureClientSaleIdAsync\(conn\s*,\s*tx\s*,\s*saleId\)" },
        [pscustomobject]@{ Name = "refund line ownership"; Pattern = "line\.SaleId\s*=\s*saleId" },
        [pscustomobject]@{ Name = "line insert and budget"; Pattern = "InsertSaleLinesAsync\(conn\s*,\s*tx\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "stock movements"; Pattern = "ApplyLocalStockMovementsAsync\(conn\s*,\s*tx\s*,\s*refundSale\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "outbox enqueue"; Pattern = "EnqueueSalesSyncOutboxAsync\(conn\s*,\s*tx\s*,\s*saleId\s*,\s*refundSale\.ClientSaleId\)" },
        [pscustomobject]@{ Name = "audit"; Pattern = "new\s+AuditLogRepository\(\)" },
        [pscustomobject]@{ Name = "void mark"; Pattern = "_reversalWriter\s*\.\s*MarkSaleVoidedAsync\(\s*conn\s*,\s*tx" },
        [pscustomobject]@{ Name = "original proof refresh"; Pattern = "NativeSalesOwnershipRepository\.RefreshForSaleInTransactionAsync\(\s*conn\s*,\s*tx\s*,\s*originalSaleIdToMarkVoided\.Value\s*,\s*CancellationToken\.None\s*\)" },
        [pscustomobject]@{ Name = "authorized publication commit and budget"; Pattern = "CommitWithPublicationAsync\(\s*conn\s*,\s*tx\s*,\s*authorizationCommitGuard\s*,\s*commitTransaction\s*,\s*SqliteConnectionFactory\.DurableCommitSafetyBudget\s*\)" }
    )
    if (-not (Test-GuardedCommitRoute $refundOrVoidWriter)) {
        Fail "F6 refund/void writer must commit only through the conditional authorized delegate, with its fence, headroom budget and durable-completion flag"
    } else {
        Pass "F6 refund/void writer retains the conditional commit fence, guard budget and single durable commit delegate"
    }
    if ($refundOrVoidWriter -notmatch "catch\s*\{\s*if\s*\(\s*!transactionCommitted\s*\)\s*\{\s*try\s*\{\s*tx\.Rollback\(\)\s*;\s*\}\s*catch\s*\{[^{}]*\}\s*\}\s*throw\s*;\s*\}" -or
        [regex]::Matches($refundOrVoidWriter, "\btx\s*\.\s*Rollback\s*\(").Count -ne 1) {
        Fail "F6 refund/void writer must roll back all uncommitted persistence effects together and preserve the original failure"
    } else {
        Pass "F6 refund/void writer rolls back all uncommitted persistence effects together and preserves the original failure"
    }
    $refundDapper = @(Get-CSharpDapperAsyncInvocations $refundOrVoidWriter)
    $refundDapperWithoutTx = @($refundDapper | Where-Object {
            -not (Test-CSharpInvocationUsesTransaction $_.Text "tx")
        })
    if ($refundDapper.Count -ne 1 -or $refundDapperWithoutTx.Count -gt 0) {
        Fail "F6 refund/void header write must be the one direct Dapper command on the active transaction. dapper=$($refundDapper.Count) missingTx=$($refundDapperWithoutTx.Count)"
    } else {
        Pass "F6 refund/void header write uses the active transaction"
    }
}

$authorizedReversalFacades = @(Get-MethodSlices $saleRepository "public" "InsertAuthorizedRefundOrVoidAsync")
$bindingDeclaration = [regex]::Match($writer, "\bprivate\s+static\s+void\s+ValidateAuthorizationBinding\s*\(")
$bindingSlice = ""
if ($bindingDeclaration.Success) {
    $bindingBody = Find-CSharpMethodBodyStart $writer $bindingDeclaration.Index
    if ($null -ne $bindingBody -and $bindingBody.Kind -eq "block") {
        $bindingEnd = Find-CSharpMatchingDelimiter $writer $bindingBody.Index '{' '}'
        if ($bindingEnd -ge $bindingBody.Index) {
            $bindingSlice = $writer.Substring($bindingBody.Index, $bindingEnd - $bindingBody.Index + 1)
        }
    }
}
$bindingMarkers = @(
    "if\s*\(authorizationCommitGuard\s*==\s*null\)\s*return\s*;",
    "saleKind\s*!=\s*authorizationCommitGuard\.AuthorizedSaleKind",
    "!sale\.OperatorId\.HasValue",
    "sale\.OperatorId\.Value\s*!=\s*authorizationCommitGuard\.OperatorId",
    "authorizationCommitGuard\.OperatorId\s*<=\s*0",
    "authorizationCommitGuard\.AuthorizationEpoch\s*<\s*0",
    "string\.IsNullOrWhiteSpace\(\s*authorizationCommitGuard\.GenerationFingerprint\)",
    "string\.IsNullOrWhiteSpace\(\s*authorizationCommitGuard\.GenerationId\)",
    "string\.IsNullOrWhiteSpace\(\s*authorizationCommitGuard\.ShopId\)\s*&&\s*string\.IsNullOrWhiteSpace\(\s*authorizationCommitGuard\.ShopCode\)",
    "string\.IsNullOrWhiteSpace\(\s*authorizationCommitGuard\.ShopDeviceId\)",
    "string\.IsNullOrWhiteSpace\(\s*authorizationCommitGuard\.StaffId\)",
    "authorizationCommitGuard\.StaffCredentialVersion\s*<\s*0"
)
$missingBindingMarkers = @($bindingMarkers | Where-Object { $bindingSlice -notmatch $_ })
if ($authorizedReversalFacades.Count -ne 1 -or
    $authorizedReversalFacades[0] -notmatch "if\s*\(authorizationCommitGuard\s*==\s*null\)\s*throw\s+new\s+ArgumentNullException\(nameof\(authorizationCommitGuard\)\)" -or
    $missingBindingMarkers.Count -gt 0 -or
    $refundOrVoidWriter -notmatch "INSERT\s+INTO\s+sales\([^)]*\boperator_id\b[^)]*\)\s*VALUES\([^)]*@OperatorId\b") {
    Fail "F6 authorized refund/void must require a guard and persist its validated kind/operator/shop/generation/staff binding. missing=$($missingBindingMarkers -join ', ')"
} else {
    Pass "F6 authorized refund/void requires a guard and preserves the validated kind/operator/shop/generation/staff binding"
}

$refundSaleWriters = @(Get-MethodSlices $writer "internal" "InsertRefundSaleAsync")
$standaloneRefund = @($refundSaleWriters | Where-Object { $_ -match "RefundCreateRequest req" -and $_ -notmatch "SqliteConnection conn" })
$callerOwnedRefund = @($refundSaleWriters | Where-Object { $_ -match "SqliteConnection conn" -and $_ -match "SqliteTransaction tx" })
if ($refundSaleWriters.Count -ne 2 -or $standaloneRefund.Count -ne 1 -or $callerOwnedRefund.Count -ne 1) {
    Fail "F6 legacy refund writer must retain standalone and caller-owned overloads"
} else {
    Assert-OrderedMarkers $standaloneRefund[0] "F6 standalone legacy refund" @(
        [pscustomobject]@{ Name = "legacy refund validation"; Pattern = "SalesReceiptContentPolicy\.EnsureValidSaleReason\(req\.Reason\)" },
        [pscustomobject]@{ Name = "connection open"; Pattern = "_factory\.Open\(\)" },
        [pscustomobject]@{ Name = "transaction begin"; Pattern = "conn\.BeginTransaction\(\)" },
        [pscustomobject]@{ Name = "caller-owned forward"; Pattern = "InsertRefundSaleAsync\(\s*conn\s*,\s*tx\s*,\s*req" },
        [pscustomobject]@{ Name = "ownership proof"; Pattern = "NativeSalesOwnershipRepository\.RefreshForSaleInTransactionAsync\(conn, tx, saleId, CancellationToken\.None\)" },
        [pscustomobject]@{ Name = "publication commit"; Pattern = "CommitWithPublicationAsync\(conn, tx, null, tx\.Commit, TimeSpan\.Zero\)" }
    )
    if ($standaloneRefund[0] -notmatch "catch\s*\{\s*tx\.Rollback\(\)\s*;\s*throw;\s*\}") {
        Fail "F6 standalone legacy refund must roll back on failure"
    } else {
        Pass "F6 standalone legacy refund retains its rollback boundary"
    }
    Assert-CallerOwnedSlice $callerOwnedRefund[0] "F6 caller-owned legacy refund" @(
        "SELECT kind FROM sales WHERE id = @originalSaleId",
        "INSERT\s+INTO\s+sales\("
    )
}

$lineWriters = @(Get-MethodSlices $writer "internal" "InsertSaleLinesAsync")
if ($lineWriters.Count -ne 1) {
    Fail "F6 caller-owned line writer must have exactly one implementation"
} else {
    Assert-CallerOwnedSlice $lineWriters[0] "F6 caller-owned line writer" @(
        "ReferenceEquals\(tx\.Connection\s*,\s*conn\)",
        "SalesReceiptContentPolicy\.EnsureValidLines\(lines\)",
        "SaleLineReadRepository\s*\.\s*EnsureStoredLineBudgetAsync\(conn\s*,\s*tx\s*,\s*group\.Key\)",
        "SalesReceiptContentPolicy\.EnsureCumulativeLineBudget\(",
        "InsertSaleLineAsync\(conn\s*,\s*tx\s*,\s*line\)"
    ) -AllowDelegatedDatabaseWork
}

$clientIdWriters = @(Get-MethodSlices $writer "internal" "EnsureClientSaleIdAsync")
if ($clientIdWriters.Count -ne 1) {
    Fail "F6 caller-owned client-ID writer must have exactly one implementation"
} else {
    Assert-CallerOwnedSlice $clientIdWriters[0] "F6 caller-owned client-ID writer" @(
        "SELECT client_sale_id FROM sales WHERE id = @saleId",
        "BuildClientSaleId\(saleId\)",
        "UPDATE sales SET client_sale_id = @clientSaleId WHERE id = @saleId"
    )
}

$lineInsertHelpers = @(Get-MethodSlices $writer "private" "InsertSaleLineAsync")
if ($lineInsertHelpers.Count -ne 1) {
    Fail "F6 line insert helper must have exactly one implementation"
} else {
    Assert-CallerOwnedSlice $lineInsertHelpers[0] "F6 line insert helper" @(
        "INSERT\s+INTO\s+sale_lines\("
    )
}

$stockFacades = @(Get-MethodSlices $writer "internal" "ApplyLocalStockMovementsAsync")
if ($stockFacades.Count -ne 1 -or
    $stockFacades[0] -notmatch "EnsureClientSaleIdAsync\(conn\s*,\s*tx\s*,\s*sale\.Id\)" -or
    $stockFacades[0] -notmatch "_stockMovementWriter\s*\.\s*ApplyAsync\(conn\s*,\s*tx\s*,\s*sale\s*,\s*lines\s*,\s*clientSaleId\)" -or
    $stockFacades[0] -match "local_stock_movements|UPDATE\s+product_meta") {
    Fail "F6 stock facade must preserve F3 fallback then delegate to the F3 writer without ledger ownership"
} else {
    Pass "F6 stock facade preserves F3 client-ID fallback and delegates ledger ownership"
}

$outboxFacades = @(Get-MethodSlices $writer "internal" "EnqueueSalesSyncOutboxAsync")
if ($outboxFacades.Count -ne 1 -or
    $outboxFacades[0] -notmatch "BuildClientSaleId\(saleId\)" -or
    $outboxFacades[0] -notmatch "clientSaleId\.Trim\(\)" -or
    $outboxFacades[0] -notmatch "_salesSyncOutbox\s*\.\s*EnqueueAsync\(conn\s*,\s*tx\s*,\s*saleId\s*,\s*normalizedClientSaleId\)" -or
    $outboxFacades[0] -match "INSERT\s+OR\s+IGNORE\s+INTO\s+sales_sync_outbox|SerializeCanonical|Sha256Hex") {
    Fail "F6 outbox facade must preserve client-ID normalization and delegate immutable payload ownership to F4"
} else {
    Pass "F6 outbox facade preserves normalization and delegates immutable payload ownership to F4"
}

$forbiddenWriterOwnership = @(
    "\bnew\s+SaleRepository\s*\(",
    "\bSaleReadRepository\b",
    "\bLastSalesAsync\b",
    "\bGetSalesBetweenAsync\b",
    "\bGetDailySummaryAsync\b",
    "\bGetByIdAsync\b",
    "\bGetLinesBySaleIdAsync\b",
    "\bSerializeCanonical\b",
    "\bSha256Hex\b",
    "\bPosReversalEconomicsReader\b",
    "\bReversalEconomicsPolicy\b",
    "\bINSERT\s+OR\s+IGNORE\s+INTO\s+sales_sync_outbox\b",
    "\b(?:INSERT\s+OR\s+IGNORE\s+INTO|UPDATE)\s+local_stock_movements\b",
    "\bUPDATE\s+product_meta\b"
) | Where-Object { $writer -match $_ }
if ($forbiddenWriterOwnership.Count -gt 0) {
    Fail "F6 writer must exclude F1 reads and F3/F4 implementation ownership: $($forbiddenWriterOwnership -join ', ')"
} else {
    Pass "F6 writer excludes F1 reads and F3/F4 implementation details"
}

$testFailures = New-Object System.Collections.Generic.List[string]

# Pure source mutations exercise each new boundary without executing a writer,
# opening SQLite or weakening the existing persistence/caller-ownership checks.
if ($commitHelpers.Count -eq 1) {
    $helper = $commitHelpers[0]
    $acceptHelper = { param($candidate) Test-PublicationCommitProtocol $candidate $coordinatorCommit }
    foreach ($fixture in @(
        @{ Name = 'final guard bypass'; From = 'if (guard == null)'; To = 'if (true)' },
        @{ Name = 'lost safety budget'; From = 'guard.CommitIfStillValid(safety, commit)'; To = 'guard.CommitIfStillValid(TimeSpan.Zero, commit)' },
        @{ Name = 'swapped commit callback'; From = 'guard.CommitIfStillValid(safety, commit)'; To = 'guard.CommitIfStillValid(safety, () => { })' },
        @{ Name = 'extra commit before publication gate'; From = 'var coordinator ='; To = 'commit(); var coordinator =' },
        @{ Name = 'extra pre-demand changes guard contract'; From = 'var coordinator ='; To = 'guard?.DemandStillValid(); var coordinator =' },
        @{ Name = 'missing publication gate'; From = 'coordinator.EnterPublicationAsync'; To = 'coordinator.EnterOtherGateAsync' }
    )) {
        Assert-RejectedMutation $fixture.Name $helper ($helper.Replace($fixture.From, $fixture.To)) $acceptHelper
    }
    $acceptCoordinator = { param($candidate) Test-PublicationCommitProtocol $helper $candidate }
    Assert-RejectedMutation 'commit before revision read' $coordinatorCommit ($coordinatorCommit.Replace('var row =', 'commit(); var row =')) $acceptCoordinator
    Assert-RejectedMutation 'revision outside supplied transaction' $coordinatorCommit ($coordinatorCommit.Replace('transaction: transaction', 'transaction: null')) $acceptCoordinator
    $publishBeforeCommit = [regex]::Replace($coordinatorCommit, 'commit\(\);\s*(PublishCommitted\([^;]+;)', '$1 commit();')
    Assert-RejectedMutation 'stamp published before durable commit' $coordinatorCommit $publishBeforeCommit $acceptCoordinator
}
if ($saleWriters.Count -eq 1) {
    $acceptSale = { param($candidate) Test-GuardedCommitRoute $candidate -ExactReplay }
    Assert-RejectedMutation 'new sale loses durable headroom' $saleWriter ($saleWriter.Replace('SqliteConnectionFactory.DurableCommitSafetyBudget', 'TimeSpan.Zero')) $acceptSale
    Assert-RejectedMutation 'exact replay loses zero headroom' $saleWriter ($saleWriter.Replace('commitTransaction, TimeSpan.Zero', 'commitTransaction, SqliteConnectionFactory.DurableCommitSafetyBudget')) $acceptSale
    Assert-RejectedMutation 'current sale guard omitted' $saleWriter ($saleWriter.Replace('tx, authorizationCommitGuard, commitTransaction', 'tx, null, commitTransaction')) $acceptSale
}
if ($refundOrVoidWriters.Count -eq 1) {
    $acceptReversal = { param($candidate) Test-GuardedCommitRoute $candidate }
    Assert-RejectedMutation 'reversal callback bypasses completion flag' $refundOrVoidWriter ($refundOrVoidWriter.Replace('authorizationCommitGuard, commitTransaction', 'authorizationCommitGuard, tx.Commit')) $acceptReversal
    $earlyFlag = [regex]::Replace($refundOrVoidWriter, 'tx\.Commit\(\);\s*transactionCommitted = true;', 'transactionCommitted = true; tx.Commit();')
    Assert-RejectedMutation 'completion flag precedes durable commit' $refundOrVoidWriter $earlyFlag $acceptReversal
    Assert-RejectedMutation 'extra reversal durable commit' $refundOrVoidWriter ($refundOrVoidWriter.Replace('tx.Commit();', 'tx.Commit(); tx.Commit();')) $acceptReversal
    Assert-RejectedMutation 'rollback after successful commit' $refundOrVoidWriter ($refundOrVoidWriter.Replace('if (!transactionCommitted)', 'if (transactionCommitted)')) $acceptReversal
    Assert-RejectedMutation 'complete void refreshes reversal instead of original proof' $refundOrVoidWriter ($refundOrVoidWriter.Replace('tx, originalSaleIdToMarkVoided.Value, CancellationToken.None', 'tx, saleId, CancellationToken.None')) $acceptReversal
    $finalStatement = [regex]::Match($refundOrVoidWriter, 'await\s+CommitWithPublicationAsync\([^;]+;\s*return saleId;').Value
    $earlyCommit = $refundOrVoidWriter.Replace($finalStatement, '').Replace('await EnqueueSalesSyncOutboxAsync(', $finalStatement + ' await EnqueueSalesSyncOutboxAsync(')
    Assert-RejectedMutation 'commit moved before outbox persistence' $refundOrVoidWriter $earlyCommit $acceptReversal
}
if ($standaloneVoidWriters.Count -eq 1) {
    $standaloneVoid = $standaloneVoidWriters[0]
    $acceptVoid = { param($candidate) Test-StandaloneVoidRoute $candidate }
    Assert-RejectedMutation 'standalone void refreshes wrong proof' $standaloneVoid ($standaloneVoid.Replace('tx, originalSaleId, CancellationToken.None', 'tx, refundSaleId, CancellationToken.None')) $acceptVoid
    Assert-RejectedMutation 'standalone void bypasses publication' $standaloneVoid ($standaloneVoid.Replace('await CommitWithPublicationAsync(conn, tx, null, commitTransaction, TimeSpan.Zero).ConfigureAwait(false);', 'commitTransaction();')) $acceptVoid
}

 $testContracts = @(
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_StandaloneVoidPublishesChangedOriginalProof"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyStandaloneVoidProofAsync")
        Helper = "VerifyStandaloneVoidProofAsync"
        HelperMarkers = @("MarkSaleVoidedAsync", "source_fingerprint", "data_revision", "Assert.AreNotEqual(beforeFingerprint, afterFingerprint)", "afterRevision > beforeRevision")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_StandaloneVoidProofFailureRollsBackMarkAndRevision"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyStandaloneVoidProofAsync")
        Helper = "VerifyStandaloneVoidProofAsync"
        HelperMarkers = @("CREATE TRIGGER fail_f6_original_proof", "Assert.ThrowsExactlyAsync<SqliteException>", "Assert.IsNull(mark)", "Assert.AreEqual(beforeRevision, afterRevision)")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_PreserveFullSalePersistenceStockOutboxAndHash"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "InsertSaleAsync", "LoadFullSaleSnapshotAsync", "PayloadHash")
        Helper = ""
        HelperMarkers = @()
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_CallerOwnedLineAndClientIdRollback"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyCallerOwnedLineAndClientIdRollbackAsync")
        Helper = "VerifyCallerOwnedLineAndClientIdRollbackAsync"
        HelperMarkers = @("factory.Open()", "conn.BeginTransaction()", "EnsureClientSaleIdAsync(conn, tx", "InsertSaleLinesAsync(conn, tx", "tx.Rollback()", "SELECT client_sale_id FROM sales", "SELECT COUNT(1) FROM sale_lines")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_CallerOwnedLegacyRefundRollback"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyCallerOwnedLegacyRefundRollbackAsync")
        Helper = "VerifyCallerOwnedLegacyRefundRollbackAsync"
        HelperMarkers = @("factory.Open()", "conn.BeginTransaction()", "InsertRefundSaleAsync(", "tx.Rollback()", "related_sale_id")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_CommitVoidAuditAndMarkAtomically"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "CommitVoidAndLoadAsync", "Audit.Action", "PayloadHash")
        Helper = "CommitVoidAndLoadAsync"
        HelperMarkers = @("InsertSaleAsync(", "InsertRefundOrVoidAsync(", "originalSaleId", "f6_void")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_RollBackVoidAuditAndMarkWhenMarkFails"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyVoidRollbackWhenMarkFailsAsync")
        Helper = "VerifyVoidRollbackWhenMarkFailsAsync"
        HelperMarkers = @("CREATE TRIGGER fail_f6_void_mark", "InsertRefundOrVoidAsync(", "sales_sync_outbox", "audit_log", "local_stock_movements")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_RejectLegacyRefundBeforeMutation"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyLegacyRefundValidationAsync")
        Helper = "VerifyLegacyRefundValidationAsync"
        HelperMarkers = @("ReceiptContentValidationException", "InsertRefundSaleAsync(", "SELECT COUNT(1) FROM sales", "sales_sync_outbox")
    },
    [pscustomobject]@{
        Name = "SaleTransactionWriter_AndSaleFacade_PreservePdfPrintedNarrowUpdate"
        BodyMarkers = @("new DirectTransactionSurface", "new FacadeTransactionSurface", "VerifyPdfPrintedNarrowUpdateAsync", "PdfPrinted")
        Helper = "VerifyPdfPrintedNarrowUpdateAsync"
        HelperMarkers = @("MarkPdfPrintedAsync(targetSaleId)", "LoadPdfSaleAsync", "Control", "Target")
    }
)
foreach ($contract in $testContracts) {
    $slices = @(Get-CSharpTestMethodSlices $tests $contract.Name)
    if ($slices.Count -ne 1) {
        $testFailures.Add("$($contract.Name) bodies=$($slices.Count)") | Out-Null
        continue
    }

    $missingBodyMarkers = @($contract.BodyMarkers | Where-Object {
            $slices[0].Text -notmatch [regex]::Escape($_)
        })
    if ($missingBodyMarkers.Count -gt 0) {
        $testFailures.Add("$($contract.Name) body missing: $($missingBodyMarkers -join ', ')") | Out-Null
        continue
    }

    if (-not [string]::IsNullOrWhiteSpace($contract.Helper)) {
        $helperSlices = @(Get-CSharpMethodSlices $tests "private" $contract.Helper)
        if ($helperSlices.Count -ne 1) {
            $testFailures.Add("$($contract.Name) helper bodies=$($helperSlices.Count)") | Out-Null
            continue
        }
        $missingHelperMarkers = @($contract.HelperMarkers | Where-Object {
                $helperSlices[0].Text -notmatch [regex]::Escape($_)
            })
        if ($missingHelperMarkers.Count -gt 0) {
            $testFailures.Add("$($contract.Name) helper missing: $($missingHelperMarkers -join ', ')") | Out-Null
        }
    }
}
if ($tests -notmatch "new SaleTransactionWriter\(" -or
    $tests -notmatch "new SaleRepository\(" -or
    $tests -notmatch "tx\.Rollback\(\)" -or
    $tests -notmatch "sales_sync_outbox" -or
    $tests -notmatch "pdf_printed" -or
    $tests -notmatch "audit_log") {
    $testFailures.Add("direct writer/facade or atomic rollback/PDF/audit/outbox evidence missing") | Out-Null
}
if ($testFailures.Count -gt 0) {
    Fail "F6 direct writer/facade persistence regression matrix is incomplete: $($testFailures -join '; ')"
} else {
    Pass "F6 direct writer/facade persistence, rollback, audit, outbox and PDF regressions are present"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
