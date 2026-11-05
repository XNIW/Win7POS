# ASUS_W7POS_019_EXECUTION_RESULT

Stato: **EXECUTION_IN_PROGRESS — correzione dei gate hosted**. Il primo candidato
è su main; CI conserva FAIL per alias8.3 e apply oltre soglia. La qualifica reale
e Windows7 restano gate esterni distinti.
Mandato: richiesta Execution A+B+C del 2026-11-04 e successive istruzioni di
proseguire l'implementazione mentre l'operatore prepara il nuovo negozio.

Checkpoint correttivo:113/113 regressioni mirate e build net48/x86 pulita;
foundation5/5, Smoke8/8 e Load11/11 PASS nel run
`Win7POS.QA.20261105T030459605Z.e0d33b992cf94cf192cc895c56917d80`, fingerprint
`842ca111714aecfb5928d3929293b17644c6e134a1b6dac79efc7427cba2d428` invariato.
Apply2184.173ms, analisi1450.286ms, servizio2393.690ms, warm177.664ms;
280/280 campioni Dispatcher e140/140 memoria, p95 .489ms/max21.621ms,
picco82.445MiB. Misura completa, ambiente e soglie validi. Anche la copia NTFS
locale aggiuntiva prova alias8.3 reale, stessa identità, rifiuto database_in_use
e rilascio owner. Il nuovo commit richiede ancora CI/package/QA dello stesso SHA.
Release41955182205 del primo candidato è CANCELLED per sostituzione dopo il FAIL
CI; i suoi Core/package incompleti non sono dichiarati PASS.

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
| Primo candidato codice | [`77427d1f943c271e040dd5a67f58c34a3d2b83c1`](https://github.com/XNIW/Win7POS/commit/77427d1f943c271e040dd5a67f58c34a3d2b83c1), pubblicato su main senza force push; gate hosted da correggere |
| Fingerprint del checkout pulito del candidato | `3100112310734093d2e5137be1993171473e09342081fdd2c3c9a79054c7f1a4`; gli hash del diff tracked e sorgenti untracked sono entrambi SHA256 del contenuto vuoto |
| Ultimo candidato focalizzato | Smoke/Load `Win7POS.QA.20261105T021708997Z.fc392d44a84e4e9888e683e0040dbe1d`, fingerprint `f38745b6b1f1c6134daf9eecfb7081af977b8477fee3448a98f40f9832870e76` invariato prima/dopo |
| Ultima Windows completa | Run03 FAIL, fingerprint `3f9a943f47bfde406d1336253223f8399c06829c4e27340bb419cc9040920fac` invariato durante la corsa |
| CI All / Release Pack del primo candidato | [CI41955182242](https://github.com/XNIW/Win7POS/actions/runs/41955182242) FAIL; [Release41955182205](https://github.com/XNIW/Win7POS/actions/runs/41955182205) IN_PROGRESS sullo stesso SHA |
| Security Supply Chain | [41955182236](https://github.com/XNIW/Win7POS/actions/runs/41955182236) PASS, inclusi CodeQL, dipendenze, segreti e riproducibilità |
| Installer / payload / SBOM / provenance | NOT_RUN; nessun pacchetto candidato dichiarato |
| QA net48/x86 separata | [WPF41955181554](https://github.com/XNIW/Win7POS/actions/runs/41955181554) PASS;39 file verificati dopo download, Smoke8/8 da CWD esterna PASS |
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
| Gate finali / privacy sorgenti | Required-gates26:50/50 PASS;216 file +10 parti OOXML puliti | CI seleziona HistoricalSalesMode All dopo i PASS C/load; CI finale IN_PROGRESS |
| Q-F01 distribuzione | PASS sul bundle scaricato del candidato | Smoke8/8 e44 assert, CWD inizialmente vuota fuori repo/payload; context legato agli hash dei binari e fixture scaricati, cleanup=true/remaining0 |
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
| Analisi / apply DB / query warm | 1390.464ms /7862.516ms /169.733ms; limiti riferimento30000/15000/500ms |
| Dispatcher p95 / massimo | 0.464ms /16.341ms; limiti100/500ms |
| Probes Dispatcher / campioni memoria | 393/393 /197/197 |
| Pending / dropped / overflow / suspended | 0 /0 /0 /0; anche memoryDropped=0 |
| Picco private bytes campionato | 86753280 byte,82.734MiB; limite700MiB |
| Crescita picco rispetto al baseline | 50716672 byte,48.367MiB; limite150MiB |
| Servizio completo / backup / ownership cold | 8074.998ms /174.232ms /4367.090ms; apply DB esclude backup/ammissione misurati separatamente |

Smoke dura25.658s e Load21.812s nella rispettiva receipt; i wrapper riportano
27.731s e24.088s, exit0, cleanup=true e processRemaining=0. Questi risultati
focalizzati dichiarano `fullWindowsQualification=false` e non riscrivono Run03.

## Componenti e correzioni verificate

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

## Requisiti R3 e prove puntuali

Istruzioni operatore: dal riepilogo aprire **Importazione storico**, selezionare
gli export e leggere l'anteprima; completare soltanto con prove autorevoli
mapping, tempo e inizio POS. **Verifica importazione** espone nuovi movimenti,
duplicati e conflitti prima della conferma e del backup. Consultare lotti e
ricevute dopo un esito incerto prima di riprovare. **Annulla lotto** disattiva i
soli movimenti posseduti dal lotto e conserva l'audit. Per ripristinarli serve
una nuova analisi/importazione. Permessi e procedura completa sono nella guida.

La matrice mantiene l'attribuzione dei checkpoint locali; la CI del commit finale
è registrata separatamente nella sezione consegna.

Evidence keys:

- **CORE03**: retained Windows run03, `Win7POS.QA.20261105T010900248Z.ea3306d57ec841c3bd06c6da4bcdc838`, `core/core.trx`:1692/1692 PASS, zero skipped. Run03 overall remains **FAIL**, functional19/20, foundation4/5, smoke6/8, Load NOT_RUN. It predates subsequent focused corrections.
- **B**: focused `Win7POS.QA.20261105T015030831Z.b1eb4ac536a844e38725d1c9779705ba`, foundation5/5 PASS; real receipt, exit0, cleanup=true, processRemaining=0. Source fingerprint `5d5d153a4143f7bc67beec85608e2a83bdd78ae3b2bf1a5c544af641e150b82c`.
- **C/L**: focused `Win7POS.QA.20261105T021708997Z.fc392d44a84e4e9888e683e0040dbe1d`, Smoke8/8 and44/44 checks; Load1/1 and11/11 checks. Validated receipts, both exit0/cleanup=true/remaining0; identical before/after fingerprint `f38745b6b1f1c6134daf9eecfb7081af977b8477fee3448a98f40f9832870e76`. `fullWindowsQualification=false`.
- **N/AUDIT25**: `tests/security-audit-maintenance-25.trx`:23/23 PASS, the22 real native-proof cases plus real audit-maintenance regression. Do not add these to CORE03 as a new full-Core total.
- **G26**: `tests/required-gates-final-26.log`:50/50 PASS; includes actual QA AST/Runtime, identity, measurement and diagnostic-projection tests. Public publication of run03 PASS is separate from its scenario FAIL.

All evidence keys above resolve below `C:\Dev\_codex-private\ASUS-W7POS-019`;
canonical runs are under `qa-runs`, focused groups under `focused`.

| R3 requirement | Actual test / exact harness evidence | Observed attribution |
| --- | --- | --- |
| A-D01 profiles/header | `Import/GetnetImportTests.cs`, distributed fixed-oracle/profile/header tests; `HIST01_PROFILES_PREVIEW` profiles_detected/header_mapping | CORE03 + C |
| A-D02 exact values/IDs | `HistoricalSalesValueRulesTests`, `HistoricalSalesFingerprintTests`, `GetnetImportTests`; HIST01 exact_values/text_ids | CORE03 + C |
| A-D03 states/signs/coverage | Getnet rejected-row, signed-sale/cancellation and unsettled-net coverage tests; HIST01 exact_values/invalid_input_blocked | CORE03 + C |
| A-D04 time | Getnet unresolved/explicit interpretation tests, ValueRules time rules; `HistoricalSalesReportContractsTests` gap/fold/fractional bounds; HIST01 policy_visible | CORE03 + C; real provider meaning remains external |
| A-D05 bounds/file safety | Getnet hostile OOXML/archive/XML/formula/link/cancellation tests; HIST01 invalid_input_blocked | CORE03 + C |
| B-D01 schema/mapping | `Data/MigrationRunnerTests.cs`, `Persistence/HistoricalLedgerTests.cs`; additive0015 and mapping constraints; B backup mapping_fk_preserved | CORE03 + B |
| B-D02 dedup/conflict | HistoricalLedger namespace/IDs, active/inactive economics and approval cases; HIST03 unchanged/renamed/reordered/overlap/distinct/conflict | CORE03 + C |
| B-D03 commit authorization | `HIST_B_COMMIT_AUTHORITY`: before-insert/precommit Apply revocation, Undo precommit revocation, expiry, operator/permission and linearized-once | B; Core FinalAuthorityRevocation also CORE03 |
| B-D04 tenant/source proof | `Reports/NativeSalesOwnershipProofTests.cs`: real N1 cold refresh, N2/N3, NX invalid sources before first refresh, mixed read-surface exclusion, supported native void proof/revision | N/AUDIT25; HIST07 tenant_scope supplementary. No arbitrary external tamper-detection claim |
| B-D05 / B-D12 process/entry/admission | `HIST_B_PROCESS_ADMISSION` real overlapped holder/contender, bounded SQLite busy, auth/workflow/maintenance cancellation, lock order and actual nonempty cart/commands; `HIST_B_ENTRYPOINT_AUTHORITY` actual WPF normal/safe, CLI/harness, different DBs, case/8.3 aliases, hardlink/junction, restart | B, all closed subcases; private per-variant native/identity/refusal codes retained |
| B-D06 cancellation/fault | HistoricalLedger source mutation/protected handles/revocation/admission cancellation; HIST05 cancel, precommit fault, backup-IO fault | CORE03 + C; failpoints are not the genuine crash proof |
| B-D07 genuine journal crash | `HIST_CRASH_HOT_JOURNAL`: same existing DB, nonce/sentinel, spill, expected abrupt79, technical RW recovery without seed/init, data+revision/operational invariants/cleanup | B |
| B-D08 terminal replay | HistoricalLedger DuplicateObserverOwnsNothingAndReplayNeedsNoSourceOrBackup, ReconstructedRequestReplaysAcrossRevisionAndRestartWithoutSource, ReadOnlyIdentityReplayNeedsNoMutationGuardAndRejectsWrongShopOrFingerprint, immutable terminal receipt | CORE03; HIST05 lost_response_reconciled supplementary |
| B-D09 undo/reimport | HistoricalLedger ThreeCyclesKeepAuditAndUseNewInternalIds and duplicate observer; HIST06 owner-only/observer/cycles/audit | CORE03 + C |
| B-D10 / B-D13 backup/restore/AUX | `HIST_B_BACKUP_RESTORE` real verified backup/publish, wrong AUX scope, real swap/rebind, exact injected post-swap rollback+identity restoration, epoch-stale plan, mapping/FK, zero reimport, handles released; Core backup/restore/authority/lifetime tests | B + CORE03 |
| B-D11 no operational effects | HistoricalLedger operational snapshots; HIST02 operational_invariants; B actual preserved nonempty cart/VM rows/commands probe | CORE03 + C + B |
| C-H01..H08 | Exact closed receipt cases: HIST01 profiles/preview; HIST02 apply/reopen; HIST03 identity/dedup; HIST04 stale auth/scope; HIST05 atomic cancel/recovery; HIST06 cancel/reimport; HIST07 reports/export; HIST08 UI lifecycle | C:8/8,44 checks. HIST08 includes actual keyboard/Dispatcher work and2 fresh-session pending-reader epoch/logout witnesses |
| C-R01 signed movements | ReportContracts signed cash/change/native-average tests, ReportQuery signed/keyset tests; HIST07 signed_totals/hourly_parity | CORE03 + C |
| C-R02 CSV/scope/routing | ReportContracts typed CSV quoting/formula protection, origin-safe keyset; HIST07 csv_contract/operator_scope/detail_routing | CORE03 + C; pure CSV implementation resides in Core |
| C-R03 snapshot/KPI | ReportContracts native average, query cursor/token/revision; `SalesReportAuthorityLifetimeTests` epoch, dispose/reacquire, real restore between validate/publish and serialized authority transition; HIST07 native_ticket_average/snapshot_invalidation, Load snapshot_invalidation | CORE03 + C/L |
| C-R04 export authorization/outcome | HIST07 export_publication_authorization/outcome: actual issuer revocation/expiry/maintenance boundaries, allowed completed permit, no-overwrite and known1-byte prepared-temp modification rejection preserving destination | C; no arbitrary pathname-tamper or deterministic CAS-race claim |
| C-R05 coverage/geometry | Real native-proof mixed fixture admits allowed subset and excludes other-shop source; HIST07 coverage_state_no_disclosure/hourly_signed_geometry actual realized WPF bars and safe synthetic captures | N/AUDIT25 + C |
| C-P01 full/mixed load | `HIST_LOAD_50K`, exact11 assertion IDs and HISTORY-MEASUREMENT-v2 receipt:50k source,49k accepted,10k native+49k historical query; complete sampling/phase/memory and exclusion/cancellation/cold-warm checks | L, reference-windows only; measurementCompleted/environmentValid/thresholdsPassed all true |
| Q-F01 distribution | Content fixture declarations and BaseDirectory lookup; distributed corpus/hash tests and HIST01. Verified QA payload manifest includes explicit synthetic XLSX | CORE03/C + downloaded exact-commit bundle:8/8 Smoke,44 checks from unrelated empty CWD PASS (run022928230Z.6d6b5892cd06491193d683655047681e) |
| Q-P01 measurement protocol | `tests/qa/Test-HistoricalSalesMeasurement.ps1` negative missing/gap/truncation/pending/environment cases + actual dedicated-thread Load grids | G26 + L;393/393 Dispatcher and197/197 memory samples, zero pending/drop/overflow/suspend |
| Q-U01 / Q-T03 safe upload/privacy | Autonomous Runtime sentinels through command/process/stdout/TRX/receipt/CSV/PNG; strict projection/visual provenance, ZIP/manifest allowlist; raw private evidence preserved | G26 + run03 local public publication. Final GitHub uploaded pair must be observed from current exact-commit workflow; not inferred yet |
| Q-U02 safe useful diagnosis | Closed console DTO/fallback; typed origin diagnostics plus `Win7PosQaDiagnosticProjection` verified TRX/gate adapters; unknown lines withheld/partial; first failure retained | G26; actual run03 public publication preserves independent primary/publication states |
| Q-U03 current-run bundle | Autonomous Runtime collision/stale pair/failed projector/digest-crosscheck/atomic publish tests; workflows upload only two verified current-run outputs | G26; final downloaded workflow outputs pending |
| Q-T01 validator negatives | `Test-Win7PosAutonomousTests.ps1` strict mode/scenario/assertion/context/partial receipt/JSON/privacy negative cases | G26 |
| Q-T02 supervision/export | Same test with -Runtime + `Test-Win7PosOwnedProcessIdentity.ps1`; actual canonical/focused Jobs, residual deadlines, cleanup and retained failure output | G26 + B/C/L actual cleanup; no new launcher |
