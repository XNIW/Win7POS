# Pure projections. Free-form messages and parameter values remain in private evidence.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Get-QaProjectionSourceFiles([string]$Root,[string[]]$Extensions) {
    $rootPath=[IO.Path]::GetFullPath($Root)
    $queue=[Collections.Generic.Queue[object]]::new();$queue.Enqueue(@{path=$rootPath;depth=0});$count=0
    while($queue.Count){
        $current=$queue.Dequeue()
        foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($current.path)){
            if(++$count -gt 20000){throw 'qa_projection_source_limit'}
            $attributes=[IO.File]::GetAttributes($entry)
            if($attributes -band [IO.FileAttributes]::ReparsePoint){throw 'qa_projection_source_link'}
            if($attributes -band [IO.FileAttributes]::Directory){
                if([IO.Path]::GetFileName($entry) -in @('bin','obj','.git','node_modules')){continue}
                if($current.depth -ge 32){throw 'qa_projection_source_limit'}
                $queue.Enqueue(@{path=$entry;depth=$current.depth+1});continue
            }
            if([IO.Path]::GetExtension($entry) -cin $Extensions){
                if(([IO.FileInfo]::new($entry)).Length -gt 2MB){throw 'qa_projection_source_limit'}
                Write-Output $entry
            }
        }
    }
}

function Get-QaCoreDiagnosticCatalog([string]$RepoRoot) {
    $catalog=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $root=[IO.Path]::Combine([IO.Path]::GetFullPath($RepoRoot),'tests','Win7POS.Core.Tests')
    foreach($file in (Get-QaProjectionSourceFiles $root @('.cs'))) {
        if($file -match '[\\/](bin|obj)[\\/]'){continue}
        if(([IO.FileInfo]::new($file)).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'qa_projection_source_link'}
        $source=[IO.File]::ReadAllText($file)
        $ns=[regex]::Match($source,'\bnamespace\s+(Win7POS\.Core\.Tests(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*[;{]')
        if(-not $ns.Success){continue}
        $classes=[regex]::Matches($source,'\[TestClass\]\s*(?:\[[^\]]+\]\s*)*public\s+(?:(?:sealed|partial|abstract)\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)')
        $methods=[regex]::Matches($source,'\bpublic\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|ValueTask|void)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(')
        foreach($type in $classes){foreach($method in $methods){$null=$catalog.Add($ns.Groups[1].Value+'.'+$type.Groups[1].Value+'.'+$method.Groups[1].Value)}}
    }
    return ,$catalog
}

function New-QaProjectionEnvelope([string]$RunId,[string]$SourceFingerprintSha256,$Events,[int]$Withheld,[string]$Kind) {
    if($RunId -cnotmatch '^Win7POS\.QA\.\d{8}T\d{9}Z\.[a-f0-9]{32}$' -or $SourceFingerprintSha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'qa_projection_context_invalid'}
    return [pscustomobject]@{schemaVersion='win7pos-qa-diagnostics-v1';runId=$RunId;sourceFingerprintSha256=$SourceFingerprintSha256;results=@($Events.ToArray());projection=@{kind=$Kind;complete=($Withheld -eq 0);withheld=$Withheld}}
}

function ConvertFrom-QaTrxDiagnostics {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TrxPath,[Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$RunId,[Parameter(Mandatory)][string]$SourceFingerprintSha256)
    $info=[IO.FileInfo]::new([IO.Path]::GetFullPath($TrxPath))
    if(-not $info.Exists -or $info.Length -le 0 -or $info.Length -gt 32MB -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'qa_projection_trx_invalid'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=32MB
    $reader=[Xml.XmlReader]::Create($info.FullName,$settings)
    try {$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)} finally {$reader.Dispose()}
    $catalog=Get-QaCoreDiagnosticCatalog $RepoRoot
    $definitions=@{}
    foreach($definition in $document.SelectNodes('//*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]')) {
        $method=$definition.SelectSingleNode('*[local-name()="TestMethod"]')
        if($null -eq $method){continue}
        $name=$method.GetAttribute('className')+'.'+$method.GetAttribute('name')
        if($catalog.Contains($name)){$definitions[$definition.GetAttribute('id')]=$name}
    }
    $events=[Collections.Generic.List[object]]::new();$withheld=0;$sequence=0
    foreach($result in $document.SelectNodes('//*[local-name()="Results"]/*[local-name()="UnitTestResult"]')) {
        if($events.Count -ge 20000){throw 'qa_projection_result_limit'}
        $id=$result.GetAttribute('testId');if(-not $definitions.ContainsKey($id)){$withheld++;continue}
        $status=switch -CaseSensitive ($result.GetAttribute('outcome')) {'Passed' {'PASS'} 'Failed' {'FAIL'} 'Timeout' {'TIMEOUT'} default {'NOT_RUN'}}
        $expected=$null;$actual=$null;$partial=$false
        if($status -eq 'FAIL') {
            $message=$result.SelectSingleNode('*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
            $text=if($null -ne $message){$message.InnerText}else{''}
            $pair=[regex]::Match($text,'(?im)\bexpected:\s*([+-]?[0-9]+(?:\.[0-9]+)?)\s+actual:\s*([+-]?[0-9]+(?:\.[0-9]+)?)\b')
            if($pair.Success){
                $left=[decimal]0;$right=[decimal]0
                if([decimal]::TryParse($pair.Groups[1].Value,[Globalization.NumberStyles]::Number,[Globalization.CultureInfo]::InvariantCulture,[ref]$left) -and [decimal]::TryParse($pair.Groups[2].Value,[Globalization.NumberStyles]::Number,[Globalization.CultureInfo]::InvariantCulture,[ref]$right)){$expected=$left;$actual=$right}else{$partial=$true}
            } elseif($text -match 'Expected condition to be true\.[\s\S]*actual:\s*false'){$expected=$true;$actual=$false}
            elseif($text -match 'Expected condition to be false\.[\s\S]*actual:\s*true'){$expected=$false;$actual=$true}
            else{$partial=$true}
        }
        if($partial){$withheld++}
        $duration=[TimeSpan]::Zero;$null=[TimeSpan]::TryParse($result.GetAttribute('duration'),[Globalization.CultureInfo]::InvariantCulture,[ref]$duration)
        $events.Add([pscustomobject]@{sequence=$sequence++;stepId='core';scenarioId=$null;assertionId=('TRX_'+$definitions[$id]);status=$status;expected=$expected;actual=$actual;checkpoint='assertion';code=$(if($partial){'unavailable'}elseif($status -eq 'FAIL'){'assertion_failed'}else{'none'});durationMs=[Math]::Max(0,$duration.TotalMilliseconds);diagnosticCompleteness=$(if($partial){'partial'}else{'complete'})})
    }
    if($events.Count -eq 0){$withheld++}
    return New-QaProjectionEnvelope $RunId $SourceFingerprintSha256 $events $withheld 'trx'
}

function Get-QaGateDiagnosticTemplates([string]$RepoRoot) {
    $templates=[Collections.Generic.List[object]]::new()
    $root=[IO.Path]::GetFullPath($RepoRoot)
    foreach($folder in @('scripts','tests/qa')) {
        $directory=[IO.Path]::Combine($root,$folder)
        foreach($file in (Get-QaProjectionSourceFiles $directory @('.ps1','.js','.mjs'))) {
            if([IO.Path]::GetExtension($file) -cnotin @('.ps1','.js','.mjs')){continue}
            if(([IO.FileInfo]::new($file)).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'qa_projection_source_link'}
            $lines=[IO.File]::ReadAllLines($file)
            for($i=0;$i -lt $lines.Length;$i++) {
                $statement=[regex]::Match($lines[$i],'(?:Write-(?:Host|Output|Error|Warning)\s+|console\.(?:log|error)\s*\(\s*)([\x22\x27])(?<text>[^\x22\x27\r\n]{1,500})\1')
                if(-not $statement.Success){continue}
                $literal=$statement.Groups['text'].Value
                if($literal -match '[`$]' -or $literal -notmatch '(?i)^(?:PASS\b|FAIL\b|OK\b|ERROR\b|=== RESULT)'){continue}
                $status=if($literal -match '(?i)^(FAIL|ERROR)\b' -or $literal -match '(?i)RESULT.*FAIL'){'FAIL'}else{'PASS'}
                $templates.Add(@{text=$literal;stem=[IO.Path]::GetFileNameWithoutExtension($file);assertionId=('GATE_'+[IO.Path]::GetFileNameWithoutExtension($file)+'_L'+($i+1));status=$status})
            }
            if([IO.Path]::GetExtension($file) -ceq '.ps1') {
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
                if($errors.Count){continue}
                foreach($command in $ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst]},$true)) {
                    $name=$command.GetCommandName()
                    if($name -cnotin @('Fail','Pass','Require-Text') -or $command.CommandElements.Count -lt 2){continue}
                    $argument=$command.CommandElements[-1]
                    if($argument -isnot [Management.Automation.Language.StringConstantExpressionAst] -and
                       ($argument -isnot [Management.Automation.Language.ExpandableStringExpressionAst] -or $argument.NestedExpressions.Count)){continue}
                    $literal=$argument.Value
                    if([string]::IsNullOrWhiteSpace($literal) -or $literal.Length -gt 500 -or $literal -match '[\r\n]'){continue}
                    $statuses=if($name -ceq 'Require-Text'){@('PASS','FAIL')}elseif($name -ceq 'Fail'){@('FAIL')}else{@('PASS')}
                    foreach($status in $statuses) {
                        $templates.Add(@{text=($status+': '+$literal);stem=[IO.Path]::GetFileNameWithoutExtension($file);assertionId=('GATE_'+[IO.Path]::GetFileNameWithoutExtension($file)+'_L'+$command.Extent.StartLineNumber);status=$status})
                    }
                }
            }
        }
    }
    return ,$templates
}

function ConvertFrom-QaGateTranscript {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TranscriptPath,[Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$RunId,[Parameter(Mandatory)][string]$SourceFingerprintSha256)
    $info=[IO.FileInfo]::new([IO.Path]::GetFullPath($TranscriptPath))
    if(-not $info.Exists -or $info.Length -gt 16MB -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'qa_projection_transcript_invalid'}
    $templates=Get-QaGateDiagnosticTemplates $RepoRoot
    $lookup=@{};foreach($template in $templates){if(-not $lookup.ContainsKey($template.text)){$lookup[$template.text]=@()};$lookup[$template.text]+=$template}
    $events=[Collections.Generic.List[object]]::new();$withheld=0;$sequence=0
    $failedGateStem=$null
    foreach($rawLine in [IO.File]::ReadLines($info.FullName)) {
        $line=$rawLine.Trim();if(-not $line){continue}
        if($line -cmatch '^=== FAILED GATE: ([A-Za-z0-9_-]+)\.ps1(?: \[release\])? \(exit [0-9]+\) ===$'){$failedGateStem=$Matches[1];$sequence++;continue}
        if($line -cmatch '^=== END FAILED GATE: [A-Za-z0-9_-]+\.ps1(?: \[release\])? ===$'){$failedGateStem=$null;$sequence++;continue}
        if(-not $lookup.ContainsKey($line)){$withheld++;$sequence++;continue}
        $candidates=@($lookup[$line] | Where-Object {$null -eq $failedGateStem -or $_.stem -ceq $failedGateStem} | Sort-Object assertionId -Unique)
        # Shared RESULT lines cannot identify their source without a failed-gate
        # frame. Never attribute the first matching literal to an unrelated gate.
        if($candidates.Count -ne 1){$withheld++;$sequence++;continue}
        $template=$candidates[0]
        $events.Add([pscustomobject]@{sequence=$sequence++;stepId='gates';scenarioId=$null;assertionId=$template.assertionId;status=$template.status;expected=$true;actual=($template.status -eq 'PASS');checkpoint='assertion';code=$(if($template.status -eq 'FAIL'){'assertion_failed'}else{'none'});durationMs=0;diagnosticCompleteness='complete'})
    }
    if($withheld){foreach($event in $events){$event.diagnosticCompleteness='partial'}}
    return New-QaProjectionEnvelope $RunId $SourceFingerprintSha256 $events $withheld 'gates'
}

Export-ModuleMember -Function ConvertFrom-QaTrxDiagnostics,ConvertFrom-QaGateTranscript
