[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet(20000,100000)][int]$Products = 100000,
    [ValidateRange(0,120)][int]$SoakMinutes = 0,
    [string]$HarnessDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (-not $HarnessDirectory) {
    $HarnessDirectory = Join-Path $PSScriptRoot '../tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48'
}
$exe = Join-Path $HarnessDirectory 'Win7POS.Wpf.UiSmokeHarness.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build the Release x86 harness first.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new; previous samples must be preserved.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$manifest = @(Get-ChildItem -LiteralPath $HarnessDirectory -File | Where-Object { $_.Extension -in '.exe','.dll' } |
    ForEach-Object { [ordered]@{ file=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
[ordered]@{ products=$Products; soakMinutes=$SoakMinutes; startedUtc=[DateTimeOffset]::UtcNow.ToString('O');
    protocol='31 samples per case: first call 0, warm 1..30; no forced GC; bitmap is not physical paint; no process-start measurement';
    binaries=$manifest } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'protocol.json')
$previous = $env:WIN7POS_QA_SOAK_MINUTES
try {
    $env:WIN7POS_QA_SOAK_MINUTES = if ($SoakMinutes) { [string]$SoakMinutes } else { $null }
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"'+$OutputDirectory+'"'), '--cart-performance', '--products', $Products) -WindowStyle Hidden -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($SoakMinutes + 10)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            throw 'Performance harness timed out; all partial samples are preserved.'
        }
    }
    if ($process.ExitCode -ne 0) { throw "Performance harness exit=$($process.ExitCode); inspect preserved harness-error.txt." }
    $result = Get-Content -LiteralPath (Join-Path $OutputDirectory 'functional-completion.txt') -Raw
    if (-not $result.StartsWith('PASS')) { throw $result }
    foreach ($entry in $manifest) {
        if ((Get-FileHash -LiteralPath (Join-Path $HarnessDirectory $entry.file) -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Harness binary changed during measurement: $($entry.file)"
        }
    }
    if (@(Import-Csv (Join-Path $OutputDirectory 'cart-performance.csv')).Count -ne 155 -or
        @(Import-Csv (Join-Path $OutputDirectory 'render-stages.csv')).Count -ne 31) { throw 'Incomplete measurement samples.' }
    if ($SoakMinutes) {
        $samples = @(Import-Csv (Join-Path $OutputDirectory 'cart-soak.csv'))
        if ($samples.Count -eq 0 -or [double]::Parse($samples[-1].elapsed_s, [cultureinfo]::InvariantCulture) -lt $SoakMinutes * 60) {
            throw 'Incomplete soak duration.'
        }
    }
    Write-Output $result.Trim()
    Write-Output "PERFORMANCE_ARTIFACT=$OutputDirectory"
}
finally { $env:WIN7POS_QA_SOAK_MINUTES = $previous }
