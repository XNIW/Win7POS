Set-StrictMode -Version Latest

function Get-ScopedPayloadPath([string]$Root, [string]$Relative) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $prefix $Relative))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'qa_payload_path_outside_root' }
    return $path
}

function Assert-Win7PosQaPayload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$HarnessDirectory, [Parameter(Mandatory)][object[]]$Manifest)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Manifest) {
        if (-not $seen.Add([string]$entry.file) -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'qa_payload_manifest_invalid' }
        $path = Get-ScopedPayloadPath $HarnessDirectory $entry.file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
            throw 'qa_payload_binary_mismatch'
        }
    }
    foreach ($required in @('Win7POS.Core.dll','Win7POS.Data.dll','Win7POS.Wpf.exe')) {
        if (-not $seen.Contains($required)) { throw 'qa_payload_application_missing' }
    }
}

function Copy-Win7PosQaPayload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$VerifiedPayloadDirectory, [Parameter(Mandatory)][string]$HarnessDirectory)
    # The caller must run test-downloaded-release-pack.ps1 first. This helper
    # preserves that verified payload's byte identity in the existing QA harness.
    $source = (Resolve-Path -LiteralPath $VerifiedPayloadDirectory).Path.TrimEnd('\','/')
    $target = (Resolve-Path -LiteralPath $HarnessDirectory).Path.TrimEnd('\','/')
    if ($source -eq $target -or $target.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'qa_payload_source_destination_overlap'
    }
    $manifest = @(Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
        [pscustomobject]@{file=$_.FullName.Substring($source.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    Assert-Win7PosQaPayload -HarnessDirectory $source -Manifest $manifest
    foreach ($entry in $manifest) {
        $destination = Get-ScopedPayloadPath $target $entry.file
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
        Copy-Item -LiteralPath (Get-ScopedPayloadPath $source $entry.file) -Destination $destination -Force
    }
    Assert-Win7PosQaPayload -HarnessDirectory $target -Manifest $manifest
    return $manifest
}

Export-ModuleMember -Function Copy-Win7PosQaPayload, Assert-Win7PosQaPayload
