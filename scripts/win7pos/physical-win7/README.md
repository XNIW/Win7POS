# Physical Windows 7 runner bridge

Questo folder contiene un bridge minimale per usare un PC fisico Windows 7 come runner Win7POS controllato dal Mac tramite cartella condivisa.

Il bridge non usa SSH, non richiede UTM, non installa nulla su Windows 7 e non esegue comandi arbitrari.

## File

- `start-physical-win7-bridge.bat`: bridge batch Win7. Crea cartelle, guarda `inbox` e processa job allowlistati.
- `run-physical-smoke.bat`: smoke Win7POS fisico con `WIN7POS_DATA_DIR=C:\Win7POSTest\data`.
- `send-physical-win7-job.sh`: sender Mac dry-run di default.
- `collect-physical-win7-output.sh`: collector Mac dry-run di default.

## Setup consigliato: Windows App folder redirection

1. Sul Mac crea una cartella locale dedicata, per esempio `~/Win7POSBridge`.
2. In Windows App modifica la connessione al PC Windows 7 `192.168.0.40`.
3. Abilita folder redirection / redirected folders e seleziona la cartella.
4. Connettiti a Windows 7.
5. In Windows 7 verifica che la cartella sia visibile, per esempio come `\\tsclient\Win7POSBridge`.
6. Verifica lettura/scrittura Mac-Win7 con file temporanei non sensibili.

Avvio bridge su Windows 7:

```cmd
set "WIN7POS_BRIDGE_ROOT=\\tsclient\Win7POSBridge"
start-physical-win7-bridge.bat
```

## Fallback SMB

1. Sul Mac abilita File Sharing solo su LAN/VPN fidata.
2. Condividi una cartella dedicata, senza dati reali.
3. Su Windows 7 apri `\\<mac-lan-ip>\<share-name>` o mappa un drive, per esempio `Z:`.
4. Non salvare password nel repo o negli script.

Avvio bridge:

```cmd
set "WIN7POS_BRIDGE_ROOT=Z:\Win7POSBridge"
start-physical-win7-bridge.bat
```

## Test lettura/scrittura Mac-Win7

Dal Mac crea un file non sensibile nella share:

```bash
printf 'mac write check\n' > /Volumes/Win7POSBridge/mac-write-check.txt
```

Da Windows 7 verifica che sia visibile, poi crea un file simile:

```cmd
echo win7 write check > %WIN7POS_BRIDGE_ROOT%\win7-write-check.txt
```

Sul Mac verifica che `win7-write-check.txt` sia leggibile. Rimuovi manualmente solo questi file temporanei.

## Invio job dal Mac

Dry-run:

```bash
scripts/win7pos/physical-win7/send-physical-win7-job.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --job env-report
```

Execute:

```bash
scripts/win7pos/physical-win7/send-physical-win7-job.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --job env-report \
  --execute
```

Job supportati:

- `env-report`
- `smoke-pos`
- `tasklist`
- `collect-logs`

Il bridge crea log in `outbox` e sposta i job in `done` o `failed`.

Il bridge processa un job alla volta e non include un timeout per singolo job:
su Windows 7 va monitorato dalla console. Se un job resta bloccato, fermare il
bridge con `Ctrl+C`, spostare manualmente il file `.job` in `failed` o
risolvere la causa, quindi riavviare il bridge.

## Smoke Win7POS

Il drop deve esistere in Windows 7:

```text
C:\Win7POSTest\drop\Win7POS\Win7POS.Wpf.exe
```

Inviare:

```bash
scripts/win7pos/physical-win7/send-physical-win7-job.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --job smoke-pos \
  --execute
```

Lo smoke:

- imposta `WIN7POS_DATA_DIR=C:\Win7POSTest\data`;
- avvia `Win7POS.Wpf.exe`;
- attende 5 secondi;
- esegue `tasklist | findstr /I Win7POS`;
- salva `C:\Win7POSTest\data\logs\physical-smoke.txt`;
- copia il log in `outbox`.

Non testa hardware reale.

## Lettura output

Dry-run:

```bash
scripts/win7pos/physical-win7/collect-physical-win7-output.sh \
  --bridge-root /Volumes/Win7POSBridge
```

Execute:

```bash
scripts/win7pos/physical-win7/collect-physical-win7-output.sh \
  --bridge-root /Volumes/Win7POSBridge \
  --execute
```

Default output:

```text
.win7pos-physical/reports/
```

## Screenshot UI

Usare Windows App / RDP per screenshot manuali della UI Win7POS. Salvare screenshot non sensibili nella cartella condivisa `screenshots` o in `C:\Win7POSTest\screenshots`, poi inviare il job `collect-logs`.

## Limiti sicurezza Windows 7

- Usare solo LAN/VPN.
- Non esporre RDP o SMB su Internet.
- Non usare dati reali.
- Non mettere password/token/licenze nel bridge root.
- Non aggiungere job generici.
- Non usare Windows 7 come ambiente Codex.
- Non modificare business code durante smoke/evidence.
