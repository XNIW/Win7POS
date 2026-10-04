[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet(20000,100000)][int]$Products = 100000,
    [ValidateRange(0,120)][int]$SoakMinutes = 0,
    [string]$HarnessDirectory = '',
    [ValidateSet('Diagnostic','Qualification')][string]$Mode = 'Diagnostic',
    [ValidateSet('Integrated','Final')][string]$Stage = 'Final',
    [string]$BudgetPath = '',
    [string]$PayloadBindingPath = '',
    [string]$ExpectedCommit = '',
    [switch]$DisableObserver,
    [ValidateRange(0,5)][int]$DiagnosticScanCount = 0,
    # Compatibility switch for frozen protocol-4 diagnostic binaries.
    # Protocol 5 always uses Input; this switch does not form a v5 A/B test.
    [switch]$DiagnosticInputDispatch,
    [switch]$DiagnosticPublicInput,
    [switch]$DiagnosticTimerControl,
    [switch]$DiagnosticTimerWakeup,
    [switch]$DiagnosticLegacyImageProgress,
    [switch]$DiagnosticExecutionCapture,
    [switch]$DiagnosticLegacyFixtureSetup
)
$ErrorActionPreference = 'Stop'
if ($DiagnosticPublicInput -and ($Mode -ne 'Diagnostic' -or -not $DiagnosticScanCount)) { throw 'Public input comparison requires short Diagnostic scans.' }
if ($DiagnosticLegacyFixtureSetup -and ($Mode -ne 'Diagnostic' -or -not $DiagnosticScanCount -or $DiagnosticTimerControl)) { throw 'Legacy fixture setup requires short Diagnostic scans.' }
if ($DiagnosticExecutionCapture -and ($Mode -ne 'Diagnostic' -or -not $DiagnosticScanCount -or $DisableObserver -or $DiagnosticTimerControl)) { throw 'Execution capture requires short Diagnostic scans with the observer enabled.' }
if ($DiagnosticLegacyImageProgress -and ($Mode -ne 'Diagnostic' -or $DiagnosticTimerControl -or $SoakMinutes -lt 1 -or $SoakMinutes -gt 5)) { throw 'Legacy image progress requires full Diagnostic and 1..5 minutes.' }
if ($DiagnosticTimerWakeup -and -not $DiagnosticTimerControl) { throw 'Diagnostic timer wakeup requires the minimal control.' }
if ($DiagnosticTimerControl -and ($Mode -ne 'Diagnostic' -or $SoakMinutes -lt 1 -or $SoakMinutes -gt 5 -or $DiagnosticScanCount -or $DiagnosticInputDispatch)) { throw 'Minimal timer control requires Diagnostic and 1..5 minutes.' }
if ($DiagnosticScanCount -and $SoakMinutes) { throw 'Short scan reproduction and soak duration are mutually exclusive.' }
if ($DiagnosticInputDispatch -and ($Mode -ne 'Diagnostic' -or -not $DiagnosticScanCount)) { throw 'Input dispatch comparison requires short Diagnostic scans.' }
Import-Module (Join-Path $PSScriptRoot 'qa/Win7PosPerformanceValidation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'qa/Win7PosQaPayload.psm1') -Force
if (-not $HarnessDirectory) {
    $HarnessDirectory = Join-Path $PSScriptRoot '../tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48'
}
$exe = Join-Path $HarnessDirectory 'Win7POS.Wpf.UiSmokeHarness.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build the Release x86 harness first.' }
$budget = $null
$binding = $null
if ($Mode -eq 'Qualification') {
    if ($DiagnosticScanCount) { throw 'Short scan reproduction is diagnostic only.' }
    if ($env:WIN7POS_QA_PERF_TRACE -eq '1') { throw 'Operation tracing is diagnostic only.' }
    if ($DisableObserver) { throw 'Qualification requires dispatcher observation.' }
    if ($Stage -eq 'Final' -and $Products -ne 100000) { throw 'Final qualification requires the 100000-product fixture.' }
    $minimum = if ($Stage -eq 'Final') { 60 } else { 10 }
    if ($SoakMinutes -lt $minimum) { throw "Qualification stage $Stage requires at least $minimum minutes." }
    if (-not $BudgetPath -or -not $PayloadBindingPath) { throw 'Qualification requires a preregistered budget and verified payload binding.' }
    $budget = Get-Content -LiteralPath $BudgetPath -Raw | ConvertFrom-Json
    $binding = Get-Content -LiteralPath $PayloadBindingPath -Raw | ConvertFrom-Json
    if (($Stage -eq 'Final' -or $ExpectedCommit) -and ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or $binding.clientCommit -cne $ExpectedCommit)) {
        throw 'Qualification commit does not match the verified payload binding.'
    }
    Assert-Win7PosQaPayload -HarnessDirectory $HarnessDirectory -Manifest $binding.payload
    if ((Get-FileHash -LiteralPath $exe).Hash -ine $binding.harnessSha256) { throw 'Bound harness hash mismatch.' }
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new; previous samples must be preserved.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$manifest = @(Get-ChildItem -LiteralPath $HarnessDirectory -File -Recurse | Where-Object { $_.Extension -in '.exe','.dll' } |
    ForEach-Object { [ordered]@{ file=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
$budgetHash = if ($BudgetPath) { (Get-FileHash -LiteralPath $BudgetPath).Hash.ToLowerInvariant() } else { $null }
# Environment.OSVersion in the net48 harness can report the compatibility
# version (6.2 on this Windows 11 host); preserve the installed OS separately.
$hostOperatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop |
    Select-Object Caption,Version,BuildNumber,OSArchitecture
[ordered]@{ products=$Products; soakMinutes=$SoakMinutes; mode=$Mode; stage=$Stage; startedUtc=[DateTimeOffset]::UtcNow.ToString('O');
    protocol=$(if ($DiagnosticTimerControl) { "minimal-wpf-timer-v2: focused TextBox, actual InputManager timer, no POS view/cart/service; extra 125ms QA wakeup timer=$DiagnosticTimerWakeup; 20-second idle blocks; diagnostic only" } elseif ($DiagnosticLegacyFixtureSetup) { 'win7pos-public-scan-v3: legacy fixture setup Diagnostic comparison only' } else { 'win7pos-public-scan-v5: actual child preflight; rendered 500-line fixture before useful clock; persistent identities, alternating mode order, public command delivery at Input with enqueue and WPF callbacks included; unchanged 250ms Input completion probe, bitmap separate, image/dialog workload, 20 seconds idle; no forced GC; awake duration excludes suspend; no process-start measurement' });
    inputDelivery='Input';
    diagnosticInputDispatchScope='legacy protocol-4 harness only; protocol 5 always Input';
    benchmarkProtocol='31 service samples per size: first call 0, warm 1..30; rendered-view bitmap is not monitor latency';
    budgetSha256=$budgetHash; observerEnabled=(-not $DisableObserver); diagnosticScanCount=$DiagnosticScanCount; diagnosticInputDispatch=[bool]$DiagnosticInputDispatch; diagnosticPublicInput=[bool]$DiagnosticPublicInput; diagnosticLegacyImageProgress=[bool]$DiagnosticLegacyImageProgress; diagnosticExecutionCapture=[bool]$DiagnosticExecutionCapture; hostOperatingSystem=$hostOperatingSystem; binaries=$manifest } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'protocol.json')
if ($BudgetPath) { Copy-Item -LiteralPath $BudgetPath -Destination (Join-Path $OutputDirectory 'preregistered-budget.json') }
if ($PayloadBindingPath) { Copy-Item -LiteralPath $PayloadBindingPath -Destination (Join-Path $OutputDirectory 'payload-binding.json') }
$previousSoak = $env:WIN7POS_QA_SOAK_MINUTES
$previousDiagnostic = $env:WIN7POS_QA_CART_DIAGNOSTIC
$previousObserver = $env:WIN7POS_QA_PERF_OBSERVER_OFF
$previousScanLimit = $env:WIN7POS_QA_PERF_SCAN_LIMIT
$previousInputDispatch = $env:WIN7POS_QA_PERF_INPUT_DISPATCH
$previousPublicInput = $env:WIN7POS_QA_PERF_PUBLIC_INPUT
$previousTimerWakeup = $env:WIN7POS_QA_TIMER_WAKEUP
$previousLegacyProgress = $env:WIN7POS_QA_LEGACY_IMAGE_PROGRESS
$previousExecutionCapture = $env:WIN7POS_QA_EXECUTION_CAPTURE
$previousLegacySetup = $env:WIN7POS_QA_LEGACY_FIXTURE_SETUP
$status = $null
$process = $null
$workingDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
try {
    $env:WIN7POS_QA_SOAK_MINUTES = if ($DiagnosticScanCount) { '1' } elseif ($SoakMinutes) { [string]$SoakMinutes } else { $null }
    $env:WIN7POS_QA_PERF_SCAN_LIMIT = if ($DiagnosticScanCount) { [string]$DiagnosticScanCount } else { $null }
    $env:WIN7POS_QA_PERF_INPUT_DISPATCH = if ($DiagnosticInputDispatch) { '1' } else { $null }
    $env:WIN7POS_QA_PERF_PUBLIC_INPUT = if ($DiagnosticPublicInput) { '1' } else { $null }
    $env:WIN7POS_QA_CART_DIAGNOSTIC = if ($DiagnosticTimerControl) { 'timer-control' } else { $null }
    $env:WIN7POS_QA_TIMER_WAKEUP = if ($DiagnosticTimerWakeup) { '1' } else { $null }
    $env:WIN7POS_QA_LEGACY_IMAGE_PROGRESS = if ($DiagnosticLegacyImageProgress) { '1' } else { $null }
    $env:WIN7POS_QA_EXECUTION_CAPTURE = if ($DiagnosticExecutionCapture) { '1' } else { $null }
    $env:WIN7POS_QA_LEGACY_FIXTURE_SETUP = if ($DiagnosticLegacyFixtureSetup) { '1' } else { $null }
    $env:WIN7POS_QA_PERF_OBSERVER_OFF = if ($DisableObserver) { '1' } else { $null }
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"'+$OutputDirectory+'"'), '--cart-performance', '--products', $Products) -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($deadline.Elapsed.TotalMinutes -gt $(if ($DiagnosticScanCount) { 2 } else { $SoakMinutes + 10 })) {
            throw 'Performance harness timed out; partial samples preserved.'
        }
    }
    if ($process.ExitCode -ne 0) { throw "Performance harness exit=$($process.ExitCode); inspect preserved harness-error.txt." }
    $result = Get-Content -LiteralPath (Join-Path $OutputDirectory 'functional-completion.txt') -Raw
    if (-not $result.StartsWith('PASS')) { throw $result }
    foreach ($entry in $manifest) {
        if ((Get-FileHash -LiteralPath $entry.file).Hash -ine $entry.sha256) { throw 'Harness or application binary changed during measurement.' }
    }
    if ($binding) { Assert-Win7PosQaPayload -HarnessDirectory $HarnessDirectory -Manifest $binding.payload }
    if ($BudgetPath -and (Get-FileHash -LiteralPath $BudgetPath).Hash -ine $budgetHash) { throw 'Preregistered budget changed during measurement.' }
    if (-not $DiagnosticTimerControl -and (@(Import-Csv (Join-Path $OutputDirectory 'cart-performance.csv')).Count -ne 155 -or
        @(Import-Csv (Join-Path $OutputDirectory 'render-stages.csv')).Count -ne 31)) { throw 'Incomplete benchmark samples.' }
    if ($Mode -eq 'Qualification') {
        $status = Test-Win7PosPerformance -Directory $OutputDirectory -Budget $budget -RequiredSeconds ($SoakMinutes * 60) -ProcessCompleted $true -ExpectedProducts $Products
    }
    else {
        $environmentValid = $false
        if ($DiagnosticTimerControl) {
            $receipt = Get-Content (Join-Path $OutputDirectory 'diagnostic-scans.json') -Raw | ConvertFrom-Json
            $samples = @(Import-Csv (Join-Path $OutputDirectory 'diagnostic-idle.csv'))
            if ($receipt.control -ne 'minimal-wpf-timer' -or -not $receipt.measurementCompleted -or
                $samples.Count -ne $receipt.cycles -or [double]$samples[-1].elapsed_s -lt $SoakMinutes * 60) { throw 'Incomplete minimal control.' }
            $environmentValid = $receipt.environmentValid -eq $true
        }
        elseif ($DiagnosticScanCount) {
            $receipt = Get-Content (Join-Path $OutputDirectory 'diagnostic-scans.json') -Raw | ConvertFrom-Json
            if ($receipt.schemaVersion -ne 'win7pos-short-public-scan-v1' -or $receipt.completedScans -ne $DiagnosticScanCount -or
                @(Import-Csv (Join-Path $OutputDirectory 'qualification-scans.csv')).Count -ne $DiagnosticScanCount) { throw 'Incomplete short scan reproduction.' }
            $environmentValid = $receipt.environmentValid -eq $true
        }
        elseif ($SoakMinutes) {
            $receipt = Get-Content (Join-Path $OutputDirectory 'qualification-measurement.json') -Raw | ConvertFrom-Json
            $samples = @(Import-Csv (Join-Path $OutputDirectory 'qualification-idle.csv'))
            if (-not $receipt.measurementCompleted -or $receipt.products -ne $Products -or $samples.Count -eq 0 -or
                [double]::Parse($samples[-1].elapsed_s, [cultureinfo]::InvariantCulture) -lt $SoakMinutes * 60) { throw 'Incomplete diagnostic duration.' }
            $environmentValid = $receipt.environmentValid -eq $true
        }
        $status = [pscustomobject]@{ MEASUREMENT_COMPLETED=$true; ENVIRONMENT_VALID=$environmentValid; STABILITY_PASS=$false;
            measurementReasons=@(); environmentReasons=@(); stabilityReasons=@('diagnostic_mode_not_qualified') }
    }
    $status | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'performance-result.json')
    foreach ($field in @('MEASUREMENT_COMPLETED','ENVIRONMENT_VALID','STABILITY_PASS')) { Write-Output "$field=$($status.$field)" }
    Write-Output "PERFORMANCE_ARTIFACT=$OutputDirectory"
    if ($Mode -eq 'Qualification' -and -not $status.STABILITY_PASS) {
        throw ('Performance qualification failed: ' + ((@($status.measurementReasons) + @($status.environmentReasons) + @($status.stabilityReasons)) -join '; '))
    }
}
catch {
    if (-not $status) {
        [ordered]@{MEASUREMENT_COMPLETED=$false;ENVIRONMENT_VALID=$false;STABILITY_PASS=$false;measurementReasons=@($_.Exception.Message)} |
            ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'performance-result.json')
    }
    throw
}
finally {
    $cleanup = [ordered]@{processId=$null;workingDirectory=$workingDirectory;closeRequested=$false;killRequested=$false;terminal=$false;failure=$null}
    try {
        if ($process) {
            $cleanup.processId=$process.Id
            try {
                try { $cleanup.terminal=$process.HasExited } catch { $cleanup.failure=$_.Exception.Message }
                if (-not $cleanup.terminal) {
                    $cleanup.closeRequested=$true
                    try { $null=$process.CloseMainWindow() } catch { $cleanup.failure=$_.Exception.Message }
                    try { $cleanup.terminal=$process.WaitForExit(5000) } catch { $cleanup.failure=$_.Exception.Message }
                    if (-not $cleanup.terminal) {
                        $cleanup.killRequested=$true
                        # This object belongs to our launch, never rediscover a PID/name.
                        try { $process.Kill() } catch { $cleanup.failure=$_.Exception.Message }
                        try { $cleanup.terminal=$process.WaitForExit(5000) } catch { $cleanup.failure=$_.Exception.Message }
                    }
                }
            }
            finally { try { $process.Dispose() } catch { $cleanup.failure=$_.Exception.Message } }
        }
        $cleanup | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'harness-cleanup.json')
    }
    finally {
        $env:WIN7POS_QA_SOAK_MINUTES = $previousSoak
        $env:WIN7POS_QA_CART_DIAGNOSTIC = $previousDiagnostic
        $env:WIN7POS_QA_PERF_OBSERVER_OFF = $previousObserver
        $env:WIN7POS_QA_PERF_SCAN_LIMIT = $previousScanLimit
        $env:WIN7POS_QA_PERF_INPUT_DISPATCH = $previousInputDispatch
        $env:WIN7POS_QA_PERF_PUBLIC_INPUT = $previousPublicInput
        $env:WIN7POS_QA_TIMER_WAKEUP = $previousTimerWakeup
        $env:WIN7POS_QA_LEGACY_IMAGE_PROGRESS = $previousLegacyProgress
        $env:WIN7POS_QA_EXECUTION_CAPTURE = $previousExecutionCapture
        $env:WIN7POS_QA_LEGACY_FIXTURE_SETUP = $previousLegacySetup
    }
    if ($process -and -not $cleanup.terminal) { throw 'Owned performance harness cleanup not confirmed; inspect harness-cleanup.json.' }
}
