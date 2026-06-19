# Win7POS Windows Builder

## Scopo

La Builder VM `WinPOS-Builder` serve a produrre un artefatto Windows reale per Win7POS. Deve buildare il progetto WPF `net48` x86, creare un drop completo e opzionalmente generare l'installer Inno Setup. Non e l'ambiente di smoke legacy: il test runtime resta nella VM `Win7POS-Test` con Windows 7 SP1 x64.

## Cosa installare

- Windows 10 o Windows 11.
- Visual Studio oppure Build Tools con supporto MSBuild per progetti SDK-style.
- .NET Framework 4.8 targeting pack.
- .NET SDK compatibile con il progetto, se usi `dotnet build` su Windows.
- NuGet restore tramite MSBuild/dotnet.
- Inno Setup 6 solo se devi produrre `installer\output\Win7POS-Setup.exe`.
- Git solo se vuoi clonare o aggiornare la repo dentro la Builder VM.

## Cosa non installare

- Codex dentro Windows 7.
- Tool di sviluppo nella VM `Win7POS-Test`.
- Runtime o driver non dedotti dalla repo.
- Secret, licenze, token o config reali nel drop.
- Supabase client o Admin Web extra: il POS usa solo la configurazione gia presente (`WIN7POS_ADMIN_WEB_BASE_URL` o `pos-admin-web.config`) se serve.

## Repo e solution

In questa repo non e stata trovata una `.sln`. Il target build verificato e:

```text
src\Win7POS.Wpf\Win7POS.Wpf.csproj
```

Il progetto WPF referenzia:

- `src\Win7POS.Core\Win7POS.Core.csproj`
- `src\Win7POS.Data\Win7POS.Data.csproj`

Target verificati:

- WPF: `net48`, `x86`, `WinExe`
- Core/Data: `netstandard2.0`

## Restore NuGet

Da Developer Command Prompt o PowerShell con MSBuild nel PATH:

```cmd
msbuild src\Win7POS.Wpf\Win7POS.Wpf.csproj /t:Restore /p:Configuration=Release /p:Platform=x86
```

Alternativa Windows, se `dotnet` e configurato con targeting pack .NET Framework:

```cmd
dotnet restore src\Win7POS.Wpf\Win7POS.Wpf.csproj
```

Non eseguire questi comandi su macOS Apple Silicon per validare il WPF `net48`.

## Build tramite script

Gli script operativi per la Builder VM sono in:

```text
scripts\win7pos\windows\
```

Dry-run:

```cmd
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -DryRun
```

Build normale:

```cmd
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1
```

Build con installer, se Inno Setup e disponibile:

```cmd
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
```

Wrapper CMD:

```cmd
scripts\win7pos\windows\build-release-x86.cmd -DryRun
scripts\win7pos\windows\build-release-x86.cmd
scripts\win7pos\windows\build-release-x86.cmd -BuildInstaller
```

Lo script cerca MSBuild in questo ordine:

1. variabile ambiente `MSBUILD_EXE`;
2. `vswhere.exe`;
3. `where msbuild`.

Se MSBuild non viene trovato, apri Developer Command Prompt for VS oppure installa Visual Studio Build Tools con .NET Framework 4.8 targeting pack.

## Bridge Builder via cartella condivisa

Se Codex resta sul Mac e non puo controllare direttamente la VM Windows, usa il bridge documentato in:

```text
docs/dev/win7pos-vm-control-bridge.md
scripts/win7pos/windows/bridge/
```

Il bridge non e un command runner libero: accetta solo job allowlistati e scrive output in una cartella condivisa.

Avvio bridge nella Builder VM, dalla root repo Win7POS dentro `WinPOS-Builder`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\bridge\start-builder-bridge.ps1 -BridgeRoot C:\Win7POSBridge -Watch
```

Per elaborare un solo job:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\bridge\start-builder-bridge.ps1 -BridgeRoot C:\Win7POSBridge -Once
```

Dal Mac, puntando alla stessa cartella condivisa vista come bridge:

```bash
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job env-report --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-dry-run --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-release --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job package-drop --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job screenshot --execute
```

Output atteso:

```text
C:\Win7POSBridge\outbox\<timestamp>-<job>.log
C:\Win7POSBridge\screenshots\builder-<timestamp>.png
dist\Win7POS-drop.zip
dist\Win7POS-drop.sha256.txt
```

Se non esistono hypervisor o VM accessibili dal Mac, eseguire prima:

```bash
scripts/win7pos/vm/discover-vm-host.sh
```

e configurare manualmente la VM prima di dichiarare la build controllabile.

## Build Release x86

Comando MSBuild consigliato:

```cmd
msbuild src\Win7POS.Wpf\Win7POS.Wpf.csproj /t:Build /p:Configuration=Release /p:Platform=x86 /p:PlatformTarget=x86
```

Comando documentato nel README, da usare sulla Builder VM Windows:

```cmd
dotnet build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86
```

Output atteso:

```text
src\Win7POS.Wpf\bin\Release\net48\
```

Verifica minima:

```cmd
dir src\Win7POS.Wpf\bin\Release\net48\Win7POS.Wpf.exe
```

## Creare `dist\Win7POS`

La repo contiene un installer Inno Setup che si aspetta:

```text
dist\Win7POS
```

Crea il drop copiando l'output completo. Esempio da PowerShell nella root repo:

```powershell
$out = "src\Win7POS.Wpf\bin\Release\net48"
$dist = "dist\Win7POS"
New-Item -ItemType Directory -Force $dist | Out-Null
robocopy $out $dist /E
if ($LASTEXITCODE -le 3) { exit 0 } else { exit $LASTEXITCODE }
```

Non usare opzioni mirror/delete per questa fase. Il drop deve contenere `Win7POS.Wpf.exe` e le DLL richieste.

## Controllare contenuto drop

Verifica eseguibile:

```cmd
dir dist\Win7POS\Win7POS.Wpf.exe
```

Verifica DLL progetto:

```cmd
dir dist\Win7POS\Win7POS.Core.dll
dir dist\Win7POS\Win7POS.Data.dll
```

Verifica dipendenze note da csproj/NuGet:

```cmd
dir dist\Win7POS\Microsoft.Data.Sqlite*.dll
dir dist\Win7POS\SQLitePCLRaw*.dll
dir dist\Win7POS\Dapper*.dll
dir dist\Win7POS\PDFsharp*.dll
dir dist\Win7POS\ZXing*.dll
```

Verifica asset:

```cmd
dir dist\Win7POS\Assets\sii_qrcode.png
```

Verifica native SQLite se presente nel formato prodotto dalla build:

```cmd
dir /s dist\Win7POS\e_sqlite3.dll
dir /s dist\Win7POS\SQLite.Interop.dll
```

Se i file nativi non compaiono, annotalo nella checklist e verifica sul test Win7: la dipendenza `SQLitePCLRaw.bundle_e_sqlite3` puo produrre layout diversi a seconda del restore/build.

## Installer Inno Setup

Solo se serve un installer:

```cmd
iscc installer\Win7POS.iss
```

Output atteso:

```text
installer\output\Win7POS-Setup.exe
```

L'installer verifica .NET Framework 4.8 e mostra un avviso se non rileva Microsoft Visual C++ Runtime x86. Non installa automaticamente runtime e non deve cancellare `%ProgramData%\Win7POS`.

## Interpretare `Win7POS-build-report.md`

Lo script genera:

```text
dist\Win7POS-build-report.md
```

Controlla:

- branch/commit buildati;
- path/versione MSBuild;
- `Configuration=Release`;
- `Platform=x86`;
- output path;
- `Exe present: True`;
- installer generato, se richiesto;
- warning su DLL native SQLite o file mancanti.

Se `Exe present` e `False`, il drop non e pronto per Mac/Win7.

## Esportare il drop verso Mac

Opzioni sicure:

- Zip di `dist\Win7POS`.
- Cartella condivisa UTM.
- Share host-only.
- Copia manuale su supporto locale di test.

Non includere `C:\ProgramData\Win7POS`, DB reali, log con dati sensibili, token, licenze o file `pos-admin-web.config` reali.

Sul Mac, valida il drop:

```bash
scripts/win7pos/validate-drop.sh --source <drop>
```

Poi prepara la cartella test Win7:

```bash
scripts/win7pos/prepare-test-drop.sh --execute --source <drop>
```

## Passaggio al test Windows 7

Quando il drop e in `.win7pos-vm/drop/Win7POS`, passa alla VM `Win7POS-Test`:

1. ripristina manualmente lo snapshot baseline se serve;
2. verifica cartella condivisa;
3. copia o monta il drop come `C:\Win7POSTest\drop\Win7POS`;
4. esegui `C:\Win7POSTest\run-pos-smoke.bat`;
5. salva screenshot e log;
6. compila `docs/dev/templates/win7pos-smoke-report-template.md`.

## Troubleshooting

### Build script fallisce

Controlla `dist\Win7POS-build-report.md`, se generato, e verifica:

- stai eseguendo su Windows 10/11 Builder;
- MSBuild e nel PATH o `MSBUILD_EXE` punta a `MSBuild.exe`;
- .NET Framework 4.8 targeting pack e installato;
- restore NuGet non e bloccato;
- `Platform=x86` e `Configuration=Release`.

Non provare a correggere copiando DLL a mano senza sapere da quale restore/build NuGet arrivano.

### Targeting pack mancante

Sintomo: MSBuild non trova reference assemblies per `.NETFramework,Version=v4.8`.

Azione: installa .NET Framework 4.8 targeting pack nella Builder VM, poi riesegui restore/build.

### Build x64 invece di x86

Sintomo: output o native DLL non coerenti con x86.

Azione: riesegui build con:

```cmd
msbuild src\Win7POS.Wpf\Win7POS.Wpf.csproj /t:Build /p:Configuration=Release /p:Platform=x86 /p:PlatformTarget=x86
```

### DLL SQLite native mancanti

Sintomo: l'app parte ma fallisce quando inizializza DB/SQLite, oppure log con errore su native provider.

Azione: controlla `e_sqlite3.dll`, `SQLite.Interop.dll` o cartelle `runtimes\win-x86\native` nel drop. Non inventare DLL: usa quelle prodotte da restore/build NuGet.

### Config mancante

Il POS non richiede una config Admin Web per funzionare offline/localmente. Se si testa Admin Web, configurare solo in data dir test:

```text
C:\Win7POSTest\data\pos-admin-web.config
```

oppure variabile:

```cmd
set WIN7POS_ADMIN_WEB_BASE_URL=<url-test>
```

Non mettere config reali nel drop.

### App parte su Win10/11 ma non su Win7

Cause probabili:

- .NET Framework 4.8 runtime mancante su Win7.
- VC++ Runtime x86 mancante.
- DLL native non copiate nel drop.
- Dipendenza NuGet non compatibile con Win7.
- Differenza driver/video/stampante.

Azione: raccogli `C:\Win7POSTest\data\logs\app.log`, screenshot dell'errore e contenuto drop, poi compila il report smoke.

### .NET 4.8 runtime mancante su Win7

Sintomo: Windows non apre l'app o segnala framework richiesto.

Azione: installa .NET Framework 4.8 runtime nella VM Win7 baseline, poi crea nuovo snapshot baseline pulito.
