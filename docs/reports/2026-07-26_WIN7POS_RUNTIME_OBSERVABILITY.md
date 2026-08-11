# Runtime Observability Win7POS — 2026-07-26

## Problema e soluzione

Nel bootstrap reale del catalogo un HTTP 500 `db_failure` completava il login ma
lasciava al personale un'indicazione insufficiente per distinguere un problema di
rete, server, contratto o persistenza locale. Questa patch aggiunge una
diagnostica runtime strutturata e limitata per login/catalog pull: fase, codice,
HTTP status, retryabilita, correlation ID sanitizzati, stato sale-safe e azione
consigliata. L'interfaccia mostra un messaggio semplice e un pannello tecnico
chiuso di default, copiabile e senza payload o segreti.

## File principali

- `src/Win7POS.Core/Online/PosRuntimeDiagnostic.cs` e contratti online;
- `src/Win7POS.Data/Online/PosAdminWebClient.cs` per la classificazione HTTP e
  degli ID di correlazione;
- servizi WPF di bootstrap, catalog pull, supervisor e status reader;
- `PosOnlineFirstLoginDialog` e localizzazioni raggiungibili;
- test Core e harness WPF locale.

## Classificazione e sicurezza

- `401/403` restano auth denied e non diventano fallback offline impropri;
- `500`/`db_failure` e' `server_response`/catalog unavailable;
- timeout e rete restano retryabili; JSON non valido e' `invalid_response`;
- operation, stage, code, content type, exception e correlation ID sono bounded
  e sanitizzati; non vengono conservati o copiati il corpo della risposta HTTP,
  credenziali, cookie, password, PIN o token;
- una failure di catalogo mantiene `sale-safe=false`; una failure nel cleanup
  diagnostico e' best-effort e non puo' rendere il catalogo sale-safe;
- dopo una riuscita il dettaglio diagnostico precedente viene ripulito e il
  retry resta single-flight/protetto da busy.

## Verifiche locali

| Verifica | Esito |
| --- | --- |
| Focus diagnostica/transport HTTP | PASS, 16/16 |
| Suite Core/Data | PASS, 642/642 |
| Gate canonici | PASS, 44/44 |
| WPF Release `net48/x86` | PASS, 0 warning/errori |
| UiSmokeHarness `x86` | PASS, 0 warning/errori |
| Solution Release | PASS, 0 warning/errori |
| `git diff --check` e scan secret/body sul diff staged | PASS, 0 match |

Lo smoke visuale usa solo un endpoint loopback che restituisce HTTP 500,
`db_failure`, `X-Request-Id` e `CF-Ray` sintetici. A 1024x768 ha verificato
messaggio fail-closed, pannello tecnico chiuso/aperto, copia sicura e retry
disabilitato durante l'azione. Le evidence esterne al repository sono in
`C:\Dev\_codex-evidence\win7pos-observability-20260726\final-review`.

## Tracciabilita e limiti esterni

- Base SHA: `24d6e0d5f82b5c32e48b42d31333459dbc7d4c6b`.
- Feature SHA: `1c64baac673afbcfa4be21ffbd14df03742ce3c2`.
- PR: [#42](https://github.com/XNIW/Win7POS/pull/42), contro `main`.
- CI: i workflow richiesti vengono verificati sulla SHA esatta della testa PR
  prima del merge; il risultato finale e' riportato nella PR e nel handoff di
  consegna.
- Windows 7 fisico: `EXTERNAL_PENDING`; questo smoke e' WPF locale su host
  Windows, non una certificazione su hardware/OS Windows 7 fisico.
- Nessuna modifica e' stata fatta a Admin Web, Supabase o staging.
