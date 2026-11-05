# ASUS_W7POS_019_EXECUTION_RESULT

Stato: **SOFTWARE_DELIVERED; BLOCKED_EXTERNAL per qualifica reale e Win7**. Le prove sono attribuite al relativo SHA;
i gate esterni ancora non eseguiti restano espliciti. Nessun esito di qualifica reale
o fisica viene dedotto dalle prove sintetiche.
Mandato: Execution A+B+C del 2026-11-04 e successive istruzioni di proseguire
mentre l'operatore prepara il nuovo negozio. Shop, tempo Getnet e Win7 fisico
restano **BLOCKED_EXTERNAL**; nessun DONE integrale è dedotto dai sintetici.

## Candidato e consegna

| Identità / gate | Risultato osservato |
| --- | --- |
| Baseline preservata | `32618249fc01f29e86c6774d2b2c9262a90f226f`, main |
| Piano normativo R3 originale | SHA256 `47b9de5c4ced21f6deaa139717b153d8ea0d57ada761dfd3607f6bd193bfcf28` |
| Candidato finale codice | [`5c3327bd320293ad48edc7d9cc01ad2466e591b4`](https://github.com/XNIW/Win7POS/commit/5c3327bd320293ad48edc7d9cc01ad2466e591b4), pubblicato su main senza force push |
| Fingerprint checkout pulito | `72d047e1665b2dce5308a7d5f68540e5f603cef2da74e8ba090a8e4388e251e9`; dirty=false,0 sorgenti untracked, digest diff/untracked vuoti |
| Commit finale documentazione | Il commit che contiene questo report; SHA esatto nel messaggio di consegna. Delta rispetto a5c3327b esclusivamente documentale, senza rietichettare prove di altri commit |
| CI All | [41959769748](https://github.com/XNIW/Win7POS/actions/runs/41959769748): `PASS`; head SHA `5c3327bd320293ad48edc7d9cc01ad2466e591b4`; Core/assert/runner/cleanup `runWin7POS.QA.20261105T031014179Z.733127f7e27545a6aac98da382290591;15/15 passi; Core gatePASS1051290ms, functional20/20, foundation5scenari/37assert, smoke8/44, Load1/11; cleanup verificato dal supervisore canonico` |
| Security Supply Chain | [41959769752](https://github.com/XNIW/Win7POS/actions/runs/41959769752): `PASS, head5c3327bd320293ad48edc7d9cc01ad2466e591b4, CodeQL+dipendenze/segreti/SBOM/riproducibilità` |
| Release Pack | [41959769763](https://github.com/XNIW/Win7POS/actions/runs/41959769763): `PASS, head5c3327bd320293ad48edc7d9cc01ad2466e591b4; Core1715/1715,0skipped,38m01; gate50/50` |
| QA net48/x86 separata | [WPF41959770648](https://github.com/XNIW/Win7POS/actions/runs/41959770648) **PASS** su5c3327b; artifact13385071104, opzione `historical_qa_payload=true` |
| Download Release / installer | `PASS via scripts/win7pos/windows/test-downloaded-release-pack.ps1; 1.0.0-dev.5c3327bd3202; artifact13386956585, archiveSHA256852e099c98d7e898b1396181e30010659917b2298f60e1bf2d04b207cc1cf2a4; payloadZIP c5dbaa380378d382f5c6a29178e081d444a3c9f57a1864cec8af3e80e53f558c`; installer `f0897aab5a9d33ecdc6a411d1c367c81a8885268d20deca5324e08dc5683394d` |
| SBOM / provenance / closure | `PASS development-unsigned,110 file checksummed e41 file payload; dist/ZIP/setup correlati, SBOM/provenance/attestation verificati,0 signing phases;33/33 file condivisi QA/prodotto byte-identici (incluse Core/Data/WPF);0 harness/XLSX/DB/TRX nel payload operativo` |
| Download QA / manifest / fixture / exe | Archive GitHub `000d2c3bd4faec27a1580d5ea55e9be920ea1c78f6f14ff784438131a376c13e`; QA ZIP `15d7a8b0c5aeeee9273ce7deda65685bd88a96d4ed794f0db9c011c5c3d81ff8`; manifest `8c00ffe12e477b3cd449403dad9c433b0f29e8fb1a55aaf525e0e8af7fbe2233`; fixture `72cd6f0afc82e418d5676fc329f61761d224e8aade30da98ada4ad30ee64620e`; exe harness `88e9d154a570164016c30e5414e615cfa48776a58b94b58af7adcdfad400bc16`, WPF `d4da1e27c2b2359c1242db1c4e16be3344357f4bc6322dc1b2eb88d09adfc092` |
| Smoke QA scaricata da CWD esterna | PASS, run `Win7POS.QA.20261105T031330057Z.3557fff79f954e9cbe82fa4a1eed9907`,8/8 scenari+44/44 assert; source/fingerprint puliti invariati prima/dopo, context/hash corrispondenti, cleanup=true e processRemaining=0 |
| Artifact pubblici CI corrente | `PASS, artifact13386211267, archiveSHA25682021767d315afdff947598c64da19535e67c1d2d15045f4e6d68c8c5d8581f3`; nomi esatti `qa-public.zip` e `upload-manifest.json`, digest e identità del singolo run verificati |
| Firma / installazione / Win7 fisico | `development-unsigned; installazione NOT_EXECUTED; qualifica fisica NOT_RUN`; Win7 SP1/periferiche **BLOCKED_EXTERNAL**, nessuna prova Windows11 li sostituisce |

Una conclusione workflow o exit0 non sostituisce la verifica delle receipt e dei
file scaricati. Head SHA, manifest, fingerprint del contesto e hash dei binari
devono appartenere al candidato dichiarato. Un successivo commit documentale
va indicato separatamente. Il checkout e la cronologia sono preservati: nessun
reset/stash/clean, branch/PR aggiuntivo, force push o modifica agli originali/dati operativi.

## Comportamento e correzioni implementati

L'importazione legge i due formati Getnet in un ledger locale separato, presenta
analisi/copertura, richiede prova temporale, mapping esercente e inizio POS;
apply e undo conservano receipt terminali e audit. Il lotto possiede soltanto i
movimenti nuovi: duplicati osservati non diventano proprietà del reimport.
Import/undo non creano vendite native, stock, cassa, documenti fiscali o outbox
commerciali. Migration0015 è additiva; identità delle14 precedenti e seed permessi
pubblicato restano preservati.

Riepilogo, registro, dettaglio, grafico firmato e CSV condividono scope e fotografia
mista. Senza `register.view_all` la lettura è personale; operatore storico ignoto
è escluso. La media scontrino considera soltanto i POS positivi. La proiezione
nativa usa prove autorevoli, non il negozio corrente come attribuzione implicita.
Policy temporali canoniche versionate per negozio non usano il fuso PC come fallback.
Revoca, scadenza, cambio autorità e restore invalidano letture/cursori. Il controllo
dei token sul Dispatcher legge stamp immutabili e stato in memoria. Il permit CSV viene
linearizzato al latch finale; rename/replace e controllo dei byte approvati sono
fuori da DB/monitor. Una pubblicazione già autorizzata non promette il dato più
recente al momento in cui diventa visibile.

La correzione del primo candidato riusa cinque comandi SQLite tipizzati nella
stessa transazione, preserva lookup per riga e visibilità intra-batch, aggiorna
used_at al primo inserimento per binding e mantiene una cache locale di mapping
limitata a200. Non modifica somme, null, regole dedup/correzione, guard, receipt o
soglie. Il test alias esercita anche una copia NTFS posseduta con alias8.3 reale,
stessa identità fisica, rifiuto `database_in_use` e rilascio owner; nessuna modifica
globale del volume e nessuna asserzione saltata.

## Evidence osservate prima della CI finale

Le chiavi seguenti sono checkpoint attribuiti al loro sorgente, non nuovi full
Core e non PASS anticipati di5c3327b. I path privati sono sotto
`C:\Dev\_codex-private\ASUS-W7POS-019`; la cronologia completa è nel registro.

| Chiave | Prova osservata |
| --- | --- |
| CORE03 | Windows run03 `Win7POS.QA.20261105T010900248Z.ea3306d57ec841c3bd06c6da4bcdc838`: Core1692/1692 PASS,0 skipped. Run03 complessivo **FAIL**: functional19/20, foundation4/5, smoke6/8, Load NOT_RUN. Non rietichettato dopo correzioni successive |
| REG27 | `tests/prepared-apply-regression-27.trx`:113/113 PASS,0 skipped,38s; Historical/Getnet, autorità DB, audit-maintenance e prove native. Non sommare a CORE03 come nuovo totale full Core |
| B28 / C28 / L28 | Focused `Win7POS.QA.20261105T030459605Z.e0d33b992cf94cf192cc895c56917d80`: foundation5/5, Smoke8/8+44 assert, Load11/11 PASS; fingerprint `842ca111714aecfb5928d3929293b17644c6e134a1b6dac79efc7427cba2d428` identico prima/dopo; tutti exit0/cleanup=true/residui0. `fullWindowsQualification=false` |
| BUILD28 | `tests/prepared-apply-harness-28.log`: Release net48/x86 PASS,0 warning/error |
| G29 | `tests/required-gates-prepared-29.log`:50/50 PASS dopo correzioni apply/alias, inclusi controlli protocolli/measurement/privacy e regressioni dei guard |
| PRIVACY | Inventario precedente216 file eleggibili+10 parti OOXML puliti; inventario esatto finale `PASS:216 file distinti del delta baseline→5c3327b, incluse le6 versioni documentali finali (222 versioni di contenuto verificate), e10 parti OOXML dei2 XLSX sintetici; zero finding privacy azionabili, nessun corpus/controllo privato/profilo utente/credenziale reale/DB/trust/raw QA incluso`. Raw/DB/dump/originali/trust non entrano negli artifact pubblici |

### Ultimo carico sintetico qualificato, profilo reference-windows

| Misura L28 | Valore osservato |
| --- | --- |
| Sorgente / accettati / respinti | 50000 /49000 /1000 righe |
| Popolazione report mista | 10000 native+49000 storiche; saldo56800000 CLP;9000 scontrini POS/media1000 CLP, oracolo sintetico congelato |
| Analisi / DB apply / servizio completo | 1450.2855 /2184.1727 /2393.6904ms |
| Backup / ownership cold / query warm | 175.4882 /4384.695 /177.6638ms |
| Dispatcher p95 / massimo | 0.4894 /21.6212ms |
| Campioni Dispatcher / memoria | 280/280 /140/140; pending/drop/memoryDropped/overflow/suspend0 |
| Picco private bytes | 86450176 byte,82.445MiB; crescita `50384896 byte,48.051MiB` |
| Wrapper B / Smoke / Load | 24142 /26986 /18114ms; cleanup riuscito/residui0 |
| Tre flag indipendenti | measurementCompleted=true /environmentValid=true /thresholdsPassed=true |

Soglie invariate: analisi30000ms, apply15000ms, query warm500ms sul reference;
Dispatcher p95≤100/max≤500ms, picco≤700MiB e crescita≤150MiB. Profilo Win7 e budget
relativi richiedono host fisico6.1 e contesto hardware qualificato, non la sola
stringa OS nella receipt. Queste misure locali non sostituiscono Load CI finale:
`run031014179Z.733127f7e27545a6aac98da382290591; measurementCompleted/environmentValid/thresholdsPassed=true. Analisi2537.2931ms, apply3783.8476ms, servizio4031.5146ms, backup192.5817ms, ownership cold6928.9462ms, warm365.9836ms. Dispatcher p95 .2572ms/max9.8629ms;506/506 campioni Dispatcher e253/253 memoria, zero drop/pending/overflow/suspend; picco89022464byte/baseline35958784byte`. Ambiente osservato: Windows11 Home
Single Language10.0.26300 x64, SDK10.0.301, PowerShell7.6.9, Node24.18.0; prodotto e
harness net48/x86. Nessuna esecuzione fisica Windows7 o runtime PowerShell7.4 è dedotta.

## Risultati precedenti conservati

| Checkpoint / candidato | Esito storico e limite di attribuzione |
| --- | --- |
| Run01/Run02/Run03 e gruppi intermedi | FAIL/TIMEOUT/NOT_RUN e diagnostici conservati, senza retry live automatici o promozione retroattiva. Run03 resta l'ultima Windows locale completa e rimane FAIL; focused e CI sono prove distinte |
| Primo main `77427d1f943c271e040dd5a67f58c34a3d2b83c1` | Fingerprint pulito `3100112310734093d2e5137be1993171473e09342081fdd2c3c9a79054c7f1a4`; sostituito dal candidato5c3327b |
| [CI41955182242](https://github.com/XNIW/Win7POS/actions/runs/41955182242) | **FAIL**: alias corto non provato sul prepared path; backup case NOT_RUN; Load apply18014.0284ms>15000ms, pur con campionamento completo e ambiente valido. Core gate/functional20/20/Smoke8/8 osservati non promuovono il risultato complessivo |
| [Release41955182205](https://github.com/XNIW/Win7POS/actions/runs/41955182205) | **CANCELLED** come superseded dopo FAIL CI; package incompleto. I log recuperati attestano separatamente Core step **SUCCESS**,1715/1715 PASS,0 skipped,29m51s, concluso03:00:46 UTC. Non promuove overall Release e non appartiene al candidato5c3327b |
| [Security41955182236](https://github.com/XNIW/Win7POS/actions/runs/41955182236) e [WPF41955181554](https://github.com/XNIW/Win7POS/actions/runs/41955181554) | PASS soltanto per77427d1. Vecchio QA bundle39 file e Smoke8/8+44 da CWD esterna (run022928230Z.6d6b5892cd06491193d683655047681e) non qualificano il nuovo artifact5c3327b |

## Matrice R3 — requisito, prova e attribuzione

Le sigle B28/C28/L28 designano lo stesso ultimo gruppo focalizzato. CORE03 resta
una prova del checkpoint precedente; REG27 e G29 coprono le successive correzioni.
Nella colonna finale i placeholder richiedono osservazione effettiva del candidato.

| R3 requirement | Actual test / exact harness evidence | Observed attribution |
| --- | --- | --- |
| A-D01 profiles/header | `Import/GetnetImportTests.cs`, distributed fixed-oracle/profile/header tests; `HIST01_PROFILES_PREVIEW` profiles_detected/header_mapping | CORE03 + C28 |
| A-D02 exact values/IDs | `HistoricalSalesValueRulesTests`, `HistoricalSalesFingerprintTests`, `GetnetImportTests`; HIST01 exact_values/text_ids | CORE03 + C28 |
| A-D03 states/signs/coverage | Getnet rejected-row, signed-sale/cancellation and unsettled-net coverage tests; HIST01 exact_values/invalid_input_blocked | CORE03 + C28 |
| A-D04 time | Getnet unresolved/explicit interpretation tests, ValueRules time rules; `HistoricalSalesReportContractsTests` gap/fold/fractional bounds; HIST01 policy_visible | CORE03 + C28; real provider meaning remains external |
| A-D05 bounds/file safety | Getnet hostile OOXML/archive/XML/formula/link/cancellation tests; HIST01 invalid_input_blocked | CORE03 + C28 |
| B-D01 schema/mapping | `Data/MigrationRunnerTests.cs`, `Persistence/HistoricalLedgerTests.cs`; additive0015 and mapping constraints; B backup mapping_fk_preserved | CORE03 + B28 |
| B-D02 dedup/conflict | HistoricalLedger namespace/IDs, active/inactive economics and approval cases; HIST03 unchanged/renamed/reordered/overlap/distinct/conflict | CORE03 + C28 |
| B-D03 commit authorization | `HIST_B_COMMIT_AUTHORITY`: before-insert/precommit Apply revocation, Undo precommit revocation, expiry, operator/permission and linearized-once | B28; Core FinalAuthorityRevocation also CORE03 |
| B-D04 tenant/source proof | `Reports/NativeSalesOwnershipProofTests.cs`: real N1 cold refresh, N2/N3, NX invalid sources before first refresh, mixed read-surface exclusion, supported native void proof/revision | REG27 (native/audit); HIST07 tenant_scope supplementary. No arbitrary external tamper-detection claim |
| B-D05 / B-D12 process/entry/admission | `HIST_B_PROCESS_ADMISSION` real overlapped holder/contender, bounded SQLite busy, auth/workflow/maintenance cancellation, lock order and actual nonempty cart/commands; `HIST_B_ENTRYPOINT_AUTHORITY` actual WPF normal/safe, CLI/harness, different DBs, case/8.3 aliases, hardlink/junction, restart | B28, all closed subcases; private per-variant native/identity/refusal codes retained |
| B-D06 cancellation/fault | HistoricalLedger source mutation/protected handles/revocation/admission cancellation; HIST05 cancel, precommit fault, backup-IO fault | CORE03 + C28; failpoints are not the genuine crash proof |
| B-D07 genuine journal crash | `HIST_CRASH_HOT_JOURNAL`: same existing DB, nonce/sentinel, spill, expected abrupt79, technical RW recovery without seed/init, data+revision/operational invariants/cleanup | B28 |
| B-D08 terminal replay | HistoricalLedger DuplicateObserverOwnsNothingAndReplayNeedsNoSourceOrBackup, ReconstructedRequestReplaysAcrossRevisionAndRestartWithoutSource, ReadOnlyIdentityReplayNeedsNoMutationGuardAndRejectsWrongShopOrFingerprint, immutable terminal receipt | CORE03; HIST05 lost_response_reconciled supplementary |
| B-D09 undo/reimport | HistoricalLedger ThreeCyclesKeepAuditAndUseNewInternalIds and duplicate observer; HIST06 owner-only/observer/cycles/audit | CORE03 + C28 |
| B-D10 / B-D13 backup/restore/AUX | `HIST_B_BACKUP_RESTORE` real verified backup/publish, wrong AUX scope, real swap/rebind, exact injected post-swap rollback+identity restoration, epoch-stale plan, mapping/FK, zero reimport, handles released; Core backup/restore/authority/lifetime tests | B28 + CORE03 |
| B-D11 no operational effects | HistoricalLedger operational snapshots; HIST02 operational_invariants; B actual preserved nonempty cart/VM rows/commands probe | CORE03 + C28 + B28 |
| C-H01..H08 | Exact closed receipt cases: HIST01 profiles/preview; HIST02 apply/reopen; HIST03 identity/dedup; HIST04 stale auth/scope; HIST05 atomic cancel/recovery; HIST06 cancel/reimport; HIST07 reports/export; HIST08 UI lifecycle | C28:8/8,44 checks. HIST08 includes actual keyboard/Dispatcher work and2 fresh-session pending-reader epoch/logout witnesses |
| C-R01 signed movements | ReportContracts signed cash/change/native-average tests, ReportQuery signed/keyset tests; HIST07 signed_totals/hourly_parity | CORE03 + C28 |
| C-R02 CSV/scope/routing | ReportContracts typed CSV quoting/formula protection, origin-safe keyset; HIST07 csv_contract/operator_scope/detail_routing | CORE03 + C28; pure CSV implementation resides in Core |
| C-R03 snapshot/KPI | ReportContracts native average, query cursor/token/revision; `SalesReportAuthorityLifetimeTests` epoch, dispose/reacquire, real restore between validate/publish and serialized authority transition; HIST07 native_ticket_average/snapshot_invalidation, Load snapshot_invalidation | CORE03 + C28/L28 |
| C-R04 export authorization/outcome | HIST07 export_publication_authorization/outcome: actual issuer revocation/expiry/maintenance boundaries, allowed completed permit, no-overwrite and known1-byte prepared-temp modification rejection preserving destination | C28; no arbitrary pathname-tamper or deterministic CAS-race claim |
| C-R05 coverage/geometry | Real native-proof mixed fixture admits allowed subset and excludes other-shop source; HIST07 coverage_state_no_disclosure/hourly_signed_geometry actual realized WPF bars and safe synthetic captures | REG27 (native/audit) + C28 |
| C-P01 full/mixed load | `HIST_LOAD_50K`, exact11 assertion IDs and HISTORY-MEASUREMENT-v2 receipt:50k source,49k accepted,10k native+49k historical query; complete sampling/phase/memory and exclusion/cancellation/cold-warm checks | L28, reference-windows only; measurementCompleted/environmentValid/thresholdsPassed all true |
| Q-F01 distribution | Content fixture declarations and BaseDirectory lookup; distributed corpus/hash tests and HIST01. Verified QA payload manifest includes explicit synthetic XLSX | CORE03/C28; proof scaricata del primo SHA solo storica. Candidato 5c3327b: PASS: run `Win7POS.QA.20261105T031330057Z.3557fff79f954e9cbe82fa4a1eed9907`,8/8 scenari+44/44 assert da CWD esterna; source/fingerprint puliti invariati prima/dopo |
| Q-P01 measurement protocol | `tests/qa/Test-HistoricalSalesMeasurement.ps1` negative missing/gap/truncation/pending/environment cases + actual dedicated-thread Load grids | G29 + L28:280/280 Dispatcher e140/140 memoria, zero pending/drop/overflow/suspend; CI finale PASS sul runner Windows hosted, stesso protocollo e soglie; profilo reference-windows, non Win7 fisico |
| Q-U01 / Q-T03 safe upload/privacy | Autonomous Runtime sentinels through command/process/stdout/TRX/receipt/CSV/PNG; strict projection/visual provenance, ZIP/manifest allowlist; raw private evidence preserved | G29 + pubblicazione privata/pubblica precedente conservata; coppia artifact del run CI finale PASS, artifact13386211267, archiveSHA25682021767d315afdff947598c64da19535e67c1d2d15045f4e6d68c8c5d8581f3 |
| Q-U02 safe useful diagnosis | Closed console DTO/fallback; typed origin diagnostics plus `Win7PosQaDiagnosticProjection` verified TRX/gate adapters; unknown lines withheld/partial; first failure retained | G29; cause primarie e risultati precedenti conservati, pubblicazione distinta dagli esiti scenario |
| Q-U03 current-run bundle | Autonomous Runtime collision/stale pair/failed projector/digest-crosscheck/atomic publish tests; workflows upload only two verified current-run outputs | G29; artifact del candidato finale scaricati e verificati run031014179Z.733127f7e27545a6aac98da382290591, bundleSHA2561e0c0ab3968becc55f849fbc24748e0c9d5651f36a77eeb48667160499c1a38c, upload-manifestSHA2563e806a7f34dad98516bd89ccccaafea2b93baeba8faeea795cce092ffe50592d; Test-QaPublicBundle PASS dopo download |
| Q-T01 validator negatives | `Test-Win7PosAutonomousTests.ps1` strict mode/scenario/assertion/context/partial receipt/JSON/privacy negative cases | G29 |
| Q-T02 supervision/export | Same test with -Runtime + `Test-Win7PosOwnedProcessIdentity.ps1`; actual canonical/focused Jobs, residual deadlines, cleanup and retained failure output | G29 + B28/C28/L28 actual cleanup; no new launcher |

## Istruzioni operatore e confini rimasti

Dal riepilogo aprire **Importazione storico**, selezionare gli export e analizzare;
leggere coverage, respinte e interpretazioni ancora irrisolte. Solo prove
autorevoli consentono di completare mapping, tempo e inizio POS. **Verifica
importazione** mostra nuovi movimenti, duplicati e conflitti prima della conferma
con backup verificato. `register.import_history` abilita apply e **Fuso report**;
`register.cancel_history` abilita undo, `register.view_all` consulta tutti gli
operatori/lotti. Dopo un esito incerto consultare receipt e stato corrente prima
di riprovare. **Annulla lotto** disattiva solo i movimenti posseduti dal lotto;
per ripristinarli serve una nuova analisi/importazione. La policy JSON canonica
per i nativi richiede provenienza/copertura approvate, hash ed evidenza: un file
sintetico non approva una zona reale. Guida completa: [HISTORICAL_CARD_IMPORT](../HISTORICAL_CARD_IMPORT.md).

| Gate reale / residuo | Stato e sblocco |
| --- | --- |
| Audit indipendente originali | PASS privato precedente; riferimenti dei7 file e attesi restano privati. Verifica finale integrità/attesi `PASS:7/7 identità originali corrispondono al manifest indipendente prima e dopo il reader finale; controlli finanziari privati congelati rispettati, originali invariati; nessun nome/hash reale/atteso finanziario pubblicato` |
| Reader prodotto sul binario finale | `PASS, processo net48/x86 console nuovo in root privata; DLL del Release41959769763 realmente caricate, CoreSHA2569c39b8cfbe3f4d4bcfeb39477a368afce836d5cd96eee09176a237a238d9bf8e /DataSHA25631a17d9828477062417a83fad94c3f0738a1ebb2ad82fbb3bc27075ac5b07dd4; input7/7 e attesi indipendenti corrispondono, hash prima/dopo invariati, NeedsEvidence=true/CanApply=false, HasErrors=false, exit0 in4313ms; receipt nuova temporalmente coerente`; verificare7/7 hash invariati, attesi privati e NeedsEvidence/CanApply reali; nessun DB/apply implicito. Il vecchio reader run02 PASS non viene trasferito al nuovo binario |
| P-REAL apply/reopen/reimport/undo/report | **BLOCKED_EXTERNAL**: nuovo Shop/staff/device, merchant mapping, inizio POS/overlap e semantica oraria provider/voucher non ancora autorevoli |
| EXT-TIME / policy reale | **BLOCKED_EXTERNAL**: prova Z+offset e transizioni pertinenti al medesimo esercente/periodo; policy nativa reale approvata. Nessun fallback al PC |
| Win7 SP1 e periferiche | **BLOCKED_EXTERNAL**: macchina fisica identificata, package esatto net48/x86, DPI/tastiera e scanner/stampante ordinari. QA sintetica Windows11 non li certifica |
| Server / Supabase | Storico V1 locale senza nuovo trasporto/RLS/Storage/deployment. First-login/heartbeat esistenti richiedono profilo reale autorizzato; nessuna operazione live o creazione account dedotta |
| Risorse / cleanup finale | Ultimi focused cleanup=true/residui0; candidato/CI/artifact run `PASS per le Job QA possedute: cleanup verificato/zero residui nei focused e nello smoke del bundle; CI canonica15/15 con cleanup obbligatorio. Reader finale exit0, MSBuild e compiler server terminati via dotnet build-server shutdown. Root private FAIL/TIMEOUT, W7Q e copie W7Q83 conservate con mapping/provenienza; nessuna cancellazione ricorsiva o kill di processi non posseduti`. Le root private di diagnosi sono conservate intenzionalmente |

Nessun dato finanziario, nome originale, profilo trust o secret privato va copiato
nel report pubblico. Gli artifact caricabili sono soltanto quelli ammessi dal
proiettore e verificati nel singolo run. Pubblicazione PASS è distinta dagli
esiti degli scenari; un FAIL/TIMEOUT rimane tale anche se i diagnostici falliscono.

La [scheda esterna](ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md) deve riportare gli
hash del nuovo candidato prima della consegna fisica; non riusare quelli di77427d1.
Il [registro Execution](../plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md) conserva
run, cause primarie, comandi e checkpoint completi. Dopo i campi osservati sopra,
il prossimo sblocco è **creare/configurare la nuova Shop, fornire mapping/inizioPOS/overlap e prova temporale autorevoli; assegnare owner e Win7 SP1 fisico, poi eseguire la scheda con questi artifact**; i gate esterni
rimangono espliciti e non diventano PASS per effetto del commit o dell'upload.
