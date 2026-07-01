# Mac Final ASUS Review and Main Merge - 2026-07-01

## Scope

- Source Mac branch: `audit/win7pos-full-hardening`
- ASUS QA branch: `qa/asus-win7pos-result-20260701` / `63cdaaa`
- ASUS hardening branch: `qa/asus-printer-cashdrawer-hardening-20260701` / `8ba8a25`
- Integration branch: `integration/win7pos-asus-final-review-20260701`
- Integration merge commit: `63501e0`
- Main base used for integration: `origin/main` updated to `8c94275`
- Data produzione: non usati.
- Secret: nessun secret inserito.

## Review ASUS

| Area | Esito | Evidenza |
|------|-------|----------|
| Branch ancestry | PASS | `qa/asus-printer-cashdrawer-hardening-20260701` contiene `qa/asus-win7pos-result-20260701`. |
| ASUS realignment | PASS | ASUS ha abbandonato il vecchio QA su `main`, installato SDK `.NET 10.0.301` e lavorato su handoff allineato. |
| ASUS Windows build | PASS | Core/Data/CLI/WPF x86 Release PASS su Windows. |
| ASUS WPF smoke | PASS_WITH_LIMITATIONS | Avvio, login operatore dummy, vendita cash, vendita card, backup, offline sale e restore guard PASS dopo fix. |
| ASUS release pack | PASS | `dist\Win7POS` completo, `Win7POS.Wpf.exe` e `e_sqlite3.dll` presenti, `*.pdb` e sorgenti assenti. |
| ASUS installer Inno | PASS | `installer\output\Win7POS-Setup.exe` generato con Inno Setup. |
| Printer/cash drawer software | PASS | Vendita salvata prima di stampa/cassetto; no fallback automatico a default Windows/PDF; cassetto disabled di default. |

## Conflitto risolto

- File: `src/Win7POS.Wpf/Localization/PosLocalization.cs`
- Scelta: mantenuto il catalogo traduzioni lazy ASUS tramite `Lazy<Dictionary<...>>` e `TranslationCatalog.Value`.
- Motivo: evita la costruzione prematura che aveva causato crash startup WPF su ASUS, senza reintrodurre static constructor fragile.

## Fix applicati durante review Mac

| File | Tipo | Motivo | Check |
|------|------|--------|-------|
| `scripts/check-pos-startup-win7-safe.ps1` | P2 script fragile | Il check accettava solo la vecchia forma con static constructor e falliva sul comportamento corretto lazy. | `pwsh -File scripts/check-pos-startup-win7-safe.ps1` -> ALL PASS |

## Check Mac integration

| Check | Risultato |
|-------|-----------|
| `git diff --check` | PASS |
| `dotnet restore src/Win7POS.Cli/Win7POS.Cli.csproj` | PASS |
| `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` | PASS, 0 warning/errori |
| `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` | PASS, 0 warning/errori |
| `dotnet build src/Win7POS.Cli/Win7POS.Cli.csproj -c Release` | PASS, 0 warning/errori |
| `WIN7POS_DATA_DIR=/tmp/win7pos-final-merge-selftest dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` | PASS, `自检 PASS` |
| `dotnet restore src/Win7POS.Wpf/Win7POS.Wpf.csproj` | PASS |
| `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | PASS, 0 warning/errori |
| `pwsh -File scripts/check-dialog-standards.ps1` | ALL PASS |
| `pwsh -File scripts/check-public-staging-config.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-debug-logging.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-online-client.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-online-bootstrap.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-first-login-sale-safe-ui.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-start-of-day-sync.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-catalog-pull.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-sales-sync.ps1` | ALL PASS |
| `pwsh -File scripts/check-win7pos-restore-guard.ps1` | ALL PASS |
| `pwsh -File scripts/check-win7pos-legacy-db-migrations.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-startup-win7-safe.ps1` | ALL PASS dopo fix script |
| `pwsh -File scripts/check-win7pos-startup-no-eager-db.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-online-linking-task084b.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-sync-status-ux.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-shop-data-readonly.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-revenue-copy.ps1` | ALL PASS |
| `pwsh -File scripts/check-product-dialog-free-text.ps1` | ALL PASS |
| `pwsh -File scripts/check-pos-printer-cashdrawer-safety.ps1` | ALL PASS |

## Security e artifact

- Secret scan integration: PASS con falsi positivi documentali/checker soltanto (`service_role`, `token`, `password`, flag LAN insecure citati come pattern vietati).
- Nessun `sk-*`, private key, JWT reale, bearer token reale o service role secret trovato.
- Artifact scan tracciati: PASS, nessun `dist/`, `installer/output/`, `bin/`, `obj/`, `*.zip`, `*.exe`, `*.db`, `*.log` tracciato per errore.

## Limiti non bloccanti

- Stampante POS fisica non disponibile su ASUS: non dichiarata PASS.
- Cassetto fisico non disponibile su ASUS: non dichiarato PASS.
- Windows 7 fisico non disponibile: test effettuati su Windows host ASUS, non su macchina Win7 reale.
- Multi-monitor/DPI hardware non completo: ASUS ha verificato DPI del monitor corrente; non multi-monitor reale.
- Pagamento `other` non esposto dalla UI vista su ASUS; non e' stato inventato un flusso contabile nuovo.

## Decisione

READY_FOR_MAIN_MERGE

Motivo: non restano P0/P1 software aperti; build, selftest, static checks, review printer/cash drawer, secret scan e artifact scan sono PASS. I limiti residui sono hardware/ambiente e sono documentati senza dichiarare PASS non eseguiti.
