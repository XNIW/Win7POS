$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../scripts/qa/Win7PosQaPayload.psm1') -Force
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase ('Win7POS.PayloadBinding.' + [Guid]::NewGuid().ToString('N'))))
$null = New-Item -ItemType Directory -Path $testRoot
$source = Join-Path $testRoot 'payload'
$target = Join-Path $testRoot 'harness'
$null = New-Item -ItemType Directory -Path $source,$target,(Join-Path $source 'Assets')
$negative = 0
function Reject([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw 'Expected payload binding failure.' }
    $script:negative++
}
try {
    foreach ($file in @('Win7POS.Core.dll','Win7POS.Data.dll','Win7POS.Wpf.exe','Assets/synthetic.txt')) {
        [IO.File]::WriteAllText((Join-Path $source $file), ('synthetic verified bytes: ' + $file))
    }
    [IO.File]::WriteAllText((Join-Path $target 'Win7POS.Core.dll'), 'stale local build')
    [IO.File]::WriteAllText((Join-Path $target 'Win7POS.Wpf.UiSmokeHarness.exe'), 'synthetic harness preserved')
    $expected = @(Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
        [pscustomobject]@{file=$_.FullName.Substring($source.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    Reject { Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest $expected }
    $manifest = @(Copy-Win7PosQaPayload -VerifiedPayloadDirectory $source -HarnessDirectory $target)
    Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest $manifest
    if ($manifest.Count -ne 4 -or [IO.File]::ReadAllText((Join-Path $target 'Win7POS.Wpf.UiSmokeHarness.exe')) -cne 'synthetic harness preserved') {
        throw 'Payload copy omitted files or replaced the QA harness.'
    }
    [IO.File]::AppendAllText((Join-Path $target 'Win7POS.Data.dll'), 'modified after copy')
    Reject { Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest $manifest }
    $null = Copy-Win7PosQaPayload -VerifiedPayloadDirectory $source -HarnessDirectory $target
    Reject { Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest @($manifest + $manifest[0]) }
    Reject { Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest @([pscustomobject]@{file='../outside.dll';sha256=('0'*64)}) }
    Reject { Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest @($manifest | Where-Object file -NE 'Win7POS.Wpf.exe') }
    Reject { Copy-Win7PosQaPayload -VerifiedPayloadDirectory $source -HarnessDirectory $source }
    "QA_PAYLOAD_BINDING=PASS positive=1 negative=$negative"
} finally {
    $resolved = (Resolve-Path -LiteralPath $testRoot).Path
    if ($resolved -cne $testRoot -or -not $resolved.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the generated payload test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
