# Win7POS physical Windows 7 runner

## Scopo

Questo runbook prepara un bridge pratico per usare un PC fisico Windows 7 come runner reale Win7POS, raggiungibile dal Mac tramite Windows App / Remote Desktop sulla LAN.

Decisione operativa:

- Codex resta sul Mac.
- Windows 7 fisico e solo runner/test target.
- Non installare Codex dentro Windows 7.
- Non richiedere SSH.
- Usare una cartella condivisa, RDP folder redirection o SMB, piu file job allowlistati.
- Windows App serve per vedere la UI e fare screenshot manuali.
- La cartella condivisa serve per drop, job, log e report.

## Perche PC fisico invece di UTM

La VM UTM/QEMU su Mac Apple Silicon e utile per pianificare, ma nel workflow Win7POS ha mostrato limiti operativi: prestazioni lente, guest agent non affidabile, exec remoto fragile e raccolta evidence macchinosa.

Un PC fisico Windows 7 riduce quel rischio per lo smoke test reale:

- runtime Win7 nativo;
- UI WPF visibile in Windows App / RDP;
- meno dipendenza da `utmctl`, qemu-ga o automazione hypervisor;
- trasferimento file controllabile con cartella condivisa;
- evidence leggibile dal Mac senza installare agenti dentro Win7.

## Perche Codex resta sul Mac

Codex deve restare nell'ambiente di sviluppo controllato, con repo, git, scanner, Node/npm e strumenti di verifica disponibili. Windows 7 non deve diventare ambiente agente o sviluppo.

Windows 7 resta un target legacy:

- riceve drop gia preparati;
- legge job allowlistati;
- esegue smoke test limitati;
- produce log/output/screenshot;
- non contiene secret, service role key, token o codice agente.

## Componenti

### Windows App / RDP per UI

Usare Windows App / Remote Desktop per:

- aprire la sessione Windows 7;
- lanciare o osservare Win7POS;
- catturare screenshot UI manuali;
- verificare errori visuali che non compaiono nei log.

RDP non e il canale principale per automazione. Serve a vedere e guidare la UI.

### RDP folder redirection

Folder redirection rende una cartella del Mac visibile dentro Windows 7, spesso come percorso `\\tsclient\...` o come drive/cartella sotto Computer.

E la prima opzione consigliata perche non richiede esporre SMB sulla LAN. Il bridge root puo stare nella cartella del Mac reindirizzata e Windows 7 puo puntarci con:

```cmd
set WIN7POS_BRIDGE_ROOT=\\tsclient\<nome-cartella>\Win7POSBridge
```

Se il percorso contiene spazi, usare le virgolette nei comandi:

```cmd
set "WIN7POS_BRIDGE_ROOT=\\tsclient\<nome-cartella>\Win7POSBridge"
```

### SMB share

SMB e il fallback se folder redirection non basta o non e stabile.

Schema:

- il Mac condivide una cartella locale;
- Windows 7 la apre come `\\<mac-lan-ip>\<share-name>` o la mappa come drive, per esempio `Z:`;
- il bridge root diventa `Z:\Win7POSBridge`.

Usare SMB solo su LAN/VPN fidata. Non esporre SMB su Internet.

### Bridge job file

Il bridge non legge comandi dal contenuto del job. Guarda solo il nome file in `inbox` e accetta una allowlist fissa:

- `env-report.job`
- `smoke-pos.job`
- `tasklist.job`
- `collect-logs.job`

Ogni job produce un log in:

```text
outbox\<timestamp>-<job>.log
```

I job riusciti vengono spostati in `done`; quelli falliti in `failed`. Il bridge non cancella dati.

## Layout consigliato

Bridge root default locale Windows:

```text
C:\Win7POSBridge
```

Sottocartelle create dal bridge:

```text
C:\Win7POSBridge\
  inbox\
  outbox\
  done\
  failed\
  logs\
  screenshots\
  drop\
```

Layout smoke Win7POS:

```text
C:\Win7POSTest\
  drop\Win7POS\Win7POS.Wpf.exe
  data\
  logs\
  screenshots\
```

Il data dir di test e:

```cmd
set WIN7POS_DATA_DIR=C:\Win7POSTest\data
```

## Setup consigliato

### Opzione 1: RDP folder redirection

1. Sul Mac crea una cartella locale fuori dal repo, per esempio `~/Win7POSBridge`.
2. In Windows App modifica la connessione al PC `192.168.0.40`.
3. Abilita folder redirection / redirected folders e seleziona la cartella Mac.
4. Connettiti al PC Windows 7.
5. In Windows 7 verifica che la cartella sia visibile da Esplora risorse.
6. Crea un file temporaneo non sensibile dal Mac e verifica che sia visibile in Windows.
7. Crea un file temporaneo non sensibile da Windows e verifica che sia visibile sul Mac.
8. Avvia il bridge con `WIN7POS_BRIDGE_ROOT` puntato al percorso reindirizzato.

Esempio su Windows 7:

```cmd
set "WIN7POS_BRIDGE_ROOT=\\tsclient\Win7POSBridge"
scripts\win7pos\physical-win7\start-physical-win7-bridge.bat
```

### Opzione 2: SMB fallback

1. Sul Mac abilita File Sharing.
2. Aggiungi una cartella condivisa dedicata, senza dati reali.
3. Abilita accesso SMB solo per un utente autorizzato.
4. Non salvare password nel repo, nei job o negli script.
5. Su Windows 7 apri `\\<mac-lan-ip>\<share-name>` o mappa un drive.
6. Verifica lettura/scrittura con file temporanei non sensibili.
7. Avvia il bridge con `WIN7POS_BRIDGE_ROOT` puntato al path SMB o al drive mappato.

Esempio:

```cmd
set "WIN7POS_BRIDGE_ROOT=Z:\Win7POSBridge"
scripts\win7pos\physical-win7\start-physical-win7-bridge.bat
```

## Flusso operativo

### Avvio bridge su Windows 7

1. Copiare o rendere visibili gli script `scripts\win7pos\physical-win7\` al PC Windows 7.
2. Aprire `cmd.exe`.
3. Impostare `WIN7POS_BRIDGE_ROOT` se il root e condiviso.
4. Eseguire:

```cmd
start-physical-win7-bridge.bat
```

Il processo resta in polling. Fermarlo con `Ctrl+C` quando non serve.

### Invio job dal Mac

Dry-run:

```bash
scripts/win7pos/physical-win7/send-physical-win7-job.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --job env-report
```

Esecuzione:

```bash
scripts/win7pos/physical-win7/send-physical-win7-job.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --job env-report \
  --execute
```

Leggere poi:

```text
<bridge-root>/outbox
```

### Smoke Win7POS

Preparare il drop completo in:

```text
C:\Win7POSTest\drop\Win7POS\Win7POS.Wpf.exe
```

Poi inviare:

```bash
scripts/win7pos/physical-win7/send-physical-win7-job.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --job smoke-pos \
  --execute
```

Lo smoke:

- usa `WIN7POS_DATA_DIR=C:\Win7POSTest\data`;
- avvia `Win7POS.Wpf.exe`;
- attende 5 secondi;
- verifica `tasklist | findstr /I Win7POS`;
- scrive `C:\Win7POSTest\data\logs\physical-smoke.txt`;
- copia il log in `outbox`.

Non testa hardware reale, stampanti, cassetti o dispositivi fiscali.

### Raccolta output dal Mac

Dry-run:

```bash
scripts/win7pos/physical-win7/collect-physical-win7-output.sh \
  --bridge-root /Volumes/Win7POSBridge
```

Esecuzione:

```bash
scripts/win7pos/physical-win7/collect-physical-win7-output.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --execute
```

Output locale default:

```text
.win7pos-physical/reports/
```

## Sicurezza

Regole obbligatorie:

- LAN/VPN only.
- Non esporre RDP o SMB su Internet.
- Non usare dati reali di negozio.
- Non salvare password, token, licenze o secret nel repo.
- Non salvare service role key o chiavi Supabase nel bridge.
- Non mettere comandi nei file `.job`: il bridge ignora il contenuto.
- Non aggiungere job generici tipo `run-command`.
- Non cancellare drop, log, screenshot o dati dal bridge.
- Non modificare business code Win7POS durante la raccolta evidence.

## Limiti

- Il bridge non sostituisce un test manuale UI completo.
- Gli screenshot UI restano manuali tramite Windows App / RDP.
- Se la share RDP/SMB non e visibile da entrambi i lati, Codex puo solo preparare job e leggere output gia esportati.
- Windows 7 resta un sistema legacy: usare rete minima, ambiente isolato e dati sintetici.
