# Win7POS Builder Bridge

## Scopo

Questo bridge permette a Codex sul Mac di chiedere alla VM Windows 10/11 Builder di eseguire solo job allowlistati. Non e un runner generico e non accetta comandi liberi.

Usalo nella VM `WinPOS-Builder`, non nella VM Windows 7.

## Cartelle

Default:

```text
C:\Win7POSBridge
```

Sottocartelle create dallo script:

```text
C:\Win7POSBridge\inbox
C:\Win7POSBridge\outbox
C:\Win7POSBridge\outbox\done
C:\Win7POSBridge\outbox\failed
C:\Win7POSBridge\logs
C:\Win7POSBridge\screenshots
```

## Job supportati

- `env-report.job`
- `build-dry-run.job`
- `build-release.job`
- `package-drop.job`
- `screenshot.job`

Il contenuto del file job non viene eseguito. Conta solo il nome del file e solo se rientra nell'allowlist.

## Avvio nella Builder VM

Dalla root repo Win7POS:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\bridge\start-builder-bridge.ps1 -BridgeRoot C:\Win7POSBridge -Watch
```

Per elaborare un solo job e terminare:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\bridge\start-builder-bridge.ps1 -BridgeRoot C:\Win7POSBridge -Once
```

## Invio job dal Mac

La cartella bridge deve essere la stessa cartella condivisa vista dal Mac. Esempio:

```bash
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job env-report --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-dry-run --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-release --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job package-drop --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job screenshot --execute
```

## Output

Ogni job produce un log in:

```text
C:\Win7POSBridge\outbox\<timestamp>-<job>.log
```

I job completati vengono spostati in `outbox\done`. I job falliti vengono spostati in `outbox\failed`.

Il job `package-drop` crea, se la build esiste:

```text
dist\Win7POS-drop.zip
dist\Win7POS-drop.sha256.txt
```

e copia nella cartella condivisa:

```text
C:\Win7POSBridge\outbox\Win7POS-drop.zip
C:\Win7POSBridge\outbox\Win7POS-drop.sha256.txt
C:\Win7POSBridge\outbox\Win7POS-build-report.md
```

## Limiti

- Non richiede admin.
- Non installa nulla.
- Non modifica business code.
- Non crea servizi Windows.
- Non modifica registry.
- Non cancella dati.
- Non contiene secret.
- Non usare dati reali del negozio nel drop o nella cartella condivisa.
