param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

try {
    if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        throw "This script must be run from an interactive Windows desktop session."
    }

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $parent = Split-Path -Parent $resolvedOutput
    if ($parent -and -not (Test-Path $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) {
        throw "Primary screen bounds are empty. Run from an interactive desktop session."
    }

    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    Write-Host "Screenshot saved: $resolvedOutput"
}
catch {
    Write-Host "ERROR: screenshot capture failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
