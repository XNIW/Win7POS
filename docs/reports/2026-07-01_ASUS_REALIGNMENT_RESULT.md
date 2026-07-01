# ASUS Realignment Result - 2026-07-01

## Vecchio tentativo
- Backup creato: si
- Percorso backup: `C:\Temp\win7pos-asus-old-main-attempt`
- Vecchio QA su main: NON valido

## Branch handoff
- Branch remota trovata: si
- Branch locale corrente verificata: `handoff/win7pos-asus-qa-20260701`
- Commit handoff: `caad88c`
- Report Mac presenti: si

## SDK
- SDK 10 presente: si
- Percorso SDK locale: `C:\Dev\dotnet10`
- dotnet --info:

```text
.NET SDK:
 Version:           10.0.301
 MSBuild version:   18.6.4+96856fd72
 Base Path:         C:\Dev\dotnet10\sdk\10.0.301\

Host:
  Version:      10.0.9
  Architecture: x64

.NET SDKs installed:
  10.0.301 [C:\Dev\dotnet10\sdk]

.NET runtimes installed:
  Microsoft.AspNetCore.App 10.0.9 [C:\Dev\dotnet10\shared\Microsoft.AspNetCore.App]
  Microsoft.NETCore.App 10.0.9 [C:\Dev\dotnet10\shared\Microsoft.NETCore.App]
  Microsoft.WindowsDesktop.App 10.0.9 [C:\Dev\dotnet10\shared\Microsoft.WindowsDesktop.App]
```

## Build minima
| Check | Risultato | Note |
|-------|-----------|------|
| git diff --check | PASS | Solo warning CRLF previsto da Git su working copy. |
| restore CLI | PASS | SDK 10 locale, sequenziale. |
| build Core | PASS | `Release`. |
| build Data | PASS | `Release`. |
| build CLI | PASS | `Release`. |
| restore WPF | PASS | SDK 10 locale. |
| build WPF x86 | PASS | `Release`, `Platform=x86`, `PlatformTarget=x86`. |

## Decisione
READY_FOR_ASUS_FULL_QA

## Note
- Il riallineamento e' stato fatto solo nel clone pulito `C:\Dev\Win7POS_handoffQA`.
- Non ho usato dati produzione.
- Non ho continuato su main.
- Il commit/push finale viene fatto solo su branch risultato `qa/asus-win7pos-result-20260701`.
