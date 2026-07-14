# UI/UX Products POS Settings Next Pass

Date: 2026-07-08
Branch: feature/ui-ux-products-settings-unification
Starting HEAD: 41d0ea4 fix: correct Win7POS UI visual regressions
Final HEAD: commit hash is produced by the commit containing this report; recorded in the final Codex response.

## Outcome

Previous Products/Settings visual-regression task is closed. This pass completes the next UI/UX polish on Products, POS and Settings, rebuilds the x86 release pack, and validates the current `dist/Win7POS` package with a real WPF smoke run.

No real payment was confirmed during smoke validation. The payment view was opened for visual regression coverage, then cancelled.

## Implemented

- Products catalog stats header: Products, Categories, Suppliers and Stock units are surfaced as compact header chips.
- POS header title: shop title remains left aligned like the previous Win7POS header, with `Win7POS` retained in the window title.
- POS footer status: persistent startup/initialized text was removed from the footer and replaced by transient toast status for actionable messages.
- Operator switch: operator list was replaced with manual staff code plus PIN entry, matching POS device workflow.
- Settings hub: converted to a fixed 3x2 card grid without body scrolling.
- Language settings: moved language selection into its own dialog from the Settings card.
- Language selected text fix: `SupportedLanguageOption.ToString()` now returns the display name, so the ComboBox shows values such as `English` instead of the CLR type name.
- ComboBox dropdown arrow: uses the shared Material Symbols Rounded `IconExpandMore` geometry with a smaller 14x14 glyph, matching the other modern button icons.
- Guard scripts updated to lock the new UI behavior and prevent regression.

## Main Files

- `src/Win7POS.Data/Repositories/ProductRepository.cs`
- `src/Win7POS.Wpf/Products/ProductsWorkflowService.cs`
- `src/Win7POS.Wpf/Products/ProductsViewModel.cs`
- `src/Win7POS.Wpf/Products/ProductsView.xaml`
- `src/Win7POS.Wpf/Pos/PosViewModel.cs`
- `src/Win7POS.Wpf/Pos/PosView.xaml`
- `src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml`
- `src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml.cs`
- `src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml`
- `src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml.cs`
- `src/Win7POS.Wpf/Pos/Dialogs/LanguageSettingsDialog.xaml`
- `src/Win7POS.Wpf/Pos/Dialogs/LanguageSettingsDialog.xaml.cs`
- `src/Win7POS.Wpf/ModernStyles.xaml`
- `src/Win7POS.Wpf/Icons/MaterialSymbols.xaml`
- `src/Win7POS.Wpf/MainWindow.xaml`
- `src/Win7POS.Wpf/MainWindow.xaml.cs`
- `src/Win7POS.Wpf/Localization/PosLocalization.cs`
- `src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs`
- `scripts/check-pos-unified-login-ux.ps1`
- `scripts/check-win7pos-ui-ux-guard.ps1`

## Screenshots

Stored under `artifacts/ui-next-pass/screenshots/`:

- `01-phase1-review-products.png`
- `02-products-stats-header.png`
- `03-products-stats-1024x768.png`
- `04-products-combobox-modern-dropdown.png`
- `05-operator-switch-manual-staff-code.png`
- `06-settings-hub-no-scroll-1366.png`
- `07-settings-hub-no-clip-1024.png`
- `08-language-dialog-or-language-card.png`
- `09-pos-header-shop-name.png`
- `10-pos-footer-no-persistent-initialized-log.png`
- `11-pos-status-toast-warning-or-info.png`
- `12-payment-no-regression.png`
- `13-sidebar-no-regression.png`

Latest smoke details:

- `07-settings-hub-no-clip-1024.png` was regenerated from the real `dist/Win7POS` package with only the Settings hub open.
- `08-language-dialog-or-language-card.png` was regenerated from the real package and verifies the selected language displays as `English` with the smaller shared icon arrow.
- `11-pos-status-toast-warning-or-info.png` captures the non-blocking `No receipt to print.` toast.
- `12-payment-no-regression.png` captures the payment view open before confirmation; the flow was cancelled afterward.

## Validation

Passed:

- `C:\Dev\dotnet10\dotnet.exe restore .\Win7POS.slnx`
- `pwsh -NoProfile -File scripts\check-dialog-standards.ps1`
- `pwsh -NoProfile -File scripts\check-architecture-boundaries.ps1`
- `pwsh -NoProfile -File scripts\check-pos-startup-win7-safe.ps1`
- `pwsh -NoProfile -File scripts\check-pos-unified-login-ux.ps1`
- `pwsh -NoProfile -File scripts\check-pos-login-logging.ps1`
- `pwsh -NoProfile -File scripts\check-win7pos-ui-ux-guard.ps1`
- `pwsh -NoProfile -File scripts\check-pos-shop-data-readonly.ps1`
- `C:\Dev\dotnet10\dotnet.exe build .\Win7POS.slnx -c Release --no-restore`
- `C:\Dev\dotnet10\dotnet.exe test .\tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-restore` (35 passed)
- `C:\Dev\dotnet10\dotnet.exe run --project .\src\Win7POS.Cli\Win7POS.Cli.csproj -c Release --no-restore -- --selftest --keepdb`
- `C:\Dev\dotnet10\dotnet.exe build .\src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore`
- `pwsh -NoProfile -File scripts\win7pos\windows\build-release-x86.ps1`
- `pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS`
- `pwsh -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS`
- `git diff --check` (only LF to CRLF conversion warnings)

Real WPF smoke:

- Launched `C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe`.
- Logged in, completed start-of-day, opened Products, Settings, Language and POS payment flow.
- Verified title left alignment, Settings hub fit, Language selected display, ComboBox arrow, operator switch manual staff code, POS toast and payment view.
- Payment was not confirmed.

## Notes

- Physical Windows 7 validation was not available in this session.
- Local Application Control blocks launching the rebuilt `src\Win7POS.Wpf\bin\x86\Release\net48\Win7POS.Wpf.exe` on this machine, but `dist\Win7POS\Win7POS.Wpf.exe` launches and was used for smoke validation.
- The release build still reports existing CS1998 warnings in unrelated async methods; no new warning family was introduced by this patch.
