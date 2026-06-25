# Win7POS

POS (Point of Sale) per Windows 7 / .NET Framework 4.8, architettura x86. Valuta: CLP (pesos cileni).

## Build (x86)

```bash
# Dalla root del repository
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release
```

Il progetto è configurato con `PlatformTarget=x86` e `TargetFramework=net48`. L’eseguibile si trova in `src/Win7POS.Wpf/bin/Release/net48/`.

## Dialog WPF

Per architettura, standard e checklist dei dialog WPF vedi `docs/DIALOG_STANDARD.md`.

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

Il collegamento online passa sempre da Admin Web POS API e non comunica direttamente con Supabase. Su installazione nuova con DB SQLite vuoto, l'app propone il bootstrap online prima del wizard locale di recovery/dev. Per abilitarlo configurare l'URL base di Admin Web in uno di questi modi:

- variabile ambiente `WIN7POS_ADMIN_WEB_BASE_URL`;
- file dati `pos-admin-web.config` con una riga `AdminWebBaseUrl=<url-base-admin-web>`.

Il collegamento normale richiede solo codice negozio (`shop_code`), codice staff (`staff_code`) e PIN/password. L'URL Admin Web e una configurazione tecnica da impostare una volta con `WIN7POS_ADMIN_WEB_BASE_URL` oppure con `C:\ProgramData\Win7POS\pos-admin-web.config`; nella finestra **Collega POS online** resta disponibile solo in **Impostazioni avanzate / Server** per manutenzione. Inserire solo l'URL base HTTPS del pannello, per esempio `https://<workers-dev-host>`, senza `/auth/login`, `/shop`, query string o hash. Il nome dispositivo viene generato automaticamente dal nome PC sanitizzato, senza username, path, MAC address o seriali hardware. Se il DB locale è già configurato, si può avviare da **Accesso operatore** → **Collega POS online**. I token del dispositivo e della sessione vengono salvati protetti con DPAPI dell'utente Windows. PIN/password non vengono salvati in chiaro; dopo un first-login riuscito vengono usati solo per creare hash/salt locale dell'operatore mirror, così il POS può lavorare offline.

Il ReleasePack per staging pubblica include `set-admin-web-staging-url.bat`, che scrive `AdminWebBaseUrl=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev` in `C:\ProgramData\Win7POS\pos-admin-web.config`. Lo script non imposta override HTTP LAN.

Per test locale manuale e non release, `WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB=1` consente HTTP non-loopback; non abilitarlo nei pacchetti distribuiti. Per workers.dev/staging usare sempre HTTPS.

## Stampante

- Dal menu laterale: **Stampa** → **Impostazioni stampante** per scegliere la stampante e le opzioni (copie, stampa automatica, salvataggio copia).
- **Stampa ultima ricevuta** per ristampare l’ultimo scontrino.
- Per test rapidi è possibile usare “Salva in file” o una stampante PDF.

## Backup e restore del database SQLite

- **Backup:** menu **Database** → **Backup database**. Il file viene salvato in `C:\ProgramData\Win7POS\backups` con nome tipo `pos_backup_yyyyMMdd_HHmmss.db`.
- **Restore:** menu **Database** → **Manutenzione database** (o percorso equivalente per il restore). Copiare il file `.db` di backup nella cartella desiderata e, dall’app, selezionare il restore da quel file (l’app sovrascrive il `pos.db` corrente con il file scelto). Eseguire sempre un backup prima di un restore.

**Manuale (senza UI):** copiare `C:\ProgramData\Win7POS\pos.db` in un luogo sicuro per il backup. Per il restore, chiudere l’applicazione, sostituire `pos.db` con il file di backup e riavviare.
