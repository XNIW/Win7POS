[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineRoot,
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$baselineSha = '5cbd6e5da8f3841b29beec11bcf661deff53ce0b'
$candidateSha = '06d52681084b80196e40d640e669a927a159c7e8'
New-Item -ItemType Directory -Path $OutputRoot -ErrorAction Stop | Out-Null
$arms = @(
    [pscustomobject]@{ name='baseline'; root=$BaselineRoot; sha=$baselineSha; ref='refs/heads/main' },
    [pscustomobject]@{ name='candidate'; root=$CandidateRoot; sha=$candidateSha; ref='refs/heads/dependabot/nuget/src/Win7POS.Data/nuget-minor-patch-d76f50a9d7' }
)
$order = @('baseline','candidate','candidate','baseline')
$provenance = [ordered]@{
    diagnosticOnly=$true; certifiesCanonicalCI=$false; certifiesWindows7=$false
    createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); status='PREPARING'; stage='INITIAL_MANIFEST'
    design='One hosted Windows VM, locked builds, fixed A-B-B-A order, fresh process/data per trial, no warmup/retry'
    scenario='IMPORT_RECOVERY_completion'; processBudgetMilliseconds=60000; cleanupBudgetMilliseconds=5000
    controllerSha=$env:GITHUB_SHA; controllerRef=$env:GITHUB_REF; controllerActualSha=$null; scriptSha256=$null
    runId=$env:GITHUB_RUN_ID; runAttempt=$env:GITHUB_RUN_ATTEMPT; sdkRequested=$null; sdkActual=$null
    pwshVersion=$PSVersionTable.PSVersion.ToString(); image=$env:ImageOS; imageVersion=$env:ImageVersion; runner=$env:RUNNER_NAME
    runtimeManifestScope='Selected app/provider/native files only, not a complete runtime manifest'
    arms=@(); trials=@(); error=$null
}
foreach ($arm in $arms) {
    $provenance.arms += [ordered]@{ name=$arm.name; expectedSha=$arm.sha; actualSha=$null; root=$arm.root; ref=$arm.ref; sourceHashes=@{}; runtimeFileSample=@{}; restoreExit=$null; buildExit=$null }
}
for ($index=0; $index -lt $order.Count; $index++) {
    $provenance.trials += [ordered]@{ trial=($index+1); arm=$order[$index]; outcome='NOT_RUN'; stage='PLANNED'; reason='Awaiting preparation and preceding independent trials' }
}
function Save-Manifest {
    $provenance.updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $provenance | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputRoot 'comparison.json')
}
# This initial manifest survives a preparation, source-verification or build failure.
Save-Manifest
$previousEndpoint = $env:WIN7POS_ADMIN_WEB_BASE_URL
$failures = 0
try {
    $provenance.scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $controllerRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $provenance.controllerActualSha = (& git -C $controllerRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $provenance.controllerActualSha -ne $env:GITHUB_SHA) { throw 'Diagnostic controller SHA mismatch.' }
    $provenance.stage = 'HOST_AND_SDK'
    Save-Manifest
    $provenance.os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
    $provenance.system = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,TotalPhysicalMemory,NumberOfLogicalProcessors
    $provenance.cpu = Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
    $provenance.disk = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | Select-Object DeviceID,FileSystem,Size,FreeSpace
    $provenance.sdkRequested = (Get-Content -Raw -LiteralPath (Join-Path $CandidateRoot 'global.json') | ConvertFrom-Json).sdk.version
    Push-Location $CandidateRoot
    try {
        $provenance.sdkActual = (& dotnet --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $provenance.sdkActual -ne $provenance.sdkRequested) { throw 'Exact SDK mismatch.' }
    }
    finally { Pop-Location }
    $provenance.stage = 'VERIFY_IMMUTABLE_SOURCE'
    Save-Manifest
    foreach ($arm in $arms) {
        $evidence = $provenance.arms | Where-Object name -eq $arm.name
        $evidence.actualSha = (& git -C $arm.root rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $evidence.actualSha -ne $arm.sha) { throw "Exact SHA mismatch for $($arm.name)." }
        foreach ($relative in @('scripts/run-functional-completion-smoke.ps1', 'tests/Win7POS.Wpf.UiSmokeHarness/CatalogImportRecoveryCompletionSmoke.cs')) {
            $evidence.sourceHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $arm.root $relative) -Algorithm SHA256).Hash
        }
        Save-Manifest
    }
    foreach ($relative in $provenance.arms[0].sourceHashes.Keys) {
        if ($provenance.arms[0].sourceHashes[$relative] -ne $provenance.arms[1].sourceHashes[$relative]) {
            throw "Recovery source or original wrapper differs: $relative"
        }
    }
    # Probe the actual runner cmdlet default with the same absolute FilePath/Hidden/PassThru,
    # without Redirect* parameters (which would change UseShellExecute). The OS child only writes cwd.
    $provenance.stage = 'DEFAULT_CWD_PREFLIGHT'
    Save-Manifest
    foreach ($arm in $arms) {
        $evidence = $provenance.arms | Where-Object name -eq $arm.name
        $cwdReceipt = Join-Path $OutputRoot ($arm.name + '-default-cwd.txt')
        $evidence.cwdProbe = [ordered]@{ expected=$arm.root; observed=$null; outcome='NOT_RUN'; psVersion=$PSVersionTable.PSVersion.ToString(); command=$env:ComSpec; timeoutMilliseconds=5000; cleanupConfirmed=$null }
        Save-Manifest
        $probe = $null
        Push-Location $arm.root
        try {
            $probe = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d','/c', ('cd > "' + $cwdReceipt + '"')) -WindowStyle Hidden -PassThru
            if (-not $probe.WaitForExit(5000)) {
                $evidence.cwdProbe.outcome = 'FAIL'; $evidence.cwdProbe.reason = 'OS cwd probe timed out'
                Save-Manifest
                Stop-Process -Id $probe.Id -ErrorAction SilentlyContinue
                $evidence.cwdProbe.cleanupConfirmed = $probe.WaitForExit(5000)
                Save-Manifest
                throw 'Default cwd preflight timed out; no product trials permitted.'
            }
            $evidence.cwdProbe.exitCode = $probe.ExitCode
            if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $cwdReceipt -PathType Leaf)) { throw 'Default cwd probe produced no valid receipt.' }
            $evidence.cwdProbe.observed = ([IO.File]::ReadAllText($cwdReceipt)).Trim()
            $expected = [IO.Path]::GetFullPath($arm.root).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
            $observed = [IO.Path]::GetFullPath($evidence.cwdProbe.observed).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
            $evidence.cwdProbe.outcome = if ([string]::Equals($expected,$observed,[StringComparison]::OrdinalIgnoreCase)) { 'PASS' } else { 'FAIL' }
            Save-Manifest
            if ($evidence.cwdProbe.outcome -ne 'PASS') { throw 'Runner default cwd differs from canonical repo root; no product trials permitted.' }
        }
        finally {
            if ($probe) { $probe.Dispose() }
            Pop-Location
        }
    }
    # Both revisions are built before any trial; builds are not interleaved with timings.
    foreach ($arm in $arms) {
        $evidence = $provenance.arms | Where-Object name -eq $arm.name
        $provenance.stage = 'BUILD_' + $arm.name
        Save-Manifest
        Push-Location $arm.root
        try {
            $version = & pwsh -NoProfile -File scripts/win7pos/windows/resolve-release-version.ps1 -CommitSha $arm.sha -Ref $arm.ref -AsJson | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or $version.CommitSha -ne $arm.sha) { throw 'Exact-SHA version resolution failed.' }
            $evidence.version = $version
            dotnet restore Win7POS.slnx --locked-mode
            $evidence.restoreExit = $LASTEXITCODE
            Save-Manifest
            if ($evidence.restoreExit -ne 0) { throw "Locked restore failed: $($arm.name)" }
            dotnet build tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore "-p:Win7PosBuildVersion=$($version.BuildVersion)" "-p:Win7PosInformationalVersion=$($version.InformationalVersion)" "-p:Win7PosCommitSha=$($arm.sha)"
            $evidence.buildExit = $LASTEXITCODE
            Save-Manifest
            if ($evidence.buildExit -ne 0) { throw "Harness build failed: $($arm.name)" }
            $bin = Join-Path $arm.root 'tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48'
            foreach ($file in @('Win7POS.Wpf.UiSmokeHarness.exe','Win7POS.Core.dll','Win7POS.Data.dll','Win7POS.Wpf.exe','Dapper.dll','Microsoft.Data.Sqlite.dll','e_sqlite3.dll')) {
                $path = Join-Path $bin $file
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime file: $path" }
                $evidence.runtimeFileSample[$file] = @{ sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; fileVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion }
            }
            Copy-Item -LiteralPath (Join-Path $arm.root 'tests/Win7POS.Wpf.UiSmokeHarness/packages.lock.json') -Destination (Join-Path $OutputRoot ($arm.name + '-packages.lock.json'))
            Save-Manifest
        }
        finally { Pop-Location }
    }
    $provenance.status = 'RUNNING'
    $env:WIN7POS_ADMIN_WEB_BASE_URL = 'http://127.0.0.1:9'
    for ($index=0; $index -lt $order.Count; $index++) {
        $arm = $arms | Where-Object name -eq $order[$index]
        $trial = $provenance.trials[$index]
        $trialRoot = Join-Path $OutputRoot (('trial-{0}-{1}' -f ($index+1),$arm.name))
        $dataDir = Join-Path $trialRoot 'data'
        New-Item -ItemType Directory -Path $trialRoot | Out-Null
        $exe = Join-Path $arm.root 'tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48/Win7POS.Wpf.UiSmokeHarness.exe'
        $trial.sha = $arm.sha; $trial.workingDirectory = $arm.root; $trial.startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $trial.stage = 'STARTING'; $trial.reason = ''; $trial.cleanupAttempted = $false; $trial.cleanupConfirmed = $null
        $provenance.stage = 'TRIAL_' + ($index+1)
        Save-Manifest
        $process = $null
        try {
            # Explicit repo root preserves the canonical workflow's current directory.
            # Same executable arguments and unchanged 60000ms process budget as the original wrapper.
            $process = Start-Process -FilePath $exe -WorkingDirectory $arm.root -ArgumentList @('--data-dir', ('"' + $dataDir + '"'), '--functional-completion-smoke', '--scenario', 'IMPORT_RECOVERY_completion') -WindowStyle Hidden -PassThru
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $trial.stage = 'RUNNING'; $trial.pid = $process.Id
            # No file I/O between Start-Process and the same constant canonical wait.
            $completed = $process.WaitForExit(60000)
            $watch.Stop()
            $trial.completed = $completed; $trial.elapsedMilliseconds = $watch.Elapsed.TotalMilliseconds; $trial.budgetMilliseconds = 60000
            if (-not $completed) {
                # Preserve the first timeout outcome BEFORE cleanup; it cannot become PASS later.
                $trial.outcome = 'FAIL'; $trial.stage = 'TIMEOUT_OBSERVED'; $trial.reason = 'Unchanged 60000ms process budget exceeded'
                $trial | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $trialRoot 'result-before-cleanup.json')
                Save-Manifest
                $trial.cleanupAttempted = $true
                Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
                $trial.cleanupConfirmed = $process.WaitForExit(5000)
                Save-Manifest
                if (-not $trial.cleanupConfirmed) { throw 'Timed-out process termination not confirmed; remaining trials NOT_RUN.' }
            }
            $trial.exitCode = $process.ExitCode
            $resultPath = Join-Path $dataDir 'functional-completion.txt'
            $trial.terminalMarker = if (Test-Path -LiteralPath $resultPath -PathType Leaf) { [IO.File]::ReadAllText($resultPath) } else { '' }
            if ($completed) {
                $passed = $process.ExitCode -eq 0 -and $trial.terminalMarker.StartsWith('PASS')
                $trial.outcome = if ($passed) { 'PASS' } else { 'FAIL' }
                if (-not $passed) { $trial.reason = 'Harness exit code or terminal PASS assertion failed' }
            }
            $trial.stage = 'COMPLETE'
            $trial | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $trialRoot 'result.json')
            Save-Manifest
            Write-Output ($trial | ConvertTo-Json -Compress)
            if ($trial.outcome -ne 'PASS') { $failures++ }
        }
        catch {
            $trial.outcome = 'FAIL'; $trial.stage = 'ABORTED'; $trial.error = $_.Exception.Message
            Save-Manifest
            throw
        }
        finally {
            if ($process) {
                if (-not $process.HasExited -and -not $trial.cleanupAttempted) {
                    $trial.outcome = 'FAIL'; $trial.stage = 'EXCEPTION_BEFORE_CLEANUP'; $trial.cleanupAttempted = $true
                    $trial | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $trialRoot 'result-before-cleanup.json')
                    Save-Manifest
                    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
                    $trial.cleanupConfirmed = $process.WaitForExit(5000)
                    Save-Manifest
                }
                $process.Dispose()
            }
        }
        # Independent predeclared trials continue after a safely terminated FAIL, never dependent acceptance gates.
    }
    $provenance.status = if ($failures) { 'FAIL' } else { 'PASS' }
    $provenance.stage = 'COMPLETE_DIAGNOSTIC_ONLY'
    Write-Output "RECOVERY_COMPARISON_DIAGNOSTIC_ONLY=true; canonical_failure_preserved=true; trials=4; failures=$failures"
    if ($failures) { throw "$failures of four fixed diagnostic trials failed; all outcomes retained." }
}
catch {
    $provenance.status = 'FAIL'; $provenance.error = $_.Exception.Message
    foreach ($trial in $provenance.trials) {
        if ($trial.outcome -eq 'NOT_RUN') { $trial.reason = 'Preparation or safe continuation prerequisite failed: ' + $provenance.error }
    }
    throw
}
finally {
    $env:WIN7POS_ADMIN_WEB_BASE_URL = $previousEndpoint
    Save-Manifest
}
