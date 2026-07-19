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

$policy = Read-Text "src/Win7POS.Core/Pos/ReversalEconomicsPolicy.cs"
$keys = Read-Text "src/Win7POS.Core/Pos/PosSession.cs"
$reader = Read-Text "src/Win7POS.Data/Online/PosReversalEconomicsReader.cs"
$builder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$repository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$viewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/RefundViewModel.cs"
$sync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$policyTests = Read-Text "tests/Win7POS.Core.Tests/Pos/ReversalEconomicsPolicyTests.cs"
$builderTests = Read-Text "tests/Win7POS.Core.Tests/Online/PosSalesSyncRequestBuilderTests.cs"
$repositoryTests = Read-Text "tests/Win7POS.Core.Tests/Data/OutboxShopBindingTests.cs"

if ($policy -notmatch "BigInteger" -or $policy -notmatch "remainder \* 2 >= denominator") { Fail "PostgreSQL numeric half-away rounding implementation missing" } else { Pass "PostgreSQL numeric half-away rounding is explicit and overflow-safe" }
if ($policy -notmatch "targetDiscount" -or $policy -notmatch "ActualPriorDiscountClp" -or $policy -notmatch "targetTax" -or $policy -notmatch "ActualPriorTaxClp") { Fail "cumulative discount/tax allocation missing" } else { Pass "discount and tax use cumulative allocation with actual-prior verification" }
if ($keys -notmatch 'TaxPrefix = "TAX:"' -or $keys -notmatch "IsEconomicAdjustment") { Fail "discount/tax pseudo-line classification missing" } else { Pass "discount and tax pseudo-lines share an economic-adjustment classifier" }
if ($repository -notmatch "COALESCE\(l\.barcode, ''\) NOT LIKE @discountPrefix" -or $repository -notmatch "COALESCE\(l\.barcode, ''\) NOT LIKE @taxPrefix" -or $repository -notmatch "rs\.kind IN \(@kindRefund, @kindVoid\)") { Fail "returnable selection does not exclude pseudo rows or count every reversal" } else { Pass "returnable selection is item-only and counts refunds plus voids" }
if ($reader -notmatch "payload_hash" -or $reader -notmatch "Sha256Hex\(payloadJson\)" -or $reader -notmatch "HasExpectedReversalEconomics") { Fail "immutable reversal history validation missing" } else { Pass "original and prior immutable payload/hash economics are revalidated" }
if ($reader -notmatch "ORDER BY s\.id ASC" -or $reader -notmatch "PriorGrossClp = checked" -or $reader -notmatch "PriorSyncUnresolvedCode") { Fail "successive partial ordering/fail-closed history missing" } else { Pass "successive partials are accumulated in deterministic order and unresolved history fails closed" }
if ($builder -notmatch "ReversalEconomicsResult reversalEconomics" -or $builder -notmatch "HasExpectedReversalEconomics" -or $builder -notmatch 'LineType, "item"') { Fail "item-only reversal payload contract missing" } else { Pass "reversal payloads require explicit economics and item-only lines" }
if ($builder -notmatch "payments\.Sum\(payment => payment\.AmountClp\) != sale\.Total" -or $builder -notmatch "PaidClp = payments\.Sum") { Fail "reversal payment/header equality missing" } else { Pass "reversal payment sum, paid header and persisted total are coherent" }
if ($repository -notmatch "PriorReversalNotAcked" -or $repository -notmatch "prior\.id < current\.id" -or $repository -notmatch "prior_outbox\.status") { Fail "successive reversal send dependency missing" } else { Pass "later reversals wait for every earlier reversal ACK" }
if ($workflow -notmatch "GetReversalEconomicsSnapshotAsync" -or $workflow -notmatch "ReversalEconomicsPolicy\.Calculate" -or $viewModel -notmatch "ReversalEconomicsPolicy\.Calculate") { Fail "workflow/UI refund amount is still gross-only" } else { Pass "preview, creation and dialog totals use the shared economic allocation" }
if ($sync -notmatch "PrepareSalesSyncAttemptAsync[\s\S]*GetPersistedReversalEconomicsErrorAsync" -or $sync -notmatch "MarkBlockedAsync\(item, economicsError") { Fail "legacy immutable payload preflight missing" } else { Pass "legacy gross-only payloads are blocked after CAS claim and before network" }
if ($policyTests -notmatch "SuccessivePartialsPreserveCumulativeResidual" -or $policyTests -notmatch "UsesPostgresHalfAwayFromZeroRounding" -or $builderTests -notmatch "ReversalIsItemOnlyWithAllocatedHeaderAndExactPayment" -or $repositoryTests -notmatch "LegacyGrossOnlyPayloadFailsClosedWithoutMutation" -or $repositoryTests -notmatch "DiscountedTaxedSaleEmitsItemOnlyPartialAndFullVoid") { Fail "reversal economics regression matrix incomplete" } else { Pass "discount/tax, rounding, partial, void, payment and legacy regression coverage present" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
