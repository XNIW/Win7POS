# Win7POS Dialog Positioning Standardization Plan

## Summary

- Build dialog chrome in `SourceInitialized` instead of `Loaded` to eliminate first-frame resize/recenter jumps.
- Centralize monitor-aware work-area lookup and non-overlay clamp behavior in `MonitorHelper`.
- Normalize dialog owner resolution through `DialogOwnerHelper` on the dialog creation paths and shared helpers touched by this patch.
- Centralize shared title/footer/button styles for recurring dialog layouts.
- Enforce the standard with documentation, a PowerShell audit script, and CI checks on pull requests.

## Implementation

1. Core positioning
- `DialogShellWindow` builds chrome in `SourceInitialized`.
- Overlay dialogs use a full-owner host window and a centered card sized from the original dialog contract.
- Non-overlay dialogs install `MonitorHelper.AddWorkAreaClampHook(this)` only from `DialogShellWindow.OnSourceInitialized`.
- `WindowSizingHelper` uses `SourceInitialized` and monitor-aware `MaxWidth`/`MaxHeight` without any manual `Left`/`Top`.

2. Owner policy
- Use `DialogOwnerHelper.GetSafeOwner()` for top-level dialog creation.
- Nested dialogs opened from `UserManagementDialog` use `OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()`.
- Shared helper entrypoints `ApplyConfirmDialog.ShowConfirm`, `ModernMessageDialog.Show`, and `ImportDataDialog.ShowDialog` all route through `DialogOwnerHelper`.
- `App.xaml.cs` keeps the intentional fatal-exception `ModernMessageDialog.Show(null, ...)` exception.

3. Layout normalization
- Shared resources in `ModernStyles.xaml`:
  - `DialogTitleStyle`
  - `DialogFooterMargin`
  - `DialogActionButtonStyle`
  - `DialogCancelButtonStyle`
- Normalize the dialog files covered by this patch to those shared footer/title resources.
- Keep keypad/touch button exceptions unchanged in `ChangeQuantityDialog` and `BoletaNumberDialog`.

4. Small-screen policy
- Categories A/B must fit `1024x600`.
- All touched dialogs must fit `1024x768`.
- Category D dialogs (`DailyReportDialog`, `SalesRegisterDialog`) are best-effort at `1024x600` and must be documented as such.

## Validation

- `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
- `pwsh -File scripts/check-dialog-standards.ps1`
- Manual smoke tests:
  - no first-frame jump
  - correct owner centering
  - correct work-area clamp
  - nested dialogs centered on parent
  - second-monitor scenarios on real Windows hardware before merge

## Assumptions

- Windows remains the source of truth for WPF runtime validation.
- `Dispatcher.BeginInvoke` is allowed for focus/data UX only, never for positioning.
- `docs/plans` and `.cursor/rules` are created by this patch because they may not exist on the target branch.
