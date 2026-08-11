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
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $path)) {
        Fail "$relativePath missing"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Require-Pattern([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) {
        Pass $label
    }
    else {
        Fail $label
    }
}

$contract = Read-Text "src/Win7POS.Core/Online/PosOnlineContract.cs"
$policy = Read-Text "src/Win7POS.Core/Online/PosOfflineAuthorizationLeasePolicy.cs"
$contracts = Read-Text "src/Win7POS.Core/Online/PosOnlineTransportContracts.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$guard = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOfflineAuthorizationLeaseGuard.cs"
$authorizationCommitGuard = Read-Text "src/Win7POS.Core/Pos/SaleAuthorizationCommitGuard.cs"
$operatorSessionContract = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/IOperatorSession.cs"
$session = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs"
$saleRepository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$saleWriter = Read-Text "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs"
$connectionFactory = Read-Text "src/Win7POS.Data/SqliteConnectionFactory.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$dataProject = Read-Text "src/Win7POS.Data/Win7POS.Data.csproj"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$permission = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/PermissionService.cs"
$recoveryPermission = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/LocalRecoveryPermissionService.cs"
$override = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/OverrideAuthService.cs"
$accessDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$operatorSwitch = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml.cs"
$main = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$posViewModel = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$productsViewModel = Read-Text "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$salesBuilder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$syncHost = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs"
$revocationLatch = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$wpfProject = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$uiHarness = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$authorizationSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/AuthorizationLeaseWpfSmoke.cs"
$authorizationSmokeRunner = Read-Text "scripts/run-authorization-lease-smoke.ps1"
$saleWriterTests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleTransactionWriterTests.cs"
$connectionPolicyTests = Read-Text "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs"

Require-Pattern "offline lease maximum is the 12-hour POS session contract" $contract 'OfflineAuthorizationMaxAgeSeconds\s*=\s*12\s*\*\s*60\s*\*\s*60'
Require-Pattern "policy fails closed on missing legacy receipt timestamp" $policy 'local_receipt_time_invalid'
Require-Pattern "policy rejects rollback and exact expiry" $policy 'clock_rollback[\s\S]*trustedServerNow\s*>=\s*effectiveExpiry'
Require-Pattern "policy advances expiry with a separate trusted monotonic lower bound" $policy 'minimumTrustedServerNow[\s\S]*trustedServerNow[\s\S]*trustedServerNow\s*>=\s*effectiveExpiry'
Require-Pattern "first-login DTO consumes authenticated serverTime" $contracts 'class\s+PosFirstLoginResponse[\s\S]*DataMember\(Name\s*=\s*"serverTime"\)'
Require-Pattern "heartbeat DTO consumes authenticated serverTime" $contracts 'class\s+PosHeartbeatResponse[\s\S]*DataMember\(Name\s*=\s*"serverTime"\)'
Require-Pattern "first-login DTO consumes the optional authoritative offline expiry" $contracts 'class\s+PosFirstLoginResponse[\s\S]*DataMember\(\s*Name\s*=\s*"effectiveOfflineAuthorizationExpiresAt"'
Require-Pattern "offline policy requires an attested authoritative expiry" $policy 'requireAuthoritativeOfflineExpiry[\s\S]*offline_attestation_required[\s\S]*authoritativeOfflineExpiry\s*<\s*effectiveExpiry'
Require-Pattern "trusted state v4 persists an integrity-bound offline expiry" $store 'CurrentFormatVersion\s*=\s*4[\s\S]*EffectiveOfflineAuthorizationExpiresAt[\s\S]*ProtectedOfflineAuthorizationBinding[\s\S]*HasValidOfflineAuthorizationBinding'
Require-Pattern "legacy v1/v2/v3 trusted state remains online-readable without offline authorization" $store 'PreviousFormatVersion\s*=\s*3[\s\S]*OlderFormatVersion\s*=\s*2[\s\S]*LegacyFormatVersion\s*=\s*1[\s\S]*ValidateOnlineReceipt'
Require-Pattern "offline binding is scoped to a random process secret" $store 'ProcessAuthorizationScope\s*=\s*[\s\S]{0,120}CreateProcessAuthorizationScope\(\)[\s\S]*ComputeOfflineAuthorizationBinding[\s\S]*AppendBoundedValue\(material,\s*ProcessAuthorizationScope\)'
if ($store -match 'DataMember\([\s\S]{0,100}processAuthorizationScope') {
    Fail "process authorization scope must never be serialized"
}
else {
    Pass "process authorization scope is not serialized"
}
Require-Pattern "trusted store persists server and local receipt clocks" $store 'LastOkLocalAt\s*=\s*candidate\.LastOkLocalAt[\s\S]*LastOkServerAt\s*=\s*candidate\.LastOkServerAt'
Require-Pattern "trusted state reads and writes are byte-bounded" $store 'MaximumTrustedDeviceStateBytes\s*=\s*64\s*\*\s*1024[\s\S]*TryReadBoundedUtf8[\s\S]*GetByteCount\(serialized\)'
Require-Pattern "exact first-login retry reuses its active generation" ($store + $bootstrap) 'TryGetReusableGenerationId[\s\S]*IsExactFirstLoginResponse[\s\S]*ExpectedCurrentState\.Fingerprint[\s\S]*activatedGenerationId\s*=\s*reusableGenerationId'
Require-Pattern "process trusted time is bounded and keyed only by clock domain and frequency" $store 'MaximumProcessClockScopes\s*=\s*16[\s\S]*MaximumMonotonicClockDomainCharacters\s*=\s*128[\s\S]*class\s+ProcessClockKey[\s\S]*ClockDomain[\s\S]*Frequency[\s\S]*class\s+ProcessTrustedTimeHighWater'
Require-Pattern "active generation anchor is separated from the process clock high-water" $store 'class\s+ProcessAuthorizationClockState[\s\S]*HighWaters[\s\S]*ActiveAnchor[\s\S]*WithoutActiveAnchor'
Require-Pattern "clock-state preparation clones before capacity checks and never evicts" $store 'TryPrepareProcessAuthorizationClockState[\s\S]*new\s+Dictionary<[\s\S]*currentState\.HighWaters[\s\S]*TryGetValue\([\s\S]*existingHighWater[\s\S]*Count\s*>=\s*MaximumProcessClockScopes[\s\S]*new\s+ProcessTrustedTimeHighWater[\s\S]*new\s+ProcessAuthorizationClockState'
Require-Pattern "clock-state preparation rejects active domain or frequency mismatch" $store 'activeAnchor\s*!=\s*null[\s\S]{0,500}activeAnchor\.ClockDomain[\s\S]{0,260}activeAnchor\.Frequency\s*!=\s*_monotonicFrequency[\s\S]{0,120}return\s+false'
Require-Pattern "Clear and TryClear remove only the active authorization anchor" $store 'public\s+void\s+Clear\(\)[\s\S]{0,350}WithoutActiveAnchor\(\)[\s\S]*public\s+bool\s+TryClear\([\s\S]{0,600}WithoutActiveAnchor\(\)'
$clockPublishCount = [regex]::Matches(
    $store,
    'SaveState\([\s\S]{0,180}_processAuthorizationClockState\s*=\s*preparedClockState').Count
if ($clockPublishCount -ne 2) {
    Fail "first login and heartbeat must publish prepared clock state only after SaveState; observed=$clockPublishCount"
} else {
    Pass "first login and heartbeat publish prepared clock state only after SaveState"
}
$clockResetCount = [regex]::Matches(
    $store,
    'ProcessAuthorizationClockState\.CreateEmpty\(\)').Count
if ($clockResetCount -ne 1) {
    Fail "process trusted-time high-water must have no runtime or test reset path; observed initializers=$clockResetCount"
} else {
    Pass "process trusted-time high-water has no reset path after initialization"
}
Require-Pattern "only the non-shipping UI harness receives WPF internal test access" $wpfProject 'InternalsVisibleToAttribute[\s\S]{0,180}Win7POS\.Wpf\.UiSmokeHarness'
Require-Pattern "authorization lease dynamic smoke is wired into the UI harness" $uiHarness '--authorization-lease-smoke[\s\S]*AuthorizationLeaseWpfSmoke\.RunAsync'
Require-Pattern "authorization lease restart prepare and verify are separate harness modes" $uiHarness '--authorization-lease-restart-prepare[\s\S]*--authorization-lease-restart-verify[\s\S]*PrepareRestartProbeAsync[\s\S]*VerifyRestartProbeAsync'
Require-Pattern "clock capacity is exercised in a separate process harness" $uiHarness '--authorization-lease-clock-capacity-smoke[\s\S]*RunClockCapacityAsync'
Require-Pattern "seeded authorization modes require an explicit trusted-session seed" $uiHarness 'authorizationLeaseModeRequiresSeed[\s\S]{0,500}!seedTrustedSession[\s\S]{0,300}requires --seed-trusted-session'
Require-Pattern "restart verification explicitly forbids reseeding prepared state" $uiHarness 'authorizationLeaseRestartVerify\s*&&\s*seedTrustedSession[\s\S]{0,300}without --seed-trusted-session'
Require-Pattern "authorization artifacts and unhandled errors use diagnostics outside the data root" $uiHarness 'EnsureAuthorizationLeaseDiagnosticsPath[\s\S]*harnessErrorPath[\s\S]*artifactDirectory\s*=\s*authorizationLeaseMode\s*\?\s*diagnosticsDir\s*:\s*dataDir'
Require-Pattern "seeded QA data rejects non-empty roots with entry names" $uiHarness 'requireEmpty\s*&&\s*entries\.Length\s*>\s*0[\s\S]{0,500}Found:[\s\S]{0,500}string\.Join'
Require-Pattern "persistent restart state is validated without the empty-directory seed guard" $uiHarness 'requirePreparedState\s*&&\s*entries\.Length\s*==\s*0[\s\S]{0,250}non-empty prepared QA data directory'
Require-Pattern "wrong PIN dynamic regression leaves the generation uncommitted" $authorizationSmoke 'LoginAsync\(username,\s*WrongPin\)[\s\S]{0,900}sync_generation_inactive'
Require-Pattern "epoch and generation changes are denied dynamically" $authorizationSmoke 'InvalidateAuthorizationState\(\)[\s\S]{0,900}sync_generation_changed[\s\S]{0,1500}qa-auth-generation-2[\s\S]{0,900}sync_generation_changed'
Require-Pattern "successful PIN primes a monotonic authorization high-water" $authorizationSmoke 'LoginAsync\(username,\s*CorrectPin\)[\s\S]{0,1400}successful PIN did not prime[\s\S]{0,3200}clock_rollback'
Require-Pattern "dynamic smoke denies legacy Admin and tampered offline state" $authorizationSmoke 'legacy Admin response synthesized an offline lease[\s\S]*tampered expiry retained offline authorization'
Require-Pattern "dynamic smoke denies v1/v2/v3 state after reread" $authorizationSmoke 'VerifyLegacyStateReread[\s\S]*formatVersion:\s*3[\s\S]*legacy v[\s\S]*offline_attestation_required'
Require-Pattern "dynamic smoke preserves the bound through retry and concurrent heartbeat CAS" $authorizationSmoke 'first-login retry after a lost response[\s\S]*heartbeat extended the authoritative offline expiry[\s\S]*concurrent heartbeat receipts did not resolve with one CAS winner'
Require-Pattern "dynamic smoke denies sale and publication sinks before any write" $authorizationSmoke 'deniedPermissionService\.Demand[\s\S]{0,500}InsertUnauthorizedSaleAndOutbox[\s\S]{0,500}CountSaleRows\(factory\)\s*==\s*salesBefore[\s\S]{0,300}CountSalesOutboxRows\(factory\)\s*==\s*outboxBefore'
Require-Pattern "dynamic smoke freezes wall time and advances injected monotonic time to exact expiry" $authorizationSmoke 'frozenClock[\s\S]*monotonicTicks[\s\S]*TimeSpan\.TicksPerSecond[\s\S]*offline_lease_expired'
Require-Pattern "dynamic smoke denies frozen-clock sale and outbox persistence" $authorizationSmoke 'frozenPermissionService\.Demand[\s\S]{0,500}InsertUnauthorizedSaleAndOutbox[\s\S]{0,700}frozenClockDeniedBeforeSink[\s\S]{0,300}frozenSalesBefore[\s\S]{0,300}frozenOutboxBefore'
Require-Pattern "dynamic smoke fails closed on monotonic regression, provider failure and overflow" $authorizationSmoke 'monotonic counter regression did not fail closed[\s\S]*monotonic provider failure did not fail closed[\s\S]*invalid monotonic frequency did not fail closed[\s\S]*monotonic elapsed-time overflow did not fail closed'
Require-Pattern "dynamic smoke advances trusted time across the asynchronous generation check" $authorizationSmoke 'preflightClockGuard[\s\S]*preflightTicks[\s\S]*preflightExpiry[\s\S]*offline_lease_expired[\s\S]*preflight generation check froze authorization time'
Require-Pattern "dynamic smoke counts local activation delay from the authenticated receipt" $authorizationSmoke 'capturedBeforeActivation[\s\S]*activationDelayTicks\s*=\s*\(activationDelayExpiry\s*-\s*activationDelayServerAt\)\.Ticks[\s\S]*SaveFirstLogin[\s\S]*offline_lease_expired[\s\S]*expired local activation persisted'
Require-Pattern "dynamic smoke anchors trusted time at first-login receipt before the first offline login" $authorizationSmoke 'firstUseStore[\s\S]*SaveFirstLogin[\s\S]*firstUseTicks\s*=\s*\(firstUseExpiry\s*-\s*firstUseServerAt\)\.Ticks[\s\S]*LoginResult\.AuthorizationExpired[\s\S]*frozen wall clock allowed the first offline login'
Require-Pattern "dynamic smoke proves retry and heartbeat cannot reset the receipt clock" $authorizationSmoke 'qa-auth-retry-clock[\s\S]*lost-response retry reset the trusted receipt clock[\s\S]*qa-auth-heartbeat-clock[\s\S]*non-advancing heartbeat reset the trusted receipt clock'
Require-Pattern "independent retry, heartbeat and preflight clocks never move backwards" $authorizationSmoke 'retryClockTicks\s*=\s*0[\s\S]*retryClockTicks\s*=\s*TimeSpan\.FromSeconds\(2\)\.Ticks[\s\S]*retryClockTicks\s*=\s*\(firstUseExpiry\s*-\s*firstUseServerAt\)\.Ticks[\s\S]*heartbeatClockTicks\s*=\s*0[\s\S]*heartbeatClockTicks\s*=\s*TimeSpan\.FromSeconds\(2\)\.Ticks[\s\S]*heartbeatClockTicks\s*=\s*\(firstUseExpiry\s*-\s*firstUseServerAt\)\.Ticks[\s\S]*betweenPreflightsTicks\s*=\s*0[\s\S]*betweenPreflightsTicks\s*=\s*\(firstUseExpiry\s*-\s*firstUseServerAt\)\.Ticks'
Require-Pattern "dynamic smoke rejects heartbeat-mutated response replay across generations and clearing" $authorizationSmoke 'qa-cross-generation-g1[\s\S]*qa-cross-generation-heartbeat-token[\s\S]*qa-cross-generation-g2[\s\S]*qa-cross-generation-g3-clear[\s\S]*qa-cross-generation-g3-try-clear[\s\S]*offline_lease_expired'
Require-Pattern "dynamic smoke proves fresh recovery and failed-save non-publication" $authorizationSmoke 'qa-cross-generation-fresh-recovery[\s\S]*qa-cross-generation-failed-save[\s\S]*qa-cross-generation-save-failure-control[\s\S]*failed SaveState published'
Require-Pattern "isolated clock smoke fills sixteen scopes, denies the seventeenth and reuses the first" $authorizationSmoke 'RunClockCapacityAsync[\s\S]*index\s*<\s*16[\s\S]*qa-clock-capacity-16[\s\S]*qa-clock-capacity-generation-00-reuse[\s\S]*saturation evicted'
Require-Pattern "dynamic smoke rejects monotonic regression across preflight tokens" $authorizationSmoke 'orderedFirst[\s\S]*crossPreflightTicks\s*=\s*90[\s\S]*regressedSecond[\s\S]*crossPreflightTicks\s*=\s*100[\s\S]*trusted_time_continuity_lost'
Require-Pattern "unauthorized sink probe reaches the real sale and publication tables if the guard regresses" $authorizationSmoke 'InsertUnauthorizedSaleAndOutbox[\s\S]*INSERT INTO sales\([\s\S]*INSERT INTO sales_sync_outbox\('
Require-Pattern "cross-process restart denies offline authorization before sale persistence" $authorizationSmoke 'PrepareRestartProbeAsync[\s\S]*VerifyRestartProbeAsync[\s\S]*offline_attestation_required[\s\S]*deniedBeforeSink[\s\S]*unauthorizedSaleSinkRows=0[\s\S]*freshOnlineRecovery=True'
Require-Pattern "Windows runner executes prepare and verify in distinct processes" $authorizationSmokeRunner 'authorization-lease-restart-prepare[\s\S]*authorization-lease-restart-verify[\s\S]*prepareInstance[\s\S]*verifyInstance[\s\S]*offlineAttestationAfterRestart'
Require-Pattern "Windows runner separates run-scoped data and diagnostics directories" $authorizationSmokeRunner 'AuthorizationLease\.[\s\S]*main-data[\s\S]*main-diagnostics[\s\S]*restart-data[\s\S]*restart-diagnostics[\s\S]*capacity-data[\s\S]*capacity-diagnostics'
Require-Pattern "Windows runner rejects nested diagnostics before creating either process output stream" $authorizationSmokeRunner 'diagnostics must be outside WIN7POS_DATA_DIR[\s\S]*New-Item\s+-ItemType\s+Directory\s+-Path\s+\$DiagnosticsDirectory'
Require-Pattern "Windows runner redirects process output and result artifacts only to diagnostics" $authorizationSmokeRunner 'Join-Path\s+\$DiagnosticsDirectory\s+\(\$ArtifactName\s*\+\s*"\.stdout\.txt"\)[\s\S]*Join-Path\s+\$DiagnosticsDirectory\s+\(\$ArtifactName\s*\+\s*"\.stderr\.txt"\)[\s\S]*Join-Path\s+\$DiagnosticsDirectory\s+\$ArtifactName'
Require-Pattern "Windows runner lists unexpected seeded data entries before process start" $authorizationSmokeRunner 'Get-ChildItem\s+-LiteralPath\s+\$DataDirectory\s+-Force[\s\S]{0,500}Found:\s+\$entryNames[\s\S]*Start-Process'
Require-Pattern "Windows runner cleans the unique run root only in finally" $authorizationSmokeRunner 'try\s*\{[\s\S]*finally\s*\{[\s\S]*\^AuthorizationLease\\\.\[0-9a-f\]\{32\}\$[\s\S]*Remove-Item\s+-LiteralPath\s+\$runRoot\s+-Recurse\s+-Force'
$mainHarnessInvocation = [regex]::Match(
    $authorizationSmokeRunner,
    '\$smoke\s*=\s*Invoke-AuthorizationHarness[\s\S]*?(?=\r?\n\s*\$prepare\s*=)').Value
$prepareHarnessInvocation = [regex]::Match(
    $authorizationSmokeRunner,
    '\$prepare\s*=\s*Invoke-AuthorizationHarness[\s\S]*?(?=\r?\n\s*\$verify\s*=)').Value
$verifyHarnessInvocation = [regex]::Match(
    $authorizationSmokeRunner,
    '\$verify\s*=\s*Invoke-AuthorizationHarness[\s\S]*?(?=\r?\n\s*\$capacity\s*=)').Value
$capacityHarnessInvocation = [regex]::Match(
    $authorizationSmokeRunner,
    '\$capacity\s*=\s*Invoke-AuthorizationHarness[\s\S]*?(?=\r?\n\s*if\s*\()').Value
if ($mainHarnessInvocation -match '-SeedTrustedSession' -and
    $prepareHarnessInvocation -match '-SeedTrustedSession' -and
    $verifyHarnessInvocation -match '-RequirePreparedData' -and
    $verifyHarnessInvocation -notmatch '-SeedTrustedSession' -and
    $capacityHarnessInvocation -match '-SeedTrustedSession') {
    Pass "harness invocation matrix seeds main/prepare/capacity once and never restart verify"
}
else {
    Fail "harness invocation matrix must seed main/prepare/capacity and reuse restart data without reseeding verify"
}
Require-Pattern "Windows runner executes and validates the isolated bounded clock map" $authorizationSmokeRunner 'authorization-lease-clock-capacity-smoke[\s\S]*trustedClockCapacityFailClosed[\s\S]*trustedClockCapacityNoEviction[\s\S]*trustedClockDomainMismatchDenied[\s\S]*trustedClockInvalidKeyDenied'
Require-Pattern "Windows runner requires monotonic expiry and zero sink rows" $authorizationSmokeRunner 'frozenClockMonotonicExpiry[\s\S]*frozenClockUnauthorizedSaleSinkRows[\s\S]*frozenClockUnauthorizedPublicationOutboxRows[\s\S]*monotonicCounterRegressionDenied[\s\S]*monotonicProviderFailureDenied[\s\S]*monotonicElapsedOverflowDenied[\s\S]*preflightDelayExpiryDenied[\s\S]*activationDelayCountedFromReceipt[\s\S]*firstUseReceiptClockExpiryDenied[\s\S]*firstUseUnauthorizedSaleSinkRows[\s\S]*firstUseUnauthorizedPublicationOutboxRows[\s\S]*firstLoginRetryClockNotReset[\s\S]*heartbeatClockNotReset[\s\S]*betweenPreflightsExpiryDenied[\s\S]*crossPreflightRegressionDenied'
Require-Pattern "Windows runner requires distinct checkpoint counts, rollback, retry and concurrency proofs" $authorizationSmokeRunner 'saleRevocationDemandCount[\s\S]*saleGenerationDemandCount[\s\S]*saleCommitExpiryDemandCount[\s\S]*saleCommitBlockedReaderDemandCount[\s\S]*saleExactRetryIdempotent[\s\S]*concurrentAuthorizedSalesRows[\s\S]*concurrentAuthorizedSalesOutboxRows'
Require-Pattern "bootstrap captures the authenticated receipt before local activation" $bootstrap 'var\s+response\s*=\s*result\.Value[\s\S]{0,900}CaptureOnlineReceiptClock[\s\S]*ActivateAuthenticatedTrustAsync\([\s\S]{0,300}authoritativeReceiptClock'
Require-Pattern "cancelled operator switch rejects durable authority changes dynamically" $authorizationSmoke 'IsSessionBoundToCurrentTrustedIdentityAsync[\s\S]*users\.UpdateAsync[\s\S]*durable authority change left the cached operator session bound[\s\S]*durableAuthorityChangeDenied=True'
Require-Pattern "authorization lease smoke has an explicit zero-hardware boundary" $authorizationSmoke 'hardwareEffects=0'
Require-Pattern "cancelled operator switch reloads and compares durable authority" $main 'IsSessionBoundToCurrentTrustedIdentityAsync[\s\S]{0,1400}GetByUsernameAsync[\s\S]{0,500}HasSameDurableAuthority'
Require-Pattern "durable authority comparison covers role, status, limits and permissions" $main 'HasSameDurableAuthority[\s\S]{0,1700}RoleId[\s\S]{0,500}RoleCode[\s\S]{0,500}IsActive[\s\S]{0,500}RequirePinChange[\s\S]{0,500}MaxDiscountPercent[\s\S]{0,500}CanOverride[\s\S]{0,500}SequenceEqual'
Require-Pattern "runtime guard is internal and cannot be composed ad hoc" $guard 'internal\s+sealed\s+class\s+PosOfflineAuthorizationLeaseGuard'
$syncEvaluateStart = $guard.IndexOf("public PosOfflineAuthorizationLeaseDecision Evaluate(out", [System.StringComparison]::Ordinal)
$preflightStart = $guard.IndexOf("public async Task<PosOfflineAuthorizationLeaseEvaluation> PreflightAsync()", [System.StringComparison]::Ordinal)
$commitStart = $guard.IndexOf("public async Task<PosOfflineAuthorizationLeaseEvaluation> CommitAuthenticationAsync", [System.StringComparison]::Ordinal)
$evaluationClassStart = $guard.IndexOf("internal sealed class PosOfflineAuthorizationLeaseEvaluation", [System.StringComparison]::Ordinal)
if ($syncEvaluateStart -lt 0 -or $preflightStart -le $syncEvaluateStart -or
    $commitStart -le $preflightStart -or $evaluationClassStart -le $commitStart) {
    Fail "authorization guard method boundaries are missing"
} else {
    $syncEvaluateBody = $guard.Substring($syncEvaluateStart, $preflightStart - $syncEvaluateStart)
    $preflightBody = $guard.Substring($preflightStart, $commitStart - $preflightStart)
    $commitBody = $guard.Substring($commitStart, $evaluationClassStart - $commitStart)
    $syncCapture = $syncEvaluateBody.IndexOf("TryCaptureAuthorizationEpoch", [System.StringComparison]::Ordinal)
    $syncStore = $syncEvaluateBody.IndexOf("_store.TryRead", [System.StringComparison]::Ordinal)
    $preflightCapture = $preflightBody.IndexOf("TryCaptureAuthorizationEpoch", [System.StringComparison]::Ordinal)
    $preflightStore = $preflightBody.IndexOf("_store.TryRead", [System.StringComparison]::Ordinal)
    $preflightDurable = $preflightBody.IndexOf("await _generationIsActive", [System.StringComparison]::Ordinal)
    $preflightLock = $preflightBody.IndexOf("lock (_sync)", $preflightDurable, [System.StringComparison]::Ordinal)
    $preflightEpoch = $preflightBody.IndexOf("IsAuthorizationEpochCurrent", $preflightLock, [System.StringComparison]::Ordinal)
    $preflightReread = $preflightBody.IndexOf("_store.TryReadGeneration", $preflightLock, [System.StringComparison]::Ordinal)
    $commitDurable = $commitBody.IndexOf("await _generationIsActive", [System.StringComparison]::Ordinal)
    $commitLock = $commitBody.IndexOf("lock (_sync)", $commitDurable, [System.StringComparison]::Ordinal)
    $commitReread = $commitBody.IndexOf("_store.TryReadGeneration", $commitLock, [System.StringComparison]::Ordinal)
    $epochAssignment = $commitBody.IndexOf("_validatedAuthorizationEpoch = first.Token.AuthorizationEpoch", [System.StringComparison]::Ordinal)
    $fingerprintAssignment = $commitBody.IndexOf("_validatedGenerationFingerprint = generation.Fingerprint", [System.StringComparison]::Ordinal)
    $finalEpoch = $commitBody.LastIndexOf("IsAuthorizationEpochCurrent", [System.StringComparison]::Ordinal)
    if ($syncCapture -lt 0 -or $syncStore -le $syncCapture -or
        $syncEvaluateBody -notmatch 'HasValidatedScope\(authorizationEpoch,\s*generation\.Fingerprint\)' -or
        $syncEvaluateBody -notmatch 'TryCaptureMonotonicTimestamp[\s\S]*TryAdvanceTrustedAnchor[\s\S]*minimumTrustedServerNow' -or
        $syncEvaluateBody -notmatch 'IsAuthorizationEpochCurrent' -or
        $preflightCapture -lt 0 -or $preflightStore -le $preflightCapture -or
        $preflightDurable -le $preflightStore -or $preflightLock -le $preflightDurable -or
        $preflightEpoch -le $preflightLock -or $preflightReread -le $preflightEpoch -or
        $preflightBody -notmatch 'firstMonotonicTimestamp[\s\S]*secondMonotonicTimestamp[\s\S]*TryAdvanceTrustedAnchor' -or
        $preflightBody -match '_validatedAuthorizationEpoch\s*=\s*authorizationEpoch' -or
        $preflightBody -match '_validatedGenerationFingerprint\s*=\s*generation\.Fingerprint' -or
        $commitDurable -lt 0 -or $commitLock -le $commitDurable -or
        $commitReread -le $commitLock -or $epochAssignment -le $commitReread -or
        $fingerprintAssignment -le $commitReread -or
        $finalEpoch -le $epochAssignment -or
        $commitBody -notmatch 'first\.Token\.MonotonicTimestamp[\s\S]*second\.Token\.MonotonicTimestamp[\s\S]*firstLowerBound[\s\S]*secondLowerBound[\s\S]*scopedLowerBound' -or
        $commitBody -notmatch 'first\.Token\.AuthorizationEpoch\s*==\s*second\.Token\.AuthorizationEpoch' -or
        $commitBody -notmatch 'first\.Token\.GenerationFingerprint[\s\S]{0,180}second\.Token\.GenerationFingerprint') {
        Fail "runtime guard must keep preflight non-mutating and commit only a token-matched epoch plus generation"
    } else {
        Pass "runtime guard uses non-mutating preflight and atomic token-matched commit"
    }
}

if ($revocationLatch -notmatch 'TryCaptureAuthorizationEpoch[\s\S]{0,260}_authorizationMaintenanceDepth\s*==\s*0' -or
    $revocationLatch -notmatch 'IsAuthorizationEpochCurrent[\s\S]{0,300}_authorizationMaintenanceDepth\s*==\s*0[\s\S]{0,100}_authorizationEpoch\s*==\s*epoch' -or
    $revocationLatch -notmatch 'CommitIfAuthorizationCurrent\([\s\S]{0,1200}lock \(Gate\)[\s\S]{0,420}_authorizationEpoch\s*!=\s*authorizationEpoch[\s\S]{0,260}RevokedFingerprints\.Contains[\s\S]{0,220}demandFinalAuthority\(\)[\s\S]{0,100}commit\(\)' -or
    $revocationLatch -notmatch 'TryInvalidateAuthorizationState\(long expectedEpoch\)[\s\S]{0,420}_authorizationMaintenanceDepth\s*>\s*0[\s\S]{0,120}_authorizationEpoch\s*!=\s*expectedEpoch[\s\S]{0,140}_authorizationEpoch\+\+' -or
    $revocationLatch -notmatch 'public static void InvalidateAuthorizationState\(\)[\s\S]{0,700}lock \(Gate\)[\s\S]{0,120}_authorizationEpoch\+\+' -or
    $revocationLatch -notmatch 'Revoke\(OnlineSyncGeneration generation\)[\s\S]{0,120}RevokeFingerprint\(generation\?\.Fingerprint\)' -or
    $revocationLatch -notmatch 'RevokeFingerprint\(string fingerprint\)[\s\S]{0,500}RevokeFingerprintCore\(fingerprint\)' -or
    $revocationLatch -notmatch 'RevokeFingerprintCore\(string fingerprint\)[\s\S]{0,260}_authorizationEpoch\+\+[\s\S]{0,800}RevokedFingerprints\.Add' -or
    $revocationLatch -notmatch 'MaximumRevokedFingerprints[\s\S]*_revocationHistoryOverflowed\s*=\s*true' -or
    $revocationLatch -match 'InvalidateAuthorizationState\(\)[\s\S]{0,500}AuthorizationUseGate\.Wait' -or
    $revocationLatch -match 'RevokeFingerprint\(string fingerprint\)[\s\S]{0,500}AuthorizationUseGate\.Wait') {
    Fail "authorization epoch latch is not publish-first, maintenance-aware and bounded"
} else {
    Pass "authorization epoch latch is publish-first, maintenance-aware and bounded"
}

Require-Pattern "sale authorization token is sealed, internally constructed and binds the full authority" $authorizationCommitGuard 'public\s+sealed\s+class\s+SaleAuthorizationCommitGuard[\s\S]*internal\s+SaleAuthorizationCommitGuard\([\s\S]*AuthorizationEpoch[\s\S]*GenerationFingerprint[\s\S]*GenerationId[\s\S]*OperatorId[\s\S]*ShopCode[\s\S]*ShopDeviceId[\s\S]*ShopId[\s\S]*StaffCredentialVersion[\s\S]*StaffId[\s\S]*CommitIfStillValid'
Require-Pattern "operator authorization-use lease exposes only the concrete repository capability" $operatorSessionContract 'internal\s+interface\s+IPosAuthorizationUseLease\s*:\s*IDisposable[\s\S]{0,160}SaleAuthorizationCommitGuard\s+CommitGuard'
Require-Pattern "diagnostic sale entry is internal while the authorized entry requires the sealed token" $saleRepository 'internal\s+Task<long>\s+InsertSaleAsync[\s\S]*public\s+Task<long>\s+InsertAuthorizedSaleAsync\([\s\S]{0,220}SaleAuthorizationCommitGuard'
if ($dataProject -match 'InternalsVisibleTo\s+Include="Win7POS\.Wpf"') {
    Fail "production WPF must not see the unguarded Data internals"
}
else {
    Pass "production WPF cannot reach the unguarded Data internals"
}
Require-Pattern "workflow accepts only the concrete operator authority" $workflow 'CompleteSaleAsync\([\s\S]{0,180}OperatorSession\s+operatorSession'
Require-Pattern "production operator authority cannot be constructed outside the trusted WPF assembly" $session 'internal\s+OperatorSession\(\s*UserRepository\s+userRepo\s*,\s*SecurityRepository\s+securityRepo\s*\)'
if ($session -match 'public\s+OperatorSession\s*\(') {
    Fail "operator authority must not expose a public constructor"
}
else {
    Pass "operator authority exposes no public constructor"
}
Require-Pattern "workflow acquires the authorization capability after its cart gate and releases it before post-commit work" $workflow '_gate\.WaitAsync[\s\S]*BeginAuthorizationUseAsync[\s\S]*using \(authorizationUse\)[\s\S]*authorizationUse\.CommitGuard[\s\S]*InsertAuthorizedSaleAsync\([\s\S]*QueueSalesOutboxSyncNoThrow'
if ($workflow -match '\.PayCashAsync\(') {
    Fail "workflow must not bypass the authorized repository sale boundary"
}
else {
    Pass "workflow cannot reach the legacy PayCashAsync sale boundary"
}
Require-Pattern "sale writer drains readers before its provider transaction and guarded durable commit" $saleWriter 'OpenAsync\(\)[\s\S]{0,300}AcquireExclusiveCommitFence\(conn\)[\s\S]{0,180}conn\.BeginTransaction\(\)[\s\S]*authorizationCommitGuard\?\.DemandStillValid\(\)[\s\S]*INSERT INTO sales\([\s\S]*authorizationCommitGuard\.CommitIfStillValid\([\s\S]{0,180}DurableCommitSafetyBudget[\s\S]{0,120}commitTransaction'
Require-Pattern "writer preserves ambiguous COMMIT failures for exact replay" $saleWriter 'transactionCommitted\s*=\s*false[\s\S]*tx\.Commit\(\)[\s\S]*transactionCommitted\s*=\s*true[\s\S]*if \(!transactionCommitted\)[\s\S]*tx\.Rollback\(\)'
Require-Pattern "exact replay keeps a zero-headroom guarded commit" $saleWriter 'replaySaleId\.HasValue[\s\S]{0,700}authorizationCommitGuard\.CommitIfStillValid\(\s*\(\)\s*=>\s*CommitExactSaleReplay'
Require-Pattern "SQLite factory establishes a bounded EXCLUSIVE commit fence" $connectionFactory 'ExpectedLockingMode\s*=\s*"normal"[\s\S]*DurableCommitSafetyBudget\s*=[\s\S]{0,120}TimeSpan\.FromSeconds\(10\)[\s\S]*AcquireExclusiveCommitFence[\s\S]*SetLockingMode\(connection,\s*"EXCLUSIVE"\)[\s\S]*BEGIN EXCLUSIVE;[\s\S]*COMMIT;'
Require-Pattern "SQLite fence restores NORMAL through a real main-database access" $connectionFactory 'EnsureNormalLockingMode\([\s\S]{0,600}SetLockingMode\(connection,\s*"NORMAL"\)[\s\S]{0,300}TouchMainDatabase\(connection\)[\s\S]*SELECT 1 FROM main\.sqlite_schema'
Require-Pattern "SQLite fence quarantines a pooled connection when cleanup cannot be proven" $connectionFactory 'TryRestoreNormalLockingMode[\s\S]{0,800}ClearPool\(connection\)[\s\S]{0,180}ClearAllPools\(\)'
Require-Pattern "runtime policy reasserts NORMAL locking on sync and async pooled opens" $connectionFactory 'ApplyAndVerifyRuntimePolicy\([\s\S]{0,500}EnsureNormalLockingMode\(connection\)[\s\S]*ApplyAndVerifyRuntimePolicyAsync\([\s\S]{0,500}EnsureNormalLockingModeAsync'
Require-Pattern "commit capability propagates explicit nonnegative durability headroom" $authorizationCommitGuard 'Action<TimeSpan,\s*Action>[\s\S]*CommitIfStillValid\(Action commit\)[\s\S]*TimeSpan\.Zero[\s\S]*CommitIfStillValid\(\s*TimeSpan minimumRemaining[\s\S]*minimumRemaining\s*<\s*TimeSpan\.Zero'
Require-Pattern "operator session applies the same headroom before and inside the revocation latch" $session 'CommitAuthorizationUseStillValid\([\s\S]*TimeSpan minimumRemaining[\s\S]*DemandAuthorizationUseStillValid\([\s\S]{0,500}minimumRemaining[\s\S]*CommitIfAuthorizationCurrent\([\s\S]{0,800}DemandCommitExpiryStillValid\([\s\S]{0,180}minimumRemaining'
Require-Pattern "commit expiry proof budgets from the effective trusted-now snapshot" $guard 'Evaluate\(\s*TimeSpan minimumRemaining\)[\s\S]*var localNow\s*=\s*_utcNow\(\)[\s\S]*currentDecision[\s\S]*EstimatedServerNow\.Value\.Add\([\s\S]{0,160}minimumRemaining[\s\S]*requiredTrustedServerNow'
Require-Pattern "commit authority prevalidates outside the latch and rechecks pure expiry and authority inside it" $session 'CommitAuthorizationUseStillValid\([\s\S]{0,1200}DemandAuthorizationUseStillValid\([\s\S]{0,600}CommitIfAuthorizationCurrent\([\s\S]{0,700}inside_commit_gate[\s\S]{0,300}DemandCommitExpiryStillValid\([\s\S]{0,300}DemandOperatorAuthorityStillCurrent'
Require-Pattern "commit expiry proof freezes the lease and uses the injected monotonic domain without locks" $guard 'class\s+PosAuthorizationCommitExpiryGuard[\s\S]*_frozenSession[\s\S]*_anchorTimestamp[\s\S]*_monotonicTimestamp\(\)[\s\S]*currentTimestamp\s*<\s*_anchorTimestamp[\s\S]*PosOfflineAuthorizationLeasePolicy\.Evaluate\('
Require-Pattern "operator replacement and logout share the commit linearization gate" ($revocationLatch + $session) 'ChangeOperatorAuthority\([\s\S]{0,500}lock \(Gate\)[\s\S]{0,300}mutation\(\)[\s\S]*LogoutInternal[\s\S]{0,500}invalidateAuthorization:\s*true'
Require-Pattern "operator authority revisions publish atomically on x86" $session 'Interlocked\.Increment\(\s*ref\s+_operatorAuthorityVersion\)'
if ($session -match '_operatorAuthorityVersion\s*\+\+') {
    Fail "operator authority revisions must not use non-atomic long increments on x86"
}
else {
    Pass "operator authority revisions avoid non-atomic long increments on x86"
}
Require-Pattern "normal login atomically binds the committed epoch and fingerprint under the revocation gate" ($revocationLatch + $session) 'TryChangeOperatorAuthority\([\s\S]{0,800}_authorizationEpoch\s*!=\s*authorizationEpoch[\s\S]{0,300}RevokedFingerprints\.Contains\(normalized\)[\s\S]{0,300}mutation\(\)[\s\S]*committedEvaluation\.Token\.AuthorizationEpoch[\s\S]{0,300}committedEvaluation\.Token\.GenerationFingerprint'
Require-Pattern "login snapshots prior operator authority before its first asynchronous boundary" $session 'operatorAuthorityVersionAtLoginStart\s*=\s*[\s\S]{0,120}Interlocked\.Read\([\s\S]{0,220}PosTrustedDeviceSession[\s\S]{0,500}await\s+_authorizationLeaseGuard\.PreflightAsync'
Require-Pattern "asynchronous login denials are never attributed to a concurrently replaced operator" $session '!authorization\.Allowed[\s\S]{0,180}LogAuthorizationDenied\(authorization\.Code,\s*null\)[\s\S]*!committedEvaluation\.Decision\.Allowed[\s\S]{0,420}LogAuthorizationDenied\([\s\S]{0,220}null\)'
Require-Pattern "failed normal-login bind clears only the authority present when that login started" $session '!operatorAuthorityChanged[\s\S]{0,300}HandleAuthorizationUseDenied\(\s*code,\s*operatorAuthorityVersionAtLoginStart\)[\s\S]{0,160}LoginResult\.AuthorizationExpired'
Require-Pattern "local recovery is explicitly excluded from POS authorization and invalidates cached authority" $session 'requireLocalRecoveryUser[\s\S]*_currentUserCanUsePosAuthorization\s*=\s*false[\s\S]{0,300}invalidateAuthorization:\s*requireLocalRecoveryUser[\s\S]*BeginAuthorizationUseAsync[\s\S]*!_currentUserCanUsePosAuthorization[\s\S]{0,300}PosAuthorizationLeaseException'
Require-Pattern "synchronous lease validation version-fences both allow and forced logout" $session 'EnsureAuthorizationValid\(\)[\s\S]{0,260}expectedOperatorAuthorityVersion[\s\S]{0,420}decision\.Allowed[\s\S]{0,180}_currentUserCanUsePosAuthorization[\s\S]{0,180}expectedOperatorAuthorityVersion[\s\S]{0,650}HandleAuthorizationUseDenied\(\s*decision\.Code,\s*expectedOperatorAuthorityVersion\)'
Require-Pattern "permission service consumes only an epoch-and-version-bound concrete operator snapshot" ($permission + $session) 'TryGetAuthorizationBoundUser\([\s\S]*_operatorSession\.TryGetAuthorizationBoundUser[\s\S]*TryGetAuthorizationBoundUser\([\s\S]{0,320}expectedAuthorizationEpoch[\s\S]{0,220}expectedOperatorAuthorityVersion[\s\S]{0,320}EnsureAuthorizationValid\(\)[\s\S]{0,700}TryReadOperatorAuthorityIf\(\s*expectedAuthorizationEpoch[\s\S]{0,300}expectedOperatorAuthorityVersion'
if ($permission -match '_session\.CurrentUser') {
    Fail "permission service must not read a replaceable operator after authorization validation"
}
else {
    Pass "permission service never reads a replaceable operator after authorization validation"
}
Require-Pattern "denied authorization use binds its operator version and releases the global gate before returning" $session 'catch \(PosAuthorizationLeaseException ex\)[\s\S]{0,180}BindOperatorAuthorityVersion\([\s\S]{0,120}latchLease\.Dispose\(\)[\s\S]{0,120}throw;'
Require-Pattern "authorization-use exceptions preserve the authority version through every later demand" ($permission + $session) 'OperatorAuthorityVersion[\s\S]{0,300}BindOperatorAuthorityVersion[\s\S]*AuthorizationUseLease\([\s\S]{0,500}operatorAuthorityVersion[\s\S]*DemandStillValid\([\s\S]{0,900}BindOperatorAuthorityVersion\([\s\S]*CommitIfStillValid\([\s\S]{0,1500}BindOperatorAuthorityVersion\('
Require-Pattern "stale authorization denial conditionally clears only its matching operator authority" $session 'HandleAuthorizationUseDenied\([\s\S]{0,160}expectedOperatorAuthorityVersion[\s\S]{0,500}TryChangeOperatorAuthorityIf\([\s\S]{0,250}Interlocked\.Read\([\s\S]{0,160}expectedOperatorAuthorityVersion'
Require-Pattern "stale authorization denial remains audited without attribution to replacement authority" $session 'if \(!invalidated\)[\s\S]{0,180}LogAuthorizationDenied\(normalizedCode,\s*null\)[\s\S]{0,80}return;'
Require-Pattern "conditional operator-authority replacement is atomic with authorization invalidation" $revocationLatch 'TryChangeOperatorAuthorityIf\([\s\S]{0,700}lock \(Gate\)[\s\S]{0,180}!isExpectedAuthority\(\)[\s\S]{0,180}_authorizationEpoch\+\+[\s\S]{0,120}mutation\(\)'
Require-Pattern "authorization-bound operator snapshots recheck epoch and authority under the revocation gate" $revocationLatch 'TryReadOperatorAuthorityIf\([\s\S]{0,700}lock \(Gate\)[\s\S]{0,220}_authorizationEpoch\s*!=\s*authorizationEpoch[\s\S]{0,180}!isExpectedAuthority\(\)[\s\S]{0,120}capture\(\)'
Require-Pattern "every authorization denial carries its operator version and is handled only after repository rollback and both application gates release" $workflow 'catch \(PosAuthorizationLeaseException ex\)[\s\S]{0,420}authorizationDeniedCode\s*=\s*ex\.Code[\s\S]{0,220}authorizationDeniedOperatorAuthorityVersion\s*=\s*ex\.OperatorAuthorityVersion[\s\S]*finally[\s\S]{0,300}_gate\.Release\(\)[\s\S]{0,600}HandleAuthorizationUseDenied\(\s*authorizationDeniedCode,\s*authorizationDeniedOperatorAuthorityVersion'
Require-Pattern "real sink smoke revokes and switches generation at the pre-COMMIT guard" $authorizationSmoke 'SetAuthorizationUseTestHookForTesting[\s\S]*demand == 4[\s\S]*InvalidateAuthorizationState[\s\S]*generation-target[\s\S]*generationDemandCount'
Require-Pattern "begin and writer denial callbacks run only after workflow and authorization-use gates release" ($authorizationSmoke + $authorizationSmokeRunner) 'throwingBeginDenialSubscriber[\s\S]*beginDenialCallbackObservedReleasedGates[\s\S]*throwingDenialSubscriber[\s\S]*denialCallbackObservedReleasedGates[\s\S]*denialCallbacksAfterGateRelease'
Require-Pattern "real sink smoke denies exact expiry inside the COMMIT gate" ($authorizationSmoke + $authorizationSmokeRunner) 'inside_commit_gate[\s\S]*commitExpiryElapsedTicks[\s\S]*saleCommitExpiryRaceSinkRows[\s\S]*saleCommitExpiryRaceOutboxRows'
Require-Pattern "real sink smoke drains a SHARED reader before expiry validation and restores the fence" ($authorizationSmoke + $authorizationSmokeRunner) 'BeginTransaction\(\s*deferred:\s*true\)[\s\S]*WaitForPendingExclusiveLockAsync[\s\S]*blockedReaderDemandCount[\s\S]*blockedReaderElapsedTicks[\s\S]*saleCommitBlockedReaderExpiryDenied[\s\S]*saleCommitBlockedReaderSinkRows[\s\S]*saleCommitBlockedReaderOutboxRows[\s\S]*saleCommitFenceReleased'
Require-Pattern "headroom smoke enforces budget plus one, exact budget and below-budget boundaries" $authorizationSmoke 'remainingTicks\s*>\s*budget\.Ticks\s*\+\s*1[\s\S]*remainingTicks\s*-[\s\S]*budget\.Ticks\s*-[\s\S]*1[\s\S]*Evaluate\(budget\)\.Allowed[\s\S]*clockTicks\+\+[\s\S]*offline_lease_expired[\s\S]*clockTicks\+\+[\s\S]*offline_lease_expired'
Require-Pattern "real sink smoke proves in-gate revocation cannot return before COMMIT" ($authorizationSmoke + $authorizationSmokeRunner) 'inside_commit_gate[\s\S]*postValidationRevocation[\s\S]*revocationReturned[\s\S]*saleCommitRevocationLinearized'
Require-Pattern "Core/Data regression blocks authorization before writes while a SHARED reader drains" $saleWriterTests 'AuthorizedSale_SharedReaderCannotCarryValidatedCommitPastExactExpiry[\s\S]*BeginTransaction\(deferred:\s*true\)[\s\S]*WaitForPendingExclusiveLockAsync[\s\S]*guard\.Expire\(\)[\s\S]*sale_lines[\s\S]*local_stock_movements[\s\S]*sales_sync_outbox'
Require-Pattern "provider regression preserves managed transactions and restores NORMAL locking" $connectionPolicyTests 'ExclusiveCommitFence_DrainsSharedReader_PreservesProviderTransaction_AndRestoresNormalMode[\s\S]*AcquireExclusiveCommitFence[\s\S]*locking_mode[\s\S]*BeginTransaction\(\)[\s\S]*AssertConnectionPolicy'
Require-Pattern "workflow retains and reuses one pending identity after an ambiguous COMMIT" $workflow 'PendingSaleAttempt\s+_pendingSaleAttempt[\s\S]*MatchesContent\([\s\S]*MatchesAuthority\([\s\S]*ApplyIdentity\(sale\)[\s\S]*InsertAuthorizedSaleAsync\([\s\S]*ReferenceEquals\([\s\S]*_pendingSaleAttempt\s*=\s*null'
Require-Pattern "pending identity binds the complete authority" $workflow 'class\s+PendingSaleAttempt[\s\S]*AuthorizationEpoch[\s\S]*GenerationFingerprint[\s\S]*GenerationId[\s\S]*OperatorId[\s\S]*ShopCode[\s\S]*ShopDeviceId[\s\S]*ShopId[\s\S]*StaffCredentialVersion[\s\S]*StaffId[\s\S]*MatchesAuthority\('
Require-Pattern "pending identity fails closed on content or authority mismatch" $workflow '_pendingSaleAttempt\s*!=\s*null[\s\S]{0,500}!_pendingSaleAttempt\.MatchesContent\([\s\S]{0,300}!_pendingSaleAttempt\.MatchesAuthority\([\s\S]{0,300}richiesta la riconciliazione'
Require-Pattern "cart mutations invalidate a pending identity only after the frozen cart changes" $workflow 'InvalidatePendingSaleAttemptIfCartChanged\([\s\S]*MatchesCart\(_session\.Lines\)[\s\S]*AddManualPriceAsync[\s\S]*_session\.AddManualPriceAsync[\s\S]*InvalidatePendingSaleAttemptIfCartChanged\(\)[\s\S]*ClearCartAsync[\s\S]*_session\.Clear\(\)[\s\S]*InvalidatePendingSaleAttemptIfCartChanged\(\)'
Require-Pattern "real workflow retry survives a lost post-COMMIT result without duplicates" ($authorizationSmoke + $authorizationSmokeRunner) 'after_commit_before_return[\s\S]*qa_ambiguous_commit_result[\s\S]*QA-AUTH-AMBIGUOUS-REPLACEMENT[\s\S]*saleAmbiguousCommitRetryIdempotent'
Require-Pattern "real workflow abandons stale pending identity after cart mutation and rejects authority replacement" ($authorizationSmoke + $authorizationSmokeRunner) 'qa_ambiguous_mutation_result[\s\S]*ClearCartAsync[\s\S]*QA-AUTH-AMBIGUOUS-MUTATION-NEW[\s\S]*qa_ambiguous_authority_result[\s\S]*qa-auth-sale-pending-authority-replacement[\s\S]*saleAmbiguousCartMutationStartsNewIdentity[\s\S]*saleAmbiguousAuthorityMismatchDenied'
Require-Pattern "real login and local-recovery races cannot inherit POS authority" ($authorizationSmoke + $authorizationSmokeRunner) 'before_operator_authority_bind[\s\S]*LoginLocalRecoveryAsync[\s\S]*QA-LOCAL-RECOVERY-DENIED[\s\S]*loginRevocationRaceDenied[\s\S]*localRecoveryCannotInheritPosAuthority'
Require-Pattern "stale login cleanup preserves the concurrently published authority and epoch" ($authorizationSmoke + $authorizationSmokeRunner) 'replacementLoginEpoch[\s\S]*before_operator_authority_bind[\s\S]*SetUserForTesting\([\s\S]{0,120}replacementOperator[\s\S]*epochAfterStaleLoginCleanup[\s\S]*replacementLoginEpoch\s*==\s*epochAfterStaleLoginCleanup[\s\S]*staleLoginCleanupDoesNotClearNewAuthority'
Require-Pattern "stale denial regression preserves usable replacement authority and writes a fresh unattributed audit" ($authorizationSmoke + $authorizationSmokeRunner) 'staleDenialOperatorAuthorityVersion[\s\S]*replacement authority login did not authenticate[\s\S]*staleDenialAuditBefore[\s\S]*HandleAuthorizationUseDenied\([\s\S]{0,180}staleDenialOperatorAuthorityVersion[\s\S]*epochBeforeStaleDenial\s*==\s*epochAfterStaleDenial[\s\S]*WaitForAuthorizationAuditIncrementAsync[\s\S]*staleDenialDoesNotClearNewAuthority'
Require-Pattern "permission snapshot race rejects a concurrently published admin without invalidating it" ($authorizationSmoke + $authorizationSmokeRunner) 'concurrentReplacementAdmin[\s\S]*after_authorization_valid_before_operator_capture[\s\S]*SetUserForTesting\([\s\S]{0,120}concurrentReplacementAdmin[\s\S]*!replacementAdminReceivedUsersManage[\s\S]*permissionReplacementEpoch\s*==[\s\S]{0,120}epochAfterPermissionRace[\s\S]*permissionSnapshotRejectsReplacementAdmin'
Require-Pattern "permission snapshot race rejects a completed revocation with no stale grant" ($authorizationSmoke + $authorizationSmokeRunner) 'permissionRevocationEpoch[\s\S]*after_authorization_valid_before_operator_capture[\s\S]*InvalidateAuthorizationState\(\)[\s\S]*!permissionSurvivedConcurrentRevocation[\s\S]*permissionRevocationEpoch\s*==[\s\S]{0,120}epochAfterPermissionRevocation[\s\S]*permissionSnapshotRejectsConcurrentRevocation'
Require-Pattern "sale-race audit assertions require increments above operation-local baselines" $authorizationSmoke 'expiryAuditBefore[\s\S]*WaitForAuthorizationAuditIncrementAsync\([\s\S]{0,180}expiryAuditBefore[\s\S]*revocationAuditBefore[\s\S]*WaitForAuthorizationAuditIncrementAsync\([\s\S]{0,180}revocationAuditBefore'
Require-Pattern "two-sale smoke deterministically probes the semaphore and workflow serialization" $authorizationSmoke 'firstAuthorizationProbe[\s\S]*queuedAuthorizationProbe[\s\S]*!queuedAuthorizationProbe\.IsCompleted[\s\S]*firstSaleHasLease[\s\S]*secondSaleReachedAuthorizationGate[\s\S]*before_authorization_use_gate[\s\S]*authorizationGateArrivals[\s\S]*secondSaleReachedAuthorizationGate\.Wait[\s\S]*authorizationLeaseEntries\)\s*==\s*2'

$tokenConstructionFiles = @(Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf") -Recurse -File -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    Where-Object { Select-String -Path $_.FullName -Pattern "new SaleAuthorizationCommitGuard\(" -Quiet } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') -replace '\\', '/' })
if ($tokenConstructionFiles.Count -eq 1 -and
    $tokenConstructionFiles[0] -eq "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs") {
    Pass "operator session is the single production capability issuer"
}
else {
    Fail "unexpected sale authorization capability issuer(s): $($tokenConstructionFiles -join ', ')"
}

$guardConstructionFiles = @(Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf") -Recurse -File -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    Where-Object { Select-String -Path $_.FullName -Pattern "new PosOfflineAuthorizationLeaseGuard\(" -Quiet } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') -replace '\\', '/' })
if ($guardConstructionFiles.Count -eq 1 -and
    $guardConstructionFiles[0] -eq "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs") {
    Pass "operator session is the single runtime guard composition point"
}
else {
    Fail "unexpected runtime guard composition point(s): $($guardConstructionFiles -join ', ')"
}

$leaseIndex = $session.IndexOf("await _authorizationLeaseGuard.PreflightAsync()", [System.StringComparison]::Ordinal)
$pinIndex = $session.IndexOf("_userRepo.VerifyPinAsync", [System.StringComparison]::Ordinal)
if ($leaseIndex -ge 0 -and $pinIndex -gt $leaseIndex) {
    Pass "operator login checks lease before local PIN verification"
}
else {
    Fail "operator login must check lease before local PIN verification"
}

$loginStart = $session.IndexOf("private async Task<LoginResult> LoginInternalAsync", [System.StringComparison]::Ordinal)
$loginEnd = $session.IndexOf("public PosOfflineAuthorizationLeaseDecision EvaluateAuthorizationLease()", [System.StringComparison]::Ordinal)
if ($loginStart -lt 0 -or $loginEnd -le $loginStart) {
    Fail "operator login method boundaries are missing"
} else {
    $loginBody = $session.Substring($loginStart, $loginEnd - $loginStart)
    $initialLease = $loginBody.IndexOf("initialEvaluation = await _authorizationLeaseGuard.PreflightAsync()", [System.StringComparison]::Ordinal)
    $pinVerify = $loginBody.IndexOf("_userRepo.VerifyPinAsync", [System.StringComparison]::Ordinal)
    $finalLease = $loginBody.IndexOf("var finalEvaluation = await _authorizationLeaseGuard", [System.StringComparison]::Ordinal)
    $leaseCommit = $loginBody.IndexOf(".CommitAuthenticationAsync(initialEvaluation, finalEvaluation)", [System.StringComparison]::Ordinal)
    $sameGeneration = $loginBody.IndexOf("IsSameTrustedGeneration", [System.StringComparison]::Ordinal)
    $commitUser = $loginBody.IndexOf("_currentUser = result.User", [System.StringComparison]::Ordinal)
    if ($initialLease -lt 0 -or $pinVerify -le $initialLease -or
        $finalLease -le $pinVerify -or $leaseCommit -le $finalLease -or
        $sameGeneration -le $leaseCommit -or
        $commitUser -le $sameGeneration) {
        Fail "operator login must revalidate and atomically commit the exact trusted generation after PIN"
    } else {
        Pass "operator login revalidates and atomically commits the exact trusted generation after PIN"
    }
}

Require-Pattern "normal operator login requires lease and no local-only classification" $session 'LoginAsync[\s\S]{0,500}requireAuthorizationLease:\s*true[\s\S]{0,180}requireLocalRecoveryUser:\s*false'
Require-Pattern "local recovery login bypasses only lease and requires local identity" $session 'LoginLocalRecoveryAsync[\s\S]{0,500}requireAuthorizationLease:\s*false[\s\S]{0,180}requireLocalRecoveryUser:\s*true'
Require-Pattern "normal operator login resolves the exact lease-bound remote mirror" $session '_authorizationLeaseGuard\.PreflightAsync\(\)[\s\S]{0,500}trustedSession\s*=\s*initialEvaluation\.TrustedSession[\s\S]{0,900}FindTrustedRemoteStaffUsernameAsync\([\s\S]{0,500}trustedSession\.StaffCredentialVersion'
Require-Pattern "normal operator login rejects a different username before PIN verification" $session 'string\.Equals\(username,\s*trustedUsername,\s*StringComparison\.Ordinal\)[\s\S]{0,450}return\s+LoginResult\.Failed[\s\S]{0,900}VerifyPinAsync\(username,\s*pin\)'
Require-Pattern "local recovery identity is checked before PIN" $session 'IsLocalRecoveryUserAsync\(username\)[\s\S]{0,700}VerifyPinAsync\(username,\s*pin\)'

Require-Pattern "permission service checks a lease-bound snapshot before permissions" $permission 'TryGetAuthorizationBoundUser\([\s\S]*HasPermission\(user,\s*permissionCode\)'
Require-Pattern "shell permission checks delegate normal authority to the bound snapshot service" $main 'HasCurrentPermission\([\s\S]{0,700}!IsRecoveryMode[\s\S]{0,220}new PermissionService\(session\)[\s\S]{0,120}\.Has\(permissionCode\)'
Require-Pattern "shell recovery permission checks remain explicitly allowlisted" $main 'HasLeaseFreeLocalRecoveryAccess\(\)[\s\S]{0,260}new LocalRecoveryPermissionService\(session\)[\s\S]{0,400}LocalRecoveryPermissionPolicy\.IsAllowed\([\s\S]{0,220}new PermissionService\(session\)\.Has\(permissionCode\)'
Require-Pattern "products permissions fail closed through the composed permission service" $productsViewModel 'HasPermission\(string permissionCode\)[\s\S]{0,180}_permissionService\?\.Has\(permissionCode\)\s*==\s*true'
if ($productsViewModel -match 'OperatorSessionHolder\.Current\?\.CurrentUser') {
    Fail "products permissions must not fall back to an unbound current user"
}
else {
    Pass "products permissions have no unbound current-user fallback"
}
Require-Pattern "recovery permission service uses only explicit recovery policy" $recoveryPermission 'LocalRecoveryPermissionPolicy\.IsGranted'
Require-Pattern "override verifies active lease before authorizer PIN" $override 'EnsureAuthorizationValid\(\)[\s\S]*VerifyPinAsync'
Require-Pattern "override resolves only the lease-bound remote identity" $override 'ResolveLeaseBoundAuthorizerAsync[\s\S]{0,5000}FindTrustedRemoteStaffUsernameAsync'
Require-Pattern "override rechecks lease binding after PIN verification" $override 'VerifyPinAsync[\s\S]{0,900}ResolveLeaseBoundAuthorizerAsync'
if ($override -match 'ListUsersWithPermissionAsync' -or
    $override -match 'ListAdminUsersAsync') {
    Fail "override must not enumerate local recovery or stale mirror identities"
}
else {
    Pass "override has no local/stale identity enumeration"
}
Require-Pattern "POS access exposes a distinct lease-expired result" $accessDialog 'LoginResult\.AuthorizationExpired[\s\S]*access\.login\.authorizationExpired'
$denialIndex = $accessDialog.IndexOf("IsAuthorizationDenied(result)", [System.StringComparison]::Ordinal)
$denialReturnIndex = if ($denialIndex -ge 0) {
    $accessDialog.IndexOf("return;", $denialIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$nextFallbackIndex = if ($denialIndex -ge 0) {
    $accessDialog.IndexOf("if (IsOfflineFallbackAllowed(result.Code))", $denialIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$rejectStart = $syncHost.IndexOf("internal async Task<bool> RejectAuthenticatedTrustTransitionAsync", [System.StringComparison]::Ordinal)
$publicRevokeStart = $syncHost.IndexOf("public async Task RevokeCurrentTrustAsync", [System.StringComparison]::Ordinal)
$rejectBody = if ($rejectStart -ge 0 -and $publicRevokeStart -gt $rejectStart) {
    $syncHost.Substring($rejectStart, $publicRevokeStart - $rejectStart)
} else { "" }
$firstAttemptCheck = $rejectBody.IndexOf("transition.AttemptId", [System.StringComparison]::Ordinal)
$stateRead = $rejectBody.IndexOf("ReadCurrentPredecessorAsync", [System.StringComparison]::Ordinal)
$secondAttemptCheck = if ($stateRead -ge 0) {
    $rejectBody.IndexOf("transition.AttemptId", $stateRead, [System.StringComparison]::Ordinal)
} else { -1 }
$predecessorCheck = $rejectBody.IndexOf("PredecessorStatesMatch", [System.StringComparison]::Ordinal)
$scopedRevoke = $rejectBody.IndexOf("RevokeCurrentTrustCoreAsync", [System.StringComparison]::Ordinal)
if ($denialIndex -ge 0 -and $denialReturnIndex -gt $denialIndex -and
    $nextFallbackIndex -gt $denialReturnIndex -and
    $accessDialog -notmatch '\.RevokeCurrentTrustAsync\(' -and
    $bootstrap -match 'result\.Denied\s*\|\|[\s\S]{0,180}SharedAuthStopPolicy\.IsAuthenticationDenied\(result\.Code\)[\s\S]{0,500}RejectAuthenticatedTrustTransitionAsync\(\s*authenticatedTransition,\s*"auth_denied"' -and
    $firstAttemptCheck -ge 0 -and $stateRead -gt $firstAttemptCheck -and
    $secondAttemptCheck -gt $stateRead -and $predecessorCheck -gt $secondAttemptCheck -and
    $scopedRevoke -gt $predecessorCheck -and
    $rejectBody -match 'IsAuthorizationEpochCurrent' -and
    $rejectBody -match 'PosOnlineSyncSignalBus\.IsMaintenanceActive') {
    Pass "explicit online denial is transition-scoped and never enters fallback"
}
else {
    Fail "explicit online denial must be scoped to its attempt and return before fallback"
}
Require-Pattern "operator switch cannot bypass an expired lease" $operatorSwitch 'LoginResult\.AuthorizationExpired[\s\S]*authorization_lease_denied'
Require-Pattern "trusted mirror lookup binds opaque shop and staff ids" $userRepo 'FindTrustedRemoteStaffUsernameAsync[\s\S]{0,1800}remote_shop_id[\s\S]{0,500}remote_staff_id'
Require-Pattern "trusted mirror lookup binds the credential version" $userRepo 'FindTrustedRemoteStaffUsernameAsync[\s\S]{0,2200}remote_credential_version[\s\S]{0,300}staffCredentialVersion'
Require-Pattern "offline login binds requested staff to the trusted lease" $accessDialog 'staff_identity_mismatch[\s\S]{0,900}FindTrustedRemoteStaffUsernameAsync'
Require-Pattern "online completion resolves the lease-bound mirror" $accessDialog 'CompleteOnlineSignInAsync[\s\S]{0,700}FindLeaseBoundRemoteStaffUsernameAsync'
$unsafeCatalogBranch = [regex]::Match(
    $accessDialog,
    'if\s*\(result\.Success\s*&&\s*!result\.CanOpenPos\)[\s\S]*?(?=\r?\n\s*var\s+failureKind)').Value
if ($unsafeCatalogBranch -match 'FindLeaseBoundRemoteStaffUsernameAsync' -and
    $unsafeCatalogBranch -notmatch 'LoginLocalUsernameAsync|LoginRemoteMirrorForRecoveryAsync|AccessMode\s*=|DialogResult\s*=') {
    Pass "unsafe catalog prepares recovery without authenticating or committing access"
}
else {
    Fail "unsafe catalog must not authenticate or commit before explicit recovery acceptance"
}
Require-Pattern "explicit remote recovery re-challenge uses normal lease-bound login" $accessDialog 'RunRemoteRecoveryLoginAsync[\s\S]{0,1800}LoginRemoteMirrorForRecoveryAsync[\s\S]{0,1800}AccessMode\s*=\s*PosAuthenticatedAccessMode\.Normal'
if ($accessDialog -match 'OperatorSessionHolder\.Current\s*=') {
    Fail "access dialog must not replace the shared operator session"
}
else {
    Pass "access dialog preserves the shared operator session object"
}
$catalogRetryMethod = [regex]::Match(
    $accessDialog,
    'private\s+async\s+Task\s+RunCatalogRetryAsync\(\)[\s\S]*?(?=\r?\n\s*private\s+)').Value
if ($catalogRetryMethod -match 'OperatorSessionHolder\.Current[\s\S]{0,120}IsLoggedIn') {
    Fail "catalog retry must not accept an unrelated already-logged-in operator"
}
else {
    Pass "catalog retry requires the pending shop/staff credential path"
}
Require-Pattern "operator switch resolves only the staff bound to the trusted lease" $operatorSwitch 'PosTrustedDeviceStore[\s\S]{0,2200}FindTrustedRemoteStaffUsernameAsync'
if ($operatorSwitch -match 'GetByUsernameAsync\(normalized\)' -or
    $operatorSwitch -match 'FindRemoteStaffUsernameAsync\(_shopCode,\s*normalized\)') {
    Fail "operator switch must not fall back to local or stale same-shop identities"
}
else {
    Pass "operator switch has no local/stale mirror fallback"
}
Require-Pattern "active window schedules exact lease expiry" $main 'RefreshAuthorizationLeaseSchedule[\s\S]*_authorizationLeaseTimer\.Interval\s*=\s*remaining'

$paymentReturnIndex = $posViewModel.IndexOf("if (!ok)", [System.StringComparison]::Ordinal)
$commitDemandIndex = $posViewModel.IndexOf("_permissionService.Demand(PermissionCodes.PosPay", $paymentReturnIndex, [System.StringComparison]::Ordinal)
$completeSaleIndex = $posViewModel.IndexOf("_service.CompleteSaleAsync", [System.StringComparison]::Ordinal)
if ($paymentReturnIndex -ge 0 -and $commitDemandIndex -gt $paymentReturnIndex -and $completeSaleIndex -gt $commitDemandIndex) {
    Pass "payment revalidates authorization immediately before sale commit"
}
else {
    Fail "payment must revalidate authorization immediately before sale commit"
}

Require-Pattern "sales contract emits clientOriginalLineId additively" $contracts 'DataMember\(Name\s*=\s*"clientOriginalLineId",\s*EmitDefaultValue\s*=\s*false\)'
Require-Pattern "new reversal payload binds original line" $salesBuilder 'reversal_original_line_missing[\s\S]*ClientOriginalLineId\s*=\s*isReversal'
Require-Pattern "legacy reversal payload is blocked after CAS claim and before network" $salesSync 'PrepareSalesSyncAttemptAsync[\s\S]*HasCompleteReversalBindings\(request\)[\s\S]*MarkBlockedAsync\(item,\s*"reversal_original_line_missing"[\s\S]*SalesSyncAsync'

$directPinCallFiles = Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf") -Recurse -File -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    Where-Object { Select-String -Path $_.FullName -Pattern "\.VerifyPinAsync\(" -Quiet } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') -replace '\\', '/' } |
    Sort-Object
$allowedPinCallFiles = @(
    "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs",
    "src/Win7POS.Wpf/Infrastructure/Security/OverrideAuthService.cs"
) | Sort-Object
if (($directPinCallFiles -join "|") -eq ($allowedPinCallFiles -join "|")) {
    Pass "raw local PIN verification has only guarded call sites"
}
else {
    Fail "unexpected raw VerifyPinAsync call site(s): $($directPinCallFiles -join ', ')"
}

if (($guard + $policy) -match '(?i)(outbox|catalog|mirror).*?(delete|clear|remove)') {
    Fail "authorization guard/policy must not delete outbox, catalog or mirror state"
}
else {
    Pass "authorization denial does not delete outbox/catalog/mirror state"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
