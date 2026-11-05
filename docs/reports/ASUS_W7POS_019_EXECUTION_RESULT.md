# ASUS_W7POS_019_EXECUTION_RESULT

Stato: **EXECUTION_IN_PROGRESS**. Questa versione registra il candidato locale e
le prove già osservate; non dichiara DONE, consegna o qualifica Windows7.
Mandato: richiesta Execution A+B+C del 2026-11-04 e successive istruzioni di
proseguire l'implementazione mentre l'operatore prepara il nuovo negozio.

## Comportamento implementato

L'importazione locale legge i due formati Getnet, presenta analisi e copertura,
richiede prove temporali/mapping/inizio uso POS e applica solo un piano verificato.
Il ledger conserva ricevute terminali, osservazioni duplicate e versioni annullate.
L'annullamento disattiva esclusivamente i movimenti posseduti dal lotto.
Riepilogo, registro, grafico firmato e CSV consumano snapshot misti con origine,
tenant, scope operatore, revisioni e policy temporale espliciti.

Le nuove capability Apply/Undo sono distinte dalle vendite ordinarie. L'autorità
del database precede inizializzazione/recovery e limita i contesti ausiliari al
percorso e ruolo consentiti. I writer nativi pubblicano revisioni/proof coerenti.
Import e undo non generano vendite, stock, cassa, documenti fiscali o outbox
commerciali. La migration0015 è additiva; le quattordici precedenti e il seed
permessi pubblicato mantengono le identità originali.

## Identità e consegna

| Identità | Stato osservato |
| --- | --- |
| Baseline locale/remota | `32618249fc01f29e86c6774d2b2c9262a90f226f`, main |
| Piano normativo R3 originale | SHA256 `47b9de5c4ced21f6deaa139717b153d8ea0d57ada761dfd3607f6bd193bfcf28` |
| Candidato qualificato / main finale | SHA di codice ancora da assegnare; working tree con prove focalizzate completate |
| Ultimo candidato focalizzato | Smoke/Load `Win7POS.QA.20261105T021708997Z.fc392d44a84e4e9888e683e0040dbe1d`, fingerprint `f38745b6b1f1c6134daf9eecfb7081af977b8477fee3448a98f40f9832870e76` invariato prima/dopo |
| Ultima Windows completa | Run03 FAIL, fingerprint `3f9a943f47bfde406d1336253223f8399c06829c4e27340bb419cc9040920fac` invariato durante la corsa |
| CI All / Security Supply Chain / Release Pack | NOT_RUN sul candidato finale |
| Installer / payload / SBOM / provenance | NOT_RUN; nessun pacchetto candidato dichiarato |
| QA net48/x86 separata | Implementata l'opzione manuale; artifact finale NOT_RUN |
| Installazione / firma / Win7 fisico | NOT_RUN; nessuna attestazione dedotta da build o download |

Il checkout e la cronologia preesistenti sono preservati. Nessun reset/stash/clean,
branch/PR aggiuntivo, force push o modifica a dati operativi/originali.

## Matrice delle prove

| Gate | Esito attuale e limiti | Evidenza / prossimo passo |
| --- | --- | --- |
| A: parser/regole/identità | FOCUSED_PASS; input ostili, precisione, due profili, respinte senza ID e fingerprint | TRX privati e registro Execution; ricontrollare il candidato finale dopo le correzioni |
| B: schema/ledger/replay/undo | FOCUSED_PASS | Integration09 185/185, NativeProof12 23/23 e cinque casi foundation focalizzati completati; candidato finale da verificare in CI |
| B: crash/autorità/backup | FOCUSED_PASS:5/5 nel run `015030831Z.b1eb4ac536a844e38725d1c9779705ba` | HOT, commit authority, admission/carrello, entrypoint e backup/restore PASS; Run03 resta4/5 FAIL |
| C: query/CSV/permit/report | FOCUSED_PASS:8/8 scenari,44/44 assert | Ultimo run focalizzato: clock coerente, scope/expiry, CSV e due letture reali pendenti attraverso epoch/logout; Run03 resta6/8 |
| Carico50k / misto59k | FOCUSED_PASS sul profilo `reference-windows`:11/11 assert | Misure complete e ambiente/soglie validi; nessuna qualifica Win7 dedotta |
| Windows canonica run03 | FAIL | Gate50/50, build, Core1692/1692 e ProductImage PASS; functional19/20, foundation4/5, smoke6/8; Load NOT_RUN |
| Supervisore/privacy/evidence | Prove AST/Runtime/proiezione PASS osservate; pubblicazione run03 PASS separata dal suo FAIL | Gli ultimi wrapper focalizzati conservano cleanup=true e zero processi; qualifica finale sullo SHA di codice ancora pendente |
| Gate finali / privacy sorgenti | Required-gates26:50/50 PASS;216 file +10 parti OOXML puliti | CI seleziona ora HistoricalSalesMode All dopo i PASS C/load; workflow sullo SHA finale ancora NOT_RUN |
| Originali: audit indipendente | PASS privato | Integrità e controlli indipendenti verificati in sola lettura; nessun dato finanziario pubblicato |
| Originali: reader prodotto net48/x86 | PASS privato sul binario run02 | Tutti gli input verificati, invariati; NeedsEvidence=true, CanApply=false; nessun DB o apply |
| P-REAL apply/reopen/reimport/undo/report | BLOCKED_EXTERNAL | Shop/mapping/inizio POS e semantica oraria autorevoli non ancora disponibili |
| Supabase | Storico locale senza nuovo trasporto/server; profilo reale BLOCKED_EXTERNAL | Applicabilità e confini nella scheda esterna; nessuna operazione live eseguita |
| Win7 SP1 e periferiche | BLOCKED_EXTERNAL | Host corrente Windows11; serve macchina fisica e pacchetto esatto del candidato |

Ambiente osservato: Windows11 Home Single Language10.0.26300 x64, SDK10.0.301
fissato, PowerShell7.6.9, Node24.18.0. Reader prodotto verificato anche nel processo
net48 a32bit. Nessun runtime PowerShell7.4 o Windows7 dichiarato eseguito.
Il Load focalizzato ha `measurementCompleted=true`, `environmentValid=true` e
`thresholdsPassed=true`, distinti dall'esito complessivo Windows ancora FAIL.
La receipt `HISTORY-MEASUREMENT-v2` usa un thread dedicato e registra:

| Misura sintetica osservata | Valore |
| --- | --- |
| Sorgente / accettati / respinti | 50000 / 49000 / 1000 righe |
| Proiezione mista | 10000 righe native +49000 storiche |
| Analisi / apply DB / query warm | 1390.464ms /7862.516ms /169.733ms |
| Dispatcher p95 / massimo | 0.464ms /16.341ms |
| Probes Dispatcher / campioni memoria | 393/393 /197/197 |
| Pending / dropped / overflow / suspended | 0 /0 /0 /0; anche memoryDropped=0 |
| Picco private bytes campionato | 86753280 byte,82.734MiB |

Smoke dura25.658s e Load21.812s nella rispettiva receipt; i wrapper riportano
27.731s e24.088s, exit0, cleanup=true e processRemaining=0. Questi risultati
focalizzati dichiarano `fullWindowsQualification=false` e non riscrivono Run03.

## Componenti e correzioni necessarie

Le modifiche interessano Core Import/Reports/Security, Data Import/Reports e
ownership/revisioni/migration, factory/backup/restore, entry point reali e sessione
operatore, workflow e dialog/riepilogo/registro/localizzazioni. I bootstrap dei
test usano fixture possedute; la pipeline QA riusa supervisore/Job/deadline
esistenti, receipt chiuse e pubblicazione verificata per singolo run.

Le correzioni riscontrate sono tracciate nel registro: aspettative migration
superate, contesti AUX mancanti nei consumer reali, ownership void/proof, attesa
WinExe, parsing JSON senza coercizione delle date, percorsi fixture troppo lunghi,
disposal del VM e scope di report per ruoli limitati. L'ultima correzione prodotto
fa attendere `SecurityRepository.LogEventAsync` su `OpenAsync`, evitando il blocco
del Dispatcher quando la manutenzione reale trattiene l'ammissione SQLite. La
regressione audit e le prove native:23/23 PASS in
`security-audit-maintenance-25.trx`; non è un nuovo full Core. Le due prove di
report pendente usano sessioni nuove valide e osservano epoch e logout reali,
senza forzare autorizzazioni o sopprimere eventi di sicurezza. Nessun limite prodotto,
timeout o autorizzazione viene ampliato per ottenere verde.

## Risorse, blocchi e ripresa

Run03 e gli ultimi wrapper focalizzati sono terminati con cleanup riuscito e zero
processi residui. I precedenti FAIL/NOT_RUN, le directory private di diagnosi, receipt parziali e asset
necessari a riprodurre FAIL/TIMEOUT sono conservati intenzionalmente. Originali,
manifest reale, trust e dati finanziari restano fuori da checkout e artifact.

Prossimo passo: assegnare lo SHA di codice e completare la qualifica finale;
Run03 resta l'ultima Windows completa ed è FAIL, distinta dai PASS focalizzati.
Consegna autorizzata su main, tre workflow sul medesimo SHA, verifica canonica
Release Pack scaricato e QA bundle separata. I prerequisiti reali e fisici non
impediscono questi rami indipendenti e non sono considerati automaticamente risolti.

La procedura operatore è nella [guida](../HISTORICAL_CARD_IMPORT.md). La
[scheda esterna](ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md) raccoglie artifact/hash,
istruzioni Win7, dati richiesti e prove da restituire. Il
[registro Execution](../plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md) conserva
comandi, run precedenti, errori primari e checkpoint di ripresa.
