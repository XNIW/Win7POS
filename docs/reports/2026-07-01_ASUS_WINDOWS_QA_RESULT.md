# ASUS Windows QA Result - 2026-07-01

## Scope
- Clone: `C:\Dev\Win7POS_handoffQA`
- Branch sorgente QA: `handoff/win7pos-asus-qa-20260701`
- Commit handoff: `caad88c`
- Data dir test: `C:\POSData\TestRun1`
- SDK: `C:\Dev\dotnet10`, .NET SDK `10.0.301`
- Dati produzione: non usati

## Fix piccoli applicati durante QA
| Area | Esito | Note |
|------|-------|------|
| Startup WPF | FIXED | `PosLocalization` inizializzava le traduzioni prima delle entries statiche e causava `NullReferenceException` all'avvio. |
| DB Maintenance UI | FIXED | Aggiunto il menu laterale per aprire la manutenzione DB gia' presente. |
| DB Maintenance bindings | FIXED | Binding read-only impostati `Mode=OneWay` per evitare eccezioni WPF su proprieta' senza setter. |
| Release pack | FIXED | Lo script ora usa `dotnet` SDK 10 quando MSBuild 17 non puo' risolvere progetti SDK 10 e genera i file supporto del release pack. |

## Build e static checks
| Check | Risultato | Note |
|-------|-----------|------|
| `git diff --check` | PASS | Warning CRLF Git, nessun errore whitespace. |
| Build WPF x86 | PASS | `dotnet build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`. |
| Dialog standards | PASS | `scripts\check-dialog-standards.ps1`, 27/27 dialogs. |
| Static script loop | PASS | Tutti gli script `scripts\check-*.ps1` presenti sono passati. |
| Release script | PASS | `scripts\win7pos\windows\build-release-x86.ps1`. |
| Release completeness | PASS | `scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS -WriteManifests`. |
| Inno Setup compile | PASS | Inno Setup 6.7.3, output `installer\output\Win7POS-Setup.exe`. |

## WPF smoke reale
| Scenario | Risultato | Evidenza |
|----------|-----------|----------|
| Avvio app build | PASS_AFTER_FIX | Crash iniziale risolto con lazy translations; app avviata e DB test creato. |
| Login operatore dummy | PASS | Login WPF riuscito con operatore test-only su DB locale. |
| Vendita cash | PASS | Sale DB id 1, totale 1200, cash 1200, outbox pending. |
| Vendita card | PASS | Sale DB id 2, totale 1200, card 1200, outbox pending. |
| Vendita other | NOT_EXPOSED | Nessun controllo pagamento "other" visibile nel dialog testato. |
| Stampa assente | LIMITED_PASS | Il sistema apre "Microsoft Print to PDF"; nessuna stampante hardware verificata. |
| Ristampa | LIMITED_PASS | `Print last` avvia il flusso stampa/PDF; nessuna stampa hardware completata. |
| Backup | PASS | Backup creato da UI in `C:\POSData\TestRun1\backups\pos_backup_20260701_134627.db`. |
| Restore test | PASS_GUARD | Restore bloccato correttamente per vendite/outbox non sincronizzate; nessun ripristino distruttivo eseguito. |
| Offline sale | PASS | Vendite registrate localmente con outbox pending in assenza di sync remoto. |
| Dialog/focus | PASS_AFTER_FIX | DB Maintenance dialog apre e accetta comandi dopo fix binding. |
| DPI/risoluzione | PASS | Schermo primario 2880x1800, working area 2880x1704, `AppliedDPI=192`. |

## Release pack Windows
| Check | Risultato | Note |
|-------|-----------|------|
| `Win7POS.Wpf.exe` presente | PASS | `dist\Win7POS\Win7POS.Wpf.exe`. |
| SQLite native x86 presente | PASS | `dist\Win7POS\e_sqlite3.dll`. |
| Nessun `*.pdb` | PASS | Nessun file trovato. |
| Nessun sorgente | PASS | Nessun `.cs`, `.xaml`, `.ps1`, `.sln`, `.csproj` trovato nel drop. |
| Secret scan | PASS_WITH_FALSE_POSITIVES | Solo `publicKeyToken` nel `.config`; nessun secret reale trovato. |
| Avvio da `dist\Win7POS` | PASS | Processo avviato da `dist\Win7POS\Win7POS.Wpf.exe` con data dir di test. |
| Installer Inno | PASS | `installer\output\Win7POS-Setup.exe`, 5,835,427 bytes. |

## Limitazioni dichiarate
- Non ho testato stampante fiscale o hardware di stampa reale.
- Non ho completato un salvataggio PDF della stampa perche' il requisito era verificare comportamento con stampa assente.
- Non ho eseguito restore distruttivo: la guardia ha bloccato correttamente il restore con outbox pendente.
- Non ho testato pagamento "other" perche' non esposto dalla UI vista.
- Non ho usato dati produzione o secret.

## Decisione
ASUS_WINDOWS_QA_PASS_WITH_LIMITATIONS
