# Dialog WPF Standard

## Scopo

Questo standard evita salti visivi all'apertura dei dialog, centralizza ownership e sizing, e definisce un layout coerente per i dialog WPF di Win7POS.

## Invarianti obbligatori

- `DialogShellWindow.BuildChrome()` deve avvenire in `SourceInitialized`, mai in `Loaded`.
- Nessun `Loaded` può fare sizing o positioning della finestra.
- Nessun `Dispatcher.BeginInvoke` può essere usato per riposizionare finestre.
- Nessun `Left`/`Top` custom nei singoli dialog.
- `WindowStartupLocation="CenterOwner"` è obbligatorio per ogni dialog basato su `DialogShellWindow`.
- `DialogOwnerHelper.GetSafeOwner()` è il fallback standard per i dialog top-level.
- Per nested dialogs: `OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()`.
- `MonitorHelper.AddWorkAreaClampHook` è ammesso solo in `DialogShellWindow.OnSourceInitialized` e solo quando `!UseModalOverlay`.
- Footer buttons standard:
  - `DialogActionButtonStyle`
  - `DialogCancelButtonStyle`
- Footer spacing standard:
  - `Margin="{StaticResource DialogFooterMargin}"`

## Tassonomia dialog

| Cat | Purpose | Overlay | SizeToContent | ResizeMode | Helper obbligatori | Size band | Reference |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A | Small confirm/prompt modal | True | WidthAndHeight | NoResize | base overlay only | ~320–460w, 240–320h | `ApplyConfirmDialog` |
| B | Standard form/action overlay | True | Manual o WidthAndHeight | NoResize o CanResize | base overlay clamp, no manual centering | ~400–860w, 260–680h | `ImportDataDialog`, `PrinterSettingsDialog` |
| C | Non-overlay dialog | False | Manual o WidthAndHeight | CanResize o NoResize | `ApplyAdaptiveDialogSizing` se variabile, `CapMaxHeightToOwner`, base clamp hook | ~560–820w+, 420–620h+ | `DbMaintenanceDialog` |
| D | Large report/workbench | Mixed, spesso Manual | Manual | CanResize preferred | base clamp, `CapMaxHeightToOwner`, no manual centering | ~900–1180w, 640–760h | `DailyReportDialog`, `SalesRegisterDialog` |

## Standard XAML

- Titolo principale:
  - `Style="{StaticResource DialogTitleStyle}"`
- Footer container:
  - `Margin="{StaticResource DialogFooterMargin}"`
- Bottone primary footer:
  - `Style="{StaticResource DialogActionButtonStyle}"`
- Bottone cancel/secondary footer:
  - `Style="{StaticResource DialogCancelButtonStyle}"`

## Reference Dialogs

- Overlay golden sample: `src/Win7POS.Wpf/Import/ApplyConfirmDialog.xaml[.cs]`
- Non-overlay golden sample: `src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml[.cs]`

## Definition Of Done

Ogni nuovo dialog deve soddisfare questa checklist:

- Deriva da `DialogShellWindow`.
- Ha `WindowStartupLocation="CenterOwner"`.
- Usa `DialogOwnerHelper.GetSafeOwner()` o `OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()`.
- Non usa `Loaded` per positioning/sizing.
- Non scrive `Left`/`Top` custom.
- Usa `DialogTitleStyle` per il titolo principale.
- Usa `DialogActionButtonStyle` / `DialogCancelButtonStyle` nel footer.
- Usa `DialogFooterMargin` nel footer.
- Passa `pwsh -File scripts/check-dialog-standards.ps1`.
- Build `Release x86` senza regressioni.
- Smoke test minimo su Windows:
  - nessun salto visivo
  - centrato sull'owner dalla prima frame
  - focus iniziale corretto se previsto
  - `Escape` chiude se previsto

## Scope della policy owner

Questa patch copre:

- dialog creation paths toccati da questo standard
- shared helper entrypoints `ApplyConfirmDialog`, `ModernMessageDialog`, `ImportDataDialog`
- nested flow di `UserManagementDialog`

Popup profondi da altri ViewModel non toccati da questa patch restano follow-up work.

## Eccezioni documentate

- `App.xaml.cs` fatal `OnUnhandledException`: `ModernMessageDialog.Show(null, ...)` è intenzionale.
- `ChangeQuantityDialog` e `BoletaNumberDialog`: keypad touch a 72 px, non normalizzare a 42 px.
- Category D:
  - `1024x768` richiesto
  - `1024x600` best-effort/documentato

## Enforcement

- macOS/Linux:
  - `pwsh -File scripts/check-dialog-standards.ps1`
- Windows PowerShell:
  - `powershell -File scripts/check-dialog-standards.ps1`
- Windows pwsh:
  - `pwsh -File scripts/check-dialog-standards.ps1`

Lo script controlla almeno:

- niente `window.Left` / `window.Top` in `WindowSizingHelper`
- niente centering custom residuo
- `CenterOwner` su tutti i dialog `DialogShellWindow`
- `AddWorkAreaClampHook` solo nella base class
- owner fallback coerenti nei file target
- coerenza root XAML / code-behind base type

## Smoke Test Matrix

- owner massimizzato
- owner non massimizzato
- bordo sinistro/destro
- taskbar bottom/left/top
- DPI 100% / 125%
- nested dialog centrato sul parent
- secondo monitor e spostamento monitor su hardware reale Windows prima del merge
