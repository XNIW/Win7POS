# Win7POS UI/UX Products + Settings Smoke Closeout

Date: 2026-07-08
Branch: `feature/ui-ux-products-settings-unification`
Baseline before closeout: `353b7f7`
Release drop: `C:\Dev\Win7POS\dist\Win7POS`

## Scope

- Products UI/UX unification: toolbar icon buttons, client-side Search/Supplier/Category filters with explicit Apply/Clear, Barcode column, paging and virtualization.
- Settings UI/UX unification: compact sidebar entry, Settings hub cards, About/Support moved out of sidebar, read-only shop/about/database information.
- POS/payment visual pass: icon buttons, compact footer, payment dialog opened without confirming a sale.
- Windows 7 guardrails: no dialog positioning regressions, x86/net48 release pack regenerated.

## Real Windows Smoke

Environment:

- Data dir: `C:\POSData\UiUxProductsSmoke`
- App exe: `C:\Dev\Win7POS\src\Win7POS.Wpf\bin\x86\Release\net48\Win7POS.Wpf.exe`
- Smoke automation: Windows desktop automation against the real WPF window.
- Credentials were supplied interactively for this smoke and are not recorded here.

Result:

- Login and start-of-day completed.
- Products loaded with catalog data: `200/1000`, page `1/5`.
- Products Search typed text remained visible after fix; Apply reduced results to `2/2`.
- Products Clear reset filters.
- Products Supplier + Category typed filters remained visible; Apply reduced results to `105/105`.
- Settings hub opened from compact sidebar; sidebar contains POS, Products, Sales register, Daily close, Settings only.
- Official shop data opened as read-only rows/cards.
- POS add item flow opened Payment; payment was cancelled and no sale confirmation was clicked.

Screenshots saved locally:

- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\04-sidebar-compact-menu.png`
- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\15-products-search-applied-native-final.png`
- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\18-products-supplier-category-applied-final.png`
- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\21-settings-hub-confirmed.png`
- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\22-settings-shop-data-readonly.png`
- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\26-payment-dialog-no-confirm.png`
- `C:\Dev\Win7POS\artifacts\ui-smoke\screenshots\28-pos-compact-restored.png`

## Smoke Fix Applied

The smoke found that `ModernTextBoxStyle` accepted typed input but did not render the TextBox text in Products Search on this WPF/.NET 4.8 runtime. The style now keeps the native WPF TextBox template while preserving foreground, caret, selection, padding, borders, disabled, and read-only states. This is the conservative Windows 7-safe path and was re-smoked successfully.

## Validation

Main validation summary: `C:\Dev\Win7POS\artifacts\ui-smoke\validation-results.txt`

- PASS: `dotnet restore .\Win7POS.slnx`
- PASS: `pwsh -NoProfile -File scripts\check-dialog-standards.ps1`
- PASS: `pwsh -NoProfile -File scripts\check-architecture-boundaries.ps1`
- PASS: `pwsh -NoProfile -File scripts\check-pos-startup-win7-safe.ps1`
- PASS: `pwsh -NoProfile -File scripts\check-pos-unified-login-ux.ps1`
- PASS: `pwsh -NoProfile -File scripts\check-pos-login-logging.ps1`
- PASS: `pwsh -NoProfile -File scripts\check-win7pos-ui-ux-guard.ps1`
- MISSING: `scripts\check-win7pos-hardcoded-strings.ps1`
- PASS: `dotnet build .\Win7POS.slnx -c Release --no-restore`
- PASS: `dotnet test .\tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-build --no-restore` (`35/35`)
- PASS: `dotnet run --project .\src\Win7POS.Cli\Win7POS.Cli.csproj -c Release --no-build --no-restore -- --selftest --keepdb`
- PASS: `dotnet build .\src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
- PASS: `git diff --check`

Release validation summary: `C:\Dev\Win7POS\artifacts\ui-smoke\release-validation-results.txt`

- PASS: `pwsh -NoProfile -File scripts\win7pos\windows\build-release-x86.ps1`
- PASS: `pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS`
- PASS: `pwsh -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS`

## Warnings And Notes

- Release MSBuild still reports existing CS1998 warnings in `DailyReportViewModel.cs:460` and `PosViewModel.cs:1504`, duplicated once for the temporary WPF project and once for the real WPF project.
- `git diff --check` passed; it prints CRLF conversion warnings for edited files.
- Smoke log review found WARN `network_error` entries during online bootstrap/heartbeat fallback, but no unhandled exception/FATAL entries.
- Physical Windows 7 hardware smoke remains outstanding; this closeout used the current Windows desktop plus Win7 runtime/release validators.
