# Win7POS final delivery closure

Date: 2026-07-05
Branch: `fix/win7pos-hardening-phase3`

## Delivery status

| Area | Status | Evidence |
| --- | --- | --- |
| Local release drop | `LOCAL_RELEASE_PACK_PASS` | `dist\Win7POS`; `check-release-pack-completeness` and `check-win7-runtime-release-validation` both `ALL PASS`. |
| Local release zip | `LOCAL_RELEASE_ZIP_PASS` | `dist\Win7POS_20260705_200950.zip`; SHA256 `889A3FB5C5503803BE542689C4C9EC47E30C2F5D7C751271511F8691FCA3D90A`. |
| Local Inno installer | `LOCAL_INSTALLER_PASS` | `installer\output\Win7POS-Setup.exe`; size `5902040`; SHA256 `E6BE99A065CD92D0F49A1E4CBA2608647D078AF0DAB480D9002B1A688474851B`. |
| VC++ x86 runtime policy | `VCREDIST_X86_REQUIRED` | Installer blocks if Microsoft Visual C++ Runtime x86 is missing; Win7 prereq smoke also fails if missing. |
| ASUS Windows 11 builder smoke | `ASUS_WIN11_STARTUP_SMOKE_PASS` | Machine `ASUSTeK COMPUTER INC. ASUS Zenbook 14 UX3405CA_UX3405CA`, Windows `10.0.26200`; prereq script `ALL PASS`; `Win7POS.Wpf.exe` stayed alive for startup smoke and created `C:\Temp\Win7POS-final-smoke-data-app\logs\app.log`. |
| GitHub release artifact | `GITHUB_RELEASE_ARTIFACT_PENDING_PR_TRIGGER` | Existing PR commit had only CI run `28747493937` and no artifacts; `release-pack.yml` now runs on `pull_request` to produce PR artifacts on next push. |
| Windows 7 physical smoke | `WIN7_HARDWARE_NOT_AVAILABLE` | No Windows 7 hardware/VM attached to this Codex session. Use `docs\WIN7_PRODUCTION_SMOKE_CHECKLIST.md` on the target. |
| Admin Web staging E2E | `E2E_STAGING_PENDING` | Staging public smoke passes, but staging Supabase URL/project ref/service role/CLI are not available in this environment. |
| Win7POS validation suite | `WIN7POS_VALIDATION_PASS` | Restore/build/test/selftests/check scripts completed; full `scripts\check-*.ps1` sweep ended `ALL CHECK SCRIPTS PASS`. |
| Admin Web validation suite | `ADMIN_WEB_VALIDATION_PASS` | Security scan, foundation tests, typecheck, production build and ESLint completed against this Win7POS checkout. |

## Commands executed

```powershell
winget list --id JRSoftware.InnoSetup -e
pwsh -NoLogo -NoProfile -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\Win7POS.iss
pwsh -NoLogo -NoProfile -File scripts\win7-smoke\check-win7-prereqs.ps1 -AppDir dist\Win7POS -DataDir C:\Temp\Win7POS-final-smoke-data -AdminWebBaseUrl https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev
pwsh -NoLogo -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS -WriteManifests
pwsh -NoLogo -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS
pwsh -NoLogo -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS_20260705_200950.zip
C:\Dev\dotnet10\dotnet.exe build src\Win7POS.Cli\Win7POS.Cli.csproj -c Release
C:\Dev\dotnet10\dotnet.exe run --project src\Win7POS.Cli\Win7POS.Cli.csproj -c Release --no-build -- --catalog-import-sync-http-harness
$env:DOTNET_EXE = "C:\Dev\dotnet10\dotnet.exe"; Get-ChildItem scripts -Filter check-*.ps1 | ForEach-Object { pwsh -NoLogo -NoProfile -File $_.FullName }
$env:WIN7POS_DATA_DIR = "C:\Temp\Win7POS-final-smoke-data-app"; Start-Process -FilePath dist\Win7POS\Win7POS.Wpf.exe -WorkingDirectory dist\Win7POS -PassThru
```

Admin Web validation commands:

```powershell
$env:PATH = "C:\Users\xniw9\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin;C:\Users\xniw9\.cache\codex-runtimes\codex-primary-runtime\dependencies\bin;" + $env:PATH
$env:WIN7POS_REPO_PATH = "C:\Dev\Win7POS-full-audit"
node scripts\security-checks.mjs
node scripts\run-foundation-tests.mjs
pnpm exec tsc --noEmit --pretty false
pnpm build
pnpm exec eslint .
node scripts\staging-readiness-check.mjs --public-only
node scripts\db\staging-status.mjs
node scripts\check-supabase-tooling.mjs --linked
```

## Notes

- `installer/output/` is ignored to prevent generated `.exe` artifacts from entering git.
- The catalog import HTTP harness now supports `--session-json` for real staging sessions; without it, it keeps the existing local fake-server matrix.
- The generated artifacts are local evidence, not tracked source. GitHub PR artifacts will be produced by the next `release-pack.yml` pull request run.
