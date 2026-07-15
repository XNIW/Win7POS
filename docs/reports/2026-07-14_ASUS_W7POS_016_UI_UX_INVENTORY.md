# ASUS-W7POS-016 — Inventario e remediation UI/UX

## Esito della lane

- Data revisione: 2026-07-14, host Windows (timezone `-04:00`).
- Branch integrato: `integration/asus-catalog-ui-runtime-20260714`.
- Closure software commit: `bb4178b506afe5ebc86313f010e2271ca8075f84`.
- Inventario statico: **42/42 artefatti XAML** censiti.
- Superfici interattive: **38/38 raggiungibili** da bootstrap, shell, tab, comandi POS, impostazioni o flow nested.
- Dialog: **31/31** derivano da `DialogShellWindow` e passano gli invarianti statici owner/sizing/chrome.
- P0 trovati: **0**.
- P1 statici residui noti dopo la remediation: **0**.
- Diagnostica catalogo e repair: modalità ultima sync, versione, completeness, conteggi sintetici, repair required e comando autorizzato sono integrati in Shop Settings con localizzazione EN/ES/IT/ZH.
- Guard di chiusura repair: pulsante Close, Escape e Alt+F4 non possono chiudere il dialog mentre il repair è in corso.
- Sale readiness: status UI e persistenza della vendita ordinaria usano lo stesso evaluatore con reason code, evitando divergenze fra indicatore e barriera transazionale.
- Lifecycle: il ViewModel del dialog implementa `IDisposable`, si disiscrive dal cambio lingua e rilascia `OwnerWindow`; il busy del repair è separato dagli altri stati UI.
- Matrice visuale autenticata e screenshot DPI: **BLOCKED_EXTERNAL / non eseguita**. Non viene dichiarato PASS visuale.

L'esito complessivo di ASUS-W7POS-016 resta `BLOCKED_EXTERNAL` finché la matrice visuale non viene eseguita su un runtime autenticato, con profili display controllati e hardware multi-monitor quando disponibile. La parte software, inventario, remediation localizzata e gate statici è completata e validata.

## Task ledger

| Task | Stato | Commit | Evidenza | Note |
|---|---|---|---|---|
| ASUS-W7POS-016-D1 inventario XAML e reachability | DONE | `bb4178b` | 42 XAML: 38 superfici, `App`, 3 resource dictionary | Tutte le superfici class-backed hanno un entrypoint raggiungibile |
| ASUS-W7POS-016-D2 remediation P0/P1 | DONE | `bb4178b` | build x86 e gate UI verdi | Fix localizzati; nessuna nuova dipendenza UI |
| ASUS-W7POS-016-D3 sync status, catalog repair e closing guard | DONE | `bb4178b` | dialog 31/31 e `check-pos-sync-status-ux.ps1` | Repair solo per ruolo autorizzato; conferma owner-safe; chiusura bloccata durante l'operazione |
| ASUS-W7POS-016-D4 regression checker | DONE | `bb4178b` | dialog/UI/supplier guard | Il checker supplier mantiene l'invariante footer fuori dallo `ScrollViewer` |
| ASUS-W7POS-016-D5 matrice visuale autenticata/screenshot | BLOCKED_EXTERNAL | — | 0 profili display autenticati eseguiti; 0 screenshot della matrice | Mancano credenziali staging e ambiente Win7/DPI/multi-monitor controllato |

## Inventario completo delle superfici interattive

Legenda: `OK` indica revisione statica senza modifica; `FIX` indica una remediation integrata nel branch corrente.

| # | XAML | Tipo | Classificazione e reachability | Esito statico |
|---:|---|---|---|---|
| 1 | `MainWindow.xaml` | `Window` | Shell principale e recovery shell; creato dal bootstrap `App`; ospita tutti i tab principali | OK |
| 2 | `Pos/PosView.xaml` | `UserControl` | POS; creato lazy da `MainWindow` solo dopo il gate sale-safe | OK; focus scanner verificato anche sui path ViewModel toccati |
| 3 | `Products/ProductsView.xaml` | `UserControl` | Catalogo prodotti; tab `ProductsTab` di `MainWindow` | OK |
| 4 | `Import/ImportView.xaml` | `UserControl` | Import generale; contenuto di `ImportDataDialog` | FIX: label conteggio e database localizzate |
| 5 | `Import/SupplierExcelImportDialog.xaml` | `DialogShellWindow` | Import fornitori; aperto dal flow manutenzione DB | FIX: riepiloghi visibili localizzati; footer fisso preservato |
| 6 | `Import/ImportDataDialog.xaml` | `DialogShellWindow` | Contenitore modale dell'import generale | FIX: titolo/footer/button shared |
| 7 | `Pos/PaymentView.xaml` | `UserControl` | Pagamento; tab `PaymentTab` di `MainWindow` | OK |
| 8 | `Pos/Dialogs/BoletaNumberDialog.xaml` | `DialogShellWindow` | Numero ricevuta/boleta; flow pagamento | FIX: titolo/footer shared; keypad 72 px preservato |
| 9 | `Pos/Dialogs/SalesRegisterDialog.xaml` | `DialogShellWindow` | Registro vendite; comando POS con permission gate | FIX: titolo shared e label permission localizzata |
| 10 | `Pos/Dialogs/RefundDialog.xaml` | `DialogShellWindow` | Refund/void; comando POS | FIX: conferma full-void unica mouse/tastiera, card accessibili, sizing adattivo |
| 11 | `Pos/DailyReportView.xaml` | `UserControl` | Chiusura/report giornaliero; tab shell e contenuto del dialog dedicato | FIX: titolo shared |
| 12 | `Pos/Dialogs/DailyReportDialog.xaml` | `DialogShellWindow` | Chiusura giornaliera dal POS | FIX: minimi 720×520, sizing adattivo e scroll orizzontale di sicurezza |
| 13 | `Pos/UserManagementView.xaml` | `UserControl` | Utenti/ruoli; tab `UsersRolesTab` di `MainWindow` | OK |
| 14 | `Pos/Dialogs/UserManagementDialog.xaml` | `DialogShellWindow` | Utenti/ruoli dal menu POS | OK |
| 15 | `Pos/Dialogs/NewUserDialog.xaml` | `DialogShellWindow` | Nuovo utente; nested da user management | OK |
| 16 | `Pos/Dialogs/RoleEditDialog.xaml` | `DialogShellWindow` | Crea/duplica/rinomina ruolo; nested da user management | OK |
| 17 | `Pos/Dialogs/SettingsHubDialog.xaml` | `DialogShellWindow` | Hub impostazioni; menu shell | FIX: body scroll-safe a bassa altezza |
| 18 | `Pos/Dialogs/LanguageSettingsDialog.xaml` | `DialogShellWindow` | Lingua; card dell'hub impostazioni | OK |
| 19 | `Pos/Dialogs/ShopSettingsDialog.xaml` | `DialogShellWindow` | Dati shop readonly, diagnostica exactness e repair catalogo; comando POS/impostazioni | FIX: status/completeness/conteggi localizzati, repair autorizzato con conferma, closing guard durante busy |
| 20 | `Pos/Dialogs/PrinterSettingsDialog.xaml` | `DialogShellWindow` | Stampante; comando POS/impostazioni | OK |
| 21 | `Pos/Dialogs/DbMaintenanceDialog.xaml` | `DialogShellWindow` | Database, backup/restore e accesso import fornitori; shell/recovery/POS | OK |
| 22 | `Pos/Dialogs/AboutSupportDialog.xaml` | `DialogShellWindow` | Informazioni/supporto; shell e POS | OK |
| 23 | `Pos/Dialogs/PosOnlineFirstLoginDialog.xaml` | `DialogShellWindow` | First login, retry online e accesso recovery; bootstrap shell | OK; gate double-submit/sale-safe verde |
| 24 | `Pos/Dialogs/FirstRunSetupDialog.xaml` | `DialogShellWindow` | Setup/recovery locale; nested dal first-login | OK |
| 25 | `Pos/Dialogs/ChangePinDialog.xaml` | `DialogShellWindow` | Cambio PIN obbligatorio; nested dal first-login | OK |
| 26 | `Pos/Dialogs/PosStartOfDaySyncDialog.xaml` | `DialogShellWindow` | Start-of-day sync; avviato dalla shell dopo accesso | OK |
| 27 | `Pos/Dialogs/OperatorSwitchDialog.xaml` | `DialogShellWindow` | Cambio/blocco operatore; shell e permission recovery | OK |
| 28 | `Pos/Dialogs/HeldCartsDialog.xaml` | `DialogShellWindow` | Recupero carrelli sospesi; comando POS | FIX: titolo/footer shared e ripristino focus barcode all'uscita |
| 29 | `Pos/Dialogs/OverrideAuthorizationDialog.xaml` | `DialogShellWindow` | Override autorizzazione; `OverrideAuthService` | OK |
| 30 | `Pos/Dialogs/PermissionDeniedDialog.xaml` | `DialogShellWindow` | Errore permesso con possibile cambio operatore; helper condiviso | OK |
| 31 | `Import/ApplyConfirmDialog.xaml` | `DialogShellWindow` | Conferma distruttiva/critica condivisa; usata anche dal full void | FIX: titolo/footer/button shared |
| 32 | `Import/ModernMessageDialog.xaml` | `DialogShellWindow` | Info/warning/error condiviso; bootstrap, shell, prodotti e POS | FIX: titolo/footer/button shared |
| 33 | `Pos/Dialogs/ChangeQuantityDialog.xaml` | `DialogShellWindow` | Modifica quantità riga carrello | FIX: titolo/footer shared e ripristino focus barcode; keypad preservato |
| 34 | `Pos/Dialogs/DiscountDialog.xaml` | `DialogShellWindow` | Sconto riga/carrello; comando POS | FIX nel chiamante: focus barcode ripristinato anche su cancel/error |
| 35 | `Products/ProductEditDialog.xaml` | `DialogShellWindow` | Crea/modifica prodotto; `ProductsViewModel` e full-edit POS | OK; chiamante POS ora ripristina focus in `finally` |
| 36 | `Products/DeleteProductConfirmDialog.xaml` | `DialogShellWindow` | Conferma eliminazione prodotto | OK |
| 37 | `Products/ExportDataDialog.xaml` | `DialogShellWindow` | Export prodotti | OK |
| 38 | `Products/ProductPriceHistoryDialog.xaml` | `DialogShellWindow` | Storico prezzi prodotto | OK |

## Artefatti XAML non interattivi

| XAML | Ruolo | Reachability/esito |
|---|---|---|
| `App.xaml` | Bootstrap e merge risorse globali | Caricato all'avvio; OK |
| `ModernStyles.xaml` | Stili globali, incluse risorse titolo/footer dialog e contrasto disabled | Referenziato da `App.xaml`; OK |
| `Icons/MaterialSymbols.xaml` | Geometrie vettoriali delle icone | Merge globale prima degli stili; gate OK |
| `Themes/DialogChrome.xaml` | Chrome e caption condivisi di `DialogShellWindow` | Merge globale; gate dialog OK |

Totale: 38 superfici interattive + `App.xaml` + 3 resource dictionary = **42 artefatti XAML**.

## Review per criterio

| Criterio | Evidenza statica | Esito |
|---|---|---|
| Shell, owner e nested owner | `check-dialog-standards.ps1`; 31/31 `CenterOwner`, owner helper e base type | PASS statico |
| Titolo/footer/button hierarchy | Nuova sezione 16 del checker; 31/31 usano risorse dialog shared | PASS statico |
| Positioning e clamp | Nessun `Left`/`Top`, `Loaded` di positioning o clamp fuori dalla base | PASS statico |
| Work area/clipping | Refund e DailyReport adattivi; SettingsHub scroll-safe; footer supplier fuori dallo scroll | PASS statico, visuale da eseguire |
| Focus e tastiera | Refund card Enter/Space + focus visuale; Enter full-void non bypassa conferma; focus barcode ripristinato dopo i modal toccati | PASS statico |
| Escape/default/double-submit | `IsCancel`/`IsDefault` presenti dove previsto; first-login double-submit gate verde; Shop Settings blocca Close/Escape/Alt+F4 durante repair | PASS statico |
| Localizzazione EN/ES/IT/ZH | Import e diagnostica/repair catalogo usano chiavi valorizzate nelle quattro lingue | PASS per i path toccati |
| Contrasto e distinzione non solo colore | Risorse disabled/vector icon e focus border verificate dal guard | PASS statico, visuale da eseguire |
| Nessuna nuova dipendenza Win10+ | Solo WPF/.NET Framework e risorse esistenti | PASS |

La revisione statica non certifica dimensioni pixel, resa font, contrasto effettivo del monitor, traduzioni espanse o comportamento multi-monitor: questi restano oggetto della matrice runtime.

## Difetti P1 corretti

| ID | Difetto | Correzione | Regressione |
|---|---|---|---|
| UI-016-01 | `Enter` nel refund chiamava direttamente `TryConfirm()` e poteva bypassare la conferma del full void | Unico path `TryConfirmWithPrompt()` per click e tastiera | UI guard + build |
| UI-016-02 | Card Full void/Partial return erano mouse-only e senza focus distinguibile | `Focusable`, Enter/Space, automation name e focus border | UI guard |
| UI-016-03 | Refund 980×700 fisso e DailyReport con minimi troppo alti per work area ridotta/DPI 125% | Sizing adattivo, minimi 720×520 e scrollbar di sicurezza | UI guard + build |
| UI-016-04 | Hub impostazioni 2×3 poteva essere clippato dopo il clamp della finestra | Scroll verticale del body e righe `Auto` | UI guard |
| UI-016-05 | Diversi exit da dialog POS non restituivano il focus allo scanner/barcode | `RequestFocusBarcode()` dopo recover/change quantity e nei `finally` di edit/discount | UI guard + build |
| UI-016-06 | Label e status import visibili erano hardcoded in italiano/inglese | Chiavi EN/ES/IT/ZH per import generale e supplier Excel | UI guard + build |
| UI-016-07 | Alcuni dialog raggiungibili non usavano titolo/footer/button shared | Allineamento localizzato alle risorse standard; checker 31/31 | dialog checker |
| UI-016-08 | Il supplier CLI selftest cercava erroneamente il testo nel `Content` del `Button`, mentre i button corretti usano icon + `TextBlock` | Il test cerca il `TextBlock` localizzato dopo il footer e continua a verificare footer fuori dallo `ScrollViewer` | supplier CLI selftest |
| UI-016-09 | Lo stato exactness catalogo e il repair non erano osservabili dalla UI shop | Aggiunti modalità/versione/completeness/conteggi/repair required e comando full repair solo autorizzato, con conferma non distruttiva e owner sicuro | sync-status UI checker + build |
| UI-016-10 | Il dialog poteva essere chiuso mentre il full repair era in corso | `CanClose` disabilita Close e `OnClosing` annulla Escape/Alt+F4 durante `IsRepairBusy` | sync-status UI checker + build |
| UI-016-11 | Status e persistenza vendita potevano valutare readiness con logiche separate | Unico evaluatore sale-safety con reason code condiviso da UI e barriera transazionale | sync-status/sales checker + Core tests |
| UI-016-12 | Il ViewModel tratteneva subscription lingua e owner oltre la vita del dialog | `IDisposable`, unsubscribe esplicito e clear di `OwnerWindow` alla chiusura | sync-status UI checker + build |

## Matrice visuale

| Profilo | Stato | Evidenza | Blocco/prossimo passo |
|---|---|---|---|
| 1024×768, 100% | BLOCKED_EXTERNAL | Nessuno screenshot; non eseguito | Fornire fixture autenticata e profilo display controllato; avviare l'EXE x86 e percorrere le 38 superfici |
| 1024×768, 125% | BLOCKED_EXTERNAL | Nessuno screenshot; non eseguito | Come sopra; verificare espansione EN/ES/IT/ZH e footer |
| 1366×768, 100% | BLOCKED_EXTERNAL | Nessuno screenshot; non eseguito | Come sopra |
| 1366×768, 125% | BLOCKED_EXTERNAL | Nessuno screenshot; non eseguito | Come sopra |
| 1024×600 best-effort | BLOCKED_EXTERNAL | Nessuno screenshot; non eseguito | Eseguire almeno Refund, DailyReport, SettingsHub, supplier import e first-login |
| Multi-monitor best-effort | BLOCKED_EXTERNAL | Nessuno screenshot; non eseguito | Richiede hardware/VM multi-monitor; verificare owner, nested owner, clamp e assenza di recenter |

Comando esatto di build e avvio per il prossimo esecutore:

```powershell
& 'C:\Dev\dotnet10\dotnet.exe' build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86
& 'src\Win7POS.Wpf\bin\x86\Release\net48\Win7POS.Wpf.exe'
```

Usare la procedura canonica in `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`, salvando screenshot redatti e timestamp. Non usare dati produzione e non includere PIN, token, connection string o percorsi sensibili.

## Validazione eseguita

Eseguita il 2026-07-14 sul branch integrato; i conteggi consolidati finali sono riportati nel closeout:

| Comando | Risultato |
|---|---|
| `C:\Dev\dotnet10\dotnet.exe build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | PASS, 0 warning, 0 errori, output `bin\x86\Release\net48` |
| `pwsh -NoProfile -File scripts/check-dialog-standards.ps1` | PASS, shared dialog resources 31/31 |
| `pwsh -NoProfile -File scripts/check-win7pos-ui-ux-guard.ps1` | PASS |
| `pwsh -NoProfile -File scripts/check-supplier-excel-wizard.ps1` | PASS |
| CLI supplier Excel UI selftest | PASS intermedio storico; non usato come gate finale diretto. Il gate finale supplier apply passa dal publish Release isolato; l'apphost in-tree è bloccato da Windows Application Control `0x800711C7` |
| `pwsh -NoProfile -File scripts/check-product-dialog-free-text.ps1` | PASS |
| `pwsh -NoProfile -File scripts/check-pos-unified-login-ux.ps1` | PASS |
| `pwsh -NoProfile -File scripts/check-pos-sync-status-ux.ps1` | PASS |
| `pwsh -NoProfile -File scripts/check-pos-first-login-sale-safe-ui.ps1` | PASS |
| `git diff --check` | PASS |

## Perimetro e rischi residui

- `ShopSettingsDialog.xaml`, `ShopSettingsDialog.xaml.cs` e `ShopSettingsViewModel.cs` sono ora parte della remediation integrata per exactness/repair e closing guard.
- Il repair richiama direttamente il workflow catalogo integrato; non resta alcun handoff applicativo separato.
- Nessuno screenshot è stato acquisito e nessun comportamento su Windows 7 reale, stampante, scanner o multi-monitor è stato certificato da questa lane.
- La matrice runtime può ancora trovare problemi P1 dipendenti da font/DPI/contenuto; l'affermazione “zero P0/P1” è limitata ai difetti rilevabili dalla review statica e dai gate eseguiti.
- La matrice autenticata 1024×768/1366×768 a 100%/125%, 1024×600 best-effort e multi-monitor resta `BLOCKED_EXTERNAL`; nessun PASS visuale viene inferito dai checker statici.
