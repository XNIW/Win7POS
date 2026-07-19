$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$srcRoot = Join-Path $repoRoot "src"
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

$paymentXaml = Read-Text "src/Win7POS.Wpf/Pos/PaymentView.xaml"
$paymentVm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PaymentViewModel.cs"
$fiscalRenderer = Read-Text "src/Win7POS.Core/Receipt/FiscalBoletaTextRenderer.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$registerVm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterViewModel.cs"
$registerXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterDialog.xaml"
$dailyDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DailyReportDialog.xaml"
$dailyView = Read-Text "src/Win7POS.Wpf/Pos/DailyReportView.xaml"
$localization = Read-Text "src/Win7POS.Wpf/Localization/PosLocalization.cs"
$secondaryLocalization = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$paymentCopy = $paymentXaml + $paymentVm + $fiscalRenderer + $localization + $secondaryLocalization
$registerCopy = $registerVm + $registerXaml + $dailyDialog + $dailyView + $localization + $secondaryLocalization
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($paymentCopy -notmatch "Stato stampa boleta") { Fail "payment view must label direct boleta print status" } else { Pass "direct boleta print status label present" }
if ($paymentXaml -match "Fiscale \(SII Web\)|Area fiscale temporaneamente disattivata") { Fail "payment view still has misleading SII Web/disabled copy" } else { Pass "misleading SII Web copy absent" }
if ($paymentCopy -notmatch "Stampa diretta boleta dopo pagamento in contanti") { Fail "direct-print cash policy copy missing" } else { Pass "direct-print cash policy copy present" }
if ($paymentXaml -match '(?i)\bPDF\b') { Fail "payment view must not present a local PDF workflow" } else { Pass "payment view presents no local PDF workflow" }
if ($paymentCopy -notmatch "Resto \(solo contanti\)") { Fail "cash-only change copy missing" } else { Pass "cash-only change copy present" }
if ($paymentCopy -notmatch "La carta non può superare il saldo da pagare") { Fail "card over-balance validation message missing" } else { Pass "card over-balance validation message present" }
if ($workflow -notmatch "CardAmountMinor > Math\.Max\(0, totalMinor - CashAmountMinor\)") { Fail "workflow must reject card over residual balance" } else { Pass "workflow rejects card over residual balance" }
if (($paymentVm + $fiscalRenderer) -match "REF\. VENDEDOR|Dirección: Santiago|Giro: VENTA PRENDAS") { Fail "hardcoded fiscal literals remain in boleta preview" } else { Pass "hardcoded fiscal literals absent" }
if ($fiscalRenderer -notmatch "BusinessGiro" -or $fiscalRenderer -notmatch "LegalRepresentativeRut") { Fail "boleta preview must use official shop fiscal fields" } else { Pass "boleta preview uses official shop fiscal fields" }
if ($registerCopy -notmatch "Documento: boleta stampata" -or $registerCopy -notmatch "Non stampata \(contanti\)" -or $registerCopy -notmatch "Non stampata \(policy carta\)") { Fail "register detail document status copy missing" } else { Pass "register document status copy present" }
if ($workflow -notmatch "document_status" -or
    $workflow -notmatch "pdf_printed" -or
    $workflow -notmatch "boleta_termica_stampata" -or
    $workflow -match "boleta_pdf_stampata") {
    Fail "CSV export must retain the compatibility column and use thermal-print document status"
} else { Pass "CSV document status is thermal while the legacy column remains compatible" }
if (($registerXaml + $dailyDialog + $dailyView) -match "Aggiorna vista completa") { Fail "fake/ambiguous complete-view lock copy remains" } else { Pass "ambiguous complete-view lock copy absent" }

$blockedCopy = "(?i)\bfatturato\b|\butile\b|ricavi dichiarati|fiscale ok|SII accettato"
if ($combined -match $blockedCopy) { Fail "ambiguous revenue/fiscal copy detected" } else { Pass "ambiguous revenue/fiscal copy absent" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
