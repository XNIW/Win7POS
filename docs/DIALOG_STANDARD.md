# Dialog WPF Standard

## Scopo

Questo standard evita salti visivi all'apertura dei dialog, centralizza ownership e sizing, e definisce un layout coerente per i dialog WPF di Win7POS.

## Invarianti obbligatori

- `DialogShellWindow.BuildChrome()` deve avvenire in `SourceInitialized`, mai in `Loaded`.
- Nessun `Loaded` può fare sizing o positioning della finestra.
- Nessun `Dispatcher.BeginInvoke` può essere usato per riposizionare finestre.
- Nessun `Left`/`Top` custom nei singoli dialog.
- `WindowStartupLocation="Manual"` delega la posizione alla policy comune di `DialogShellWindow`/`MonitorHelper`; vale anche per derivati costruiti soltanto in C#.
- `DialogOwnerHelper.GetSafeOwner()` è il fallback standard per i dialog top-level.
- Per nested dialogs: `OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()`.
- `MonitorHelper.AddWorkAreaClampHook` è ammesso solo in `DialogShellWindow.OnSourceInitialized`, per overlay e finestre normali.
- Footer buttons standard:
  - `DialogActionButtonStyle`
  - `DialogCancelButtonStyle`
- Footer spacing standard:
  - `Margin="{StaticResource DialogFooterMargin}"`

## Centro e contenimento — requisito ASUS019 aggiornato

L'owner autentico determina modalità, focus, ciclo di vita e monitor pertinente;
non determina il centro geometrico. Ogni nuova superficie operatore è centrata
nella **work area** di quel monitor, esclusa la taskbar. Non usare centro del
parent, monitor primario o mouse quando esiste un owner identificabile.

In SourceInitialized la base costruisce il chrome e limita massimi e minimi
alla work area, con margine48DIP complessivo. Gli overlay coprono tale area e
allineano la card visibile al centro; la card, non l'HWND trasparente, è la
superficie da misurare. Le finestre normali misurano il chrome sotto il cap e
risolvono SizeToContent prima della prima visualizzazione; dimensioni esplicite
restano preferenze entro il cap. La dimensione iniziale viene fissata in Manual,
il contenuto variabile scorre internamente e il footer resta fuori dal corpo.

Il solo hook comune centra la posizione nativa iniziale sulla dimensione finale
e contiene tutti i bordi. WINDOWPOS con NOMOVE/NOSIZE usa posizione/dimensione
attuali per i campi non definiti. Dopo l'apertura non ricentra trascinamenti,
focus o aggiornamenti del contenuto: contiene soltanto quando necessario.
Cambio di work area/monitor e DPI aggiornano i vincoli; niente timer o correzioni
visibili tardive. Coordinate native in pixel e layout WPF in DIP sono convertiti
una sola volta con il transform della finestra. Sono supportate origini negative.

`CapMaxHeightToOwner` mantiene il nome per i call site esistenti, ma il limite
comune è la work area; un prompt piccolo non deve comprimere il figlio.
Titolo e footer restano raggiungibili al200%; non ridurre font o scala come fix.
Importatore: mantenere Grid/corpo scorrevole/stato/footer separati. Conferme e
messaggi lunghi hanno scroll sul solo messaggio, con pulsanti esterni.

MainWindow massimizzata, CustomerDisplayWindow e MonitorIdentifyWindow seguono
i contratti dei monitor assegnati e non ereditano questa policy. Menu, tooltip e
dropdown restano ancorati; i picker nativi ricevono il vero owner, senza hook globali.

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
- Ha `WindowStartupLocation="Manual"` e usa la policy comune di work area.
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
  - card/finestra centrata sulla work area dalla prima visualizzazione, entro2pixel di arrotondamento
  - tutti i bordi e i pulsanti footer misurati entro la work area e la superficie visibile
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
- policy Manual su tutti i root `DialogShellWindow`, anche con prefissi XAML diversi; nessun override CenterOwner/CenterScreen nei derivati C#
- `AddWorkAreaClampHook` solo nella base class
- owner fallback coerenti nei file target
- coerenza root XAML / code-behind base type

## Smoke Test Matrix

- owner massimizzato
- owner non massimizzato
- bordo sinistro/destro
- taskbar bottom/left/top
- DPI 100% / 125% / 200% effettivi
- nested dialog centrato sulla stessa work area, senza deriva cumulativa e con owner autentico
- secondo monitor e spostamento monitor su hardware reale Windows prima del merge

Il harness esistente esegue OPERATOR_DIALOG_GEOMETRY come processo separato
entro60s, con receipt propria: catena geometrica esplicita login loopback → capability legacy → import
→ cancel/confirm → messaggio/ricevuta → parent, minimi/cap, prima posizione HWND,
card, footer, lingue, traversal, owner spostato/massimizzato, resize-only e
rettangoli deterministici con taskbar/origini negative. Expected geometrico
acquisito da GetMonitorInfo nel test, indipendente dalla funzione di produzione.
Il worker storico conserva budget60s e52assert; questa prova non lo gonfia.
AUTHENTICATED_SHELL_UX verifica separatamente la MainWindow reale, shell vuota/pronta,
report/import volontario, chiusure e ritorno senza logout. Nessun workspace
obbligatorio al login. Un PASS sintetico/Windows11 non sostituisce Win7 fisico o multi-monitor reale.
