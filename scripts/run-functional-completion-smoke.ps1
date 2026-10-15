[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('x86')][string]$Platform = 'x86',
    [string]$HarnessDirectory = '',
    [string[]]$Scenario = @(),
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $HarnessDirectory) {
    $HarnessDirectory = Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Platform\$Configuration\net48"
}
$exe = Join-Path $HarnessDirectory 'Win7POS.Wpf.UiSmokeHarness.exe'
$scenarios = @('IMPORT_completion','IMPORT_RECOVERY_completion','PRICE_HISTORY_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
foreach ($selected in $Scenario) {
    if ($selected -cnotin $scenarios) { throw "Unknown functional scenario: $selected" }
}
if (@($Scenario | Select-Object -Unique).Count -ne $Scenario.Count) { throw 'Duplicate functional scenario.' }
$runId = 'Win7POS.Functional.' + [Guid]::NewGuid().ToString('N')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) $runId }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Functional output must be a new directory.' }
# Evidence is retained; this runner never recursively deletes a data directory.
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$results = [Collections.Generic.List[object]]::new()
$firstError = $null
$sourceSha = (& git -C $repoRoot rev-parse HEAD)
$harnessHash = if (Test-Path -LiteralPath $exe -PathType Leaf) { (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
$previousTestEndpoint = $env:WIN7POS_ADMIN_WEB_BASE_URL
try {
    $env:WIN7POS_ADMIN_WEB_BASE_URL = 'http://127.0.0.1:9'
    $cases = @()
    if ($Scenario.Count -eq 0) { $cases += 'FUNCTIONAL_VISUAL'; $cases += $scenarios }
    else { $cases += $Scenario }
    foreach ($name in $cases) {
        $dataDir = Join-Path $OutputDirectory $name
        New-Item -ItemType Directory -Path $dataDir | Out-Null
        $visual = $name -eq 'FUNCTIONAL_VISUAL'
        $timeoutMs = if ($visual) { 120000 } else { 60000 }
        $resultPath = Join-Path $dataDir $(if ($visual) { 'ui-ux-final-closeout.txt' } else { 'functional-completion.txt' })
        $record = [ordered]@{ name=$name; status='NOT_RUN'; exitCode=$null; evidenceDirectory=$dataDir; elapsedMs=0; timeoutMs=$timeoutMs; pid=$null; creationTimeUtc=$null; executable=$exe; error=$null }
        $process = $null
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            if (-not $harnessHash) { throw 'Build the x86 WPF harness first.' }
            $arguments = @('--data-dir', ('"' + $dataDir + '"'))
            if ($visual) { $arguments += @('--ui-ux-final-closeout','--output-dir', ('"' + $dataDir + '"')) }
            else { $arguments += @('--functional-completion-smoke','--scenario',$name) }
            $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dataDir 'stdout.log') -RedirectStandardError (Join-Path $dataDir 'stderr.log')
            $record.pid = $process.Id
            $record.creationTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
            $record.executable = $process.MainModule.FileName
            if (-not $process.WaitForExit([Math]::Max(1, $timeoutMs - [int]$watch.ElapsedMilliseconds))) {
                $record.status = 'TIMEOUT'
                throw "Functional completion timed out ($name, $timeoutMs ms)."
            }
            $process.WaitForExit()
            $record.exitCode = $process.ExitCode
            if ($process.ExitCode -ne 0) { throw "Functional completion exit=$($process.ExitCode) ($name)." }
            if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf) -or
                -not ([IO.File]::ReadAllText($resultPath).TrimStart([char]0xfeff).StartsWith('PASS'))) { throw "Missing positive functional result ($name)." }
            $record.status = 'PASS'
        }
        catch {
            if ($record.status -ne 'TIMEOUT') { $record.status = if ($null -eq $record.pid) { 'NOT_RUN' } else { 'FAIL' } }
            $record.error = $_.Exception.Message
            if ($null -eq $firstError) { $firstError = $record.error }
        }
        finally {
            if ($null -ne $process) {
                try {
                    # The retained process handle owns this child; never kill by name/PID alone.
                    if (-not $process.HasExited) {
                        if ($process.StartTime.ToUniversalTime().ToString('O') -ne $record.creationTimeUtc -or
                            $process.MainModule.FileName -ne $record.executable) { throw 'Child process identity changed.' }
                        $process.Kill()
                        if (-not $process.WaitForExit(5000)) { throw 'Owned child did not terminate within cleanup budget.' }
                    }
                }
                catch {
                    $record.status = 'FAIL'
                    if ($null -eq $record.error) { $record.error = $_.Exception.Message }
                    if ($null -eq $firstError) { $firstError = $record.error }
                }
                finally { $process.Dispose() }
            }
            $record.elapsedMs = $watch.Elapsed.TotalMilliseconds
            $results.Add([pscustomobject]$record)
            [ordered]@{schemaVersion=1;runId=$runId;sourceSha=$sourceSha;harnessSha256=$harnessHash;firstError=$firstError;results=@($results)} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'functional-results.json') -Encoding utf8
            Write-Host ("FUNCTIONAL_RESULT={0}; status={1}; elapsed_ms={2:F0}; evidence={3}" -f $name,$record.status,$record.elapsedMs,$dataDir)
        }
    }
}
finally { $env:WIN7POS_ADMIN_WEB_BASE_URL = $previousTestEndpoint }
Write-Host "FUNCTIONAL_REPORT=$OutputDirectory\functional-results.json"
if ($results.Count -eq 0 -or @($results | Where-Object status -ne 'PASS').Count -gt 0) { Write-Error "Functional suite incomplete: $firstError"; exit 1 }
exit 0
