# Win7POS - Windows/ASUS Missing QA Execution

## Ruolo

Tu sei CODEX ASUS, worker Windows/hardware per Win7POS. Codex Mac e orchestratore finale.
Esegui solo i test Windows/hardware/installer richiesti e restituisci output preciso.

Regole:

- Non fare commit.
- Non cancellare modifiche.
- Non introdurre refactor.
- Non inserire secret.
- Non usare dati produzione.
- Non dichiarare passato cio che non hai provato.

## Repository

- Repo: `https://github.com/XNIW/Win7POS.git`
- Branch: `audit/win7pos-full-hardening`
- Directory probabile: `C:\Users\<utente>\Projects\Win7POS`

Se la repo non esiste, clona in una directory test.

## Prima di iniziare

```powershell
git status --short
git branch --show-current
git rev-parse --short HEAD
dotnet --info
pwsh -v
systeminfo
```

Se non sei sulla branch:

```powershell
git fetch
git switch audit/win7pos-full-hardening
```

## Ambiente test

Usa solo directory test:

```cmd
set WIN7POS_DATA_DIR=C:\POSData\TestRun1
```

oppure PowerShell:

```powershell
$env:WIN7POS_DATA_DIR="C:\POSData\TestRun1"
```

Non usare database produzione, PIN/password reali, token reali o screenshot con dati sensibili. Sanitizza log.

## Da leggere prima

- `README.md`
- `AGENTS.md`
- `docs/DIALOG_STANDARD.md`
- `docs/FORMATO_LOG_DEBUG.md`
- `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md`
- `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`, se presente
- `installer/Win7POS.iss`
- `.github/workflows/release-pack.yml`

## Check automatici Windows

Esegui:

```powershell
git diff --check

dotnet restore src/Win7POS.Cli/Win7POS.Cli.csproj
dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release
dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release
dotnet build src/Win7POS.Cli/Win7POS.Cli.csproj -c Release

dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb

dotnet restore src/Win7POS.Wpf/Win7POS.Wpf.csproj
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86

pwsh -File scripts/check-dialog-standards.ps1
pwsh -File scripts/check-public-staging-config.ps1
pwsh -File scripts/check-pos-debug-logging.ps1
pwsh -File scripts/check-pos-online-client.ps1
pwsh -File scripts/check-pos-online-bootstrap.ps1
pwsh -File scripts/check-pos-first-login-sale-safe-ui.ps1
pwsh -File scripts/check-pos-start-of-day-sync.ps1
pwsh -File scripts/check-pos-catalog-pull.ps1
pwsh -File scripts/check-pos-sales-sync.ps1
pwsh -File scripts/check-win7pos-restore-guard.ps1
pwsh -File scripts/check-win7pos-legacy-db-migrations.ps1
pwsh -File scripts/check-pos-startup-win7-safe.ps1
pwsh -File scripts/check-pos-sync-status-ux.ps1
pwsh -File scripts/check-pos-shop-data-readonly.ps1
pwsh -File scripts/check-pos-revenue-copy.ps1
pwsh -File scripts/check-product-dialog-free-text.ps1
```

Nota: se incontri i nomi vecchi del prompt (`check-online-client-safety.ps1`, `check-start-of-day.ps1`, ecc.) e sono assenti, annota `script assente` e usa gli equivalenti `check-pos-*` / `check-win7pos-*` elencati sopra.

## Smoke WPF reale

1. Imposta `WIN7POS_DATA_DIR=C:\POSData\TestRun1`.
2. Avvia `Win7POS.Wpf`.
3. Verifica primo avvio con DB vuoto.
4. Crea primo admin o operatore test solo se richiesto dal flusso.
5. Esegui login operatore.
6. Crea/importa prodotto test se serve.
7. Esegui vendita cash.
8. Esegui vendita card/other se supportata.
9. Verifica che la vendita sia salvata localmente anche offline.
10. Verifica ricevuta/stampa o fallback file/PDF.
11. Verifica ristampa ultima ricevuta.
12. Verifica backup.
13. Verifica restore in directory test.
14. Verifica, se possibile, che restore venga bloccato con outbox `pending`/`retry`/`failed_blocked`.
15. Spegni rete o simula offline e verifica che il POS non blocchi la vendita.
16. Configura AdminWebBaseUrl dummy HTTPS, esempio `https://pos-api.example.invalid`, e verifica stato/messaggio senza crash.
17. Verifica che HTTP LAN non sia default release e richieda flag test esplicito.
18. Verifica dialog: centratura, focus iniziale, Escape dove previsto, nessun salto visivo, owner corretto, nested dialog se raggiungibile.
19. Verifica DPI 100%.
20. Verifica DPI 125%, se possibile.
21. Verifica 1024x768.
22. Verifica 1024x600 best-effort, se possibile.
23. Verifica multi-monitor, se possibile.
24. Verifica comportamento con stampante assente.
25. Verifica comportamento con stampante presente, se disponibile.

## Release pack fresco

Preferisci il builder script Windows:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1
```

Con installer:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
```

Oppure usa il workflow reale `.github/workflows/release-pack.yml`.
Non usare `dist/Win7POS` vecchio come prova finale se non viene rigenerato nella sessione.

Poi esegui:

```powershell
pwsh -File scripts/check-release-pack-completeness.ps1 -ReleasePackSource <PERCORSO_RELEASE_PACK_FRESCO>
```

Verifica:

- EXE presente.
- DLL dipendenze presenti.
- SQLite native x86 presente.
- `README_RUN.txt`, `RELEASE_CHECKLIST.txt`, `VERSION.txt` presenti se previsti.
- Nessun secret/config locale.
- Nessun file dev/test inutile, inclusi `*.pdb`.
- Avvio app dal release pack con `WIN7POS_DATA_DIR` test.

## Installer Inno Setup

Se Inno Setup e installato:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Win7POS.iss
```

oppure:

```cmd
iscc installer\Win7POS.iss
```

Verifica:

- Installer creato.
- Dimensione > 0.
- Installazione in ambiente test.
- Avvio app installata.
- ProgramData/test dir ok.
- Uninstall se possibile.
- Nessun dato produzione toccato.

## Output obbligatorio per Codex Mac

Crea o aggiorna:

`docs/reports/2026-07-01_ASUS_WINDOWS_QA_RESULT.md`

Template:

```markdown
# ASUS Windows QA Result - 2026-07-01

## Ambiente
- OS:
- macchina:
- dotnet --info:
- PowerShell:
- branch:
- commit:
- WIN7POS_DATA_DIR:

## Check automatici
| Comando | Risultato | Note |
|---------|-----------|------|

## Smoke WPF
| Scenario | Risultato | Evidenza | Note |
|----------|-----------|----------|------|

## Release pack fresco
- comando:
- percorso:
- risultato:
- check completeness:
- note:

## Installer
- ISCC disponibile: si/no
- comando:
- output:
- installazione test:
- avvio post-install:
- note:

## Bug trovati
| ID | Severita | Area | Passi riproduzione | Errore/log sanitizzato | File sospetto |
|----|----------|------|--------------------|------------------------|---------------|

## Cose non testate
| Area | Motivo | Prossimo passo |
|------|--------|----------------|

## Git finale
- git status --short:
```

Regole finali: non fare commit, non allegare secret, non dichiarare passato cio che non hai provato.
