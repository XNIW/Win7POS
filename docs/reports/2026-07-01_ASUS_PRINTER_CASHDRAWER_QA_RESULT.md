# ASUS Printer/Cash Drawer QA Result - 2026-07-01

## Branch / Commit
- Branch: `qa/asus-printer-cashdrawer-hardening-20260701`
- Base: `qa/asus-win7pos-result-20260701` @ `63cdaaa`
- Commit finale: questo report viene committato nella branch risultato; hash finale riportato nell'output Codex.
- SDK: .NET SDK `10.0.301` da `C:\Dev\dotnet10`
- WIN7POS_DATA_DIR: `C:\POSData\TestRun1`

## Problema risolto
Prima il POS poteva tentare una stampa automatica anche senza una stampante POS esplicitamente configurata. Se la stampante predefinita Windows era `Microsoft Print to PDF` o un driver simile, il flusso poteva arrivare al prompt `Save Print Output As` dopo il pagamento.

Ora vendita, stampa e cassetto sono separati:
- la vendita viene salvata localmente prima di stampa/cassetto;
- la stampa automatica usa solo una stampante POS configurata e valida;
- la default Windows non viene usata a meno di consenso esplicito;
- i driver virtuali/PDF sono bloccati per auto-print POS se non consentiti;
- un errore di stampa o cassetto lascia la vendita salvata e mostra un warning non distruttivo;
- il cassetto e' disabilitato di default e richiede configurazione esplicita.

## Modifiche
| Area | File | Cambiamento |
|------|------|-------------|
| Printer discovery | `src/Win7POS.Wpf/Printing/InstalledPrinterInfo.cs` | Modello driver/stampante installata con default, virtuale, disponibilita', driver/porta e note. |
| Printer discovery | `src/Win7POS.Wpf/Printing/WindowsPrinterDiscovery.cs` | Enumerazione Win7-safe con `System.Drawing.Printing.PrinterSettings.InstalledPrinters`, rilevamento default e virtual/PDF/XPS probabile. |
| Settings keys | `src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs` | Chiavi dedicate per ricevuta, auto-print, default Windows, virtual printers e cassetto. |
| Settings UI | `src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml` | Dialog impostazioni con lista stampanti installate, selettori ricevuta/cassetto, test print e test drawer. |
| Settings VM | `src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs` | Stato UI, comandi test/refresh, settings receipt/cash drawer e sintesi stampante selezionata. |
| Payment flow | `src/Win7POS.Wpf/Pos/PosViewModel.cs` | Salvataggio vendita prima di cassetto/stampa; warning post-vendita; ristampa/manual print come azione esplicita. |
| POS service | `src/Win7POS.Wpf/Pos/PosWorkflowService.cs` | Resolver stampante configurata, blocco default/virtuale, default sicuri, apertura cassetto solo se configurata. |
| Spooler/raw | `src/Win7POS.Wpf/Printing/WindowsSpoolerReceiptPrinter.cs` | Nessun fallback alla default Windows; `PrinterName` obbligatorio; cassetto raw senza fallback. |
| Report print | `src/Win7POS.Wpf/Pos/Dialogs/DailyReportViewModel.cs` | Stampa report giornaliera marcata come azione esplicita utente. |
| Localizzazione | `src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs` | Messaggi printer/cash drawer e warning "vendita salvata" localizzati. |
| Static QA | `scripts/check-pos-printer-cashdrawer-safety.ps1` | Script di guardia per stampanti, default/PDF, flow pagamento, cassetto e secret. |

## Stampanti Windows
| Test | Risultato | Note |
|------|-----------|------|
| Enumerazione driver/stampanti Windows | PASS | Harness servizio: `PRINTER_COUNT=2`. |
| Rilevamento default printer | PASS | `Microsoft Print to PDF default=True`. |
| Rilevamento virtual/PDF/XPS | PASS | `Microsoft Print to PDF` e `OneNote (Desktop)` rilevate `virtual=True`. |
| Selezione stampante ricevuta | PASS | Settings service + UI binding/ComboBox verificati da build/static; click UI non eseguibile per desktop bloccato. |
| Test print | SKIP_HARDWARE_NOT_AVAILABLE | Nessuna stampante POS fisica disponibile; auto-print PDF virtuale bloccato correttamente. |

## Cassetto monete
| Test | Risultato | Note |
|------|-----------|------|
| Default disabled | PASS | Default `pos.cashdrawer.enabled=false`, mode `disabled`. |
| Configurazione tramite stampante | PASS | UI/settings e resolver richiedono stampante configurata non virtuale. |
| Open on cash only | PASS | Flow UI apre solo se configurato e pagamento cash; card sale smoke salvata senza cash. |
| Test hardware drawer | SKIP_CASH_DRAWER_HARDWARE_NOT_AVAILABLE | Nessun cassetto fisico disponibile. |

## Payment flow
| Scenario | Risultato | Note |
|----------|-----------|------|
| Cash sale senza stampante configurata | PASS | Harness: vendita `ASUSPRN...` salvata, `SALE_DB_COUNT=1`, `paid_cash=1234`. |
| Nessun PrintDialog automatico PDF | PASS | Auto-print bloccato prima dello spooler se stampante mancante o virtuale non consentita. |
| Vendita salvata anche se stampa non configurata | PASS | Harness: `AUTO_PRINT_BLOCKED=Sale saved... no POS receipt printer is configured.` |
| Warning chiaro post-vendita | PASS | Messaggio localizzato: vendita salvata, stampa non completata, azioni disponibili. |
| Card sale non apre cassetto | PASS | Harness card sale salvata `paid_cash=0 paid_card=2345`; flow cassetto richiede cash/configurazione. |
| Ristampa ultima ricevuta | PASS_STATIC | Funzione esistente mantenuta; stampa manuale passa `explicitUserAction=true`. Hardware non disponibile. |
| Other payment | NOT_SUPPORTED | Metodo non esposto dalla UI attuale; non e' stato inventato un nuovo metodo contabile. |

## Backup/restore
| Scenario | Risultato | Note |
|----------|-----------|------|
| Backup | PASS | Harness: `BACKUP_PASS path=C:\POSData\TestRun1\backups\pos_backup_20260701_144938.db`. |
| Restore guard outbox pending | PASS | Harness: restore bloccato con outbox non risolta; script statico restore guard PASS. |
| Restore su DB safe senza pending | SKIP | Non eseguito restore distruttivo; richiesta preservazione vendite test/outbox rispettata. |

## Check automatici
| Comando | Risultato |
|---------|-----------|
| `git diff --check` | PASS, solo warning CRLF del working copy |
| `dotnet build src\Win7POS.Core\Win7POS.Core.csproj -c Release` | PASS |
| `dotnet build src\Win7POS.Data\Win7POS.Data.csproj -c Release` | PASS |
| `dotnet build src\Win7POS.Cli\Win7POS.Cli.csproj -c Release` | PASS |
| `dotnet run --project src\Win7POS.Cli\Win7POS.Cli.csproj -c Release -- --selftest --keepdb` | PASS (`自检 PASS`) |
| `dotnet build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | PASS |
| `pwsh -File scripts\check-dialog-standards.ps1` | PASS |
| `pwsh -File scripts\check-public-staging-config.ps1` | PASS |
| `pwsh -File scripts\check-pos-debug-logging.ps1` | PASS |
| `pwsh -File scripts\check-pos-online-client.ps1` | PASS |
| `pwsh -File scripts\check-pos-online-bootstrap.ps1` | PASS |
| `pwsh -File scripts\check-pos-first-login-sale-safe-ui.ps1` | PASS |
| `pwsh -File scripts\check-pos-start-of-day-sync.ps1` | PASS |
| `pwsh -File scripts\check-pos-catalog-pull.ps1` | PASS |
| `pwsh -File scripts\check-pos-sales-sync.ps1` | PASS |
| `pwsh -File scripts\check-win7pos-restore-guard.ps1` | PASS |
| `pwsh -File scripts\check-win7pos-legacy-db-migrations.ps1` | PASS |
| `pwsh -File scripts\check-pos-startup-win7-safe.ps1` | PASS |
| `pwsh -File scripts\check-win7pos-startup-no-eager-db.ps1` | PASS |
| `pwsh -File scripts\check-pos-online-linking-task084b.ps1` | PASS |
| `pwsh -File scripts\check-pos-sync-status-ux.ps1` | PASS |
| `pwsh -File scripts\check-pos-shop-data-readonly.ps1` | PASS |
| `pwsh -File scripts\check-pos-revenue-copy.ps1` | PASS |
| `pwsh -File scripts\check-product-dialog-free-text.ps1` | PASS |
| `pwsh -File scripts\check-pos-printer-cashdrawer-safety.ps1` | PASS |

## Release/Installer
- Release pack: PASS (`pwsh -File scripts\win7pos\windows\build-release-x86.ps1`)
- Completeness: PASS (`scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS -WriteManifests`)
- Installer Inno: PASS (`installer\output\Win7POS-Setup.exe`, 5,842,706 bytes)
- Avvio da dist: PASS (`dist\Win7POS\Win7POS.Wpf.exe` avviato e processo vivo, poi chiuso)
- Note pack: `Win7POS.Wpf.exe` presente, `e_sqlite3.dll` presente, `*.pdb` assenti, sorgenti assenti, secret non rilevati.

## Limiti reali
- `SKIP_UI_DESKTOP_LOCKED`: Computer Use ha rilevato la schermata di blocco Windows; non e' stato possibile cliccare campi WPF reali.
- `SKIP_PHYSICAL_PRINTER_NOT_AVAILABLE`: nessuna stampante POS fisica disponibile.
- `SKIP_CASH_DRAWER_HARDWARE_NOT_AVAILABLE`: nessun cassetto fisico disponibile.
- `SKIP_WINDOWS7_PHYSICAL_MACHINE`: test eseguito su Windows host corrente, non su macchina fisica Windows 7.
- `SKIP_MULTI_MONITOR_DPI_REAL`: nessuna verifica reale multi-monitor/DPI oltre build/static.

## Decisione
PRINTER_CASHDRAWER_PARTIAL_HARDWARE_LIMITED
