#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../../scripts/qa/Win7PosQaDiagnosticProjection.psm1') -Force
$temporaryBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root=[IO.Path]::Combine($temporaryBase,'Win7POS.DiagnosticProjection.'+[Guid]::NewGuid().ToString('N'))
if(-not $root.StartsWith($temporaryBase,[StringComparison]::OrdinalIgnoreCase)){throw 'test_root_invalid'}
function Require([bool]$Condition,[string]$Code){if(-not $Condition){throw $Code}}
try {
    $null=New-Item -ItemType Directory -Path (Join-Path $root 'tests/Win7POS.Core.Tests/Data'),(Join-Path $root 'tests/qa'),(Join-Path $root 'scripts')
    [IO.File]::WriteAllText((Join-Path $root 'tests/Win7POS.Core.Tests/Data/SyntheticTests.cs'),'namespace Win7POS.Core.Tests.Data; [TestClass] public sealed class SyntheticTests { public void ExactCount() {} public void Reject() {} }')
    [IO.File]::WriteAllText((Join-Path $root 'scripts/check-synthetic.ps1'),"Write-Host 'PASS: fixed check'`nWrite-Host 'FAIL: fixed check'`n")
    $run='Win7POS.QA.20261104T000000000Z.'+('a'*32);$fingerprint='b'*64
    $trx=Join-Path $root 'synthetic.trx'
    [IO.File]::WriteAllText($trx,@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="a"><TestMethod className="Win7POS.Core.Tests.Data.SyntheticTests" name="ExactCount"/></UnitTest><UnitTest id="b"><TestMethod className="Win7POS.Core.Tests.Data.SyntheticTests" name="Reject"/></UnitTest><UnitTest id="c"><TestMethod className="Unapproved.PrivateClass" name="private-secret"/></UnitTest></TestDefinitions><Results><UnitTestResult testId="a" testName="private-secret" outcome="Failed" duration="00:00:01"><Output><ErrorInfo><Message>Assertion failed. expected: 42 actual: 41 C:\private-secret\source.xlsx</Message></ErrorInfo></Output></UnitTestResult><UnitTestResult testId="b" outcome="Passed" duration="00:00:00.010"/><UnitTestResult testId="c" outcome="Failed"/></Results></TestRun>
'@)
    $projected=ConvertFrom-QaTrxDiagnostics $trx $root $run $fingerprint
    Require ($projected.results.Count -eq 2) 'trx_count'
    Require ($projected.results[0].expected -eq 42 -and $projected.results[0].actual -eq 41) 'numeric_assertion'
    Require ($projected.results[0].assertionId -ceq 'TRX_Win7POS.Core.Tests.Data.SyntheticTests.ExactCount') 'method_catalog'
    Require ($projected.results[1].status -ceq 'PASS') 'pass_status'
    Require (-not $projected.projection.complete -and $projected.projection.withheld -eq 1) 'unknown_method_withheld'
    Require (($projected|ConvertTo-Json -Depth 20) -notmatch 'private-secret|source.xlsx') 'trx_privacy'
    $transcript=Join-Path $root 'gates.log'
    [IO.File]::WriteAllText($transcript,"PASS: fixed check`nprivate-secret one`nFAIL: fixed check`nprivate-secret two`nPASS: fixed check`n")
    $gates=ConvertFrom-QaGateTranscript $transcript $root $run $fingerprint
    Require ($gates.results.Count -eq 3 -and $gates.results[1].status -ceq 'FAIL') 'full_ordered_transcript'
    Require ($gates.results[0].sequence -eq 0 -and $gates.results[1].sequence -eq 2 -and $gates.results[2].sequence -eq 4) 'original_line_order'
    Require (-not $gates.projection.complete -and $gates.projection.withheld -eq 2) 'withheld_is_partial'
    Require (($gates|ConvertTo-Json -Depth 20) -notmatch 'private-secret') 'gate_privacy'
    [IO.File]::WriteAllText((Join-Path $root 'scripts/check-migrations.ps1'),@'
Require-Text $registry 'fixed-regex' 'published sequence is pinned'
Fail 'append-only sequence mismatch'
Fail $privateMessage
Write-Host '=== RESULT: FAIL ==='
'@)
    [IO.File]::WriteAllText((Join-Path $root 'scripts/check-release.ps1'),"Write-Host '=== RESULT: FAIL ==='`n")
    [IO.File]::WriteAllText($transcript,@'
=== FAILED GATE: check-migrations.ps1 (exit 1) ===
FAIL: published sequence is pinned
FAIL: append-only sequence mismatch
FAIL: private-secret dynamic failure
=== RESULT: FAIL ===
=== END FAILED GATE: check-migrations.ps1 ===
=== RESULT: FAIL ===
'@)
    $scoped=ConvertFrom-QaGateTranscript $transcript $root $run $fingerprint
    Require ($scoped.results.Count -eq 3) 'gate_command_literals_projected'
    Require (@($scoped.results | Where-Object {$_.assertionId -cnotmatch '^GATE_check-migrations_L[124]$'}).Count -eq 0) 'failed_gate_source_scope'
    Require ($scoped.projection.withheld -eq 2) 'ambiguous_summary_and_dynamic_message_withheld'
    Require (($scoped|ConvertTo-Json -Depth 20) -notmatch 'private-secret|privateMessage|fixed-regex') 'scoped_gate_privacy'
    [IO.File]::WriteAllText($trx,'<!DOCTYPE x [<!ENTITY e SYSTEM "file:///C:/private-secret">]><TestRun>&e;</TestRun>')
    $denied=$false;try{$null=ConvertFrom-QaTrxDiagnostics $trx $root $run $fingerprint}catch{$denied=$true}
    Require $denied 'external_entity_denied'
    Write-Output 'PASS: typed TRX and full gate transcript projections'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(-not $resolved.StartsWith($temporaryBase,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'Win7POS.DiagnosticProjection.*'){throw 'cleanup_root_invalid'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
