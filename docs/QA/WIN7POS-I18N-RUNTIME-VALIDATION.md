# Win7POS i18n runtime validation

Questa checklist chiude il gate runtime i18n Win7POS. Va eseguita su Windows/VM reale; non sostituisce l'avvio WPF. Se viene eseguita solo su Windows 10/11, segnare il risultato come `WINDOWS_MODERN_RUNTIME_SMOKE`; Windows 7 resta external gate.

## Ambiente richiesto

- Windows 7 SP1 con .NET Framework 4.8 runtime, preferito.
- Accettabile per smoke preliminare: Windows 10/11 con .NET Framework 4.8 e build x86.
- Build `Release` x86 di `src/Win7POS.Wpf/Win7POS.Wpf.csproj`.
- Data dir di test isolata: `C:\Win7POSTest\data`.
- Evidence dir: `C:\Win7POSTest\evidence`.
- Nessuna configurazione cloud/produzione nel data dir di test.
- Stampante fisica opzionale. Se manca, segnare `PHYSICAL_PRINTER_EXTERNAL_GATE`.

## Comandi

Da PowerShell nella root repo Win7POS:

```powershell
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86
pwsh -NoLogo -NoProfile -File scripts\win7pos\windows\run-i18n-runtime-validation.ps1
```

Per non rebuildare:

```powershell
pwsh -NoLogo -NoProfile -File scripts\win7pos\windows\run-i18n-runtime-validation.ps1 -SkipBuild
```

Per verificare solo piano/script senza avviare la UI:

```powershell
pwsh -NoLogo -NoProfile -File scripts\win7pos\windows\run-i18n-runtime-validation.ps1 -PlanOnly
```

Lo script imposta `WIN7POS_DATA_DIR=C:\Win7POSTest\data`, `WIN7POS_SAFE_START=1`, avvia `Win7POS.Wpf.exe --safe-start`, non imposta URL Admin Web e fallisce se trova `pos-admin-web.config` nel data dir isolato.

## Lingue da testare

Per ogni lingua `en`, `it`, `es`, `zh-CN`:

- Avviare app.
- Aprire menu lingua in MainWindow e selezionare la lingua, oppure impostare `app_settings`/`ui.language` nel DB di test.
- Riavviare app.
- Verificare che la lingua resti persistita dopo il restart.
- Fotografare/salvare screenshot della MainWindow.

Query opzionale se `sqlite3.exe` e disponibile:

```powershell
sqlite3 C:\Win7POSTest\data\pos.db "select value from app_settings where key='ui.language';"
```

## Schermate da verificare

- MainWindow/menu.
- Login operatore o FirstRun, se richiesto.
- POS/cart.
- Payment.
- Refund/void.
- Discount.
- Held carts.
- Boleta number.
- Import.
- Products e product edit.
- Users/UserManagement.
- Settings/shop settings.
- Support/About.
- DailyReport.
- Printer settings.

PASS se le label principali sono nella lingua scelta e non compaiono fallback inglesi evidenti, salvo acronimi/literal tecnici ammessi come `POS`, `SII`, `RUT`, `SKU`, `PDF`, `CLP`, estensioni file e nomi di colonne tecniche.

## zh-CN visual check

Per `zh-CN`, verificare:

- Nessun quadratino/tofu nelle schermate principali.
- Menu, bottoni, titoli dialog, label form e tabelle leggibili.
- Nessun testo sovrapposto in cart, payment, import, products, users, settings, daily report.
- Se il font della macchina cliente non copre CJK, annotare font Windows disponibile e screenshot. Non cambiare font globali senza patch separata.

## Fallback

- Impostare temporaneamente `ui.language` a un valore non supportato, per esempio `fr`.
- Riavviare.
- PASS se l'app parte senza crash e torna a `en`.
- Per key mancante, il fallback atteso e sicuro: nessun crash e marker `[missing:<key>]` solo in ambiente di sviluppo/review.

## Printer/export/dialog

- Aprire Printer settings.
- Eseguire test/fallback senza stampante fisica.
- PASS se nessuna stampante non causa crash e viene mostrato messaggio localizzato.
- Aprire DailyReport export.
- Verificare filtro file localizzato e cancel senza crash.
- Verificare receipt/boleta preview/test string localizzate.
- Se non c'e stampante fisica, segnare `PHYSICAL_PRINTER_EXTERNAL_GATE`.

## Offline/no cloud

- Avviare con `WIN7POS_SAFE_START=1`.
- Non configurare `WIN7POS_ADMIN_WEB_BASE_URL`.
- Verificare che app parta offline senza cloud obbligatorio.
- Verificare nei log che heartbeat/sync online siano saltati per safe-start o non richiesti.

## Evidenze da raccogliere

- `i18n-runtime-validation-report.md` generato dallo script.
- Screenshot per `en`, `it`, `es`, `zh-CN`.
- Screenshot di printer fallback e export cancel.
- `app.log` e `startup-trace.log` redatti.
- Versione Windows, architettura, .NET Framework release.
- Indicazione stampante: fisica si/no.

## Criteri PASS/FAIL

- `PASS`: app WPF avviata, finestra principale stabile, nessuna eccezione WPF/binding/localization nei log, 4 lingue testate, persistenza verificata, fallback `en` verificato, zh-CN leggibile, printer/export non crashano.
- `FAIL`: crash, finestra non si apre, binding/localization exception, lingua non persistita, `zh-CN` illeggibile su schermate core, printer/export crash.
- `EXTERNAL_GATE`: stampante fisica non disponibile o hardware Windows 7 reale non ancora eseguito.

## Stato consentito

- Usare `DONE_RUNTIME_VALIDATED` solo dopo avvio reale WPF su Windows/VM e completamento PASS.
- Usare `DONE_CODE_READY` quando codice, build, scanner statici, Admin runtime
  smoke, build POS x86 e questo package di validazione sono completi, ma
  Windows 7 physical/VM runtime validation non e disponibile.
- Usare `READY_FOR_EXTERNAL_WINDOWS_RUNTIME_VALIDATION_PACKAGE` quando questo pacchetto e pronto ma il runtime Windows non e stato eseguito e manca ancora la decisione di chiusura code-ready.
- Non usare `VERIFIED_RUNTIME`, `VERIFIED_DONE_READY` o `DONE_RUNTIME_VALIDATED`
  senza avvio reale WPF su Windows/VM.

## Chiusura code-ready 2026-06-30

Stato finale autorizzato: `DONE_CODE_READY`.

Windows 7 physical/VM runtime validation unavailable; code, build, static
scanner, Admin runtime smoke, POS x86 build and validation package are complete.
Runtime WPF validation remains documented as external/manual evidence only if
environment becomes available.
