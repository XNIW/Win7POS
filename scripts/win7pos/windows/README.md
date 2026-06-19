# Win7POS Windows Builder Scripts

## Quando usarli

Usa questi script nella VM Windows 10/11 Builder `WinPOS-Builder` per produrre il drop reale di Win7POS. Non usarli nella VM Windows 7 `Win7POS-Test`: Win7 deve restare ambiente runtime pulito.

## Prerequisiti

- Visual Studio o Build Tools compatibili con MSBuild.
- .NET Framework 4.8 targeting pack.
- NuGet restore tramite MSBuild.
- Inno Setup 6 opzionale, solo se vuoi generare installer.
- Repo Win7POS disponibile nella Builder VM.

Non servono password, token, licenze o config reali.

## Comandi

Dry-run:

```cmd
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -DryRun
```

Build normale:

```cmd
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1
```

Build con installer Inno Setup:

```cmd
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
```

Wrapper CMD:

```cmd
scripts\win7pos\windows\build-release-x86.cmd -DryRun
scripts\win7pos\windows\build-release-x86.cmd
scripts\win7pos\windows\build-release-x86.cmd -BuildInstaller
```

## Output atteso

Drop:

```text
dist\Win7POS
```

Eseguibile:

```text
dist\Win7POS\Win7POS.Wpf.exe
```

Report:

```text
dist\Win7POS-build-report.md
```

Installer opzionale:

```text
installer\output\Win7POS-Setup.exe
```

## Copiare il drop verso Mac

Esporta `dist\Win7POS` o uno zip equivalente verso il Mac usando cartella condivisa, share host-only o copia manuale sicura.

Non includere DB reali, log sensibili, token, licenze o `pos-admin-web.config` reale.

## Prossimo passo su Mac

Valida il drop:

```bash
scripts/win7pos/validate-drop.sh --source <drop>
```

Prepara la cartella per Win7:

```bash
scripts/win7pos/prepare-test-drop.sh --execute --source <drop>
```

## Prossimo passo su Win7

Nella VM `Win7POS-Test`, dopo aver copiato il drop in `C:\Win7POSTest\drop\Win7POS`:

```cmd
C:\Win7POSTest\run-pos-smoke.bat
```

Raccogli screenshot e `C:\Win7POSTest\data\logs\app.log`.
