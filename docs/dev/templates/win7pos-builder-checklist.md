# Win7POS Builder Checklist

## Identificazione Build

- Data build:
- Branch:
- Commit:
- Operatore:
- Repo path nella Builder VM:

## Builder VM

- VM name: `WinPOS-Builder`
- Windows version:
- Visual Studio / Build Tools version:
- MSBuild path:
- .NET Framework 4.8 targeting pack presente:
- NuGet restore disponibile:
- Inno Setup installato:

## Configurazione Build

- Project target:
- Configuration: `Release`
- Platform: `x86`
- PlatformTarget: `x86`
- Restore eseguito:
- Build eseguita:
- Comando restore:
- Comando build:

## Output

- Output path: `src\Win7POS.Wpf\bin\Release\net48\`
- `Win7POS.Wpf.exe` presente:
- `Win7POS.Core.dll` presente:
- `Win7POS.Data.dll` presente:
- `.config` presente:
- `Assets\sii_qrcode.png` presente:
- DLL principali presenti:
- DLL SQLite/native assets presenti:
- File mancanti osservati:

## Dist / Installer

- `dist\Win7POS` creato:
- Comando copia usato:
- Drop esportato:
- Zip/drop path:
- Installer generato:
- Installer path:
- Hash/checksum opzionale:

## Esito

- Build result:
- Errori:
- Warning rilevanti:
- Note:
- Pronto per validazione Mac:
- Pronto per Win7 smoke:
