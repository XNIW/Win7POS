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
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
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

function Assert-ExactFacadeDelegations(
    [string]$saleRepository,
    [string]$writer,
    [object[]]$contracts) {
    $failures = New-Object System.Collections.Generic.List[string]
    $facadeLeakPatterns = @(
        "\b(?:conn|_factory)\s*\.\s*(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async\b",
        "\b(?:conn|tx)\s*\.\s*(?:Open|OpenAsync|BeginTransaction|Commit|Rollback|Close|CloseAsync|Dispose|DisposeAsync)\s*\(",
        "(?m)^\s*using\s+",
        "(?i)\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|UPDATE|DELETE\s+FROM)\s+(?:sales|sale_lines|sales_sync_outbox)\b",
        "\b(?:PosReversalEconomicsReader|ReversalEconomicsPolicy|OutboxShopBinding)\b"
    )

    foreach ($contract in $contracts) {
        $facadeSlices = @(Get-MethodSlices $saleRepository $contract.Access $contract.Facade)
        $facadeCount = Get-MethodDeclarationCount $saleRepository $contract.Access $contract.Facade
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
            $delegationPattern = "_reversalWriter\s*\.\s*" +
                [regex]::Escape($contract.Writer) + "\s*\("
            $forwardingPattern = "(?s)_reversalWriter\s*\.\s*" +
                [regex]::Escape($contract.Writer) + "\s*\(\s*" +
                $contract.Forwarding[$index] + "\s*\)"
            $delegated = [regex]::Matches($slice, $delegationPattern).Count
            $forwarded = [regex]::Matches($slice, $forwardingPattern).Count
            $leaks = @($facadeLeakPatterns | Where-Object { $slice -match $_ })
            if ($delegated -ne 1 -or $forwarded -ne 1 -or $leaks.Count -gt 0) {
                $failures.Add(
                    "$($contract.Facade) overload $($index + 1) delegates=$delegated exact-forward=$forwarded leaks=$($leaks -join ', ')") | Out-Null
            }
        }
    }

    if ($failures.Count -gt 0) {
        Fail "F5 public/internal façade delegation must be method-slice scoped: $($failures -join '; ')"
    } else {
        Pass "Every F5 public/internal façade forwards its exact argument order without reversal SQL or lifecycle ownership"
    }
}

function Assert-WriterSlice(
    [string]$slice,
    [string]$label,
    [string[]]$markers) {
    if ([string]::IsNullOrWhiteSpace($slice)) {
        Fail "$label method slice is missing"
        return
    }

    $missing = @($markers | Where-Object { $slice -notmatch $_ })
    if ($missing.Count -gt 0) {
        Fail "$label is missing scoped markers: $($missing -join ', ')"
    } else {
        Pass "$label retains its scoped reversal semantics"
    }
}

function Assert-CallerOwnedWriterSlice(
    [string]$slice,
    [string]$label,
    [string[]]$markers) {
    Assert-WriterSlice $slice $label $markers
    if ([string]::IsNullOrWhiteSpace($slice)) {
        return
    }

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
    if ($dapper.Count -eq 0 -or $unbound.Count -gt 0 -or $lifecycleLeaks.Count -gt 0) {
        Fail "$label must use only the supplied connection/transaction without lifecycle ownership. dapper=$($dapper.Count) missingTx=$($unbound.Count) lifecycle=$($lifecycleLeaks -join ', ')"
    } else {
        Pass "$label propagates the caller transaction to every DB operation without lifecycle ownership"
    }
}

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleReversalWriter.cs",
    "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs",
    "tests/Win7POS.Core.Tests/Data/SaleReversalWriterTests.cs"
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
$writer = Read-Text "src/Win7POS.Data/Repositories/SaleReversalWriter.cs"
$salesSyncOutbox = Read-Text "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleReversalWriterTests.cs"

if ($saleRepository -notmatch "private readonly SaleReversalWriter _reversalWriter" -or
    $saleRepository -notmatch "_reversalWriter\s*=\s*new SaleReversalWriter\(factory\)") {
    Fail "SaleRepository must retain the F5 SaleReversalWriter collaborator"
} else {
    Pass "SaleRepository retains the F5 SaleReversalWriter collaborator"
}

if ($writer -notmatch "internal sealed class SaleReversalWriter" -or
    $writer -notmatch "internal SaleReversalWriter\(\s*SqliteConnectionFactory factory\s*\)") {
    Fail "SaleReversalWriter must retain its internal factory constructor"
} else {
    Pass "SaleReversalWriter exposes the intended internal factory constructor"
}

$expectedWriterMethods = @{
    "IsVoidedAsync" = 1
    "GetRefundedQtyAsync" = 1
    "GetReturnableLinesAsync" = 1
    "GetReversalEconomicsSnapshotAsync" = 1
    "GetReversalEconomicsSnapshotExcludingAsync" = 1
    "GetPersistedReversalEconomicsErrorAsync" = 1
    "IsReversalDependencyReadyAsync" = 1
    "EvaluateReversalDependencyAsync" = 1
    "ValidateReversalBoundaryAsync" = 1
    "MarkSaleVoidedAsync" = 2
}
$writerMethodPattern = '(?m)^\s*internal\s+(?:static\s+)?(?:async\s+)?Task(?:<[^\r\n]+>)?\s+([A-Za-z0-9_]+)\s*\('
$actualWriterMethods = @(
    [regex]::Matches($writer, $writerMethodPattern) |
    ForEach-Object { $_.Groups[1].Value })
$writerSurfaceFailures = New-Object System.Collections.Generic.List[string]
foreach ($method in $expectedWriterMethods.Keys) {
    $count = @($actualWriterMethods | Where-Object { $_ -eq $method }).Count
    if ($count -ne $expectedWriterMethods[$method]) {
        $writerSurfaceFailures.Add("$method=$count expected=$($expectedWriterMethods[$method])") | Out-Null
    }
}
$unexpectedWriterMethods = @($actualWriterMethods | Where-Object {
        -not $expectedWriterMethods.ContainsKey($_)
    } | Sort-Object -Unique)
if ($writerSurfaceFailures.Count -gt 0 -or $unexpectedWriterMethods.Count -gt 0) {
    Fail "SaleReversalWriter must expose exactly the F5 reversal surface. $($writerSurfaceFailures -join '; ') unexpected=$($unexpectedWriterMethods -join ', ')"
} else {
    Pass "SaleReversalWriter exposes exactly the F5 reversal surface"
}

$facadeContracts = @(
    [pscustomobject]@{ Access = "public"; Facade = "IsVoidedAsync"; Writer = "IsVoidedAsync"; Forwarding = @("saleId") },
    [pscustomobject]@{ Access = "public"; Facade = "GetRefundedQtyAsync"; Writer = "GetRefundedQtyAsync"; Forwarding = @("originalSaleId\s*,\s*originalLineId") },
    [pscustomobject]@{ Access = "public"; Facade = "GetReturnableLinesAsync"; Writer = "GetReturnableLinesAsync"; Forwarding = @("saleId") },
    [pscustomobject]@{ Access = "public"; Facade = "GetReversalEconomicsSnapshotAsync"; Writer = "GetReversalEconomicsSnapshotAsync"; Forwarding = @("originalSaleId") },
    [pscustomobject]@{ Access = "internal"; Facade = "GetReversalEconomicsSnapshotExcludingAsync"; Writer = "GetReversalEconomicsSnapshotExcludingAsync"; Forwarding = @("originalSaleId\s*,\s*excludedReversalSaleId") },
    [pscustomobject]@{ Access = "internal"; Facade = "ValidateReversalBoundaryAsync"; Writer = "ValidateReversalBoundaryAsync"; Forwarding = @("conn\s*,\s*tx\s*,\s*sale\s*,\s*lines") },
    [pscustomobject]@{ Access = "public"; Facade = "GetPersistedReversalEconomicsErrorAsync"; Writer = "GetPersistedReversalEconomicsErrorAsync"; Forwarding = @("saleId\s*,\s*request") },
    [pscustomobject]@{ Access = "public"; Facade = "IsReversalDependencyReadyAsync"; Writer = "IsReversalDependencyReadyAsync"; Forwarding = @("saleId") },
    [pscustomobject]@{ Access = "public"; Facade = "EvaluateReversalDependencyAsync"; Writer = "EvaluateReversalDependencyAsync"; Forwarding = @("saleId") },
    [pscustomobject]@{ Access = "public"; Facade = "MarkSaleVoidedAsync"; Writer = "MarkSaleVoidedAsync"; Forwarding = @(
            "originalSaleId\s*,\s*refundSaleId\s*,\s*nowMs",
            "conn\s*,\s*tx\s*,\s*originalSaleId\s*,\s*refundSaleId\s*,\s*nowMs") }
)
Assert-ExactFacadeDelegations $saleRepository $writer $facadeContracts

$staleRepositoryImplementationMarkers = @(
    "private static bool IsReversalEconomicsCode",
    "private static ReversalDependencyDecision Dependency",
    "private static async Task ValidateReversalBoundaryAsync",
    "internal sealed class ReversalDependencyRow",
    "internal sealed class PersistedReversalRow",
    "internal sealed class BoundaryOriginalLineRow",
    "internal sealed class OriginalAckBindingRow",
    "PosReversalEconomicsReader"
) | Where-Object { $saleRepository -match $_ }
if ($staleRepositoryImplementationMarkers.Count -gt 0) {
    Fail "SaleRepository must not retain F5 reversal implementation details: $($staleRepositoryImplementationMarkers -join ', ')"
} else {
    Pass "SaleRepository no longer owns F5 reversal implementation details"
}

$publicContractMarkers = @(
    "public enum ReversalDependencyState",
    "public sealed class ReversalDependencyDecision",
    "public sealed class SaleLineReturnableDto"
)
$missingPublicContractMarkers = @($publicContractMarkers | Where-Object { $saleRepository -notmatch $_ })
if ($missingPublicContractMarkers.Count -gt 0) {
    Fail "F5 must preserve the public reversal API types in the current namespace: $($missingPublicContractMarkers -join ', ')"
} else {
    Pass "F5 preserves the public reversal API types in the current namespace"
}

$writerImplementationMarkers = @(
    "private static bool IsReversalEconomicsCode",
    "private static ReversalDependencyDecision Dependency",
    "internal sealed class ReversalDependencyRow",
    "internal sealed class PersistedReversalRow",
    "internal sealed class BoundaryOriginalLineRow",
    "internal sealed class OriginalAckBindingRow"
)
$missingWriterImplementationMarkers = @($writerImplementationMarkers | Where-Object { $writer -notmatch $_ })
if ($missingWriterImplementationMarkers.Count -gt 0) {
    Fail "SaleReversalWriter must own the extracted reversal helpers and internal row DTOs: $($missingWriterImplementationMarkers -join ', ')"
} else {
    Pass "SaleReversalWriter owns the extracted reversal helpers and internal row DTOs"
}

$returnableWriters = @(Get-MethodSlices $writer "internal" "GetReturnableLinesAsync")
if ($returnableWriters.Count -ne 1) {
    Fail "F5 returnable-line reader must have exactly one writer implementation"
} else {
    Assert-WriterSlice $returnableWriters[0] "F5 GetReturnableLinesAsync" @(
        "COALESCE\(l\.barcode, ''\) NOT LIKE @discountPrefix",
        "COALESCE\(l\.barcode, ''\) NOT LIKE @taxPrefix",
        "rs\.kind IN \(@kindRefund, @kindVoid\)",
        "(?:x|row)\.RemainingQty = (?:x|row)\.SoldQty - (?:x|row)\.RefundedQty",
        "if \((?:x|row)\.RemainingQty < 0\) (?:x|row)\.RemainingQty = 0"
    )
}

$persistedEconomicsWriters = @(Get-MethodSlices $writer "internal" "GetPersistedReversalEconomicsErrorAsync")
if ($persistedEconomicsWriters.Count -ne 1) {
    Fail "F5 persisted-economics reader must have exactly one writer implementation"
} else {
    Assert-WriterSlice $persistedEconomicsWriters[0] "F5 GetPersistedReversalEconomicsErrorAsync" @(
        "PosSalesSyncRequestBuilder\.TryGetReversalGross",
        "PosReversalEconomicsReader\s*\.\s*LoadAsync",
        "ReversalEconomicsPolicy\.Calculate",
        "PosSalesSyncRequestBuilder\.HasExpectedReversalEconomics",
        "catch \(InvalidOperationException ex\) when \(IsReversalEconomicsCode\(ex\.Message\)\)"
    )
}

$dependencyWriters = @(Get-MethodSlices $writer "internal" "EvaluateReversalDependencyAsync")
if ($dependencyWriters.Count -ne 1) {
    Fail "F5 dependency evaluator must have exactly one writer implementation"
} else {
    Assert-WriterSlice $dependencyWriters[0] "F5 EvaluateReversalDependencyAsync" @(
        "OutboxShopBinding\.GetMismatchCode",
        "original_sale_blocked",
        "original_sale_outbox_missing",
        "prior_reversal_blocked",
        "prior_reversal_not_acked",
        "ReversalDependencyState\.PermanentBlock",
        "ReversalDependencyState\.Wait",
        "ReversalDependencyState\.Ready"
    )
}

$validateWriters = @(Get-MethodSlices $writer "internal" "ValidateReversalBoundaryAsync")
if ($validateWriters.Count -ne 1) {
    Fail "F5 reversal-boundary validator must have exactly one caller-owned writer implementation"
} else {
    $validateWriter = $validateWriters[0]
    Assert-CallerOwnedWriterSlice $validateWriter "F5 ValidateReversalBoundaryAsync" @(
        "SqliteConnection conn",
        "SqliteTransaction tx",
        "OutboxShopBinding\.ResolveRequiredAsync\(conn, tx\)",
        "PosReversalEconomicsReader\s*\.\s*LoadAsync\(conn, tx,",
        "ReversalEconomicsPolicy\.Calculate",
        "ReversalEconomicsPolicy\.MismatchCode"
    )
    $validationWrites = @(Get-CSharpDapperAsyncInvocations $validateWriter | Where-Object {
            $_.Method -eq "ExecuteAsync"
        })
    if ($validationWrites.Count -gt 0) {
        Fail "F5 ValidateReversalBoundaryAsync must validate only; ExecuteAsync writes=$($validationWrites.Count)"
    } else {
        Pass "F5 ValidateReversalBoundaryAsync retains read-only caller-owned validation"
    }
}

$voidWriters = @(Get-MethodSlices $writer "internal" "MarkSaleVoidedAsync")
$callerOwnedVoidWriters = @($voidWriters | Where-Object { $_ -match "SqliteConnection conn" -and $_ -match "SqliteTransaction tx" })
if ($voidWriters.Count -ne 2 -or $callerOwnedVoidWriters.Count -ne 1) {
    Fail "F5 void marker must retain standalone and caller-owned writer overloads"
} else {
    Assert-CallerOwnedWriterSlice $callerOwnedVoidWriters[0] "F5 MarkSaleVoidedAsync caller-owned overload" @(
        "UPDATE\s+sales",
        "voided_by_sale_id = @refundSaleId",
        "voided_at = @nowMs",
        "WHERE id = @originalSaleId"
    )
}

$forbiddenWriterOwnership = @(
    "SaleStockMovementWriter",
    "ApplyLocalStockMovements",
    "EnsureClientSaleId",
    "BuildClientSaleId",
    "EnqueueSalesSyncOutbox",
    "SalesSyncOutboxRepository",
    "AuditLogRepository",
    "InsertSaleAsync",
    "InsertRefundSaleAsync",
    "InsertRefundOrVoidAsync",
    "InsertSaleLinesAsync",
    "(?i)\bINSERT(?:\s+OR\s+\w+)?\s+INTO\s+sales\b",
    "(?i)\bINSERT(?:\s+OR\s+\w+)?\s+INTO\s+sale_lines\b",
    "(?i)\bINSERT(?:\s+OR\s+\w+)?\s+INTO\s+local_stock_movements\b",
    "(?i)\bUPDATE\s+product_meta\b",
    "(?i)\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|UPDATE|DELETE\s+FROM)\s+sales_sync_outbox\b",
    "\b(?:conn|tx)\s*\.\s*(?:BeginTransaction|Commit|Rollback)\s*\("
) | Where-Object { $writer -match $_ }
if ($forbiddenWriterOwnership.Count -gt 0) {
    Fail "SaleReversalWriter must not acquire header/line, stock, outbox, audit or transaction-lifecycle ownership: $($forbiddenWriterOwnership -join ', ')"
} else {
    Pass "SaleReversalWriter excludes header/line, stock, outbox, audit and transaction-lifecycle ownership"
}

$f4EnqueueWriters = @(Get-MethodSlices $salesSyncOutbox "internal" "EnqueueAsync")
if ($f4EnqueueWriters.Count -ne 1 -or
    $f4EnqueueWriters[0] -notmatch "PosReversalEconomicsReader\s*\.\s*LoadAsync\(\s*conn\s*,\s*tx\s*," -or
    $f4EnqueueWriters[0] -notmatch "ReversalEconomicsPolicy\.Calculate" -or
    $f4EnqueueWriters[0] -match "(?:_reversalWriter|SaleReversalWriter|GetReversalEconomicsSnapshot)") {
    Fail "F4 EnqueueAsync must retain inline caller-transaction reversal economics, without acquiring the F5 writer"
} else {
    Pass "F4 EnqueueAsync retains inline caller-transaction reversal economics"
}

$requiredTestContracts = @(
    [pscustomobject]@{
        Name = "SaleReversalWriter_AndSaleFacade_KeepReadParity"
        TestMarkers = @(
            "new DirectReversalSurface",
            "new FacadeReversalSurface",
            "GetReturnableLinesAsync",
            "GetReversalEconomicsSnapshotExcludingAsync",
            "GetPersistedReversalEconomicsErrorAsync",
            "EvaluateReversalDependencyAsync")
        Helper = ""
        HelperMarkers = @()
    },
    [pscustomobject]@{
        Name = "SaleReversalWriter_AndSaleFacade_ValidateBoundaryReadsUncommittedCallerData"
        TestMarkers = @(
            "new DirectReversalSurface",
            "new FacadeReversalSurface",
            "AssertValidateBoundaryReadsUncommittedCallerDataAsync")
        Helper = "AssertValidateBoundaryReadsUncommittedCallerDataAsync"
        HelperMarkers = @(
            "factory.Open()",
            "conn.BeginTransaction()",
            "ValidateReversalBoundaryAsync(conn, tx",
            "tx.Rollback()",
            "SELECT COUNT(1) FROM sales WHERE related_sale_id")
    },
    [pscustomobject]@{
        Name = "SaleReversalWriter_AndSaleFacade_MarkVoidedCallerTransactionRollbackLeavesNoMutation"
        TestMarkers = @(
            "new DirectReversalSurface",
            "new FacadeReversalSurface",
            "AssertMarkVoidedCallerTransactionRollbackLeavesNoMutationAsync")
        Helper = "AssertMarkVoidedCallerTransactionRollbackLeavesNoMutationAsync"
        HelperMarkers = @(
            "factory.Open()",
            "conn.BeginTransaction()",
            "MarkSaleVoidedAsync(",
            "insideTransaction",
            "tx.Rollback()",
            "afterRollback")
    }
)
$testEvidenceFailures = New-Object System.Collections.Generic.List[string]
foreach ($contract in $requiredTestContracts) {
    $testSlices = @(Get-CSharpTestMethodSlices $tests $contract.Name)
    if ($testSlices.Count -ne 1) {
        $testEvidenceFailures.Add("$($contract.Name) structural test bodies=$($testSlices.Count)") | Out-Null
        continue
    }

    $missingTestMarkers = @($contract.TestMarkers | Where-Object {
            $testSlices[0].Text -notmatch [regex]::Escape($_)
        })
    if ($missingTestMarkers.Count -gt 0) {
        $testEvidenceFailures.Add("$($contract.Name) test body missing: $($missingTestMarkers -join ', ')") | Out-Null
        continue
    }

    if (-not [string]::IsNullOrWhiteSpace($contract.Helper)) {
        $helperSlices = @(Get-CSharpMethodSlices $tests "private" $contract.Helper)
        if ($helperSlices.Count -ne 1) {
            $testEvidenceFailures.Add("$($contract.Name) helper bodies=$($helperSlices.Count)") | Out-Null
            continue
        }

        $missingHelperMarkers = @($contract.HelperMarkers | Where-Object {
                $helperSlices[0].Text -notmatch [regex]::Escape($_)
            })
        if ($missingHelperMarkers.Count -gt 0) {
            $testEvidenceFailures.Add("$($contract.Name) linked helper missing: $($missingHelperMarkers -join ', ')") | Out-Null
        }
    }
}
if ($tests -notmatch "private readonly SaleReversalWriter _writer" -or
    $tests -notmatch "_writer\s*=\s*new SaleReversalWriter\(factory\)" -or
    $tests -notmatch "private readonly SaleRepository _repository" -or
    $tests -notmatch "_repository\s*=\s*new SaleRepository\(factory\)") {
    $testEvidenceFailures.Add("direct writer/facade test surfaces are not instantiated") | Out-Null
}
if ($testEvidenceFailures.Count -gt 0) {
    Fail "F5 named direct/facade parity and caller-owned transaction regressions are incomplete: $($testEvidenceFailures -join '; ')"
} else {
    Pass "F5 direct/facade parity and caller-owned transaction regressions link each named test body to its transaction evidence helper"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
