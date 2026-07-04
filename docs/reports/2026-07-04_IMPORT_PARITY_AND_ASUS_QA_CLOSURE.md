# Import parity and ASUS QA closure - 2026-07-04

## Stato

- Base iniziale: `bd95d80112af482e3c0063936e2578c49ad7328d` (`TASK-092 fix supplier import dialog owner on database maintenance`).
- `HEAD` e `origin/main` erano allineati prima della patch.
- `docs/MASTER-PLAN.md` e `docs/TASKS` non sono presenti in questo repo; la chiusura e' documentata in `docs/reports/` come da convenzione esistente.
- Stato software Mac: `REVIEW_READY`.
- Stato finale richiesto: `SOFTWARE_PATCH_PUSHED_ASUS_RERUN_REQUIRED`.

## Commit chain

| Task | Commit | Stato | Sintesi |
|------|--------|-------|---------|
| TASK-086 | `837a0d0` | DONE | Prova il workflow operativo Supplier Excel e aggiunge selftest/contratto. |
| TASK-087 | `ea5d20b` | DONE | Completa correct-or-skip: mapping, correzioni righe e skip operatore. |
| TASK-088 | `c21a7b5` | DONE | Aggiunge Step 4 Sync DB review prima dell'apply. |
| TASK-089 | `4986f89` | DONE | Chiude parity import sync review e stale-preview checks. |
| TASK-090 | `53f0700` | DONE | Blocca parity algoritmo canonico e preserva Android come oracle. |
| TASK-091 | ASUS QA clone/report | REVIEW | Validazione ASUS su `bd95d80`: ha confermato build/file picker/Product flow e ha trovato il layout fail Database/Maintenance. |
| TASK-092 | `bd95d80` | DONE | Corregge owner file picker nel nested flow Database/Maintenance. ASUS ha confermato file picker front/enabled/selectable. |
| TASK-093 | questo commit | REVIEW | Patch layout/geometry supplier import: footer fisso, contenuto scrollabile, overlay nested centrato su work area quando l'owner e' troppo piccolo. Richiede rerun ASUS. |

## Root cause TASK-093

Il supplier wizard e' un overlay WPF (`UseModalOverlay=True`) alto 720. Quando veniva aperto da Database/Maintenance, l'owner era il dialog nested `DbMaintenanceDialog`, alto circa 520. `DialogShellWindow` dimensionava la card usando la work area del monitor, ma il contenitore overlay veniva applicato ai bounds dell'owner nested: la card poteva quindi essere piu' alta del contenitore e il footer finiva fuori viewport.

In piu', il contenuto dei passi non aveva uno scroll dedicato nella riga centrale del wizard: su viewport ridotto il contenuto poteva competere con il footer invece di scorrere.

## Fix TASK-093

- `src/Win7POS.Wpf/Chrome/DialogShellWindow.cs`
  - `ApplyOverlayPosition` ora riceve la card effettiva.
  - Se l'owner e' parzialmente fuori work area o troppo piccolo per contenere la card, l'overlay usa la work area del monitor dell'owner.
  - Il clamp resta nella base class, senza `Loaded`, senza `Dispatcher.BeginInvoke`, senza positioning custom nei singoli dialog.
- `src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml`
  - aggiunti `MaxWidth="1120"` e `MaxHeight="720"`;
  - riga centrale dei passi dentro `ScrollViewer`;
  - footer in riga root separata con `DialogFooterMargin`, fuori dallo scroll.
- `scripts/check-supplier-excel-wizard.ps1`
  - verifica footer fuori dallo `ScrollViewer`;
  - verifica bottoni footer `Indietro`, `Analizza`, `Avanti`, `Continua a Sync DB`, `Conferma e applica`, `Annulla`;
  - verifica `MaxWidth`/`MaxHeight` e fallback overlay work-area.
- `src/Win7POS.Cli/Program.cs`
  - selftest UI allineato alle nuove guardie statiche.

## Evidenza locale Mac

| Comando | Risultato |
|---------|-----------|
| `git diff --check` | PASS |
| `dotnet restore src/Win7POS.Core/Win7POS.Core.csproj` | PASS, progetti aggiornati |
| `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` | PASS, 0 warning, 0 errori |
| `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` | PASS, 0 warning, 0 errori |
| `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86` | PASS, WPF `net48` x86, 0 warning, 0 errori |
| `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | PASS, WPF `net48` x86, 0 warning, 0 errori |
| `dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --supplier-excel-selftest` | PASS, `SUPPLIER EXCEL SELFTEST PASS` |
| `dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --supplier-excel-ui-selftest` | PASS, `SUPPLIER EXCEL UI SELFTEST PASS` |
| `dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --supplier-excel-apply-selftest` | PASS, rollback/backup/history proof `ok: true` |
| `pwsh -NoProfile -File scripts/check-supplier-excel-wizard.ps1` | PASS, `SUPPLIER EXCEL WIZARD CHECK PASS` |
| `pwsh -NoProfile -File scripts/check-dialog-standards.ps1` | PASS, `=== RESULT: ALL PASS ===` |

## ASUS evidence summary

- ASUS QA clone: `C:\Dev\Win7POS_TASK090_QA`.
- HEAD testato prima della patch: `bd95d80112af482e3c0063936e2578c49ad7328d`.
- Confermati PASS su ASUS:
  - WPF Release x86 build;
  - PowerShell checks;
  - CLI supplier UI selftest;
  - Products/Catalog supplier import, file picker, `.xlsx` Step 1, Analizza, Step 2 mapping;
  - Database/Maintenance file picker owner fix da TASK-092;
  - Database/Maintenance `.xls` ritorna a Step 1.
- FAIL confermato prima di TASK-093:
  - Database/Maintenance supplier import Step 1 con file selezionato aveva footer clipping;
  - click manuale su `Analizza` non riusciva;
  - Step 2 mapping da Database/Maintenance non raggiungibile.
- Screenshot ASUS indicato dal task: `C:\Temp\Win7POS-ImportQA\screens\task092-manual\db_manual_step1_selected_layout_fail.png`.

## Parity note

Android resta oracle per il comportamento Supplier Excel. La catena TASK-086..TASK-090 non e' stata modificata da TASK-093; questa patch non cambia parser, analyzer, fixture canoniche, apply SQLite, price history o preview semantics. Resta documentata la correzione gia' provata per formati Excel con leading zero.

## Rischi residui

| Area | Stato |
|------|-------|
| ASUS rerun post-TASK-093 | REVIEW: richiesto per confermare layout reale su Windows 11 ASUS. |
| Windows 7 SP1 fisico | EXTERNAL_PENDING: hardware/VM fisica non disponibile su Mac. |
| Multi-monitor/DPI reale | REVIEW/EXTERNAL_PENDING: static checks PASS, serve rerun Windows. |

## Rerun ASUS richiesto

Usare il clone ASUS o prepararne uno nuovo su:

`C:\Dev\Win7POS_TASK093_QA`

Prompt breve:

```text
Checkout origin/main dopo TASK-093. Esegui build WPF Release x86, scripts/check-supplier-excel-wizard.ps1, CLI --supplier-excel-ui-selftest.
Poi valida manualmente Products/Catalog -> Import Excel fornitore e Database/Maintenance -> Import Excel fornitore con .xls/.xlsx.
Conferma che i footer buttons Analizza, Avanti, Continua a Sync DB, Conferma/Applica, Indietro, Annulla restano sempre visibili e cliccabili, inclusa apertura da Database/Maintenance nested dialog e viewport piccolo.
Verifica che il file picker nativo resti owner-aware/front/enabled.
```

## Decisione

`SOFTWARE_PATCH_PUSHED_ASUS_RERUN_REQUIRED`
