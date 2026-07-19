$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $path)) {
        Fail "missing file: $relativePath"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Require-Match([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) {
        Pass $label
    }
    else {
        Fail "$label missing"
    }
}

function Forbid-Match([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) {
        Fail $label
    }
    else {
        Pass $label
    }
}

$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$operatorSwitch = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$logger = Read-Text "src/Win7POS.Wpf/Infrastructure/FileLogger.cs"
$uiSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"

Require-Match "POS access category" $dialog '"pos\.access"'
Require-Match "POS access attempt id field" $dialog 'attemptId='
Require-Match "POS access attempt id generator" $dialog 'CreateAccessAttemptId'
Require-Match "POS access duration" $dialog 'durationMs='
Require-Match "POS access start stage" $dialog '"start"'
Require-Match "POS access end stage" $dialog '"end"'
Require-Match "POS access offline fallback stage" $dialog '"offline_fallback"'
Require-Match "POS access catalog sale-safe stage" $dialog '"catalog_sale_safe'
Require-Match "POS access local login result" $dialog '"local_login_result"'
Require-Match "POS access bootstrap result" $dialog '"online_bootstrap_result"'
Require-Match "POS access catalog retry category" $dialog '"pos\.access\.catalog_retry"'
Require-Match "POS bootstrap logs safe remote role key" $bootstrap 'role_key="\s*\+\s*SafeAuditValue\(response\.Staff\.RoleKey\)'
Require-Match "Start-of-day blocked category" $mainWindow 'category=start_of_day result=blocked reason='

Require-Match "Operator switch category" $operatorSwitch 'category=operator\.switch'
Require-Match "Operator switch attempt id field" $operatorSwitch 'attemptId='
Require-Match "Operator switch success result" $operatorSwitch '"success"'
Require-Match "Operator switch failed result" $operatorSwitch 'result=failed'
Require-Match "Operator switch cancelled result" $operatorSwitch '"cancelled"'
Require-Match "Permission denied category" $mainWindow 'category=permission\.denied'
Require-Match "Permission denied current role field" $mainWindow 'currentRole='
Require-Match "Permission denied missing permission field" $mainWindow 'missingPermission='
Require-Match "Permission denied action field" $mainWindow 'action='

Require-Match "FileLogger redacts credential keywords" $logger 'pin\|password\|credential'
Require-Match "FileLogger redacts camel/snake token aliases" $logger 'trusted\[_-\]\?device\[_-\]\?token[\s\S]*access\[_-\]\?token[\s\S]*refresh\[_-\]\?token'
Require-Match "FileLogger redacts client secret/API key aliases" $logger 'client\[_-\]\?secret[\s\S]*api\[_-\]\?key'
Require-Match "FileLogger redacts bearer auth" $logger 'Authorization\\s\*:\\s\*Bearer'
Require-Match "UI smoke executes secret-body redaction vectors" $uiSmoke 'VerifyLogRedactionTestVectors'
foreach ($vectorMarker in @('CorrectHorseBatteryStaple', 'PRIVATEKEYBODY123456789', 'TRUNCATEDPRIVATEKEYBODY987654321', 'secrets\.All')) {
    Require-Match "UI smoke secret-body marker: $vectorMarker" $uiSmoke $vectorMarker
}

Forbid-Match "no CredentialBox.Password inside log calls" $dialog 'Log(?:Access|CatalogRetry|Info|Warning|Error)[\s\S]{0,160}CredentialBox\.Password'
Forbid-Match "no credential variable logged by key" $dialog '(?i)credential\s*=\s*"\s*\+\s*credential|credential="\s*\+\s*credential|credential=\s*"\s*\+\s*credential'
Forbid-Match "no pin variable logged by key" $dialog '(?i)pin\s*=\s*"\s*\+\s*pin|pin="\s*\+\s*pin|pin=\s*"\s*\+\s*pin'
Forbid-Match "no password variable logged by key" $dialog '(?i)password\s*=\s*"\s*\+\s*password|password="\s*\+\s*password|password=\s*"\s*\+\s*password'
Forbid-Match "no token variable logged by key" $dialog '(?i)token\s*=\s*"\s*\+\s*token|token="\s*\+\s*token|token=\s*"\s*\+\s*token'
Forbid-Match "operator switch does not log PIN box" $operatorSwitch 'Log(?:\w*)?[\s\S]{0,160}PinBox\.Password'
Forbid-Match "operator switch does not log pin variable by key" $operatorSwitch '(?i)pin\s*=\s*"\s*\+\s*pin|pin="\s*\+\s*pin|pin=\s*"\s*\+\s*pin'
Forbid-Match "permission denied log has no credential keys" $mainWindow 'category=permission\.denied[^\r\n]*(?i:pin|password|credential|token)'

if ($fail) {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}

Write-Host "`nRESULT: PASS" -ForegroundColor Green
exit 0
