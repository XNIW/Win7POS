# Win7POS su Mac Apple Silicon con UTM - Fase 1

## Stato TASK-034

`TASK-034` mette in pausa il live E2E VM/UTM/Win7: stato operativo `PAUSED_VM_SETUP_REQUIRED`.

Non usare questo documento come autorizzazione a scaricare ISO, creare VM, avviare UTM o forzare smoke Win7 reale durante TASK-034. Rimane valido come piano di ripresa quando UTM, `WinPOS-Builder`, `Win7POS-Test`, toolchain Windows, runtime .NET Framework 4.8, cartella condivisa e drop reale saranno disponibili.

Senza VM restano testabili solo scanner statici, bootstrap/config/logging/catalog pull, documentazione e check compatibili con macOS/PowerShell.

## Obiettivo

Questo workflow prepara un ciclo minimo e ripetibile per testare Win7POS in una VM Windows 7 SP1 x64 su Mac Apple Silicon:

1. preparazione artefatto su host o macchina Windows;
2. drop controllato nella cartella condivisa;
3. avvio VM Windows 7;
4. smoke test manuale o assistito;
5. raccolta log, screenshot e report.

Codex resta sul Mac e lavora sulla repo locale. Windows 7 resta solo il target legacy di esecuzione: non installare Codex nella VM, non usare la VM come ambiente di sviluppo primario e non salvare credenziali/licenze nel workflow.

## Risultato analisi repo

- Progetto principale: WPF C# in `src/Win7POS.Wpf/Win7POS.Wpf.csproj`.
- Target WPF: `.NET Framework 4.8` (`net48`), `x86`, `WinExe`.
- Librerie condivise: `src/Win7POS.Core` e `src/Win7POS.Data` sono `netstandard2.0`.
- CLI di supporto: `src/Win7POS.Cli` e `net10.0`; non e l'app POS WPF.
- Entry point app: `src/Win7POS.Wpf/App.xaml` con `StartupUri="MainWindow.xaml"`.
- Avvio runtime: `src/Win7POS.Wpf/App.xaml.cs` crea directory dati/log e imposta emulazione IE11 WebBrowser.
- Primo flusso WPF: `src/Win7POS.Wpf/MainWindow.xaml.cs` inizializza SQLite, eventuale bootstrap Admin Web, wizard primo avvio e login operatore.
- Installer: `installer/Win7POS.iss`, con sorgente prevista `dist\Win7POS` e output `installer\output\Win7POS-Setup.exe`.
- Non ho trovato file `.sln`, `build/`, `release/` o `dist/` presenti in repo.

## Build e packaging

La build completa WPF non e realistica come passaggio affidabile su questo Mac Apple Silicon:

- il progetto POS reale e `net48` + WindowsDesktop/WPF;
- il target e Windows 7 first, x86;
- l'host attuale e macOS arm64 con .NET SDK 10;
- il worklog repo registra gia build WPF `net48` non concluse su macOS.

Quindi:

- **Build WPF reale:** solo su Windows con Visual Studio/MSBuild/dotnet compatibile con .NET Framework 4.8.
- **Test eseguibile:** nella VM Windows 7.
- **Packaging installer:** su Windows con Inno Setup 6.
- **Build Core/Data su Mac:** possibile o probabile, ma non produce l'artefatto WPF da testare.

Comandi documentati in repo:

```powershell
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86
pwsh -File scripts/check-dialog-standards.ps1
iscc installer\Win7POS.iss
```

Per la Fase 1, il comando WPF va eseguito su Windows. Su Mac non forzarlo: puo fallire, restare bloccato o produrre evidenza non rappresentativa.

## Artefatto da testare

Artefatto minimo per smoke test:

- cartella completa `src/Win7POS.Wpf/bin/Release/net48/` prodotta da build Windows;
- deve contenere `Win7POS.Wpf.exe` e le DLL richieste.

Artefatto packaging:

- cartella `dist\Win7POS` preparata dal workflow release Windows;
- installer `installer\output\Win7POS-Setup.exe` generato da Inno Setup.

Per la prima Fase 1 preferire la cartella app completa rispetto all'installer: e piu facile sostituire i file, lanciare l'eseguibile e leggere i log.

## Runtime verificati dalla repo

Prerequisiti guest Windows 7 dedotti da codice e documentazione:

- Windows 7 SP1 x64 come target legacy.
- .NET Framework 4.8 obbligatorio.
- Microsoft Visual C++ Runtime x86 consigliato per dipendenze native SQLite.
- SQLite locale tramite `Microsoft.Data.Sqlite` e `SQLitePCLRaw.bundle_e_sqlite3`.
- Cartella dati default: `C:\ProgramData\Win7POS`.
- Database default: `C:\ProgramData\Win7POS\pos.db`.
- Log default: `C:\ProgramData\Win7POS\logs\app.log`.
- Override test: variabile `WIN7POS_DATA_DIR`.
- Config Admin Web opzionale: `WIN7POS_ADMIN_WEB_BASE_URL` o `pos-admin-web.config` nella data dir.
- Stampante/cassetto: il codice usa Windows spooler/raw printer; per smoke test senza hardware saltare o usare stampante PDF/salvataggio file dove disponibile.
- Barcode/QR/Excel: presenti dipendenze `ZXing.Net.Bindings.Windows.Compatibility`, `ClosedXML`, `ExcelDataReader`; la stampa automatica della boleta non genera file PDF.

Non sono stati dedotti driver fiscali esterni obbligatori, credenziali hardcoded o accesso diretto Supabase nel POS WPF.

## Prerequisiti VM

- UTM installato su macOS.
- VM Windows 7 SP1 x64 valida.
- Nome consigliato VM: `Win7POS-Test`.
- Snapshot baseline pulito prima di ogni batch.
- Rete disabilitata quando possibile; usare host-only solo se serve trasferire file o raggiungere un servizio di test.
- Cartella condivisa host/guest configurata.
- Nessun token, licenza, password o chiave dentro script, report o cartelle condivise.

UTM espone automazione via AppleScript e CLI `utmctl`, ma non tutte le funzioni sono disponibili; la Fase 1 usa `utmctl` solo per controlli leggeri e avvio VM se presente.

## Layout host

Dalla root repo:

```text
.win7pos-vm/
  drop/
  logs/
  screenshots/
  reports/
```

`.win7pos-vm/` e ignorata da git.

## Layout guest consigliato

```text
C:\Win7POSTest\drop
C:\Win7POSTest\logs
C:\Win7POSTest\screenshots
```

Per isolare dati e log dell'app durante smoke test, impostare nel guest una data dir di test, per esempio:

```cmd
set WIN7POS_DATA_DIR=C:\Win7POSTest\data
```

In questo caso il log applicativo reale sara:

```text
C:\Win7POSTest\data\logs\app.log
```

Copiarlo poi in `C:\Win7POSTest\logs` o direttamente nella cartella condivisa host `.win7pos-vm/logs`.

## Ciclo test Fase 1

1. Riparti da snapshot baseline pulito.
2. Avvia VM `Win7POS-Test`.
3. Prepara o ricevi l'artefatto Windows.
4. Sul Mac, copia l'artefatto in `.win7pos-vm/drop/Win7POS`.
5. Nel guest, copia o apri l'artefatto da `C:\Win7POSTest\drop`.
6. Imposta `WIN7POS_DATA_DIR` per non toccare dati reali.
7. Avvia `Win7POS.Wpf.exe`.
8. Esegui smoke test manuale o assistito.
9. Salva screenshot in `C:\Win7POSTest\screenshots`.
10. Copia `app.log` e note anomalie in `C:\Win7POSTest\logs`.
11. Sul Mac, raccogli output in `.win7pos-vm/logs`, `.win7pos-vm/screenshots` e `.win7pos-vm/reports`.
12. Spegni VM o torna manualmente allo snapshot baseline.

Non fare restore snapshot automatici in Fase 1: il restore e distruttivo e deve restare un'azione manuale esplicita.

## Fase 2 - Primo smoke reale

Questa fase valida il primo avvio reale di Win7POS dentro `Win7POS-Test`, senza automatizzare restore snapshot e senza modificare codice business. L'obiettivo non e coprire tutto il POS: e dimostrare che l'artefatto Windows parte su Windows 7, usa una data dir di test, produce log leggibili e permette una prima navigazione POS.

### Prerequisiti VM

- VM UTM chiamata `Win7POS-Test`.
- Windows 7 SP1 x64 gia installato e avviabile.
- Snapshot baseline pulito disponibile, ripristinato manualmente prima del test se serve.
- Rete disabilitata o host-only, salvo test esplicito del bootstrap Admin Web.
- Cartella condivisa host/guest configurata e verificata manualmente.
- Cartelle guest create o creabili:
  - `C:\Win7POSTest\drop`
  - `C:\Win7POSTest\logs`
  - `C:\Win7POSTest\screenshots`
  - `C:\Win7POSTest\data`

### Runtime Win7 richiesti

Verificati dalla repo:

- .NET Framework 4.8 obbligatorio.
- Microsoft Visual C++ Runtime x86 consigliato per dipendenze native SQLite.
- Eventuali DLL native SQLite devono essere presenti nel drop insieme a `Win7POS.Wpf.exe`.

Non installare runtime durante questo workflow da script host: annota nel report cosa e gia presente nella VM e cosa manca.

### Verificare `utmctl`

Sul Mac:

```bash
command -v utmctl
utmctl list
```

Se `utmctl` non e nel `PATH`, prova a verificare se esiste:

```bash
ls -la /Applications/UTM.app/Contents/MacOS/utmctl
```

Non e obbligatorio usare `utmctl` per il primo smoke: puoi avviare la VM dalla UI UTM. Se lo usi, prima fai dry-run:

```bash
scripts/win7pos/run-utm-smoke.sh
```

### Verificare cartella condivisa

Nel guest Windows 7:

1. apri Esplora risorse;
2. individua la cartella condivisa UTM;
3. crea un file temporaneo non sensibile, per esempio `host-share-check.txt`;
4. verifica sul Mac che il file sia visibile nella cartella condivisa;
5. rimuovi solo quel file temporaneo, manualmente.

Se la cartella condivisa non e disponibile, copia il drop con il metodo gia configurato per la VM e annotalo nel report.

### Preparare e copiare il drop

La build WPF reale deve arrivare da Windows. I path verificati dalla repo sono:

- `src/Win7POS.Wpf/bin/Release/net48/`
- `dist/Win7POS/`

Entrambi devono contenere `Win7POS.Wpf.exe`.

Sul Mac, prepara il drop nella cartella ignorata da git:

```bash
scripts/win7pos/prepare-test-drop.sh
scripts/win7pos/prepare-test-drop.sh --execute --source dist/Win7POS
```

Se usi l'output diretto della build Windows:

```bash
scripts/win7pos/prepare-test-drop.sh --execute --source src/Win7POS.Wpf/bin/Release/net48
```

Poi rendi disponibile `.win7pos-vm/drop/Win7POS` al guest come:

```text
C:\Win7POSTest\drop\Win7POS
```

### Avvio manuale del POS

Nel guest, puoi avviare manualmente:

```cmd
cd /d C:\Win7POSTest\drop\Win7POS
set WIN7POS_DATA_DIR=C:\Win7POSTest\data
Win7POS.Wpf.exe
```

In alternativa copia ed esegui lo script guest:

```cmd
copy <cartella-condivisa>\scripts\win7pos\guest\run-pos-smoke.bat C:\Win7POSTest\run-pos-smoke.bat
C:\Win7POSTest\run-pos-smoke.bat
```

Lo script guest non installa nulla, non cancella dati e non contiene password. Serve solo a impostare il layout test, controllare l'eseguibile e avviare `Win7POS.Wpf.exe`.

### Cosa fotografare o salvare

Salva screenshot in `C:\Win7POSTest\screenshots`:

- desktop/VM con Windows 7 avviato;
- cartella `C:\Win7POSTest\drop\Win7POS` con `Win7POS.Wpf.exe` visibile;
- eventuale messaggio di errore runtime mancante;
- wizard primo avvio, se appare;
- schermata login/accesso operatore;
- schermata principale POS;
- menu Prodotti aperto, se raggiungibile;
- pagamento/vendita base, se supportato senza hardware reale;
- eventuali errori o crash.

Non fotografare password, PIN, token, licenze o dati reali.

### Raccogliere log

Con `WIN7POS_DATA_DIR=C:\Win7POSTest\data`, il log applicativo atteso e:

```text
C:\Win7POSTest\data\logs\app.log
```

Al termine:

1. chiudi Win7POS;
2. copia `C:\Win7POSTest\data\logs\app.log` in `C:\Win7POSTest\logs\app.log`;
3. copia screenshot e log verso la cartella condivisa host;
4. sul Mac esegui:

```bash
scripts/win7pos/collect-test-output.sh
scripts/win7pos/collect-test-output.sh --execute --from /path/to/shared-output
```

Se i file sono gia in `.win7pos-vm/logs` e `.win7pos-vm/screenshots`:

```bash
scripts/win7pos/collect-test-output.sh --execute
```

### Checklist risultato atteso

- [ ] `utmctl list` funziona oppure VM avviata manualmente dalla UI UTM.
- [ ] Cartella condivisa verificata con file temporaneo non sensibile.
- [ ] Drop copiato e `Win7POS.Wpf.exe` presente.
- [ ] Runtime .NET Framework 4.8 presente.
- [ ] VC++ Runtime x86 presente oppure assenza annotata.
- [ ] `WIN7POS_DATA_DIR` impostata a `C:\Win7POSTest\data`.
- [ ] App avviata senza crash immediato.
- [ ] Wizard primo avvio o login visibile.
- [ ] Schermata principale POS raggiunta, se credenziali/test data lo permettono.
- [ ] Menu Prodotti raggiunto, se login completato.
- [ ] Vendita base provata solo se possibile senza hardware reale.
- [ ] Stampa/cassetto saltati o testati con stampante PDF/salvataggio file.
- [ ] Chiusura app senza crash.
- [ ] `app.log` raccolto.
- [ ] Screenshot raccolti.
- [ ] Report compilato da `docs/dev/templates/win7pos-smoke-report-template.md`.

## Fase 3 - Build VM Windows 10/11 + Test VM Windows 7

La pipeline completa usa due ambienti separati:

- **Builder VM:** `WinPOS-Builder`, Windows 10 o Windows 11.
- **Test VM:** `Win7POS-Test`, Windows 7 SP1 x64.

Codex resta sul Mac e orchestra il workflow tramite documentazione, script dry-run e raccolta output. Non installare Codex dentro Windows 7.

### Perche buildare su Windows 10/11 Builder

Win7POS e un'app WPF `net48` x86. La build reale richiede tool Windows e targeting pack .NET Framework 4.8; su macOS Apple Silicon non e un passaggio affidabile. La Builder VM puo avere Visual Studio, Build Tools, MSBuild, targeting pack e Inno Setup senza sporcare la VM Windows 7.

### Perche testare su Windows 7

Windows 7 e il target legacy runtime. La VM `Win7POS-Test` deve restare pulita, isolata e ripristinabile via snapshot. Non deve contenere Visual Studio o tool di sviluppo salvo runtime strettamente necessari.

### Differenza ambienti

| Ambiente | Scopo | Tool ammessi | Output |
|----------|-------|--------------|--------|
| `WinPOS-Builder` | Restore, build, package | Visual Studio/Build Tools, MSBuild, .NET Framework 4.8 targeting pack, NuGet, Inno Setup opzionale | `src\Win7POS.Wpf\bin\Release\net48\` o `dist\Win7POS\` |
| `Win7POS-Test` | Eseguire smoke legacy | .NET Framework 4.8 runtime, runtime nativi necessari, cartella condivisa | log, screenshot, report |
| Mac host | Orchestrare e raccogliere | script repo, UTM/`utmctl` opzionale | `.win7pos-vm/drop`, `.win7pos-vm/logs`, `.win7pos-vm/screenshots`, `.win7pos-vm/reports` |

### Prerequisiti Builder VM

- Windows 10 o Windows 11.
- Visual Studio o Build Tools compatibili con progetti SDK-style `net48` WPF.
- MSBuild disponibile da Developer Command Prompt.
- .NET Framework 4.8 targeting pack.
- NuGet restore funzionante tramite MSBuild o dotnet su Windows.
- Inno Setup 6 solo se serve generare `Win7POS-Setup.exe`.
- Git opzionale, solo se si clona/builda direttamente la repo nella Builder VM.

### Prerequisiti Win7 Test VM

- Windows 7 SP1 x64.
- .NET Framework 4.8 runtime.
- Microsoft Visual C++ Runtime x86 consigliato se richiesto dalle dipendenze native SQLite.
- Snapshot baseline pulito.
- Rete disabilitata o ridotta, salvo test Admin Web esplicito.
- Cartella condivisa host/guest verificata.

### Output atteso Builder

Output diretto build:

```text
src\Win7POS.Wpf\bin\Release\net48\
```

Drop release:

```text
dist\Win7POS\
```

Entrambi devono contenere:

```text
Win7POS.Wpf.exe
```

### Flusso manuale consigliato

#### A. Su Builder VM Windows 10/11

1. Apri o copia la repo nella Builder VM.
2. Apri Developer Command Prompt o PowerShell con MSBuild nel PATH.
3. Esegui restore NuGet per il progetto WPF.
4. Esegui build `Release` `x86`.
5. Verifica `src\Win7POS.Wpf\bin\Release\net48\Win7POS.Wpf.exe`.
6. Crea `dist\Win7POS`.
7. Copia l'output completo dentro `dist\Win7POS`.
8. Se serve installer, esegui Inno Setup su `installer\Win7POS.iss`.
9. Esporta `dist\Win7POS` o uno zip equivalente verso il Mac.
10. Compila `docs/dev/templates/win7pos-builder-checklist.md`.

#### B. Su Mac

1. Metti il drop esportato in una cartella locale.
2. Valida il drop:

```bash
scripts/win7pos/validate-drop.sh --source <drop>
```

3. Prepara il drop per la VM Win7:

```bash
scripts/win7pos/prepare-test-drop.sh --execute --source <drop>
```

4. Avvia `Win7POS-Test` dalla UI UTM oppure, solo se richiesto, con:

```bash
scripts/win7pos/run-utm-smoke.sh --execute --vm Win7POS-Test
```

#### C. Su VM Windows 7

1. Verifica .NET Framework 4.8 runtime.
2. Verifica VC++ Runtime x86 o annota l'assenza.
3. Copia o apri il drop da `C:\Win7POSTest\drop\Win7POS`.
4. Esegui `C:\Win7POSTest\run-pos-smoke.bat`.
5. Salva screenshot in `C:\Win7POSTest\screenshots`.
6. Copia `C:\Win7POSTest\data\logs\app.log` in `C:\Win7POSTest\logs`.
7. Copia log/screenshot verso Mac.
8. Compila `docs/dev/templates/win7pos-smoke-report-template.md`.

Per i dettagli Builder, vedi `docs/dev/win7pos-windows-builder.md`.

## Fase 4 - Builder scripts Windows e primo drop verificabile

La Fase 4 rende operativo il passaggio Builder VM -> Mac -> Win7 Test VM tramite script Windows dedicati:

```text
scripts\win7pos\windows\build-release-x86.ps1
scripts\win7pos\windows\build-release-x86.cmd
scripts\win7pos\windows\README.md
```

Usali solo dentro `WinPOS-Builder` Windows 10/11. Su macOS Apple Silicon non eseguire build WPF `net48`.

Flusso consigliato:

1. nella Builder VM esegui dry-run;
2. se MSBuild e disponibile, esegui build reale;
3. esporta `dist\Win7POS` verso Mac;
4. sul Mac esegui `scripts/win7pos/validate-drop.sh --source <drop>`;
5. solo dopo una validazione positiva, importa il drop con `prepare-test-drop.sh`;
6. lascia il primo avvio Win7 a un task/manual run separato.

Il report Builder atteso e:

```text
dist\Win7POS-build-report.md
```

Non dichiarare build riuscita senza quel report e senza `dist\Win7POS\Win7POS.Wpf.exe` verificato sulla Builder VM.

## Fase 7 - VM Control Bridge

Se dal Mac non sono visibili `UTM.app`, `utmctl`, file `.utm`, `WinPOS-Builder` o `Win7POS-Test`, non dichiarare la VM controllabile. Il passaggio corretto e preparare un canale di controllo verificabile.

File operativi:

- `docs/dev/win7pos-vm-control-bridge.md`;
- `scripts/win7pos/vm/discover-vm-host.sh`;
- `scripts/win7pos/vm/send-builder-job.sh`;
- `scripts/win7pos/windows/bridge/start-builder-bridge.ps1`;
- `scripts/win7pos/windows/bridge/capture-screenshot.ps1`;
- `scripts/win7pos/guest/README.md`.

Usa il bridge quando Codex resta sul Mac e la Builder VM vede una cartella condivisa. Il Mac scrive file job in `inbox`; Windows Builder esegue solo job allowlistati e scrive log in `outbox`.

Job consentiti:

- `env-report`;
- `build-dry-run`;
- `build-release`;
- `package-drop`;
- `screenshot`.

Non usare il bridge dentro Windows 7. `Win7POS-Test` resta ambiente runtime manuale/assistito: riceve drop, esegue `run-pos-smoke.bat`, produce screenshot/log e non ospita Codex.

Discovery host:

```bash
scripts/win7pos/vm/discover-vm-host.sh
```

Se il verdict indica `VM_HOST_NOT_FOUND`, `VM_CLI_NOT_FOUND` e `VM_FILES_NOT_FOUND`, il prossimo passo e configurare manualmente hypervisor e VM. Non procedere a build/smoke finche una VM reale non e disponibile.

Invio job Builder dal Mac, dopo aver avviato il bridge nella Builder VM:

```bash
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job env-report --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-dry-run --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-release --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job package-drop --execute
```

Leggi `outbox` per log, zip, checksum e screenshot. Poi valida il drop sul Mac con `validate-drop.sh` e prepara `.win7pos-vm/drop` con `prepare-test-drop.sh`.

## Cosa e automatico ora

- Creazione layout host `.win7pos-vm`.
- Dry-run di preparazione drop.
- Copia controllata dell'artefatto in `.win7pos-vm/drop/Win7POS` con `--execute`.
- Verifica presenza `utmctl` e VM per avvio leggero.
- Generazione report locale da log/screenshot gia copiati sul Mac.

## Cosa resta manuale

- Build WPF `net48` x86 su Windows.
- Preparazione `dist\Win7POS`, se serve installer.
- Creazione/configurazione VM Windows 7.
- Snapshot baseline e restore snapshot.
- Configurazione cartella condivisa UTM.
- Installazione runtime nel guest.
- Avvio POS dentro Windows se non si usa automazione guest.
- Login, first-run, vendita base, stampa/cassetto e screenshot.
- Copia dal guest verso la cartella condivisa se non e montata direttamente.

## Script disponibili

### `scripts/win7pos/prepare-test-drop.sh`

Dry-run default:

```bash
scripts/win7pos/prepare-test-drop.sh
```

Copia effettiva:

```bash
scripts/win7pos/prepare-test-drop.sh --execute --source dist/Win7POS
```

Se `dist/Win7POS` non esiste, lo script prova il default WPF `src/Win7POS.Wpf/bin/Release/net48`. Se non trova `Win7POS.Wpf.exe`, spiega che serve build Windows.

### `scripts/win7pos/run-utm-smoke.sh`

Dry-run default:

```bash
scripts/win7pos/run-utm-smoke.sh
```

Se `utmctl` non e nel `PATH`, puoi passare il binario esplicitamente:

```bash
UTMCTL_EXE=/Applications/UTM.app/Contents/MacOS/utmctl scripts/win7pos/run-utm-smoke.sh
```

Lo script usa un timeout su `utmctl list` per evitare dry-run bloccati quando UTM e appena installato o non inizializzato. Per ridurlo durante troubleshooting:

```bash
UTMCTL_TIMEOUT_SECS=3 scripts/win7pos/run-utm-smoke.sh
```

Avvio VM via `utmctl`:

```bash
scripts/win7pos/run-utm-smoke.sh --execute --vm Win7POS-Test
```

Lo script non ripristina snapshot, non spegne la VM e non esegue comandi guest.

### `scripts/win7pos/collect-test-output.sh`

Dry-run default:

```bash
scripts/win7pos/collect-test-output.sh
```

Genera report dai file gia presenti sul Mac:

```bash
scripts/win7pos/collect-test-output.sh --execute
```

Copia prima da una cartella locale di appoggio:

```bash
scripts/win7pos/collect-test-output.sh --execute --from /path/to/manual-output
```

La sorgente `--from` deve essere locale al Mac, per esempio una cartella condivisa montata o un export manuale. Lo script non legge direttamente file dal guest senza guest agent.

### `scripts/win7pos/validate-drop.sh`

Valida un drop gia prodotto su Windows:

```bash
scripts/win7pos/validate-drop.sh --source dist/Win7POS
```

Lo script controlla `Win7POS.Wpf.exe`, segnala `.config` se presente/atteso, elenca DLL principali e cerca asset nativi SQLite candidati. Non copia e non cancella nulla.

## Checklist smoke test

- [ ] VM Win7 avviata.
- [ ] Runtime verificati: .NET Framework 4.8 e, se necessario, VC++ Runtime x86.
- [ ] POS copiato nella cartella test.
- [ ] `WIN7POS_DATA_DIR` impostata su directory test, se si vuole isolamento dati.
- [ ] App avviata senza crash.
- [ ] Schermata principale visibile.
- [ ] Wizard primo avvio completato o account test gia presente.
- [ ] Login/accesso iniziale verificato, se presente.
- [ ] Menu prodotti aperto, se presente.
- [ ] Flusso vendita base provato, se supportato senza hardware reale.
- [ ] Stampa/cassetto fiscale saltati, mockati o provati con stampante PDF se manca hardware reale.
- [ ] Chiusura app senza crash.
- [ ] `app.log` raccolto.
- [ ] Screenshot salvati.
- [ ] Anomalie annotate nel report.

## Da completare dopo verifica manuale

- Confermare se il guest vede la cartella condivisa UTM come drive, share di rete o mount SPICE.
- Confermare il comando migliore per lanciare l'app nel guest: doppio click, `.bat`, PowerShell o shortcut.
- Confermare se `WIN7POS_DATA_DIR=C:\Win7POSTest\data` resta il layout migliore in Windows 7.
- Confermare se `Win7POS.Wpf.exe` richiede DLL native SQLite aggiuntive nel drop.
- Confermare se il primo login deve essere locale recovery/dev o bootstrap Admin Web.
- Confermare se la stampa PDF/scontrino funziona senza hardware fiscale reale.

## Rischi e limiti Apple Silicon/UTM/QEMU

- Windows 7 x64 su Apple Silicon gira in emulazione, quindi puo essere molto lento.
- Driver video, stampante, USB e periferiche POS possono non comportarsi come su hardware x86 reale.
- QEMU guest agent su Windows 7 puo essere non disponibile, fragile o non realistico; trattarlo come Fase 2 opzionale.
- Automazione screenshot/input dentro la VM puo essere limitata.
- Network disabilitato o host-only riduce rischio, ma puo impedire bootstrap Admin Web.
- Test positivo su UTM non sostituisce una verifica finale su hardware x86 reale o remoto quando stampante/cassetto sono critici.

## Fase 2 consigliata

- Aggiungere automazione piu forte via `utmctl` o AppleScript solo dopo aver stabilizzato VM e cartella condivisa.
- Valutare QEMU guest agent solo se installabile e stabile su Windows 7.
- Creare script guest `.bat` o PowerShell per impostare `WIN7POS_DATA_DIR`, avviare `Win7POS.Wpf.exe` e copiare `app.log`.
- Aggiungere cattura screenshot ripetibile dal guest o dal display UTM.
- Definire test case ripetibili: first-run, login, vendita base, annulla/chiudi, export/log.
- Valutare macchina x86 reale/remota se UTM e troppo lento o instabile.

## File repo letti per questa analisi

- `AGENTS.md`
- `README.md`
- `docs/AI_WORKLOG.md`
- `docs/FORMATO_LOG_DEBUG.md`
- `installer/README_INSTALLER.txt`
- `installer/Win7POS.iss`
- `src/Win7POS.Wpf/Win7POS.Wpf.csproj`
- `src/Win7POS.Core/Win7POS.Core.csproj`
- `src/Win7POS.Data/Win7POS.Data.csproj`
- `src/Win7POS.Cli/Win7POS.Cli.csproj`
- `src/Win7POS.Wpf/App.xaml`
- `src/Win7POS.Wpf/App.xaml.cs`
- `src/Win7POS.Wpf/MainWindow.xaml.cs`
- `src/Win7POS.Core/AppPaths.cs`
- `src/Win7POS.Core/PosPaths.cs`
- `src/Win7POS.Data/PosDbOptions.cs`
- `src/Win7POS.Data/DbInitializer.cs`
- `src/Win7POS.Wpf/Infrastructure/FileLogger.cs`
- `src/Win7POS.Wpf/Pos/Online/PosAdminWebOptions.cs`
- `scripts/reset-test-db.ps1`
- `scripts/check-dialog-standards.ps1`
- `scripts/check-pos-online-bootstrap.ps1`
- `scripts/check-pos-online-client.ps1`
- `scripts/check-pos-catalog-pull.ps1`
