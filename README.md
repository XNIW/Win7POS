# Win7POS

POS (Point of Sale) per Windows 7 / .NET Framework 4.8, architettura x86. Valuta: CLP (pesos cileni).

## Build (x86)

```bash
# Dalla root del repository
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86
```

Il progetto è configurato con `PlatformTarget=x86` e `TargetFramework=net48`. L’eseguibile si trova in `src/Win7POS.Wpf/bin/x86/Release/net48/`.

## Dialog WPF

Per architettura, standard e checklist dei dialog WPF vedi `docs/DIALOG_STANDARD.md`.

## Architettura

Mappa runtime:

```text
WPF shell net48/x86 -> servizi UI/composizione -> Core dominio/contratti -> Data adapter/repository -> SQLite locale/Admin Web POS API
CLI diagnostica -> Core/Data
Tests -> Core/Data
```

Regole di confine verificate da `scripts/check-architecture-boundaries.ps1`:

- `Win7POS.Core` resta `netstandard2.0` puro: niente WPF, SQLite, Dapper, HTTP transport, ClosedXML o ExcelDataReader.
- `Win7POS.Data` contiene adapter infrastrutturali: SQLite/Dapper, migrazioni, import reader Excel/HTML, outbox/sync e client HTTP Admin Web.
- `Win7POS.Wpf` resta shell WPF `net48`/x86: dialog, viewmodel, composizione, stampa Windows/spooler e scanner; niente uso diretto di `Microsoft.Data.Sqlite` o Dapper nel progetto WPF.
- Il POS non parla mai direttamente con Supabase e non contiene chiavi Supabase/service-role/anon.
- I payload persistiti in outbox non salvano token dispositivo/sessione, PIN, password o path completo del workbook.
- Il gate verifica anche la forma dei progetti: Core/Data `netstandard2.0`, Data C# 8, WPF `net48` con `UseWPF`, `x86` e `Prefer32Bit`.

Workflow dev portabile (Core/Data/tests; richiede SDK .NET 10 per i test `net10.0`):

```powershell
pwsh -File scripts/check-dialog-standards.ps1
pwsh -File scripts/check-architecture-boundaries.ps1
dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release
dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release
dotnet test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release
```

Su Windows build machine eseguire anche:

```powershell
dotnet restore Win7POS.slnx
pwsh -File scripts/check-dialog-standards.ps1
pwsh -File scripts/check-architecture-boundaries.ps1
dotnet build Win7POS.slnx -c Release --no-restore
dotnet test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release --no-build --no-restore
dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release --no-build -- --selftest --keepdb
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86
```

## Dati e log in produzione

In ambiente Windows l’applicazione usa la cartella comune:

- **Root dati:** `C:\ProgramData\Win7POS`
- **Database SQLite:** `C:\ProgramData\Win7POS\pos.db`
- **Log:** `C:\ProgramData\Win7POS\logs\app.log`
- **Backup:** `C:\ProgramData\Win7POS\backups`
- **Export:** `C:\ProgramData\Win7POS\exports`
- **Ricevute (PDF/copie):** `C:\ProgramData\Win7POS\receipts` (se abilitato)
- **Config Admin Web POS:** `C:\ProgramData\Win7POS\pos-admin-web.config` (se usato)

In caso di errore, controllare i log in `C:\ProgramData\Win7POS\logs\app.log`.
Il log applicativo ruota automaticamente quando `app.log` supera circa 5 MB e conserva fino a 5 archivi (`app.log.1`, `app.log.2`, ...).
Per errori di sync online cercare `category=online.bootstrap`,
`category=online.heartbeat`, `category=catalog.pull` o `category=sales.sync`.
Gli ID `clientRequestId`, `serverRequestId`, `syncAttemptId`, `clientBatchId`
e `clientSaleId` sono sicuri da condividere e servono a correlare Win7POS con
Admin Web audit/Recovery Center. Non condividere token, PIN, password o dump DB.

### Modalità test / data dir diverso

Per usare una directory dati diversa (es. test senza toccare i dati reali):

- **Variabile d'ambiente:** `WIN7POS_DATA_DIR`  
  Esempio: `WIN7POS_DATA_DIR=C:\POSData\TestRun1`  
  L'app userà `C:\POSData\TestRun1\pos.db`, log in `C:\POSData\TestRun1\logs\`, ecc.

- **Reset DB di test (solo ambiente dev/test):** eliminare il file `pos.db` nella cartella dati scelta. All'avvio successivo l'app mostrerà il wizard di primo avvio per creare il primo amministratore.  
  Esempio (PowerShell, dati in `C:\POSData\TestRun1`):  
  `Remove-Item -Force "C:\POSData\TestRun1\pos.db" -ErrorAction SilentlyContinue`  
  **Attenzione:** non eliminare il DB in produzione; usare solo per ambienti di test.  
In repository è disponibile lo script `scripts/reset-test-db.ps1` (usa `WIN7POS_DATA_DIR` o `-DataDir`).

## Collegamento Admin Web POS

Il collegamento online passa sempre da Admin Web POS API e non comunica direttamente con Supabase. L'avvio usa un solo dialog, **Accesso POS**. Su un DB realmente vuoto l'azione primaria resta **Riprova online**; solo dopo server non configurato, timeout o errore DNS/TLS/rete compare l'azione secondaria esplicita **Recovery locale**. Il wizard non si apre automaticamente e chiarisce che l'account creato resta locale e non collegato ad Admin Web. Un denial valido (credenziali, HTTP 401/403, device, policy o contract) non abilita recovery né fallback locale; utenti esistenti ma tutti disabilitati non sono trattati come prima installazione. Il pacchetto include il default staging pubblico, quindi il flusso normale richiede solo codice negozio (`shop_code`), codice staff (`staff_code`) e PIN/password.

Priorita risoluzione URL Admin Web:

1. variabile ambiente `WIN7POS_ADMIN_WEB_BASE_URL`;
2. file dati `pos-admin-web.config` con una riga `AdminWebBaseUrl=<url-base-admin-web>`;
3. default pacchettizzato/build-time incluso nell'assembly WPF.

Esempio staging pubblico verificato:

```text
WIN7POS_ADMIN_WEB_BASE_URL=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev
```

Esempio `C:\ProgramData\Win7POS\pos-admin-web.config`:

```ini
AdminWebBaseUrl=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev
```

Template versionato sicuro: `samples/pos-admin-web.config.example`.

Il default pacchettizzato del build corrente e:

```text
https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev
```

Per un futuro pacchetto production, impostare l'URL solo a build/package time dopo verifica reale del dominio production:

```bash
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:AdminWebEnvironment=production -p:AdminWebDefaultBaseUrl=https://<dominio-production-verificato>
```

Non usare un dominio production finche non e stato verificato davvero.

L'URL Admin Web e una configurazione tecnica automatica del pacchetto oppure un override di manutenzione. Nel dialog **Accesso POS** resta disponibile solo in **Impostazioni avanzate / Server**. Inserire solo l'URL base HTTPS del pannello, per esempio `https://<workers-dev-host>`, senza username/password nell'URL, senza `/auth/login`, `/shop`, query string o hash. Il nome dispositivo viene generato automaticamente dal nome PC sanitizzato, senza username, path, MAC address o seriali hardware. I token del dispositivo e della sessione vengono salvati protetti con DPAPI dell'utente Windows. PIN/password non vengono salvati in chiaro; dopo un first-login riuscito vengono usati solo per creare hash/salt locale dell'operatore mirror, così il POS può lavorare offline.

Admin Web invia anche una `policy` POS versionata durante first-login e catalog pull. Win7POS la salva in `app_settings` senza secret e la usa solo come contract operativo: offline sales dopo prima attivazione, mirror offline limitato allo staff corrente, revoca applicata al prossimo controllo online, pagamenti `cash/card/other`, valuta `CLP`, tax/fiscal online non configurato. Se il server richiede una capability non supportata, la status strip mostra `Richiede attenzione`; il POS non abilita funzioni non supportate.

Il contratto online usato dal client è centralizzato in `Win7POS.Core.Online.PosOnlineContract`. Il sync vendite usa `pos-sales-ledger-v2`, idempotency key stabile e outbox SQLite. La vendita normale viene persistita localmente prima di qualsiasi chiamata HTTP; i tentativi di sync remoto partono in background quando esiste una sessione online valida. Un guard app-wide evita sync concorrenti e la status strip mostra `Sync in corso`, pending/retry e vendite bloccate senza bloccare la cassa. Le richieste recovery provenienti da Admin Web sono per ora audit-only: Win7POS non esegue polling di azioni remote e non cancella outbox.

Al primo collegamento online, Win7POS non apre la cassa finche il catalogo iniziale non viene scaricato fino a `HasMore=false` e marcato `pos.catalog.sale_safe_at`. Il dialog mostra preparazione negozio con progress e retry; errori retryable o pagine parziali preservano trust/cursor ma tengono bloccato il POS. Anche `--safe-start` salta i refresh online ma non consente vendita normale senza un catalogo gia sale-safe.

Nel recovery locale con catalogo non sale-safe la shell non crea `PosView`: mostra un banner persistente, abilita solo Products/Import, retry server, backup/restore e diagnostica, e blocca vendita, pagamento, refund, void e cassetto. **Verifica nuovamente** abilita il POS solo quando esiste almeno un prodotto attivo con barcode, nome e prezzo di vendita valido; l'approvazione e il relativo security audit sono atomici.

All'avvio giornaliero, dopo il login operatore e prima di creare la schermata vendita, Win7POS esegue un preflight breve: DB locale, sessione device, outbox, sales sync e catalog delta. Se il catalogo locale e' gia sale-safe e la rete/catalog delta sono lenti, l'operatore puo continuare e la sync prosegue in background; revoca sessione, restore da rivedere, outbox blocked o catalogo non sale-safe bloccano l'apertura POS.

Runbook bootstrap/sync Win7POS: `docs/WIN7POS_BOOTSTRAP_SYNC_AUDIT.md`.

Per eseguire localmente lo stesso set di controlli obbligatori della CI usare `pwsh -NoProfile -File scripts/check-required-gates.ps1`. La toolchain e fissata da `global.json` alla feature band SDK 10.0.301; il release pack Windows usa `scripts/win7pos/windows/build-release-x86.ps1` e una sola sorgente per `README_RUN.txt`, `RELEASE_CHECKLIST.txt` e `VERSION.txt`.

Il ReleasePack per staging pubblica include ancora `set-admin-web-staging-url.bat`, che scrive `AdminWebBaseUrl=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev` in `C:\ProgramData\Win7POS\pos-admin-web.config`. Con il default pacchettizzato non e piu necessario per il flusso normale: usarlo solo come override/manual recovery o per manutenzione. Lo script non imposta override HTTP LAN.

Per test locale manuale e non release, `WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB=1` consente HTTP non-loopback; non abilitarlo nei pacchetti distribuiti. Per workers.dev/staging usare sempre HTTPS.

Check statico packaging/config:

```powershell
pwsh -File scripts/check-public-staging-config.ps1
pwsh -File scripts/check-pos-debug-logging.ps1
pwsh -File scripts/check-pos-first-login-sale-safe-ui.ps1
pwsh -File scripts/check-pos-start-of-day-sync.ps1
pwsh -File scripts/check-win7pos-restore-guard.ps1
```

## Stampante

- Dal menu laterale: **Stampa** → **Impostazioni stampante** per scegliere la stampante e le opzioni (copie, stampa automatica, salvataggio copia).
- **Stampa ultima ricevuta** per ristampare l’ultimo scontrino.
- Per test rapidi è possibile usare “Salva in file” o una stampante PDF.

## Backup e restore del database SQLite

- **Backup:** menu **Database** → **Backup database**. Il file viene salvato in `C:\ProgramData\Win7POS\backups` con nome tipo `pos_backup_yyyyMMdd_HHmmss.db`.
- **Restore:** menu **Database** → **Manutenzione database** (o percorso equivalente per il restore). Copiare il file `.db` di backup nella cartella desiderata e, dall’app, selezionare il restore da quel file. Prima di sovrascrivere `pos.db`, l'app verifica che non esistano vendite outbox `pending`, `retry` o `failed_blocked`; se esistono, il restore viene sospeso e va prima sincronizzata o revisionata l'outbox. Quando il restore è consentito, esegue `PRAGMA wal_checkpoint(FULL)`, crea automaticamente un pre-backup `pos_pre_restore_yyyyMMdd_HHmmss.db` in `C:\ProgramData\Win7POS\backups`; se il pre-backup fallisce, il restore non procede. Dopo restore esegue `PRAGMA integrity_check` e marca lo stato sync come da rivedere, bloccando lo start-of-day finche non viene revisionato.

**Manuale (senza UI):** copiare `C:\ProgramData\Win7POS\pos.db` in un luogo sicuro per il backup. Per il restore, chiudere l’applicazione, sostituire `pos.db` con il file di backup e riavviare.

## Smoke hardware Windows 7

La verifica fisica Windows 7/stampante/rete instabile resta esterna a questa repo. Usare `docs/WIN7_PRODUCTION_SMOKE_CHECKLIST.md`; non considerarla eseguita finche non esiste un report con hardware/VM reale, log e screenshot.
