# UI/UX Products + Settings Visual Fix

Date: 2026-07-08
Branch: `feature/ui-ux-products-settings-unification`

## Scope

- Replaced the temporary WPF icon geometries with selected Google Material Symbols Rounded vector paths.
- Kept the implementation Win7-safe: no icon fonts, no PNG icon assets, no new NuGet/packages.
- Fixed low-contrast disabled buttons in Products/POS footer/payment paths.
- Fixed Settings Hub clipping by using a scrollable card layout and a full-width language row.
- Revalidated the release drop at `C:\Dev\Win7POS\dist\Win7POS`.

## Icon Source

- Source: Google Material Symbols Rounded, official repository `https://github.com/google/material-design-icons`.
- License noted in source dictionary: Apache-2.0.
- Implementation: selected 24px SVG path data converted into WPF `PathGeometry` resources in `src/Win7POS.Wpf/Icons/MaterialSymbols.xaml`.
- Retrieval note: a shallow clone of the full repository was attempted but did not complete in the local session; the patch used direct official raw GitHub SVG downloads for the selected icons instead.
- No fallback icon fonts, text glyphs, PNG icons, or package dependencies were added.

## Smoke Data

- Smoke executable: `C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe`.
- Smoke data root: `C:\POSData\UiUxVisualFixSmoke` via `WIN7POS_DATA_DIR`.
- No payment/sale was confirmed during visual QA.

## Screenshots

- `artifacts/ui-visual-fix/screenshots/02-pos-buttons-fixed.png`
- `artifacts/ui-visual-fix/screenshots/03-products-default.png`
- `artifacts/ui-visual-fix/screenshots/04-products-disabled-toolbar-readable.png`
- `artifacts/ui-visual-fix/screenshots/05-products-search-text-visible.png`
- `artifacts/ui-visual-fix/screenshots/06-settings-hub-fixed.png`
- `artifacts/ui-visual-fix/screenshots/07-settings-hub-1024x768.png`
- `artifacts/ui-visual-fix/screenshots/08-shop-data-readonly.png`
- `artifacts/ui-visual-fix/screenshots/09-payment-buttons-fixed.png`
- `artifacts/ui-visual-fix/screenshots/10-sidebar-compact.png`
- `artifacts/ui-visual-fix/screenshots/11-pos-1024x768-icons.png`

The 1024x768 smoke used an outer Win32 window size of 1024x768; Computer Use captured a 1013x763 client region because of the native window frame.

## Validation

- `pwsh -NoProfile -File scripts/check-dialog-standards.ps1`: PASS.
- `pwsh -NoProfile -File scripts/check-win7pos-ui-ux-guard.ps1`: PASS.
- `pwsh -NoProfile -File scripts/check-pos-shop-data-readonly.ps1`: PASS.
- `C:\Dev\dotnet10\dotnet.exe restore Win7POS.slnx`: PASS.
- `C:\Dev\dotnet10\dotnet.exe test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release`: PASS, 35/35.
- `C:\Dev\dotnet10\dotnet.exe build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`: PASS, 0 warnings.
- `pwsh -NoProfile -File scripts\win7pos\windows\build-release-x86.ps1`: PASS, release copied to `dist\Win7POS`; MSBuild reported existing CS1998 warnings in `DailyReportViewModel.cs` and `PosViewModel.cs`.
- `pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS`: PASS.
- `pwsh -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS`: PASS.

## Environment Notes

- A Windows Update `PickerHost` prompt appeared over the first visual capture and blocked clean screenshots. It was dismissed as an environmental blocker before the smoke continued.
- The app was stopped after the smoke run.
