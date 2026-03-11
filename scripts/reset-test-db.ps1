# Reset del database di test Win7POS (solo per ambiente dev/test).
# Elimina pos.db nella directory dati; al prossimo avvio l'app mostrerà il wizard primo avvio.
#
# Uso:
#   .\reset-test-db.ps1                    # usa WIN7POS_DATA_DIR se impostata, altrimenti C:\ProgramData\Win7POS
#   .\reset-test-db.ps1 -DataDir "C:\POSData\TestRun1"
#
# Attenzione: non usare in produzione.

param(
    [string]$DataDir
)

if (-not $DataDir) {
    $DataDir = $env:WIN7POS_DATA_DIR
}
if (-not $DataDir) {
    $DataDir = Join-Path $env:ProgramData "Win7POS"
}

$dbPath = Join-Path $DataDir "pos.db"
if (Test-Path $dbPath) {
    Remove-Item -Force $dbPath
    Write-Host "Rimosso: $dbPath"
} else {
    Write-Host "File non trovato: $dbPath"
}
