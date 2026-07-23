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
        "\s+(?:async\s+)?Task(?:<[^\r\n]+>)?\s+" +
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

function Assert-SliceMarkers(
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
        Pass "$label retains its scoped ownership/CAS markers"
    }
}

function Assert-PrepareCasExecution([string]$slice) {
    $executions = @(Get-CSharpDapperAsyncInvocations $slice | Where-Object {
            $_.Method -eq "ExecuteAsync"
        })
    $outboxWrites = @($executions | Where-Object {
            $_.Text -match "UPDATE\s+sales_sync_outbox"
        })
    $failure = $null
    if ($executions.Count -ne 1 -or $outboxWrites.Count -ne 1) {
        $failure = "execute=$($executions.Count) outbox=$($outboxWrites.Count)"
    } else {
        $statementStart = Get-CSharpPreviousStructuralSemicolon $slice $outboxWrites[0].Start
        $assignment = $slice.Substring(
            $statementStart + 1,
            $outboxWrites[0].Start - $statementStart - 1)
        $afterWrite = $slice.Substring($outboxWrites[0].End + 1)
        if ($assignment -notmatch "(?s)^\s*var\s+rows\s*=\s*await\s*$" -or
            $afterWrite -notmatch "return\s+rows\s*==\s*1\s*;") {
            $failure = "the outbox CAS must assign rows and decide return rows == 1"
        }
    }

    if ($null -ne $failure) {
        Fail "F4 PrepareAttemptAsync must bind its outbox CAS to the local rows == 1 decision: $failure"
    } else {
        Pass "F4 PrepareAttemptAsync binds its outbox CAS to the local rows == 1 decision"
    }
}

function Assert-TransactionalTransitionExecutions(
    [string]$slice,
    [string]$label,
    [string]$outboxStatus,
    [string]$saleStatus,
    [switch]$OriginBlock) {
    $executions = @(Get-CSharpDapperAsyncInvocations $slice | Where-Object {
            $_.Method -eq "ExecuteAsync"
        })
    $outboxWrites = @($executions | Where-Object {
            $_.Text -match "UPDATE\s+sales_sync_outbox"
        })
    $saleWrites = @($executions | Where-Object {
            $_.Text -match "UPDATE\s+sales\s+SET"
        })
    $unbound = @($executions | Where-Object {
            -not (Test-CSharpInvocationUsesTransaction $_.Text "tx")
        })
    $failure = $null
    if ($executions.Count -ne 2 -or $outboxWrites.Count -ne 1 -or $saleWrites.Count -ne 1) {
        $failure = "execute=$($executions.Count) outbox=$($outboxWrites.Count) sales=$($saleWrites.Count)"
    } elseif ($unbound.Count -gt 0) {
        $failure = "unbound ExecuteAsync calls=$($unbound.Count)"
    } elseif ($slice -notmatch "using var tx = conn\.BeginTransaction\(\)") {
        $failure = "local transaction missing"
    } elseif ($outboxWrites[0].Text -notmatch "status\s*=\s*'$([regex]::Escape($outboxStatus))'" -or
        $saleWrites[0].Text -notmatch "sync_status\s*=\s*'$([regex]::Escape($saleStatus))'") {
        $failure = "outbox/sale status mutation is not the expected transition"
    } else {
        $statementStart = Get-CSharpPreviousStructuralSemicolon $slice $outboxWrites[0].Start
        $assignment = $slice.Substring(
            $statementStart + 1,
            $outboxWrites[0].Start - $statementStart - 1)
        if ($assignment -notmatch "(?s)^\s*var\s+rows\s*=\s*await\s*$") {
            $failure = "outbox ExecuteAsync is not assigned to rows"
        } elseif ($OriginBlock) {
            $tail = $slice.Substring($outboxWrites[0].End + 1)
            $guard = [regex]::Match($tail, "if\s*\(\s*rows\s*==\s*1\s*\)\s*\{")
            if (-not $guard.Success) {
                $failure = "rows == 1 guard missing"
            } else {
                $guardOpen = $outboxWrites[0].End + 1 + $guard.Index + $guard.Length - 1
                $guardEnd = Find-CSharpMatchingDelimiter $slice $guardOpen '{' '}'
                if ($guardEnd -lt $saleWrites[0].End -or
                    $slice.Substring($saleWrites[0].End + 1) -notmatch "return\s+rows\s*==\s*1\s*;") {
                    $failure = "sales update is not contained by the rows == 1 decision"
                }
            }
        } else {
            $between = $slice.Substring(
                $outboxWrites[0].End + 1,
                $saleWrites[0].Start - $outboxWrites[0].End - 1)
            $guard = [regex]::Match($between, "if\s*\(\s*rows\s*!=\s*1\s*\)\s*\{")
            if (-not $guard.Success) {
                $failure = "rows != 1 rejection guard missing"
            } else {
                $guardOpen = $outboxWrites[0].End + 1 + $guard.Index + $guard.Length - 1
                $guardEnd = Find-CSharpMatchingDelimiter $slice $guardOpen '{' '}'
                $guardBody = if ($guardEnd -gt $guardOpen) {
                    $slice.Substring($guardOpen + 1, $guardEnd - $guardOpen - 1)
                } else {
                    ""
                }
                $afterSales = $slice.Substring($saleWrites[0].End + 1)
                if ($guardEnd -lt 0 -or
                    $guardEnd -gt $saleWrites[0].Start -or
                    $guardBody -notmatch "tx\.Rollback\(\)" -or
                    $guardBody -notmatch "return\s+false\s*;" -or
                    $afterSales -notmatch "tx\.Commit\(\)" -or
                    $afterSales -notmatch "return\s+true\s*;") {
                    $failure = "rows == 1 success path is not transactionally guarded before sales update/commit"
                }
            }
        }
    }

    if ($null -ne $failure) {
        Fail "$label must bind every outbox/sales ExecuteAsync to tx and its local rows == 1 decision: $failure"
    } else {
        Pass "$label binds every outbox/sales ExecuteAsync to tx and its local rows == 1 decision"
    }
}

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs",
    "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs",
    "tests/Win7POS.Core.Tests/Data/SalesSyncOutboxRepositoryTests.cs"
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
$transactionWriter = Read-Text "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs"
$outboxRepository = Read-Text "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SalesSyncOutboxRepositoryTests.cs"

if ($saleRepository -notmatch "private readonly SalesSyncOutboxRepository _salesSyncOutbox" -or
    $saleRepository -notmatch "new SalesSyncOutboxRepository\(\s*factory,\s*SalesSyncInProgressLeaseMilliseconds\s*\)") {
    Fail "SaleRepository must retain the F4 sales-sync outbox collaborator with the existing lease"
} else {
    Pass "SaleRepository retains the F4 sales-sync outbox collaborator"
}

$facadeDelegations = @(
    [pscustomobject]@{ Facade = "GetPendingSalesSyncOutboxAsync"; Writer = "GetPendingAsync"; Forwarding = @("take\s*,\s*nowMs") },
    [pscustomobject]@{ Facade = "GetSalesSyncOutboxSummaryAsync"; Writer = "GetSummaryAsync"; Forwarding = @("\s*") },
    [pscustomobject]@{ Facade = "GetSalesSyncDrainStateAsync"; Writer = "GetDrainStateAsync"; Forwarding = @("nowMs") },
    [pscustomobject]@{ Facade = "HasUnresolvedSalesSyncOutboxAsync"; Writer = "HasUnresolvedAsync"; Forwarding = @("\s*") },
    [pscustomobject]@{ Facade = "GetRemoteProductIdsAsync"; Writer = "GetRemoteProductIdsAsync"; Forwarding = @("productIds") },
    [pscustomobject]@{ Facade = "PrepareSalesSyncAttemptAsync"; Writer = "PrepareAttemptAsync"; Forwarding = @(
            "outboxId\s*,\s*clientBatchId\s*,\s*payloadJson\s*,\s*payloadHash\s*,\s*nowMs\s*,\s*expectedAttemptCount",
            "outboxId\s*,\s*clientBatchId\s*,\s*payloadJson\s*,\s*payloadHash\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*expectedStatus\s*,\s*expectedNextRetryAt\s*,\s*expectedLeaseObservedAt",
            "outboxId\s*,\s*clientBatchId\s*,\s*payloadJson\s*,\s*payloadHash\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*expectedStatus\s*,\s*expectedNextRetryAt\s*,\s*expectedLeaseObservedAt\s*,\s*generation\s*,\s*claimToken") },
    [pscustomobject]@{ Facade = "MarkSalesSyncAckedAsync"; Writer = "MarkAckedAsync"; Forwarding = @(
            "outboxId\s*,\s*saleId\s*,\s*serverBatchId\s*,\s*serverSaleId\s*,\s*nowMs\s*,\s*expectedAttemptCount",
            "outboxId\s*,\s*saleId\s*,\s*serverBatchId\s*,\s*serverSaleId\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence") },
    [pscustomobject]@{ Facade = "MarkSalesSyncRetryAsync"; Writer = "MarkRetryAsync"; Forwarding = @(
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount",
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence") },
    [pscustomobject]@{ Facade = "DeferSalesSyncDependencyAsync"; Writer = "DeferDependencyAsync"; Forwarding = @(
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount",
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence") },
    [pscustomobject]@{ Facade = "ReleaseSalesSyncAttemptAsync"; Writer = "ReleaseAttemptAsync"; Forwarding = @(
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount",
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence") },
    [pscustomobject]@{ Facade = "MarkSalesSyncBlockedAsync"; Writer = "MarkBlockedAsync"; Forwarding = @(
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nowMs\s*,\s*expectedAttemptCount",
            "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence") },
    [pscustomobject]@{ Facade = "MarkSalesSyncOriginBlockedAsync"; Writer = "MarkOriginBlockedAsync"; Forwarding = @("outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nowMs\s*,\s*expectedStatus\s*,\s*expectedAttemptCount\s*,\s*expectedLeaseObservedAt") }
)

$facadeLeakPatterns = @(
    "\bsales_sync_outbox\b",
    "\b(?:conn|_factory)\s*\.\s*(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async\b",
    "\b(?:conn|_factory)\s*\.\s*Open(?:Async)?\s*\(",
    "\b(?:conn|tx)\s*\.\s*(?:BeginTransaction|Commit|Rollback|Close|CloseAsync|Dispose|DisposeAsync)\s*\(",
    "(?m)^\s*using\s+",
    "\b(?:OutboxShopBinding|PosSalesSyncRequestBuilder|PosReversalEconomicsReader|ReversalEconomicsPolicy)\b"
)

$delegationFailures = New-Object System.Collections.Generic.List[string]
foreach ($delegation in $facadeDelegations) {
    $facadeCount = Get-MethodDeclarationCount $saleRepository "public" $delegation.Facade
    $writerCount = Get-MethodDeclarationCount $outboxRepository "internal" $delegation.Writer
    $facadeSlices = @(Get-MethodSlices $saleRepository "public" $delegation.Facade)
    if ($facadeCount -ne $delegation.Forwarding.Count -or
        $facadeSlices.Count -ne $delegation.Forwarding.Count -or
        $writerCount -ne $delegation.Forwarding.Count) {
        $delegationFailures.Add(
            "$($delegation.Facade)/$($delegation.Writer) facade=$facadeCount slices=$($facadeSlices.Count) writer=$writerCount expected=$($delegation.Forwarding.Count)") | Out-Null
        continue
    }

    for ($index = 0; $index -lt $facadeSlices.Count; $index++) {
        $slice = $facadeSlices[$index]
        $delegationPattern = "_salesSyncOutbox\s*\.\s*" +
            [regex]::Escape($delegation.Writer) + "\s*\("
        $delegated = [regex]::Matches($slice, $delegationPattern).Count
        $forwardingPattern = "(?s)_salesSyncOutbox\s*\.\s*" +
            [regex]::Escape($delegation.Writer) + "\s*\(\s*" +
            $delegation.Forwarding[$index] + "\s*\)"
        $forwarded = [regex]::Matches($slice, $forwardingPattern).Count
        if ($delegated -ne 1 -or $forwarded -ne 1) {
            $delegationFailures.Add(
                "$($delegation.Facade) overload $($index + 1) delegates=$delegated exact-forward=$forwarded writer=$($delegation.Writer)") | Out-Null
        }

        $leaks = @($facadeLeakPatterns | Where-Object { $slice -match $_ })
        if ($leaks.Count -gt 0) {
            $delegationFailures.Add(
                "$($delegation.Facade) overload $($index + 1) retains direct outbox implementation: $($leaks -join ', ')") | Out-Null
        }
    }
}
if ($delegationFailures.Count -gt 0) {
    Fail "F4 facade/writer delegation must be method-slice scoped: $($delegationFailures -join '; ')"
} else {
    Pass "Every F4 public facade overload forwards its exact argument order inside its structural implementation slice"
}

$enqueueRepositoryFacades = @(Get-MethodSlices $saleRepository "public" "EnqueueSalesSyncOutboxAsync")
$enqueueTransactionFacades = @(Get-MethodSlices $transactionWriter "internal" "EnqueueSalesSyncOutboxAsync")
if ($enqueueRepositoryFacades.Count -ne 1 -or $enqueueTransactionFacades.Count -ne 1) {
    Fail "F4 enqueue must retain exactly one SaleRepository facade and one F6 transaction facade"
} else {
    Assert-SliceMarkers $enqueueRepositoryFacades[0] "F4 SaleRepository enqueue facade" @(
        "_transactionWriter\s*\.\s*EnqueueSalesSyncOutboxAsync\(conn, tx, saleId, clientSaleId\)"
    )
    if ($enqueueRepositoryFacades[0] -match "_salesSyncOutbox\s*\.\s*EnqueueAsync|BuildClientSaleId|clientSaleId\.Trim\(\)|\.Open(?:Async)?\s*\(|BeginTransaction|\.Commit\s*\(|\.Rollback\s*\(") {
        Fail "F4 SaleRepository enqueue facade must not retain normalization, outbox or lifecycle ownership after F6"
    } else {
        Pass "F4 SaleRepository enqueue facade delegates exact caller data to F6"
    }
    Assert-SliceMarkers $enqueueTransactionFacades[0] "F4 F6 enqueue facade" @(
        "string\.IsNullOrWhiteSpace\(clientSaleId\)",
        "BuildClientSaleId\(saleId\)",
        "clientSaleId\.Trim\(\)",
        "_salesSyncOutbox\s*\.\s*EnqueueAsync\(conn, tx, saleId, normalizedClientSaleId\)"
    )
    if ($enqueueTransactionFacades[0] -match "INSERT\s+OR\s+IGNORE\s+INTO\s+sales_sync_outbox|SerializeCanonical|Sha256Hex|\.Open(?:Async)?\s*\(|BeginTransaction|\.Commit\s*\(|\.Rollback\s*\(") {
        Fail "F4 F6 enqueue facade must keep only client-ID normalization and caller-transaction delegation"
    } else {
        Pass "F4 F6 enqueue facade keeps normalization while F4 owns immutable payload persistence"
    }
}

if ($outboxRepository -notmatch "internal sealed class SalesSyncOutboxRepository" -or
    $outboxRepository -notmatch "internal SalesSyncOutboxRepository\(\s*SqliteConnectionFactory factory,\s*long inProgressLeaseMilliseconds\s*\)") {
    Fail "SalesSyncOutboxRepository must retain the factory plus configurable stale-lease constructor"
} else {
    Pass "SalesSyncOutboxRepository retains the factory plus stale-lease constructor"
}

$enqueueWriters = @(Get-MethodSlices $outboxRepository "internal" "EnqueueAsync")
if ($enqueueWriters.Count -ne 1) {
    Fail "F4 writer must have exactly one EnqueueAsync implementation slice"
} else {
    $enqueueWriter = $enqueueWriters[0]
    Assert-SliceMarkers $enqueueWriter "F4 EnqueueAsync" @(
        "SqliteConnection conn",
        "SqliteTransaction tx",
        "OutboxShopBinding\.ResolveRequiredAsync\(conn, tx\)",
        "PosSalesSyncRequestBuilder\.BuildCanonical",
        "PosSalesSyncRequestBuilder\.SerializeCanonical",
        "PosSalesSyncRequestBuilder\.Sha256Hex",
        "ReversalEconomicsPolicy\.Calculate",
        "INSERT OR IGNORE INTO sales_sync_outbox"
    )

    $dapperCalls = @(Get-CSharpDapperAsyncInvocations $enqueueWriter)
    $missingCallerTransactions = @($dapperCalls | Where-Object {
        -not (Test-CSharpInvocationUsesTransaction $_.Text "tx")
    })
    $bindingCalls = [regex]::Matches(
        $enqueueWriter,
        "OutboxShopBinding\.ResolveRequiredAsync\(\s*conn\s*,\s*tx\s*\)\s*\.ConfigureAwait\(false\)").Count
    $reversalReaderCalls = [regex]::Matches(
        $enqueueWriter,
        "PosReversalEconomicsReader\s*\.\s*LoadAsync\(\s*conn\s*,\s*tx\s*,").Count
    $lifecycleLeaks = @(
        "\b_factory\b",
        "\b(?:conn|tx)\s*\.\s*(?:Open|OpenAsync|BeginTransaction|Commit|Rollback|Close|CloseAsync|Dispose|DisposeAsync)\s*\(",
        "(?m)^\s*using\s+"
    ) | Where-Object { $enqueueWriter -match $_ }
    $requiredEnqueueOperations = @(
        "SELECT\s+kind\s+FROM\s+sales\s+WHERE\s+id\s*=\s*@saleId",
        "SELECT\s+id,\s*client_sale_id\s+AS\s+ClientSaleId[\s\S]*FROM\s+sales\s+WHERE\s+id\s*=\s*@saleId",
        "FROM\s+sale_lines\s+WHERE\s+saleId\s*=\s*@saleId",
        "FROM\s+products\s+WHERE\s+id\s+IN\s+@productIds",
        "INSERT\s+OR\s+IGNORE\s+INTO\s+sales_sync_outbox[\s\S]*UPDATE\s+sales",
        "FROM\s+sales_sync_outbox\s+WHERE\s+sale_id\s*=\s*@saleId"
    )
    $missingEnqueueOperations = New-Object System.Collections.Generic.List[string]
    foreach ($operation in $requiredEnqueueOperations) {
        if (@($dapperCalls | Where-Object { $_.Text -match $operation }).Count -ne 1) {
            $missingEnqueueOperations.Add($operation) | Out-Null
        }
    }
    $requiredDapperKinds = @(
        "ExecuteScalarAsync",
        "QuerySingleAsync",
        "QueryAsync",
        "ExecuteAsync"
    )
    $missingDapperKinds = New-Object System.Collections.Generic.List[string]
    foreach ($kind in $requiredDapperKinds) {
        if (@($dapperCalls | Where-Object { $_.Method -eq $kind }).Count -eq 0) {
            $missingDapperKinds.Add($kind) | Out-Null
        }
    }
    if ($dapperCalls.Count -eq 0 -or
        $missingCallerTransactions.Count -gt 0 -or
        $bindingCalls -ne 1 -or
        $reversalReaderCalls -ne 1 -or
        $missingEnqueueOperations.Count -gt 0 -or
        $missingDapperKinds.Count -gt 0 -or
        $lifecycleLeaks.Count -gt 0) {
        Fail "F4 EnqueueAsync must bind every semantic caller-owned DB/binding operation to tx without lifecycle ownership. dapper=$($dapperCalls.Count) missingTx=$($missingCallerTransactions.Count) binding=$bindingCalls reversal=$reversalReaderCalls missingOps=$($missingEnqueueOperations.Count) missingKinds=$($missingDapperKinds -join ', ') leaks=$($lifecycleLeaks -join ', ')"
    } else {
        Pass "F4 EnqueueAsync structurally propagates the caller transaction to every DB/binding invocation"
    }
}

$pendingWriters = @(Get-MethodSlices $outboxRepository "internal" "GetPendingAsync")
if ($pendingWriters.Count -ne 1) {
    Fail "F4 pending reader must have exactly one implementation slice"
} else {
    Assert-SliceMarkers $pendingWriters[0] "F4 bounded pending reader" @(
        "if \(take <= 0\) take = 1",
        "if \(take > 50\) take = 50",
        "_inProgressLeaseMilliseconds"
    )
}

$prepareWriters = @(Get-MethodSlices $outboxRepository "internal" "PrepareAttemptAsync")
if ($prepareWriters.Count -ne 3) {
    Fail "F4 prepare writer must retain three overloads for legacy and generation-fenced callers"
} else {
    Assert-SliceMarkers $prepareWriters[2] "F4 PrepareAttemptAsync CAS" @(
        "status = 'in_progress'",
        "attempt_count = attempt_count \+ 1",
        "AND attempt_count = @expectedAttemptCount",
        "payload_json IS @payloadJson",
        "payload_hash IS @payloadHash",
        "claim_generation_id = @generationId",
        "claim_token = @claimToken",
        "current_generation\.active = 1",
        "generation\.generation_id = @generationId",
        "generation\.fingerprint = @generationFingerprint"
    )
    Assert-PrepareCasExecution $prepareWriters[2]
}

$ackWriters = @(Get-MethodSlices $outboxRepository "internal" "MarkAckedAsync")
if ($ackWriters.Count -ne 2) {
    Fail "F4 ack writer must retain legacy and generation-fenced overloads"
} else {
    Assert-SliceMarkers $ackWriters[1] "F4 MarkAckedAsync fence/CAS" @(
        "ValidateFenceAttempt\(fence, expectedAttemptCount\)",
        "FenceParameters\(",
        "status = 'acked'",
        "AND status = 'in_progress'",
        "AND attempt_count = @expectedAttemptCount",
        "claim_generation_id = @generationId",
        "claim_token = @claimToken",
        "current_generation\.active = 1",
        "generation\.generation_id = @generationId",
        "UPDATE\s+sales\s+SET\s+sync_status\s*=\s*'acked'\s+WHERE\s+id\s*=\s*@saleId",
        "using var tx = conn\.BeginTransaction\(\)",
        "tx\.Rollback\(\)",
        "tx\.Commit\(\)"
    )
    Assert-TransactionalTransitionExecutions $ackWriters[1] "F4 MarkAckedAsync" "acked" "acked"
}

$retryWriters = @(Get-MethodSlices $outboxRepository "internal" "MarkRetryAsync")
if ($retryWriters.Count -ne 2) {
    Fail "F4 retry writer must retain legacy and generation-fenced overloads"
} else {
    Assert-SliceMarkers $retryWriters[1] "F4 MarkRetryAsync fence/CAS" @(
        "ValidateFenceAttempt\(fence, expectedAttemptCount\)",
        "FenceParameters\(",
        "status = 'retry'",
        "AND status = 'in_progress'",
        "AND attempt_count = @expectedAttemptCount",
        "claim_generation_id = @generationId",
        "claim_token = @claimToken",
        "current_generation\.active = 1",
        "generation\.generation_id = @generationId",
        "UPDATE\s+sales\s+SET\s+sync_status\s*=\s*'retry'\s+WHERE\s+id\s*=\s*@saleId",
        "using var tx = conn\.BeginTransaction\(\)",
        "tx\.Rollback\(\)",
        "tx\.Commit\(\)"
    )
    Assert-TransactionalTransitionExecutions $retryWriters[1] "F4 MarkRetryAsync" "retry" "retry"
}

$deferWriters = @(Get-MethodSlices $outboxRepository "internal" "DeferDependencyAsync")
if ($deferWriters.Count -ne 2) {
    Fail "F4 dependency defer writer must retain both release aliases"
} else {
    $deferFailures = New-Object System.Collections.Generic.List[string]
    $deferForwarding = @(
        "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount",
        "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence"
    )
    for ($index = 0; $index -lt $deferWriters.Count; $index++) {
        $slice = $deferWriters[$index]
        if ([regex]::Matches($slice, "\bReleaseAttemptAsync\s*\(").Count -ne 1 -or
            [regex]::Matches(
                $slice,
                "(?s)return\s+ReleaseAttemptAsync\s*\(\s*" +
                    $deferForwarding[$index] + "\s*\)").Count -ne 1 -or
            $slice -match "\b(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async\b|\b(?:Open|BeginTransaction|Commit|Rollback)\s*\(") {
            $deferFailures.Add("DeferDependencyAsync overload $($index + 1)") | Out-Null
        }
    }
    if ($deferFailures.Count -gt 0) {
        Fail "F4 dependency defer must remain a scoped ReleaseAttemptAsync alias: $($deferFailures -join ', ')"
    } else {
        Pass "F4 dependency defer overloads remain scoped release aliases"
    }
}

$releaseWriters = @(Get-MethodSlices $outboxRepository "internal" "ReleaseAttemptAsync")
if ($releaseWriters.Count -ne 2) {
    Fail "F4 release writer must retain legacy and generation-fenced overloads"
} else {
    Assert-SliceMarkers $releaseWriters[1] "F4 ReleaseAttemptAsync fence/CAS" @(
        "ValidateFenceAttempt\(fence, expectedAttemptCount\)",
        "FenceParameters\(",
        "status = 'retry'",
        "attempt_count = attempt_count - 1",
        "AND status = 'in_progress'",
        "AND attempt_count = @expectedAttemptCount",
        "claim_generation_id = @generationId",
        "claim_token = @claimToken",
        "current_generation\.active = 1",
        "generation\.generation_id = @generationId",
        "UPDATE\s+sales\s+SET\s+sync_status\s*=\s*'retry'\s+WHERE\s+id\s*=\s*@saleId",
        "using var tx = conn\.BeginTransaction\(\)",
        "tx\.Rollback\(\)",
        "tx\.Commit\(\)"
    )
    Assert-TransactionalTransitionExecutions $releaseWriters[1] "F4 ReleaseAttemptAsync" "retry" "retry"
}

$blockedWriters = @(Get-MethodSlices $outboxRepository "internal" "MarkBlockedAsync")
if ($blockedWriters.Count -ne 2) {
    Fail "F4 blocked writer must retain legacy and generation-fenced overloads"
} else {
    Assert-SliceMarkers $blockedWriters[1] "F4 MarkBlockedAsync fence/CAS" @(
        "ValidateFenceAttempt\(fence, expectedAttemptCount\)",
        "FenceParameters\(",
        "status = 'failed_blocked'",
        "AND status = 'in_progress'",
        "AND attempt_count = @expectedAttemptCount",
        "claim_generation_id = @generationId",
        "claim_token = @claimToken",
        "current_generation\.active = 1",
        "generation\.generation_id = @generationId",
        "UPDATE\s+sales\s+SET\s+sync_status\s*=\s*'blocked'\s+WHERE\s+id\s*=\s*@saleId",
        "using var tx = conn\.BeginTransaction\(\)",
        "tx\.Rollback\(\)",
        "tx\.Commit\(\)"
    )
    Assert-TransactionalTransitionExecutions $blockedWriters[1] "F4 MarkBlockedAsync" "failed_blocked" "blocked"
}

$originBlockedWriters = @(Get-MethodSlices $outboxRepository "internal" "MarkOriginBlockedAsync")
if ($originBlockedWriters.Count -ne 1) {
    Fail "F4 origin-block writer must retain exactly one CAS implementation"
} else {
    Assert-SliceMarkers $originBlockedWriters[0] "F4 MarkOriginBlockedAsync CAS" @(
        "normalizedExpectedStatus",
        "status = 'failed_blocked'",
        "sale_id = @saleId",
        "status = @normalizedExpectedStatus",
        "attempt_count = @expectedAttemptCount",
        "COALESCE\(last_attempt_at, updated_at, 0\) = @expectedLeaseObservedAt",
        "UPDATE\s+sales\s+SET\s+sync_status\s*=\s*'blocked'\s+WHERE\s+id\s*=\s*@saleId",
        "using var tx = conn\.BeginTransaction\(\)",
        "tx\.Commit\(\)"
    )
    Assert-TransactionalTransitionExecutions $originBlockedWriters[0] "F4 MarkOriginBlockedAsync" "failed_blocked" "blocked" -OriginBlock
}

# Enqueue owns the inline reversal economics needed to serialize its immutable
# payload. F5 keeps its public/internal façade on SaleRepository, while the
# standalone reversal implementation belongs to SaleReversalWriter and must
# never migrate into the F4 writer.
$forbiddenWriterOwnership = @(
    "EnsureClientSaleId",
    "BuildClientSaleId",
    "ApplyLocalStockMovements",
    "SaleStockMovementWriter",
    "InsertSaleAsync",
    "InsertSaleLineAsync",
    "InsertRefundSaleAsync",
    "InsertRefundOrVoidAsync",
    "InsertSaleLinesAsync",
    "ValidateReversalBoundaryAsync",
    "EvaluateReversalDependencyAsync",
    "\bDependency\s*\(",
    "IsReversalDependencyReadyAsync",
    "IsReversalEconomicsCode",
    "GetPersistedReversalEconomicsErrorAsync",
    "GetRefundedQtyAsync",
    "IsVoidedAsync",
    "GetReversalEconomicsSnapshotAsync",
    "GetReversalEconomicsSnapshotExcludingAsync",
    "GetReturnableLinesAsync",
    "MarkSaleVoidedAsync",
    "RequireSaleSafeForOrdinarySaleAsync",
    "CatalogSaleSafetyPolicy"
) | Where-Object { $outboxRepository -match $_ }
if ($forbiddenWriterOwnership.Count -gt 0) {
    Fail "F4 writer must not absorb sale-header, line, stock, sale-safe or standalone reversal ownership: $($forbiddenWriterOwnership -join ', ')"
} else {
    Pass "F4 writer excludes sale-header, line, stock, sale-safe and standalone F5 reversal ownership"
}

$requiredF5FacadeMarkers = @(
    "private readonly SaleReversalWriter _reversalWriter",
    "_reversalWriter\s*=\s*new SaleReversalWriter\(factory\)",
    "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync\s*\(",
    "_reversalWriter\s*\.\s*IsReversalDependencyReadyAsync\s*\(",
    "_reversalWriter\s*\.\s*EvaluateReversalDependencyAsync\s*\(",
    "_reversalWriter\s*\.\s*GetPersistedReversalEconomicsErrorAsync\s*\(",
    "_reversalWriter\s*\.\s*GetRefundedQtyAsync\s*\(",
    "_reversalWriter\s*\.\s*IsVoidedAsync\s*\(",
    "_reversalWriter\s*\.\s*GetReversalEconomicsSnapshotAsync\s*\(",
    "_reversalWriter\s*\.\s*GetReversalEconomicsSnapshotExcludingAsync\s*\(",
    "_reversalWriter\s*\.\s*GetReturnableLinesAsync\s*\(",
    "_reversalWriter\s*\.\s*MarkSaleVoidedAsync\s*\("
)
$missingF5FacadeMarkers = @($requiredF5FacadeMarkers | Where-Object { $saleRepository -notmatch $_ })
if ($missingF5FacadeMarkers.Count -gt 0) {
    Fail "F5 reversal facades must remain on SaleRepository and delegate outside the F4 writer: $($missingF5FacadeMarkers -join ', ')"
} else {
    Pass "F5 reversal facades remain on SaleRepository while F4 keeps only inline payload economics"
}

$requiredTestContracts = @(
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_EnqueueCallerRollbackLeavesNoOutboxMutation"
        Markers = @("new DirectOutboxSurface", "new FacadeOutboxSurface", "EnqueueAndRollbackAsync", "AssertNoEnqueueMutationAsync")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_EnqueueReadsUncommittedCallerDataAndRollbackClearsAllRows"
        Markers = @("new DirectOutboxSurface", "new FacadeOutboxSurface", "AssertEnqueueReadsUncommittedCallerDataAndRollsBackAsync")
    },
    [pscustomobject]@{
        Name = "SaleFacade_BlankClientSaleIdFallsBackAndPersistsCanonicalBinding"
        Markers = @("EnqueueAndCommitAsync", "expectedClientSaleId", "SELECT client_sale_id FROM sales")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_KeepEnqueuePayloadHashImmutable"
        Markers = @("CopyForClaim", "MutatePersistedSaleAsync", "PayloadHash")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_BoundPendingTake"
        Markers = @("index < 51", "GetPendingAsync(999", "AssertOutboxSequencesEqual")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_RemoteProductIdsKeepMappedUnmappedAndDuplicateParity"
        Markers = @("SeedProductAsync", "mappedFirst", "unmapped", "whitespace", "GetRemoteProductIdsAsync")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_ExposeStaleLease"
        Markers = @("SalesSyncInProgressLeaseMilliseconds", "freshObservedAt", "GetPendingAsync")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_ClaimNullSnapshotsWithCas"
        Markers = @("clientBatchId: null", "payloadJson: null", "payloadHash: null", "PrepareAsync")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_OriginBlockCasKeepsOutboxAndSaleParity"
        Markers = @("MarkOriginBlockedAsync", "AssertOutboxItemTargetsSale", "SaleSyncStatus")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_KeepCasTransitionParity"
        Markers = @("DriveCasSequenceAsync", "GetSummaryAsync", "HasUnresolvedAsync")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_FenceGenerationScopedAck"
        Markers = @("ActivateAndRecoverAsync", "OnlineSyncAttemptFence", "MarkAckedAsync")
    },
    [pscustomobject]@{
        Name = "SalesSyncOutboxRepository_AndSaleFacade_FenceGenerationScopedNonAckTransitions"
        Markers = @("DriveFencedRetryAndReleaseAsync", "OnlineSyncAttemptFence.CreateClaimToken", "SaleSyncStatus")
    }
)
$testEvidenceFailures = New-Object System.Collections.Generic.List[string]
foreach ($contract in $requiredTestContracts) {
    $testSlices = @(Get-CSharpTestMethodSlices $tests $contract.Name)
    if ($testSlices.Count -ne 1) {
        $testEvidenceFailures.Add("$($contract.Name) structural test bodies=$($testSlices.Count)") | Out-Null
        continue
    }

    $missingMarkers = @($contract.Markers | Where-Object {
        $testSlices[0].Text -notmatch [regex]::Escape($_)
    })
    if ($missingMarkers.Count -gt 0) {
        $testEvidenceFailures.Add("$($contract.Name) body missing: $($missingMarkers -join ', ')") | Out-Null
    }
}
if ($testEvidenceFailures.Count -gt 0) {
    Fail "F4 named direct/facade regressions are incomplete: $($testEvidenceFailures -join '; ')"
} else {
    Pass "F4 named regressions contain their required body-level rollback, payload, cap, CAS, remote-ID and fence evidence"
}

$nonAckHelperSlices = @(Get-CSharpMethodSlices $tests "private" "DriveFencedRetryAndReleaseAsync")
if ($nonAckHelperSlices.Count -ne 1 -or
    $nonAckHelperSlices[0].Text -notmatch "MarkRetryAsync" -or
    $nonAckHelperSlices[0].Text -notmatch "ReleaseAttemptAsync" -or
    $nonAckHelperSlices[0].Text -notmatch "MarkBlockedAsync") {
    Fail "F4 non-ACK fence helper must retain retry, release and blocked transition assertions"
} else {
    Pass "F4 non-ACK fence helper retains retry, release and blocked transition assertions"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
