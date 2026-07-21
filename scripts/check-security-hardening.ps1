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

function Info([string]$message) {
    Write-Host "INFO: $message" -ForegroundColor Cyan
}

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Read-Many([string[]]$roots) {
    $parts = New-Object System.Collections.Generic.List[string]
    $extensions = @(".cs", ".xaml", ".config", ".ps1", ".bat", ".cmd", ".iss", ".csproj", ".props", ".targets", ".md")
    foreach ($root in $roots) {
        $path = Join-Path $repoRoot $root
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $parts.Add([System.IO.File]::ReadAllText($path))
            continue
        }

        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        Get-ChildItem -LiteralPath $path -Recurse -File |
            Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() -and $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
            ForEach-Object { $parts.Add([System.IO.File]::ReadAllText($_.FullName)) }
    }

    return ($parts -join "`n")
}

function Require([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Pass $label } else { Fail $label }
}

function Forbid([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Fail $label } else { Pass $label }
}

function Run-Check([string]$relativePath) {
    $scriptPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        Fail "$relativePath missing"
        return
    }

    $ps = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $ps) {
        $ps = (Get-Command powershell -ErrorAction Stop).Source
    }

    & $ps -NoProfile -ExecutionPolicy Bypass -File $scriptPath
    if ($LASTEXITCODE -ne 0) { Fail "$relativePath failed" } else { Pass "$relativePath passed" }
}

$runtime = Read-Many @("src/Win7POS.Wpf", "src/Win7POS.Core", "src/Win7POS.Data")
$release = Read-Many @("installer", "samples", "scripts/set-admin-web-staging-url.bat", "src/Win7POS.Wpf/Win7POS.Wpf.csproj")
$logger = Read-Text "src/Win7POS.Wpf/Infrastructure/FileLogger.cs"
$startup = Read-Text "src/Win7POS.Wpf/Infrastructure/StartupTrace.cs"
$dbInitializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$uiSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$salesBuilder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$catalogBuilder = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxPayloadBuilder.cs"
$recoveryPolicy = Read-Text "src/Win7POS.Core/Security/PosAccessRecoveryPolicy.cs"
$userRepository = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$accessRecovery = Read-Many @(
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml.cs",
    "src/Win7POS.Data/Repositories/UserRepository.cs"
)
$shippingApp = Read-Text "src/Win7POS.Wpf/App.xaml.cs"
$supplierSmokeHarness = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/SupplierExcelWpfViewModelSmoke.cs"
$supplierWorkflow = Read-Text "src/Win7POS.Wpf/Import/SupplierExcelImportWorkflowService.cs"
$posViewModel = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$denyAllPermissions = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/DenyAllPermissionService.cs"
$dbMaintenance = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs"
$migrationDetector = Read-Text "src/Win7POS.Data/Migrations/LegacySchemaDetector.cs"
$migrationTests = Read-Text "tests/Win7POS.Core.Tests/Data/MigrationRunnerTests.cs"

foreach ($redactor in @(
    @{ Label = "session/device token aliases"; Pattern = 'session\[_-\]\?token[\s\S]*device\[_-\]\?token[\s\S]*trusted\[_-\]\?device\[_-\]\?token' },
    @{ Label = "access/refresh token aliases"; Pattern = 'access\[_-\]\?token[\s\S]*refresh\[_-\]\?token' },
    @{ Label = "client secret/API key aliases"; Pattern = 'client\[_-\]\?secret[\s\S]*api\[_-\]\?key[\s\S]*apikey' },
    @{ Label = "credential aliases"; Pattern = 'pin\|password\|credential\|pwd\|db_password' },
    @{ Label = "bearer tokens"; Pattern = 'Authorization\\s\*:\\s\*Bearer' },
    @{ Label = "POS tokens"; Pattern = 'mcpos_\(device\|session\)' },
    @{ Label = "standalone secret prefixes"; Pattern = 'sb_secret' },
    @{ Label = "standalone JWTs"; Pattern = 'eyJ\[A-Za-z0-9_-' },
    @{ Label = "private-key headers"; Pattern = 'PRIVATE KEY' }
)) {
    Require "FileLogger redacts $($redactor.Label)" $logger $redactor.Pattern
    Require "StartupTrace redacts $($redactor.Label)" $startup $redactor.Pattern
}
Require "DB bootstrap logger redacts token/API aliases" $dbInitializer 'access\[_-\]\?token[\s\S]*client\[_-\]\?secret[\s\S]*api\[_-\]\?key'
Require "DB bootstrap logger redacts bearer/POS/JWT/private-key forms" $dbInitializer 'Authorization\\s\*:\\s\*Bearer[\s\S]*mcpos_[\s\S]*eyJ[\s\S]*PRIVATE KEY'
Require "FileLogger applies regex redaction" $logger "Regex\.Replace[\s\S]*\[redacted\]"
Require "StartupTrace applies regex redaction" $startup "Regex\.Replace[\s\S]*\[redacted\]"
Require "UI smoke defines executable redaction vectors" $uiSmoke 'VerifyLogRedactionTestVectors'
foreach ($vectorMarker in @('session_token', 'refresh-token', 'client_secret', 'api_key', 'Authorization: Bearer', 'sk-abcdefghijklmnopqrstuvwxyz', 'eyJheader12345', 'BEGIN PRIVATE KEY', 'TRUNCATEDPRIVATEKEYBODY987654321', 'secrets\.All')) {
    Require "UI smoke redaction vector marker: $vectorMarker" $uiSmoke $vectorMarker
}
Require "lifecycle requires redaction-vector success" $uiSmoke 'logRedactionVectorsPass[\s\S]*physicalPrinterQaPayloadPass\s*&&\s*logRedactionVectorsPass'
Forbid "direct sensitive runtime logging absent" $runtime "(?i)(Log(?:Info|Warning|Error)|StartupTrace\.Write|Console\.WriteLine)\s*\([^\r\n;]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|password|credential|payloadJson|payload_json|Authorization\s*:|Bearer)"
Forbid "literal POS token absent" $runtime "mcpos_(device|session)_[A-Za-z0-9_-]{8,}"

Require "trusted-device DPAPI CurrentUser present" $store "ProtectedData\.Protect[\s\S]*DataProtectionScope\.CurrentUser"
Require "trusted-device protected secret fields present" $store "ProtectedDeviceSecret[\s\S]*ProtectedSessionSecret"
Forbid "trusted store raw token data members absent" $store "DataMember\(Name\s*=\s*`"(deviceToken|sessionToken|trustedDeviceToken)`""

Require "sales payload redaction present" $salesBuilder "SerializeRedacted[\s\S]*DeviceToken\s*=\s*null[\s\S]*SessionToken\s*=\s*null"
Require "catalog source path redaction present" $catalogBuilder "Path\.GetFileName"
Forbid "catalog original file bytes not stored" $catalogBuilder "File\.(ReadAllBytes|ReadAllText|OpenRead)"

Require "Admin Web rejects URL credentials" $options "parsed\.UserInfo"
Require "Admin Web rejects path/query/fragment" $options "parsed\.Query[\s\S]*parsed\.Fragment[\s\S]*path != `"/`""
Require "Admin Web HTTP LAN guard present" $options "parsed\.Scheme == Uri\.UriSchemeHttp && !parsed\.IsLoopback && !AllowInsecureLanAdminWeb\(\)"
Forbid "release does not enable insecure LAN override" $release "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB\s*[:=]\s*1"
Forbid "direct Supabase/service-role absent" (Read-Many @("src", "samples", "installer")) "(?i)(SUPABASE_SERVICE_ROLE_KEY|NEXT_PUBLIC_SUPABASE|createClient\s*\(|supabase\.co|supabaseUrl|supabaseKey|\bservice_role\b)"

Require "online denial is classified before recovery" $recoveryPolicy "IsDenied\(failureKind\)[\s\S]*PosAccessNextStep\.Denied"
Require "first-run admin rechecks zero users in immediate transaction" $userRepository "BeginTransaction\(deferred:\s*false\)[\s\S]*SELECT COUNT\(\*\)[\s\S]*FirstRunAdminCreated"
Require "system-role grants are compared as an exact canonical permission set" $dbInitializer 'IsSecuritySeedSatisfied[\s\S]*actualPermissions[\s\S]*SequenceEqual[\s\S]*expectedPermissions'
Require "no-op startup fails closed when canonical system-role grants are unsafe" $dbInitializer 'SeedSecurity\(connection, transaction\);[\s\S]{0,260}!IsSecuritySeedSatisfied[\s\S]{0,260}canonical security seed'
Require "first-run credential is cleared in finally" $accessRecovery "finally[\s\S]*CredentialBox\.Clear\(\)"
Forbid "access/recovery logs contain no raw operator identifiers or credentials" $accessRecovery "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n;]{0,260}\+\s*(username|shopCode|staffCode|credential|pin)\b"

Forbid "shipping app exposes no supplier mutation smoke hook" $shippingApp 'SupplierExcelWpfViewModelSmoke|--supplier-excel-wpf-viewmodel-smoke'
Require "supplier mutation smoke is isolated in the non-shipping UI harness" $supplierSmokeHarness '--supplier-excel-wpf-viewmodel-smoke[\s\S]*RunSmokeAsync'
Require "supplier apply authorizes before initialization and again immediately before backup" $supplierWorkflow 'ApplyAsync\([\s\S]{0,300}DemandApplyAuthorization\(\);[\s\S]{0,100}DbInitializer\.EnsureCreated[\s\S]{0,900}DemandApplyAuthorization\(\);[\s\S]{0,100}CreateBackupBeforeApplyAsync'
Require "raw supplier apply reauthorizes after backup immediately before mutation" $supplierWorkflow 'CreateBackupBeforeApplyAsync\(_options\.DbPath\)\.ConfigureAwait\(true\);\s*DemandApplyAuthorization\(\);\s*\}\s*var\s+applier'
Require "preview supplier apply builds payload off-dispatcher then reauthorizes immediately before mutation" $supplierWorkflow 'BuildSupplierExcelEntry[\s\S]{0,260}ConfigureAwait\(true\);[\s\S]{0,140}DemandApplyAuthorization\(\);\s*\}\s*var\s+applier'
Require "supplier authorization smoke attributes every gate, backup and dispatcher boundary" $supplierSmokeHarness 'SequencedAuthorizer[\s\S]*AllCallsOnDispatcher[\s\S]*backupDelta\s*==\s*\(denyAfterBackup\s*\?\s*1\s*:\s*0\)'
Require "supplier authorization lease result is required by the executable smoke" $supplierSmokeHarness 'authorizationLeasePass[\s\S]{0,220}throw\s+new\s+InvalidOperationException'
Require "nullable POS permission composition becomes deny-all" $posViewModel '_permissionService\s*=\s*permissionService\s*\?\?\s*DenyAllPermissionService\.Instance'
Require "deny-all permission service cannot grant or override" $denyAllPermissions 'bool\s+Has[\s\S]{0,100}return\s+false[\s\S]*void\s+Demand[\s\S]*throw\s+new\s+InvalidOperationException[\s\S]*bool\s+CanOverride[\s\S]{0,100}return\s+false'
Require "restore permission is checked after native selection and before restore" $dbMaintenance 'dlg\.ShowDialog\(owner\)[\s\S]{0,300}_demandRestorePermission[\s\S]{0,300}RestoreDbAsync'
Require "maintenance mutations have action-time authorization" $dbMaintenance 'CompleteRestoreReviewAsync[\s\S]{0,250}_hasMaintenancePermission[\s\S]{0,500}CompleteRestoreSyncReviewAsync[\s\S]*VacuumAsync[\s\S]{0,250}_hasMaintenancePermission[\s\S]{0,500}_service\.VacuumAsync'
Require "malformed SQLite index and trigger metadata is fail-closed" $migrationDetector 'invalid-unique-key-definition[\s\S]*invalid-index-definition[\s\S]*invalid-trigger-definition'
Require "ledger metadata bypass regressions are executable" $migrationTests 'WhitespaceNamedLedgerTrigger_IsRejectedBeforePendingMigrationOrLedgerInsert[\s\S]*LedgerUniqueIndexWithMissingSql_IsRejectedBeforePendingMigrationOrLedgerInsert[\s\S]*LedgerExplicitIndexWithMissingSql_IsRejectedBeforePendingMigration'
Require "fully-ledgered schema and system-grant drift regressions are executable" $migrationTests 'MissingRequiredSystemGrant_IsReconciledOnNoOpStartup[\s\S]*UnexpectedSystemGrant_IsRejectedOnNoOpStartup[\s\S]*FullyAppliedLedgerWithGenerationTrigger_FailsBeforeSecurityReconciliation'

if ($runtime -match "(?i)FileSystemAccessRule|SetAccessControl|DirectorySecurity|FileSecurity") {
    Info "custom file ACL code present; review manually"
} else {
    Info "no custom file ACL code; trusted-device confidentiality relies on DPAPI CurrentUser"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
