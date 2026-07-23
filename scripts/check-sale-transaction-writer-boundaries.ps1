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
        $facadeSlices = @(Get-MethodSlices $saleRepository "public" $contract.Facade)
        $facadeCount = Get-MethodDeclarationCount $saleRepository "public" $contract.Facade
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

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs",
    "src/Win7POS.Data/Repositories/SaleStockMovementWriter.cs",
    "src/Win7POS.Data/Repositories/SaleReversalWriter.cs",
    "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs",
    "tests/Win7POS.Core.Tests/Data/SaleTransactionWriterTests.cs"
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
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleTransactionWriterTests.cs"

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
    [pscustomobject]@{ Facade = "InsertSaleAsync"; Writer = "InsertSaleAsync"; Forwarding = @("sale\s*,\s*lines") },
    [pscustomobject]@{ Facade = "MarkPdfPrintedAsync"; Writer = "MarkPdfPrintedAsync"; Forwarding = @("saleId") },
    [pscustomobject]@{ Facade = "InsertRefundSaleAsync"; Writer = "InsertRefundSaleAsync"; Forwarding = @(
            "req\s*,\s*totalMinor\s*,\s*paidCashMinor\s*,\s*paidCardMinor\s*,\s*changeMinor",
            "conn\s*,\s*tx\s*,\s*req\s*,\s*totalMinor\s*,\s*paidCashMinor\s*,\s*paidCardMinor\s*,\s*changeMinor") },
    [pscustomobject]@{ Facade = "InsertRefundOrVoidAsync"; Writer = "InsertRefundOrVoidAsync"; Forwarding = @("refundSale\s*,\s*refundLines\s*,\s*originalSaleIdToMarkVoided\s*,\s*auditAction\s*,\s*auditDetailsFactory") },
    [pscustomobject]@{ Facade = "InsertSaleLinesAsync"; Writer = "InsertSaleLinesAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*lines") },
    [pscustomobject]@{ Facade = "EnsureClientSaleIdAsync"; Writer = "EnsureClientSaleIdAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*saleId") },
    [pscustomobject]@{ Facade = "ApplyLocalStockMovementsAsync"; Writer = "ApplyLocalStockMovementsAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*sale\s*,\s*lines") },
    [pscustomobject]@{ Facade = "EnqueueSalesSyncOutboxAsync"; Writer = "EnqueueSalesSyncOutboxAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*saleId\s*,\s*clientSaleId") }
)
Assert-StrictFacadeDelegations $saleRepository $writer $facadeContracts

$expectedWriterMethods = @{
    "InsertSaleAsync" = 1
    "MarkPdfPrintedAsync" = 1
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

$saleWriters = @(Get-MethodSlices $writer "internal" "InsertSaleAsync")
if ($saleWriters.Count -ne 1) {
    Fail "F6 full-sale writer must have exactly one implementation"
} else {
    $saleWriter = $saleWriters[0]
    Assert-OrderedMarkers $saleWriter "F6 full-sale writer" @(
        [pscustomobject]@{ Name = "sale receipt validation"; Pattern = "SalesReceiptContentPolicy\.EnsureValid\(sale\s*,\s*lines\)" },
        [pscustomobject]@{ Name = "receipt snapshot validation"; Pattern = "ReceiptDocumentPolicy\.EnsureValidSnapshotJson\(sale\.ReceiptShopSnapshotJson\)" },
        [pscustomobject]@{ Name = "connection open"; Pattern = "_factory\.OpenAsync\(\)" },
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
        [pscustomobject]@{ Name = "commit"; Pattern = "tx\.Commit\(\)" }
    )
    if ($saleWriter -notmatch "catch\s*\{\s*tx\.Rollback\(\)\s*;\s*throw;\s*\}" -or
        $saleWriter -notmatch "if \(sale\.Kind == \(int\)SaleKind\.Sale\)" -or
        $saleWriter -notmatch "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync") {
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
        [pscustomobject]@{ Name = "refund receipt validation"; Pattern = "SalesReceiptContentPolicy\.EnsureValid\(refundSale\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "refund snapshot validation"; Pattern = "ReceiptDocumentPolicy\.EnsureValidSnapshotJson\(refundSale\.ReceiptShopSnapshotJson\)" },
        [pscustomobject]@{ Name = "connection open"; Pattern = "_factory\.Open\(\)" },
        [pscustomobject]@{ Name = "transaction begin"; Pattern = "conn\.BeginTransaction\(\)" },
        [pscustomobject]@{ Name = "reversal boundary"; Pattern = "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync\(conn\s*,\s*tx\s*,\s*refundSale\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "refund header"; Pattern = "INSERT\s+INTO\s+sales\(" },
        [pscustomobject]@{ Name = "client sale id"; Pattern = "EnsureClientSaleIdAsync\(conn\s*,\s*tx\s*,\s*saleId\)" },
        [pscustomobject]@{ Name = "refund line ownership"; Pattern = "line\.SaleId\s*=\s*saleId" },
        [pscustomobject]@{ Name = "line insert and budget"; Pattern = "InsertSaleLinesAsync\(conn\s*,\s*tx\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "stock movements"; Pattern = "ApplyLocalStockMovementsAsync\(conn\s*,\s*tx\s*,\s*refundSale\s*,\s*refundLines\)" },
        [pscustomobject]@{ Name = "outbox enqueue"; Pattern = "EnqueueSalesSyncOutboxAsync\(conn\s*,\s*tx\s*,\s*saleId\s*,\s*refundSale\.ClientSaleId\)" },
        [pscustomobject]@{ Name = "audit"; Pattern = "new\s+AuditLogRepository\(\)" },
        [pscustomobject]@{ Name = "void mark"; Pattern = "_reversalWriter\s*\.\s*MarkSaleVoidedAsync\(\s*conn\s*,\s*tx" },
        [pscustomobject]@{ Name = "commit"; Pattern = "tx\.Commit\(\)" }
    )
    if ($refundOrVoidWriter -notmatch "catch\s*\{\s*tx\.Rollback\(\)\s*;\s*throw;\s*\}") {
        Fail "F6 refund/void writer must roll back header, lines, stock, outbox, audit and void mark together"
    } else {
        Pass "F6 refund/void writer rolls back all persistence effects together"
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
        [pscustomobject]@{ Name = "commit"; Pattern = "tx\.Commit\(\)" }
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
 $testContracts = @(
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
