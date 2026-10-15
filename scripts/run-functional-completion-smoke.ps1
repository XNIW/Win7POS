[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('x86')][string]$Platform = 'x86',
    [string]$HarnessDirectory = '',
    [string[]]$Scenario = @(),
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
function Read-FunctionalChildImage($Process) {
    if (-not ('Win7PosFunctionalProcessImage' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class Win7PosFunctionalProcessImage {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int length);
    public static string Read(Process process) {
        var name = new StringBuilder(32768); var length = name.Capacity;
        if (!QueryFullProcessImageName(process.Handle, 0, name, ref length))
            throw new System.ComponentModel.Win32Exception();
        return name.ToString();
    }
}
'@
    }
    return [Win7PosFunctionalProcessImage]::Read($Process)
}
function Get-FunctionalChildImage($Process, [string]$ExpectedPath) {
    $identityClock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.HasExited -and $identityClock.ElapsedMilliseconds -lt 2000) {
        try {
            $Process.Refresh()
            $path = Read-FunctionalChildImage $Process
            if ($path -and [string]::Equals($path, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) { return $path }
        } catch { }
        Start-Sleep -Milliseconds 10
    }
    if (-not $Process.HasExited) { throw 'Owned child image unavailable or unexpected.' }
    return $null
}
function Stop-FunctionalChild($Process, [string]$CreationTimeUtc) {
    if (-not $Process.HasExited) {
        # The launch handle is retained until finally. Image-query failure must
        # never strand this exact child or turn cleanup into a PID/name search.
        if ($Process.StartTime.ToUniversalTime().ToString('O') -ne $CreationTimeUtc) { throw 'Child creation identity changed.' }
        $Process.Kill()
        if (-not $Process.WaitForExit(5000)) { throw 'Owned child did not terminate within cleanup budget.' }
    }
}
function New-FunctionalDataRoot([string]$BaseDirectory, [string]$RepoRoot) {
    if (-not [IO.Path]::IsPathFullyQualified($BaseDirectory)) { throw 'Functional temporary base must be absolute.' }
    $path = [IO.Path]::GetFullPath((Join-Path $BaseDirectory ('W7F.' + [Guid]::NewGuid().ToString('N'))))
    # Native SQLite backup paths append their own names and temporary suffixes.
    # Reports may be deep; the data path must reserve room below MAX_PATH.
    if ((Join-Path $path 'case-00').Length -gt 89) { throw 'Functional temporary base is too long for native SQLite paths.' }
    $forbidden = @($RepoRoot, 'C:\POSData')
    foreach ($base in @([Environment]::GetFolderPath('CommonApplicationData'), [Environment]::GetFolderPath('LocalApplicationData'), $env:APPDATA, [IO.Path]::GetTempPath())) {
        if ($base) { $forbidden += Join-Path $base 'Win7POS' }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:WIN7POS_DATA_DIR)) { $forbidden += $env:WIN7POS_DATA_DIR.Trim() }
    foreach ($store in $forbidden) {
        $storePath = [IO.Path]::GetFullPath($store).TrimEnd('\','/')
        if ($path.Equals($storePath, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($storePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Functional data overlaps checkout or store data.'
        }
    }
    $ancestor = [IO.DirectoryInfo]::new($path)
    while ($null -ne $ancestor) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Functional data traverses a reparse point.' }
        $ancestor = $ancestor.Parent
    }
    if (Test-Path -LiteralPath $path) { throw 'Functional data must be a new directory.' }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function Copy-FunctionalEvidence([string]$DataDirectory, [string]$EvidenceDirectory) {
    # Only bounded, top-level diagnostics are exported. SQLite data stays in its
    # unique owned directory, retained for inspection without recursive cleanup.
    $files = @(Get-ChildItem -LiteralPath $DataDirectory -File | Where-Object Extension -in @('.txt','.log','.json','.csv','.png'))
    if ($files.Count -gt 256) { throw 'Functional diagnostic file count exceeds export bound.' }
    foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -gt 64MB) { throw 'Functional diagnostic is a link or exceeds export bound.' }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $EvidenceDirectory $file.Name) -ErrorAction Stop
    }
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $HarnessDirectory) {
    $HarnessDirectory = Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Platform\$Configuration\net48"
}
$exe = [IO.Path]::GetFullPath((Join-Path $HarnessDirectory 'Win7POS.Wpf.UiSmokeHarness.exe'))
$scenarios = @('IMPORT_completion','IMPORT_RECOVERY_completion','PRICE_HISTORY_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
foreach ($selected in $Scenario) {
    if ($selected -cnotin $scenarios) { throw "Unknown functional scenario: $selected" }
}
if (@($Scenario | Select-Object -Unique).Count -ne $Scenario.Count) { throw 'Duplicate functional scenario.' }
$runId = 'Win7POS.Functional.' + [Guid]::NewGuid().ToString('N')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) $runId }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Functional output must be a new directory.' }
$ancestor = [IO.DirectoryInfo]::new($OutputDirectory).Parent
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Functional output traverses a reparse point.' }
    $ancestor = $ancestor.Parent
}
# Evidence is retained; this runner never recursively deletes a data directory.
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$temporaryBase = if ($env:WIN7POS_QA_TEMP_BASE) { $env:WIN7POS_QA_TEMP_BASE } else { [IO.Path]::GetTempPath() }
$dataRoot = New-FunctionalDataRoot $temporaryBase $repoRoot
[ordered]@{runId=$runId;evidenceDirectory=$OutputDirectory;dataDirectory=$dataRoot} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'qa-work.json') -Encoding utf8
# Prepare the process observer before launching timed WPF work. No fixture or
# product initialization occurs here.
$observer = [Diagnostics.Process]::GetCurrentProcess()
try { $null = Read-FunctionalChildImage $observer } finally { $observer.Dispose() }
$observeRecoveryThreads = $env:WIN7POS_QA_RECOVERY_THREADS -eq '1'
if ($observeRecoveryThreads) { Add-Type -Path (Join-Path $PSScriptRoot 'qa/RecoveryThreadObserver.cs') }
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
    $caseIndex = 0
    $cleanupConfirmed = $true
    foreach ($name in $cases) {
        $evidenceDir = Join-Path $OutputDirectory $name
        New-Item -ItemType Directory -Path $evidenceDir | Out-Null
        $dataDir = Join-Path $dataRoot ('case-{0:D2}' -f $caseIndex++)
        New-Item -ItemType Directory -Path $dataDir | Out-Null
        $visual = $name -eq 'FUNCTIONAL_VISUAL'
        $timeoutMs = if ($visual) { 120000 } else { 60000 }
        $resultPath = if ($visual) { Join-Path $evidenceDir 'ui-ux-final-closeout.txt' } else { Join-Path $dataDir 'functional-completion.txt' }
        $record = [ordered]@{ name=$name; status='NOT_RUN'; exitCode=$null; evidenceDirectory=$evidenceDir; dataDirectory=$dataDir; elapsedMs=0; timeoutMs=$timeoutMs; pid=$null; creationTimeUtc=$null; executable=$exe; observedExecutable=$null; cleanupSucceeded=$null; cleanupError=$null; evidenceExportError=$null; error=$null }
        $process = $null
        $threadObserver = $null
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            if (-not $cleanupConfirmed) { throw 'Previous functional child cleanup was not confirmed; subsequent case not launched.' }
            if (-not $harnessHash) { throw 'Build the x86 WPF harness first.' }
            $arguments = @('--data-dir', ('"' + $dataDir + '"'))
            if ($visual) { $arguments += @('--ui-ux-final-closeout','--output-dir', ('"' + $evidenceDir + '"')) }
            else { $arguments += @('--functional-completion-smoke','--scenario',$name) }
            $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidenceDir 'stdout.log') -RedirectStandardError (Join-Path $evidenceDir 'stderr.log')
            $record.pid = $process.Id
            $null = $process.Handle
            $record.creationTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
            $record.observedExecutable = Get-FunctionalChildImage $process $exe
            if ($observeRecoveryThreads -and $name -eq 'IMPORT_RECOVERY_completion') {
                $threadObserver = [RecoveryThreadObserver]::new($process, (Join-Path $evidenceDir 'recovery-thread-observer.csv'))
            }
            if (-not $process.WaitForExit([Math]::Max(1, $timeoutMs - [int]$watch.ElapsedMilliseconds))) {
                $record.status = 'TIMEOUT'
                throw "Functional completion timed out ($name, $timeoutMs ms)."
            }
            # On Windows Start-Process redirects to inherited file handles, not
            # asynchronous readers on this Process. The bounded wait is enough;
            # do not introduce an unbounded second wait after the test deadline.
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
                    Stop-FunctionalChild $process $record.creationTimeUtc
                    $record.cleanupSucceeded = $process.HasExited
                    if (-not $record.cleanupSucceeded) { throw 'Owned functional child still active after cleanup.' }
                }
                catch {
                    $cleanupConfirmed = $false
                    $record.cleanupSucceeded = $false
                    $record.cleanupError = $_.Exception.Message
                    if ($record.status -eq 'PASS') { $record.status = 'FAIL' }
                    if ($null -eq $record.error) { $record.error = $_.Exception.Message }
                    if ($null -eq $firstError) { $firstError = $record.error }
                }
                finally {
                    try { if ($null -ne $threadObserver) { $threadObserver.Dispose() } }
                    catch {
                        if ($record.status -eq 'PASS') { $record.status = 'FAIL' }
                        if ($null -eq $record.error) { $record.error = $_.Exception.Message }
                        if ($null -eq $firstError) { $firstError = $_.Exception.Message }
                    }
                    finally { $process.Dispose() }
                }
            }
            $record.elapsedMs = $watch.Elapsed.TotalMilliseconds
            try { if ($cleanupConfirmed) { Copy-FunctionalEvidence $dataDir $evidenceDir } }
            catch {
                $record.evidenceExportError = $_.Exception.Message
                if ($record.status -eq 'PASS') { $record.status = 'FAIL' }
                if ($null -eq $record.error) { $record.error = $_.Exception.Message }
                if ($null -eq $firstError) { $firstError = $record.error }
            }
            $results.Add([pscustomobject]$record)
            try {
                [ordered]@{schemaVersion=1;runId=$runId;sourceSha=$sourceSha;harnessSha256=$harnessHash;firstError=$firstError;results=$results.ToArray()} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'functional-results.json') -Encoding utf8
            }
            catch { [Console]::Error.WriteLine("FUNCTIONAL_REPORT_WRITE_FAILED; firstError=$firstError"); throw }
            Write-Host ("FUNCTIONAL_RESULT={0}; status={1}; elapsed_ms={2:F0}; evidence={3}" -f $name,$record.status,$record.elapsedMs,$evidenceDir)
        }
    }
}
finally { $env:WIN7POS_ADMIN_WEB_BASE_URL = $previousTestEndpoint }
Write-Host "FUNCTIONAL_REPORT=$OutputDirectory\functional-results.json"
if ($results.Count -eq 0 -or @($results | Where-Object status -ne 'PASS').Count -gt 0) { Write-Error "Functional suite incomplete: $firstError"; exit 1 }
exit 0
