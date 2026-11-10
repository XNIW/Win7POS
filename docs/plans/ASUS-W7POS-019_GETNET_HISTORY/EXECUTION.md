# ASUS-W7POS-019 execution ledger

<!-- ASUS019_CURRENT_START -->
## Stato corrente — postazione reale, sviluppo verificato; qualifica finale aperta

### Consolidamento dopo la prima qualifica completa

Windows-All cc6128f conclusa:13/15 PASS; Core1765 PASS/2 FAIL su fixture prive
ora del guard obbligatorio; functional21/22 (visual seed senza guard) FAIL.
Autorizzazione, logging, paging, crash/storico8/52/load11 PASS. Publication PASS,
nessun esito fallito cancellato. I due seed sono corretti con autorità valida:
Core aggiunge il negativo esplicito, visuale usa vero login/lease sintetico.

Ultimo delta prodotto: letture snapshot/prodotto con transazioni differite;
lookup barcode sul worker perché SQLite async può attendere il file lock in
modo sincrono. Nessun cambio journal policy, schema o budget. La fixture pubblica
aggiunge un lock SQL esclusivo controllato con watchdog finito e cleanup posseduto.
Target16:70 assert shell PASS24.516s; input durante lock7.4728ms≤250ms;
visuale PASS31.888s; vendita integrata PASS2.357s, tutti cleanup veri. Core08:
22/22 PASS, inclusi read durante writer e autorizzazione mancante. Build11 pulita.
Target13/14 non eseguiti per errori compile di fixture,15 per selettore visuale
non ammesso: conservati. Il runner ora seleziona anche il caso visuale già canonico;
nessun nuovo scenario o cambio della matrice default. Errori shell entrano anche
nei diagnostici tipizzati, senza esportare testi/importi.

La sola correzione documentale82d11de ha scansioni pulite e codice identico a cc;
non è il pacchetto da consegnare dopo il nuovo delta lock. WPF mirato remoto
fc3a31d/42860115702:20/22 PASS, visuale e shell FAIL conservati. La shell locale
successiva passa con ricevuta completa; la nuova CI deve produrre una diagnosi
esatta anche in caso di failure sul runner indipendente. Qualifica finale,
performance qualificata sul nuovo pacchetto e consegna rimangono aperte.

Codice congelato in cc6128f448cd36089f1c470a21885091efa4de3c; Windows-All locale
in corso, restore/gate/build PASS, Core ancora attivo. WPF42857545140 SUCCESS.
Release42857518468 FAIL: generic-api-key sul conteggio test del worklog, falso
positivo riprodotto con Gitleaks8.30.1 (working tree e storia, solo docs riga9).
Questo follow-up cambia soltanto testo e impronta storica esatta; scansioni dir/git
PASS senza esclusione di percorsi o regole. src/scripts/tests/eng e contratti di
build byte-identici a cc6128f, verificati con git diff. Nuovi pacchetti e workflow
sono legati al nuovo SHA; la run locale cc6128f resta attribuita al suo codice.
Il checkout della run attiva non viene modificato. La cronologia sotto conserva
la preparazione e i FAIL; nessuna dichiarazione di consegna completata.

Stessa ASUS019 ACTIVE. Main0378e17, PR136 draft sul ramo correttivo; installato
2ad42ca verificato41/41, profilo10/10 invariato e POS reale chiuso normalmente.
Il successore ancora WIP sostituisce la shell empty con una sola vera postazione
POS dopo autenticazione/lease/trust/policy/mirror/binding e inizializzazione locale.
Catalogo, outbox e ordini passano al supervisor della shell. Nessun Continue,
Sync Center o secondo login richiesto per usare carrello e scanner.

Contratto implementato: snapshot pubblicato distinto dal tentativo; lettura SQL
coerente senza barriera lunga di rete; candidato fallito non invalida la copia
operativa precedente; empty autorevole conserva il fence remoto. VM/carrello,
selezione, input e report persistono nei refresh. Stato e callback sono fenced
per identità/generazione; chiusura attende lane, timer, letture e scanner posseduti.
Il report nativo dipende dalla vera policy temporale attiva, non dalla PosView.

Al commit Data verifica economia, autorizzazione, shop/generazione, prodotto
attivo e prezzo corrente. Manuale richiede evidenza immutabile emessa dal dominio;
locale richiede intento originale hashato più scope del negozio persistito nella
stessa transazione della creazione, anche dopo ack coerente. Un vecchio prodotto
con client ID arbitrario non basta. Nessun backfill inferito delle vecchie prove.
Compatibilità pre-supervisor: soltanto snapshot remoto già verificato e guard
live con shop ufficiale uguale possono operare se manca del tutto la riga SQL
generazione; una riga esistente inattiva/differente non ammette fallback. Nessuna
esenzione empty/locale senza generazione durevole. Lease/permessi/fisco, commit
unico stock/righe/outbox e replay restano verificati dai protocolli esistenti.

Prove di sviluppo su HEAD2ad+WIP, **non qualifica del binario installato**:

- Core06:175/175 PASS, inclusi policy, supervisor, commit economico, provenienza,
  prezzi concorrenti/replay, cancellazione timer e candidato rifiutato con copia
  pubblicata ancora valida. Core02(3FAIL),04(1FAIL) e correzioni restano conservati;
  Core03 6/6 e05 14/14 PASS sono prove selettive con attribuzione propria.
- Required03:50/50 PASS. Required01 41/50 e02 49/50 restano storici; il difetto
  architetturale UI/SQLite corretto spostando la classificazione in Data.
  Build08 Release net48/x86 prodotto+harness PASS,0warning/0error; restore locked PASS.
- AUTH shell12:69 assert PASS,26.370s, cleanup vero; ingresso prima della risposta
  sospesa, scanner rapido, pending/unknown/cancel/permesso negato, primo locale e
  manuale con pagamento/ricevuta/stock/outbox, zero→remoto automatico, refresh con
  report/import/input/selezione,503/perdita TCP, chiusura nel pull e riavvio.
  Nessun Continue, CatalogDelta Manual o RefreshShellReadiness privato in questa
  accettazione. Target11 integrated vendita e F05 carrello/restart PASS.
- Misure sintetiche shell12,2880×1800/200%: login chiuso→interattivo155–221ms;
  Input completion0.6076ms≤250ms; tre input73.2524ms; barcode noto con50361.2055ms,
  perdita TCP35.6541ms. Campioni singoli, non p95 o qualifica performance finale.
- Storico sviluppo02:8/52 PASS, circa44s nel budget60s. Il primo run FAIL perché
  la vecchia fixture pretendeva il catalogo nel bootstrap; aggiornata la sola
  preparazione QA esplicita. AUTH pubblico resta indipendente da quella utility.
  Il preflight privato iniziale incompleto è NOT_EXECUTED, non PASS.

Evidence: U19Cartb7e31/target10..12, U19Hcart7a101(smoke FAIL),7a102(PASS),
cart-*.private.log nella root resume019. FAIL/timeout precedenti e dati sintetici
conservati; nessun harness con POS reale aperto, Apply/Undo originale o vendita reale.
Qualifica congelata Windows-All/performance, CI/pacchetto, backup aggiornamento,
installazione e percorso sul binario nuovo restano aperti. Nessun DONE.

Cloud separato: PR135b030 ha due FAIL del vecchio budget storico60s conservati
(42826371935,42832581283), nessun terzo retry cieco; sync controllata dei fix comuni
ancora richiesta. Admin151/6c2bd41 qualificato, nessun deploy. Il contesto mobile è
stato letto: [handoff](HANDOFF_MOBILE.md), nessun PASS iOS/Android derivato da Windows.
Scala200%/2880×1800 mantenuta;100/125 NOT_RUN/USER_DEFERRED; Win7 fisico
NOT_RUN/BLOCKED_TARGET; Getnet moderni C MISSING, nessun Apply moderno.

<!-- ASUS019_CURRENT_END -->

<details>
<summary>Checkpoint precedente5d8e704 (storico)</summary>

## Stato corrente — candidato finale qualificato, installazione in attesa

ASUS019/R3 ACTIVE. Sorgente pubblicata `5d8e704b2d0027c4ee1d5a9f4cb6b10842b85b54`, fingerprint
`2adbcf2a3244638d25e00897ecffa8af9972c6e0c3315b0566c3dc669d29b2dd` invariato prima/dopo la canonica.
Il codice prodotto src e identico ad a397; la prova finale aggiunge pagamento nella
MainWindow reale su fixture e guida all'ingresso volontario dal riepilogo.
I PASS di a397/e6aa non sono trasferiti: qui sono riportate le prove di5d8e704.

| Gate del candidato5d8e704 | Esito effettivo |
| --- | --- |
| Windows-All locale | PASS15/15, exit0, Core1745/1745, functional22/22, crash5, smoke8/52, load11; cleanup completo e pubblicazione evidence PASS |
| Pipeline exact-SHA | CI42719698708, Security42719698588, WPF42719773542, Release42719699281 SUCCESS |
| Pacchetto | ReleasePack13721564802 verificato dal validator canonico; prodotto41, QA40,34 file comuni byte-identici |
| Setup | SHA256 `54e087cc3cf7e04b6ef7e26921a148290ec7431c0d0f3a164c4e58b3509fe044` |
| QA scaricata | Smoke8/52 PASS in49.397s; non e una prova sul registro reale |
| MainWindow fixture | Empty/admin e no-history, login pronto/scanner/carrello/pagamento/ricevuta, chiusure e sessione PASS; AUTH18.738s, nessuna mutazione reale |
| Geometria nativa fixture | PASS22 osservazioni,192DPI (200%),work area2880x1704, titolo/footer contenuti e traversata focus;100/125 e basse risoluzioni del nuovo candidato NOT_RUN |
| Installazione reale | Primo consenso UAC annullato prima dell'avvio, esito conservato; EXE a8286c6 e10/10 file profilo invariati. Nessun installer/POS nuovo avviato da questo tentativo |
| Registro reale | Riconciliazione read-only di originali/ricevuta/invarianti PASS e backup prodotto fresco verificato; una ricevuta Apply/un lotto attivo per quattro originali legacy. Nessun nuovo Apply/undo |

Pacchetto payload ZIP SHA256 `8912fbfb18ec4bcf6982e6d6f93db58dea8c5726a7d1b5656495fde10154f31a`.
Qualifica locale: ux-canonical-03-roots.private.json; smoke scaricato:
ux-final-downloaded-smoke-root.private.txt; prova macchina:
ux-final-delivery-checkpoint.private.json. Primo e6aa13/15, timeout e failure di
cleanup/probe, interruzioni desktop e tentativo UAC annullato restano conservati.
Non sono riclassificati come PASS.

Percorso reale5d8e704 ancora NOT_RUN: installazione verificata, avvio dal
collegamento quotidiano, identita profilo prima del PIN mascherato, login -> shell
-> riepilogo -> storico volontario -> chiusura -> riepilogo -> cassa con PID/sessione
vivi. Solo lettura ricevuta/report e review legacy nuovi=0; mai Apply/undo originali.
Vendite sempre negate con catalogo vuoto. Scala concordata200%/2880x1800 conservata;
completare100/125 e1024x768,1024x600 best effort e ripristino concordato.

Kit Win7 finale: `C:\Dev\U19K09fb63a7\Win7POS-ASUS019-5d8e704-Win7-transfer.zip`,
SHA256 `0f6bb7d62496614e51a2f2b134d02109422aff71732bfb7f1f5ab79a88a95942`;9 file, template inerti, vecchi kit invariati.
Target fisico/periferiche gia dichiarati indisponibili: NOT_RUN/BLOCKED_TARGET;
nessuna nuova richiesta e nessun SDK moderno sul registratore. Secondo monitor
assente NOT_RUN. Moderni C MISSING e CSV_INPUT BLOCKED_INPUT restano distinti.

Admin: [piano B/C pubblicato](https://github.com/XNIW/merchandise-control-admin-web/blob/d20233220d451c4c8be4ffff5170e45ca224c7e6/docs/ARCHITECTURE/ASUS019-ADMIN-OPERATIONS-HISTORY-PLAN.md),
READY_FOR_REVIEW / PLANNING_ONLY: matrice, reader staff mancanti, wizard tre passi,
ledger cloud e cutover con unico writer. Nessun ruolo, credenziale, grant/RLS,
schema, dato o deploy modificato. Stessa task, nessun DONE o secondo MasterPlan.

I checkpoint sotto conservano la propria data; lo stato corrente e questo riquadro.

</details>
## Qualifica a397 conclusa; estensione QA finale — 9 novembre

Il candidato pubblicato a397a49 ha concluso Windows-All 15/15 PASS: Core1745,
functional22, crash5, smoke8/52 e load11; report e prime failure restano conservati.
CI42713452349, Security42713452311, WPF42713541044 e Release42713451813 SUCCESS.
Evidence: ux-canonical-02-roots.private.json; QA scaricato e verificato40/40.
L'installato rimane a8286c6: nessuna nuova installazione o prova reale attribuita
ad a397. Ripristino display200%/2880x1800 già confermato; nuova matrice DPI aperta.

Un'estensione soltanto del test MainWindow copre il percorso pronto scanner →
carrello → pagamento → ricevuta, commit una volta e ritorno alla cassa; stampa,
cassetto e prenotazione fiscale disattivati sulla fixture. PASS20.586s, pointer
ux-main-payment-root.private.txt. src è identico ad a397. Il successore QA e la
guida corretta vengono congelati per la qualifica finale; i PASS sopra restano
attribuiti ad a397. Il nuovo Planning Admin B/C non aggiunge codice a Windows.
Nessun Apply/undo del lotto reale; installazione e navigazione reale restano aperte.

## Successor after canonical01 failures — 9 November, ACTIVE

Unpublished local e6aa0e6 canonical Windows-All completed13/15 PASS, source
fingerprint982eb7fed0618d54a346ca76e19d5453ca2af223930afaa9e3480d10015cc690
unchanged, all owned cleanup confirmed. Core1745/1745, required50/50, smoke8/52
(48.971s parent) and load11/11 PASS. Functional21/22: ordinary MainWindow21asserts
PASS but worker failed on database_authority_connections_active at final owner
disposal. Crash2/5 complete, process admission timed out30s after5checks: cart
maintenance probe did not finish. Both failures retained; no qualification/install
claimed for e6aa.

Successor correction: successful POS initialization is once per VM, active init
is joined and disposed continuations stop. Fixture drains only its owned sync/
initialization/backup work before releasing authority; the guard is unchanged.
Shared report importer again marks the current commercial VM busy and restores
its previous state/focus. The maintenance fixture explicitly installs/restores
its valid operator holder and yields fairly within its original8s deadline.
Final authority/version is rechecked after the last awaited query and before
login transfer; a newer-operator injection proves no stale forced logout. Empty
status preserves unrelated warnings; menu uses localized sales/payment summary.

Focused successor PASS16.544s, including real MainWindow, ready scanner/cart,
button/X/Escape, filters/session, single initialization, no-history operator,
stale callback and real cart maintenance/cancel/invariants. Evidence pointer
ux-focused-pre-freeze-02-root.private.txt; required50/50 in
ux-required-gates-03.private.log. Earlier post-canonical focused failure at8s
initialization and the first wrong-setting warning fixture remain preserved.
Native200% synthetic rendered shell/import evidence is private; rendered images
are distinguished from native HWND/card measurements. Canonical successor,
exact-SHA pipeline/package/install, retained-profile path and DPI remain open.
No backend change or real original Apply/undo.

## Combined UX/geometry implementation — local, qualification pending

Same ASUS019/R3 ACTIVE. Pure shell admission and fenced login transfer implemented;
verified empty opens MainWindow with no commercial VM or history permission gate.
Start-of-day keeps CanOpenPos sale-only and separately admits authenticated shell;
timeout rechecks authority/catalog. Shared report/import composition preserves
filters, session and cart; unavailable payment totals are unavailable. Recovery,
operator change and existing sync refresh rederive admission. No backend changes.

Focused working-tree evidence: ux-navigation-03-root.private.txt (ordinary
MainWindow13.297s PASS), ux-navigation-02-root.private.txt (geometry10.870s and
integrated1.904s PASS), ux-core-initial-root.private.txt (140selected PASS),
ux-required-gates-02.private.log (50/50 PASS). First nav01 TIMEOUT and all
compile/gate failures retained. Source not yet published/installed. Canonical
Windows All and exact candidate delivery still required. Native200% restored;
new candidate100/125 and low-resolution/hardware verification remain open.

## Latest checkpoint — 2026-11-09, qualified a8286c6 installed

Latest UX addendum accepted in this same Execution: verified-empty authenticated
shell with navigation/report, commercial admission still denied; voluntary history
from sales summary, child close never substitutes for app exit or logout. This
supersedes mandatory historical login/workspace UX. UX gate ACTIVE alongside
geometry.21:21UTC: local/remote c54be47, installed a828 EXE hash fa69762f…6a6f,
no POS/harness active. Geometry dirty patch and new test saved immutably under
private pre-shell-ux-20261109T212141Z before overlapping edits; no new source
commit or pipeline yet. Readiness/session ownership/start-of-day/MainWindow
chain to implement and qualify; prior greens do not qualify this new UX.

Geometry addendum integrated into this same Execution/R3 before final delivery:
center each new operator surface in its relevant monitor work area, retaining
true ownership and reachable title/footer at200%, nested and long-content cases.
a828 is already installed; user restored200%, native empty form observed with
footer visible but off-center. No clipping inferred from this observation.
Real POS closed normally at an idle checkpoint, no backup/SQLite operation active.
No new Apply of originals: post-successor review only, new0 expected. Geometry
regression/fix/qualification is now an explicit open gate; existing greens stay
attributed to a828, not to a future geometry candidate.

Geometry implementation checkpoint: one shared work-area policy,39XAML roots
Manual, unchanged owners; measured initial normal sizing, bounded minima and
size-only clamp, long-message body scrolling with separate footer. Geometry
worker in existing functional wrapper is the additional21st case, separate
from historical60s/52asserts. Red reproduced; intermediate non-overlay centering
FAIL retained; focused22measurements PASS in10.065s process at192DPI. Native
first-visible HWND witness and independent GetMonitorInfo expected, card/footer,
four languages, six literal rectangle cases, cancel zero-write/confirm once,
move preserved/resize containment PASS. Required gates50/50/build x86 PASS.
Recovery updated from old owner convention to stronger work/card/CTA checks,
then current-source PASS30.160s. Original local weakref FAIL remains historical,
without claiming its cause fixed. Exact-SHA qualification/package/install and
current-source100/125 plus final200/live reopen still pending.

Source/package a8286c669a7c0d8ec4f82e1e7a31fc1b5d46eed3, version
1.0.0-dev.a8286c669a7c, installed41/41. CI42673051923, Security42673051757,
Release42674141171 and WPF42675026366 SUCCESS: canonical15, functional20,
foundation5/37, smoke8/52 and load11 with all measurement/environment/threshold
flags true. Downloaded QA40 verified, fresh local smoke49.678s PASS8/52 with
cleanup and unchanged bytes/fingerprint;34common QA/product files identical.
No thresholds/assertions relaxed. The production correction is unchanged from
ca4e437; the test successor reuses one actual VM fixture without redundant setup.

Verified product backup retained before update; installer exit0, no POS auto-start,
whole retained profile byte-identical through installation. User opened the
existing Start Menu shortcut; EXE/profile verified before masked PIN input.
Current login, authoritative-empty catalog, history workspace and empty import
form PASS. Logical ledger/original receipt reconciliation after reopen PASS,
integrity/FK and commercial invariants PASS. LIVE_REOPEN_PENDING: actual receipt,
report, new CSV and duplicate-only reimport still to conclude on this candidate.
Native empty form100/125% observed entirely visible, reverse Tab/Enter/Escape
observed; full forward Tab/assistive coverage remains limited. Restore200% pending.

Admin148 authenticated PASS and served identity unchanged. A/B VERIFIED, modern
C MISSING, CSV_INPUT BLOCKED_INPUT, physical Win7 NOT_RUN/BLOCKED_TARGET.
No undo of valid real history. Same019/R3, software REVIEW, overall ACTIVE.
See [current result](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md) for hashes,
private evidence pointers, first failures and qualified run links. Initial zero-job
GitHub Internal server errors and cancelled first UAC remain historical failures.

## History — 2026-11-09, durable legacy import and confirmation fix

ca4e437 CI42667255330 FAIL retained: all8/52 smoke assertions completed PASS,
but receipt body59.625s plus process startup/shutdown exceeded unchanged60s.
Load NOT_RUN dependency. Core1734, gates and foundation5/37 PASS; WPF42667293657
functional20/20, Security42667255175 and Release42667255023 PASS. No install
of this unqualified candidate. Production fix unchanged; test successor reuses
one VM fixture for stale rejection then single commit/reentry denial. Fresh
run06 PASS8/52, process50.329s, HIST02 3.352s, exit0/cleanup/no orphan. Exact-SHA
successor qualification/package pending; no assertion or budget relaxed.

Local full run retained FAIL: Core1734 and other independent gates PASS,
functional19/20 with Recovery weakref retention control4/recovery5 FAIL; no
unchanged Recovery retry. History receipts failed before tests because this
operator's deep evidence root plus atomic suffix exceeded net48 path length.
Short-root downloaded crash probe passed hot-journal and commit authority, then
correctly rejected noncanonical W7HP work ownership; partial2/5, not a PASS.
No work guard weakened. Full canonical foundation PASS belongs to hosted CI.
User confirmed display100%; actual native100/125 test and restore200% pending.

Admin148 live authenticated PASS: staff-unavailable overview and authorized safe
Staff reader observed on served a68ebec2/deployment1cd437f1. Admin docs b5c2dbc;
software REVIEW, overall ACTIVE. First147 failure/rollback retained.

Installed1823697 daily Start Menu shortcut inherited the retained NEW_SHOP_019
profile; user entered the masked PIN. Login, authoritative empty catalog and
history workspace PASS. Seven original XLSX hash checks and actual UI analysis
PASS against independent frozen controls; modern preview remains NeedsEvidence.
Separate four legacy files had applicable A/B/CLP evidence and current plan.
User confirmed once; product pre-apply backup verified and transaction committed.
UI then reported NullReferenceException. Exact rowwise reconciliation PASS:
original textual identities, source hashes/rows, local timestamps, signed amounts
and net values. One original receipt/active batch; commercial tables unchanged.
No repeat Apply or undo of this valid ledger.

Actual receipt read, historical-only daily/period report, selected-day detail
and CSV export PASS. Private CSV reconciles exactly to the ledger snapshot,
with report metadata, intact identifiers and formula-safe text. VerifiedForScope
does not certify complete source-month coverage; June remains partial.

Existing HIST02 reproduces the generic postcommit NullReference when Apply
reenters while confirmation is open. Direct service and single VM confirmation
pass; the exact original live stack is unavailable. Minimal fix holds the plan,
blocks reentry and rejects changed review after the modal. Build x86 and dialog
standards PASS. Fresh focused run05 PASS,8cases/52asserts, including single
confirmation, denied reentry and changed review with zero writes; exit0,
cleanup confirmed/no orphan,53.276s within unchanged60s process budget.
Canonical exact-SHA qualification and package remain pending.
Diagnostic runs01-04 retained; HIST08 was blocked by the real POS holding image
cache. Real POS closed normally before the next isolated QA run.

Getnet C modern MISSING; CSV_INPUT BLOCKED_INPUT; physical Win7 unavailable.
Host display user-reported200%/2880x1800; temporary100% requested, no scale change
performed by the tool. Reopen/dedup on corrected candidate and100/125 remain open.

## History — 2026-11-09, rollout before actual analysis/apply

Same ASUS019/R3, Windows1823697 unchanged. Path B qualified full Admin unit:
served Worker exported read-only, existing TASK140F staff reader restored and
actual web role resolver corrected without new grants/SQL. PR147 mergecdebb26
first rollout5cbf018f passed POS login/authoritative-empty/history workspace but
FAILED fresh pos_admin web sessions. Failure retained; owned POS stopped and
pinned399720d2 rollback/deployment1c2aaf19/public check verified.

PR148 codecc7b835/merge9016054 uses existing production web-role predicate,
45focused PASS. CI42638651781 attempt1 PREFIX_CONTAINER_HEALTH_FAILED before
bind/apply/tests retained; one targeted failed-job rerun PASS, live TASK094
SKIPPED. Cloudflare42638650988 PASS; postmerge CI42641653938 and
Cloudflare42641652917 PASS. Linux artifact13684508006,57checksums and exact
tree/lock/config verified, no-bundle dry-run PASS. Now served a68ebec2,
deployment1cd437f1,100%/tag9016054, public check PASS. Authenticated successor
verification pending; no public-only check relabeled live PASS.

User manually launched installed EXE after tool policy denial, entered masked
credential, empty history-only screen and workbench observed. Closed before
rollback; trust retained. Existing Start Menu shortcut verified; user-only
WIN7POS_DATA_DIR now selects retained profile, daily shortcut inheritance still
to verify before PIN. No alternate launcher, DB/trust move or protection change.

Seven supplied XLSX hashes match frozen independent oracle. Actual UI analysis
pending; reader7/7 reused separately. Getnet A VERIFIED scoped user association
corroborated by official identity; keep alias scopes separate. B VERIFIED user
attestation never previously used Win7POS. C MISSING applicable modern export
time evidence; Z lexical format alone insufficient. Legacy scope to assess
independently, native mixed-report policy separate. No real Apply yet.

Read-only current DB integrity/FK PASS, sales/stock/outboxes/history0, dataset
metadata1. CSV_INPUT BLOCKED_INPUT, only XLSX supplied. Native workbench readable,
actual100/125/full-import-form/keyboard not yet qualified. Physical Win7 already
unavailable; kit unchanged. ACTIVE, not DONE. [Current matrix and provenance](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## History — residual checkpoint 2026-11-08

Windows1823697 reused unchanged on Windows11 build26300 x64, net48/x86.
One newly requested ordinary launch of the installed EXE succeeded on the
retained isolated profile; user entered the masked credential. First-login
and trusted-session persistence observed PASS; local operator completion and
history remain blocked by catalog HTTP500. Same-shop server audit identifies
catalog_v2_empty_manifest. Previous tool denials are retained as history.

Admin PR144 merged919d100 with post-merge checks PASS. HTTP fix PR145,
code3e56a322,33 targeted checks and CI42522680653/Cloudflare42522680527 PASS;
1156foundation/14skip,67files/4375pgTAP. Mergedfdb30f4. Its post-merge database
failed twice at prefix startup before bind/apply/tests; no cause inferred.
Closed diagnostics PR146, codef344918, CI42525515132/Cloudflare42525515054
PASS,1157foundation/14skip; mergedf57afa6. Its post-merge CI42526220441
database/Verify and Cloudflare42526220439 PASS; live E2E/deploy SKIPPED.
No new Admin TASK146. No Windows rebuild, SQL apply, Worker deploy or
generic CI dispatch.

Concrete release dependency: reconcile immutable source equivalent to current
Worker399720d2 including TASK140F, and build/config/DB compatibility for the
chosen unit. Current target and project metadata verified; exact rollback
prepared. Receipt/correction RPC presence observed independently of migration
history; do not equate missing history rows to missing functions or consent.

Original preview UI remains NOT_RUN; reader7/7 is reused, not relabeled UI.
Private Getnet card:3exported groups/8timestamp samples, A/B/C MISSING after
manifest/mail/provider review. User delegated judgment without new facts.
Full synthetic client EN/IT rendered and observed at192DPI; no100/125 or
native keyboard PASS. Settings/input surface blocked, no scale changed.
Win7 target explicitly unavailable; existing kit hash unchanged.

Real DB integrity/FK PASS, sales/outboxes/history transactions0, no apply.
Session/trust updates after login do not imply byte-unchanged DB. Only owned
POS/observer processes stopped by verified identity; trust/backup/input/failures
retained. Test bytecode removal rejected by tool policy; retained, no bypass.
See [current report and closure matrix](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## Previous checkpoints retained as history

## Current checkpoint — 2026-11-08, qualified successor

Windows code/package `1823697f1f0c965fb8fd9d85259a862160f12c59` is
SOFTWARE_DELIVERED; overall BLOCKED_EXTERNAL. The documentation commit is
separate. The older tables and resume pointers below are dated history,
including pending authorization, installation NOT_EXECUTED and blank capture.
Authorization has been granted; the subsequent ordinary launch was rejected
by the tool (`blocked by policy`). No alternate launcher or second attempt.

| Gate | Current result / identity | Remaining dependency |
| --- | --- | --- |
| Retry real dialog wiring | PASS: initial d262ccd red retained; bounded continuation, authoritative empty history, normal full and authority rollback verified | Real HTTP/UI is separately blocked |
| Exact-SHA workflows | CI42497670447, Security42497670514, Release42497670533, WPF42497682626 SUCCESS | No software rerun for documentation-only closeout |
| Canonical validation | Core/Data1734/1734, required gates50/50, canonical15/15, functional20, foundation5/37, smoke8/52, load11/11 with all three measurement flags true | Windows11/reference qualification only |
| Download/install | Product41 files/110 checksums, QA40 files,34 common byte-identical; installed41/41 and registry version1.0.0-dev.1823697f1f0c verified | No automatic POS start; development-unsigned |
| Downloaded QA | Run Win7POS.QA.20261108T161042928Z.4be7680489dc43809a1a8461c9815ef9, exit0,8/52, external CWD, stable fingerprint; three actual synthetic captures readable | Full-form DPI100/125 and physical peripherals remain open |
| Original reader | Product-equivalent1823697 DLLs,7/7 controls and unchanged input hashes, exit0 | UI preview of originals and real apply remain blocked |
| CSV supplementary observation |50000 historical-only synthetic rows; query365.7015ms, CSV5656.8755ms, destination IO12.6125ms within original1000ms; Published/token/rows PASS | Separate from CI F-MIXED load; no total CSV budget invented |
| Live/real evidence | BLOCKED_POLICY after authorization; NeedsEvidence=true/CanApply=false | Environment owner restores ordinary launch; merchant owner supplies mapping, POS-start/overlap and provider Z/offset evidence |
| Admin/iOS | PR144 REVIEW, exact-code CI/Cloudflare PASS; existing PR29 note reused, previous server handoff observed in RESUME_STATUS | Admin owner review/rollout and ACK of new handoff pending; TASK146 ACTIVE/EXECUTION, PR29 open |
| Physical Win7 | BLOCKED_EXTERNAL; matching installer, QA, contexts and operator card prepared | Owner assigns real Win7 SP1/hardware and fresh run identity |

Detailed hashes, first failures, commands, observations and limitations:
[current E2E report](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md) and
[current Win7 operator card](../../reports/ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md).
Backup/profile/trust/originals and all failure evidence are retained. No real
apply, fiscal sale, outbox reset or account revocation was performed. Native
time policy gates mixed POS reporting separately from Getnet apply evidence.

Mandate: explicit Execution A+B+C request of 2026-11-04. Normative R3 input SHA-256
`47b9de5c4ced21f6deaa139717b153d8ea0d57ada761dfd3607f6bd193bfcf28`.
Baseline local HEAD, origin/main and GitHub main:
`32618249fc01f29e86c6774d2b2c9262a90f226f`; branch main. Preexisting plan,
index and worklog edits preserved. No reset, stash or clean.

| Requirement/gate | Status | Candidate / command / evidence | Dependency / next step |
| --- | --- | --- | --- |
| Repo sync | PASS read-only | Initial R3 baseline above; onboarding baseline e924a295; qualified code d262ccd on local/remote main | Documentation-only closeout after code qualification |
| Toolchain | PASS SDK / PowerShell | SDK 10.0.301 from pinned local installation; PowerShell 7.6.9 | No dependency upgrade |
| Original input availability/integrity | PASS independent audit | Read-only ZIP/OOXML private manifest outside checkout; all expected controls matched; source hashes unchanged | Not a product parser PASS |
| A parser / pure rules | PASS software | Final d262ccd CI and net48/x86 reader7/7; independent controls/files unchanged, QA DLLs verified identical to product | Real time/binding remain gated |
| B persistence / authority / recovery | PASS software | Final CI foundation5/37, Core/Data1734/1734; original failures and empty-delta red retained | Real onboarding requires authoritative catalog response |
| C reports / UI / export | PASS software | Final CI Smoke8/44 and Load11/11; two charts PASS | import_summary diagnostic NOT_RUN; real export and Win7 remain gated |
| QA automation/public privacy | PASS software | Canonical15/15 and public bundle/receipts verified; four explicit cleanup assertions PASS | Per-Job hosted residual counts are not exposed or inferred |
| Real time semantics | BLOCKED_EXTERNAL | Modern timestamp markers alone do not prove provider semantics | Authoritative provider/voucher evidence |
| Real shop/overlap | BLOCKED_EXTERNAL | No authoritative real-shop mapping/start date supplied | Binding, permissions and overlap proof |
| Physical Windows 7 | BLOCKED_EXTERNAL / NOT_RUN | Exact candidate package/QA and owner card ready; host unavailable | Owner assigns physical Win7 SP1 and peripherals |
| Main/CI/security/package | PASS software | Code d262ccd, four exact-SHA workflows, product/QA download verification and34 shared-file byte binding PASS | Final documentation-only closeout; external qualification remains |

Build/restore/test writer: root agent only. Delegated component file ownership is
nonoverlapping. All raw diagnostics and real financial controls stay private.
Previous FAIL/TIMEOUT evidence must remain available; no result is inferred from
static inspection, missing prerequisite, or a receipt schema alone.

Current resume point8Nov: catalog server fix Admin1a35034a applied, real lease-bound
manifest zero PASS; normal HTTP/UI NOT_RUN_POLICY (tool blocked process launch,
no process or new pull). Specific authorization pending; do not switch launchers.
See [409 fix](../../reports/ASUS_W7POS_019_CATALOG_409_FIX_RESULT.md).

Windows delivery remains SOFTWARE_DELIVERED at code/package
`d262ccd979db8e3407118ffa7275861f12fdb5e2`; see
[onboarding result](../../reports/ASUS_W7POS_019_ONBOARDING_EXECUTION_RESULT.md).
CI42432623358, Security42432623359, Release42432623356 and WPF42432636966 PASS.
Core/Data1734/1734, canonical15/15, gates50/50, functional20,
foundation5/37, smoke8/44 and load11. Downloaded product/QA, external-CWD smoke,
ordinary QA startup and reader7/7 PASS; NeedsEvidence remains true and apply denied.
The e832d6a empty→delta regression remains RED history; d262ccd requires a new
verified nonempty Full before restoring sale safety. Focused116/116 and8/44 PASS.
The historical409 is corrected at the resolver/RPC; console Mac, normal UI,
merchant/time/overlap and physical Win7 remain separately gated.
Visual diagnostic import_summary remains NOT_RUN, separate from UI contract PASS.
Next: Mac deployment/principal/scope handoff and external evidence; no rerun of
completed workflows. Documentation-only closeout differs from the binary SHA.
All earlier resume pointers, e832d6a greens, FINAL_CI_PENDING/FOCUSED_PASS and
Esc sections below are history; this pointer and newest verification are current.

## Intermediate execution evidence (2026-11-04)

- Data Release build: PASS, zero warnings/errors on the early integration candidate.
- WPF Release net48 x86 build: PASS on the intermediate candidate; six warnings in
  report code were reported to its owner. Later source edits are not qualified by it.
- Dialog standard: PASS, 37/37; no positioning/sizing exceptions introduced.
- `historical-ledger-initial.trx`: 0/8 PASS, schema postcondition rejected added
  native-table triggers. Corrected with narrow native writer/proof hooks; published
  migration materials/checksums and detector meanings retained.
- `historical-focused-03.trx`: 29/35 PASS; six failures were unregistered backup AUX
  contexts while their implementation was pending. No authority bypass added.
- `historical-ledger-04.trx`: 6/8 PASS; deferred undo-operation FK corrected before
  the next run. Migration0015 is unpublished working code; prior pins unchanged.
- `historical-backend-05.trx`: 125/126 PASS; one WAL-source backup regression found.
  Corrected owned backup partial normalization before read-only validation; source
  stays read-only. Exact final regression remains to be run.
- Full Core/Data run06 is RUNNING with the pinned SDK, one test writer. Canonical
  existing Core budget is 3600s (historical baseline measured 35m56s), not the generic
  600s default. No timeout was increased. Raw/TRX evidence is retained externally
  under the task-private test directory. Source has advanced after this test build;
  its result will be attributed to that intermediate binary candidate only.
- New foundation/smoke/load workers and closed receipt validators are implemented
  but NOT_RUN. CI remains CrashProbe until C is qualified; no synthetic PASS emitted.
- Operator confirmed the new Shop/account has not yet been created. This blocks
  real binding/apply qualification, not implementation or synthetic gates. Timestamp
  values/offsets in exports alone still do not resolve provider time semantics.

Current root-owned process: the single dotnet Core/Data run06 and its testhost;
no WPF or POS operational process launched. Do not delete its data/evidence.
Next: finish static integration review; collect run06 without rerun; build final
harness; run focused new regressions and canonical QA gates serially. Run paths,
source identity and process cleanup must be recorded before delivery.

## Budget clarification

The R3/user mandate states 60000ms per process for all twenty functional cases.
Inspection found the baseline wrapper had a 120000ms exception for FUNCTIONAL_VISUAL.
Execution applies the explicit mandate: all twenty now use 60000ms. No timeout is
raised or adjusted after a failing run; any resulting failure requires diagnosis.
The previous baseline discrepancy is preserved here rather than silently described
as unchanged. The historical foundation/smoke/load budgets remain 360/90/240s.

## Integrated working candidate before canonical Windows qualification

- Full Core06 completed: 1595 PASS / 53 FAIL / 1648 total, 12m38s, zero skipped.
  FAIL evidence retained. Root causes were explicit owner bootstrap, additive
  migration expectations/ledgerless recognition, a UI/Data boundary violation,
  and a duplicated native authorization demand; each was corrected and covered.
- Regression07: 298 PASS / 3 FAIL / 301 total. The three negative fixtures queried
  an absent ledger after the new earlier fail-closed check; their postconditions
  now accept no ledger or zero ledger rows while retaining unsafe-schema rejection.
- Native commit08: 11/11 PASS, zero skipped, including the unchanged 10s final
  safety budget and stock/outbox rollback behavior.
- Integration09: 185/185 PASS, zero skipped, covering import corrections, receipts,
  source integrity, schema negatives, report query/CSV/time policy and architecture.
- Fingerprint10: 22/22 PASS, zero skipped, including literal ASCII/Unicode digests,
  source handle protection, replay after revision/restart and merchant bounds.
- Latest WPF/harness Release net48 x86 build: PASS, zero warnings/errors.
- QA AST and Runtime tests: PASS; synthetic child output sentinels preserved.
  Owned process identity/deadline structural tests and typed TRX/gate projection
  tests PASS. These are infrastructure checks, not historical scenario PASS.
- Source privacy scan: no original corpus names/hashes/financial controls found
  in repository source, tests, documentation, scripts or workflows.
- Host verified: Windows 11 Home Single Language 10.0.26300 x64; SDK10.0.301,
  PowerShell7.6.9, Node24.18.0. This does not qualify physical Windows7.
- GitHub main rechecked at the baseline SHA; branch unprotected and effective rule
  list empty. No push has been attempted; no branch/PR created.

All component owners have frozen their files. Next command: canonical Windows
suite with HistoricalSalesMode All, sequential dependencies foundation -> smoke
-> load. C consumers are implemented; this is their integrated qualification,
not a claim of prior runtime completion. CI remains CrashProbe until C passes.
Do not edit repository files during the run: the orchestrator checks before/after
source fingerprints. Preserve all failures and private diagnostics.

## Canonical Windows run 01 — retained FAIL

Run ID: `Win7POS.QA.20261104T233753242Z.ce1ebda28b0c4253b6365a88abf2310a`.
Private evidence resides under the task-private qa-runs directory. Original reports,
all failed gate transcripts, child receipts and partial historical receipt retained.
No historical scenario PASS was inferred: foundation executed0/completed=false;
load NOT_RUN after dependency failure.

- PASS: restore, WPF build, harness build, authorization lease, bounded logging,
  paging/Dispatcher. Functional:14 PASS/6 FAIL. Overall FAIL; Core/selftest NOT_RUN
  because solution build failed in the linked CatalogPerformance fixture.
- Source gates found stale migration0014 sequence and inline-only commit markers.
  Corrected append-only0015 checks and deep helper/route validation; all four
  targeted gates PASS, including23 negative transaction/route mutations.
- CatalogPerformance now links the same owned fixture helper and its explicit
  test-only friend access. Solution `Win7POS.slnx` now builds with zero warnings/errors.
  An earlier manual `.sln` typo failed before build and was corrected to the actual file.
- Native proof v2 includes void markers; standalone void transaction ownership is
  in the transaction writer. NativeProof12:23/23 PASS, zero skipped.
- Historical WinExe workers were not awaited and an unrelated default-slot marker
  obstructed restart. Fixed with the existing waited helper/residual deadline and
  actual per-fixture authority before initialization, not a marker bypass.
- Secondary synthetic DBs and backup readers in five functional helpers/ProductImage
  now have explicit lifetime ownership or exact read-only snapshot contexts.
- Clipboard failure was a verification-read busy HRESULT after a single copy action.
  Verification yields only for that observed condition within a fixed1s subdeadline;
  it never repeats copy or increases the60000ms scenario budget. Content checks remain.
- JSON timestamp coercion rejected safe partial evidence. A strict JsonDocument
  reader now preserves date strings/Int64 on supported PowerShell hosts, rejects
  duplicate keys and bounds input. Original partial receipt structure was revalidated,
  without changing its incomplete execution status. PowerShell7.4 itself was not run.
- Latest QA AST/Runtime, process identity structural and projection tests PASS.
  Public finalization now preserves earliest primary and separates publication status.

Next: second canonical Windows/All run on the frozen corrected source, followed by
necessary focused diagnosis and exact-final CI/security/Release Pack delivery.
Do not edit source during a canonical run. PhysicalWin7, real account/binding and
provider time evidence remain distinct external gates.

## Canonical Windows run 02 — retained FAIL

Run ID: `Win7POS.QA.20261105T000608931Z.6138891a87cb4bc6a19a7d49b5146c99`.
Before/after source fingerprint is identical:
`4c489cb8831285ff4f1d1f793e8cfb429a1bd5350dc424ff858e8cce583ac490`.
Overall exit1; first failure is the source gates. Public evidence publication PASS
is separate from scenario qualification. Every launched root reports cleanup=true
and zero remaining processes. All raw failures and incomplete receipts retained.

- Solution, WPF and harness builds PASS. Core:1682 PASS/1 FAIL/1683 total, zero
  skipped; the backup settings fixture exceeds the Win7 destination length bound
  under owned TEMP. Fix fixture names, not the production limit.
- Selftest, authorization lease, bounded logging and paging/Dispatcher PASS.
- Functional:18 PASS/2 FAIL; all twenty retain the60000ms budget. ProductImage
  also FAIL; diagnose exact synthetic path/authority failures before rerunning.
- Historical foundation: genuine HOT recovery PASS; subsequent supervision FAIL.
  No complete foundation PASS. Historical smoke:seven scenarios PASS, lifecycle
  assertions pass but disposal fails, so scenario8 remains FAIL. Load NOT_RUN.
- Restore and receipt source gates still use old inline markers after the AUX
  and shared preview refactors. Replace them with deep checks and negative
  mutations, retaining their safety assertions.
- Independent review found a custom-role report scope defect: DailyCloseView
  without RegisterViewAll could request all operators. Repair the real loaders,
  immutable filter-bound publication permit, and epoch-only UI invalidation.
  Add actual VM regressions before the next canonical qualification.

The original seven private inputs were also read by the actual product reader
on net48/x86 using the run02 Core/Data binaries. Private probe PASS: independent
input controls matched, hashes unchanged, NeedsEvidence=true and CanApply=false.
The probe is read-only (no DB/apply/UI), outside the checkout; its financial
controls and input identities remain private. Windows11 net48 qualification does
not replace P-REAL apply/reimport/undo or physical Windows7 qualification.

Run02 is complete; source thawed for the corrections above. Root remains the
single build/test writer. Next: focused regressions, then frozen canonical
Windows/All on the corrected candidate. CI remains CrashProbe until C qualifies.

## Focused corrections after run02

- Data13:41/41 PASS, zero skipped, on an owned TEMP root matching the canonical
  path length. Covers rejected foreign/missing currency, explicit temporal choice
  in fingerprint/audit, receipt/replay/undo and the shortened backup fixture.
- Authority15:7/7 PASS, including distinction of lock contention from path/media
  failures. A later additional Win32 HRESULT facility assertion awaits final CI.
- Restore gate14:FAIL retained. Its new negative fixtures exposed an extractor
  that only recognized Task-returning methods, making five mutations inert.
  Fixed declaration extraction/comment handling and require actual mutation.
  Gate16 PASS, including all12 unsafe mutations; no production assert removed.
- Receipt gate16 PASS, including six unsafe analytical/print mutations. Process
  identity16 PASS for synthetic collector/departure and structural deadline
  contracts; this does not attest native process sampling or the B real probes.
- Actual WPF Release net48/x86 build after scope/epoch changes:PASS, zero warnings
  and errors. Harness runtime qualification still pending.
- ProductImage fixture basename shortened; authority failures now distinguish
  sharing/lock violations from path/access/other IO. Recovery pristine hashing
  closes the initializer's distinct pooled writer before exclusive byte checks.
  Functional scanner fixture waits for actual public CanExecute within its
  existing per-scan budget. No scan count, expected output or timeout changed.
- Before the first Load run, independent inspection identified incomplete
  measurement protocol bookkeeping: missing first/last/phase bounds and separate
  memory grid, thread-pool sampler and hardcoded completion. Complete the planned
  HISTORY-MEASUREMENT-v2 contract and negative truncation/gap/stall fixtures;
  do not certify a partial sample sequence or raise thresholds.

## Final integration corrections before run03

- Measurement17 FAIL retained: a PowerShell local `$expected` overwrote the
  `$Expected` context parameter (case-insensitive names). Renamed the slot count;
  Measurement18:82 pure protocol checks PASS, including truncation, missing
  phase/memory slots, shared pending probes, cost reconciliation, privacy and
  unqualified target-profile negatives. No actual Load PASS inferred.
- AutonomousRuntime19 PASS, including new cumulative B subbudget regressions,
  strict Load fixtures and bounded process/output handling. Reference limits
  remain30s analysis/15s DB apply/500ms warm; Win7's approved limits require an
  explicitly qualified trusted context plus hardware manifest identity.
- Intermediate final harness Release net48/x86 build PASS, zero warnings/errors;
  later sampler/read-authority corrections still require the next build.
- ReceiptMemory20 PASS:the previous six analytical/print mutations plus five
  mutations against passive authority/expiry/capture checks are rejected. R3
  forbids authorization/owner monitors on Dispatcher reads: immutable volatile
  authorization stamps replace those read-side latch calls. Final CSV permits
  continue to use the actual authorization latch outside UI callbacks.
- Finite review strengthened B subbudgets, distinct physical short-path proof,
  exact post-swap injected rollback proof, real keyboard/analysis heartbeat, and
  nonempty cart/command restoration after actual admission cancellation.
- A restore-to-export race was found in the cached report coordinator: an old
  token could remain memory-current after a completed restore/rebind. Bind it
  to current database lifetime authority; reject stale ObserveRead and serialize
  authority transitions with publication. Qualification remains pending until
  the dedicated regression and frozen canonical run complete.
- Reference host inventory was captured before the first Load:Intel Core Ultra7
  255H,16 logical processors,15.36GiB physical memory,NVMe storage,Windows11.
  Private inventory SHA256:
  `6281ecb90232147fb22ea327990b6758f8d1c373a4984d320d482901a61e448c`.
  This identifies the reference build host, not a physical Win7 target.

- AuthorityLifetime21:39/39 PASS, zero skipped, including four new lifetime,
  epoch, real restore and publication serialization tests. A nullable fixture
  warning was removed by explicitly rejecting a missing marker. Subsequent
  transition timeout semantics: failure to acquire the publication gate changes
  no stamp; successful transitions alone publish under that gate.
- Final harness build:PASS, zero warnings/errors. Its queued modal keyboard
  script is observed through its completion source; explicit discard of the
  DispatcherOperation removes the incidental compiler warning.
- RequiredGates22:50/50 PASS, including Dialog Standard and the QA protocol/
  negative suites. This is a preflight, not the full Windows qualification.

Next frozen command:

```powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode All -OutputRoot C:\Dev\_codex-private\ASUS-W7POS-019\qa-runs -PublicOutputRoot C:\Dev\_codex-public\ASUS-W7POS-019
```

Keep run03 failures/partial receipts if any. Do not edit tracked docs, source,
fixtures or workflows during the run; the before/after fingerprint covers them.
CI remains CrashProbe until the integrated C/load qualification is observed.

## Canonical Windows run03 — retained FAIL and focused continuation

Run ID:`Win7POS.QA.20261105T010900248Z.ea3306d57ec841c3bd06c6da4bcdc838`.
Source fingerprint before/after:
`3f9a943f47bfde406d1336253223f8399c06829c4e27340bb419cc9040920fac`.
Exit1; earliest failure functional-completion. Public publication PASS is a
separate result. Every launched step has cleanup=true and processRemaining=0.

- Core1692/1692 PASS, zero skipped,13m27s. Restore, gates50/50, solution/WPF/
  harness builds, CLI selftest, ProductImage, authorization lease, bounded
  logging and paging/Dispatcher PASS.
- Functional19/20:Recovery FAIL after its5000/1000 supported batches completed;
  fixture teardown attempted owner disposal while actual UI refresh was still
  running. Close/drain the real operation/refresh flags before fixture disposal,
  within the existing case60s budget. No database authority bypass.
- Foundation4/5:HOT, commit authority, admission/nonempty cart and backup/restore
  PASS. Entrypoint's case/shortpath and reparse/hardlink aggregate checks FAIL.
  Old evidence lacks individual variant codes:do not infer a production alias
  defect. Instrument exact native results/identities/codes privately and create
  a genuine owned NTFS junction; retain strict aggregate assertion IDs.
- Smoke6/8:H07/H08 failed at fixture creation with trusted_time_continuity_lost,
  after the harness exhausted the production limit of16 synthetic clock domains.
  Reuse one coherent monotonic synthetic domain and fresh response anchors.
  Product clock limits/high-water protections and all assertions remain unchanged.
  Load NOT_RUN by dependency; no performance result promoted.
- NativeProofNegatives23:22/22 PASS, zero skipped. Real schema0015/canonical
  outbox verification covers N1/N2/N3/NX, no current-shop fallback, all read
  surfaces and native writer/token invalidation. No Verified projection forged.
  These are additional tests after run03, not1692 tests relabelled as1714.
- Read-only privacy inventory:211 eligible changed/untracked files and10 OOXML
  parts, no private corpus identities/controls or operational artifacts. Three
  PEM headers are preexisting synthetic redaction vectors. Original hashes and
  metadata reconfirmed7/7 unchanged.

To avoid repeating already valid Core work for fixture-only diagnosis, reuse the
actual canonical Invoke-QaProcess supervisor/Job/isolated directories and existing
functional Scenario / historical Mode wrappers for focused regressions. Generate
fresh run/context/nonce from current source and binary hashes, validate real
receipts and cleanup, and preserve every failure. No alternate process launcher,
new mode, fabricated dependency status or overall Windows PASS is introduced.
After C/load qualifies, delivery still requires canonical CI All on the exact
commit, Security Supply Chain, Release Pack and separate QA payload.

## Focused continuation — retained failures and software qualification

The root reused `Invoke-QaProcess`, its existing Job supervision and the actual
functional `-Scenario IMPORT_RECOVERY_completion` / historical `-Mode` wrappers.
All contexts/nonces, owned directories and binary/source identities were fresh;
no alternate launcher, weakened assertion or fabricated dependency PASS was used.
These are focused canonical steps, not an overall Windows qualification.

| Focused run suffix (all `Win7POS.QA.20261105T`) | Observed outcome retained |
| --- | --- |
| `014153898Z.12f596f503db4ac98315474f16fc484e` | Recovery PASS,32.232s wrapper, cleanup=true/remaining0. Initial historical calls failed before scenario receipts because root omitted output-directory creation; setup evidence retained, not a product failure or PASS. |
| `014440523Z.681d379b6ab64dfc95b68b2e148d231e` | Foundation FAIL on shortpath distinctness; Smoke7/8, H08 failed before completing its assertion set. Load NOT_RUN. |
| `015030831Z.b1eb4ac536a844e38725d1c9779705ba` | Foundation5/5 PASS,25.264s receipt/26.389s wrapper, cleanup=true/remaining0. Smoke7/8, stale_result_ignored FAIL; Load NOT_RUN. |
| `015519428Z.ec48fc99c1eb48c1a88f30e7fe3d3b28` | Smoke7/8, stale_result_ignored FAIL; Load NOT_RUN. |
| `021148797Z.f9a515dc0e774933bb3ea192afe29b49` | Smoke7 PASS/1 NOT_RUN with incomplete H08; Load NOT_RUN. Preserve the blocked-reader diagnosis and process evidence. |
| `021708997Z.fc392d44a84e4e9888e683e0040dbe1d` | Smoke8/8,44/44 assertions; actual Load1/1,11/11 assertions. Both wrappers exit0, qualified=true, cleanup=true and processRemaining=0. |

Recovery now closes and drains the real Sync Center operation/refresh before
fixture disposal. Foundation variant evidence separately proves the case alias,
distinct8.3 spelling of the same physical file, hardlink and real NTFS junction,
with exact authority refusal codes. On .NET Framework48 `Path.GetFullPath`
expands8.3 names; compare the absolute raw Win32 spelling before that expansion,
while still requiring handle identity and rejection of the actual alias. No
production database authority change was needed for that test defect.

Fixture clocks share a coherent monotonic domain with fresh authoritative response
anchors; the product's bounded domain/high-water protections remain unchanged.
H08's stale-result proof now uses fresh valid sessions and genuine pending report
loads behind actual factory maintenance, across epoch invalidation and logout.
Both observed witnesses report pending=true, immediate=true, noOrphan=true,
cleared=true and unchanged=true. The epoch witness has zero SessionChanged events
at the immediate read; the logout witness observes its legitimate event. The
test never requires legitimate later logout events to remain absent.

The real held-maintenance probe exposed a production Dispatcher deadlock:
`SecurityRepository.LogEventAsync` synchronously called `_factory.Open()`.
It now awaits `_factory.OpenAsync().ConfigureAwait(false)`. The actual regression
`SecurityAuditMaintenanceTests.AuditReturnsAnAwaitableWhileMaintenanceIsHeld_ThenPersistsAfterRelease`
and `NativeSalesOwnershipProofTests` pass23/23, zero skipped, in private
`tests/security-audit-maintenance-25.trx`. This verifies awaitable audit admission
and eventual persistence after release; it is not an arbitrary tamper detector
or a deterministic CAS-race test, and is not a new full Core count.

Latest focused source fingerprint before/after is identical:
`f38745b6b1f1c6134daf9eecfb7081af977b8477fee3448a98f40f9832870e76`.
Harness SHA256:
`4416b901428eae14ffd0d12b8fef5195445ff8cea629f477270214dcacba3511`.
The source SHA in the receipt is still the baseline commit plus dirty working
tree; no final implementation commit SHA has been assigned here. Evidence is in
the private focused run directory named above; raw previous failures remain.

Load uses `HISTORY-MEASUREMENT-v2`, targetProfile=`reference-windows`, a dedicated
sampler thread and the fixed50ms Dispatcher/100ms memory grids. Its independent
flags are measurementCompleted=true, environmentValid=true and thresholdsPassed=true.

| Actual synthetic measurement | Observed value |
| --- | --- |
| Source / accepted / rejected | 50000 /49000 /1000 rows |
| Mixed report population | 10000 native +49000 historical rows |
| Analysis | 1390.464ms |
| DB apply / warm query | 7862.516ms /169.733ms |
| Dispatcher p95 / maximum | 0.464ms /16.341ms |
| Dispatcher probes / memory samples | 393/393 /197/197 |
| Pending / dropped / memoryDropped / overflow / suspended | 0 /0 /0 /0 /0 |
| Sampled peak private bytes | 86753280 bytes,82.734MiB |
| Smoke / Load receipt duration | 25.658s /21.812s |
| Smoke / Load supervised wrapper duration | 27.731s /24.088s |

The reference host and inventory identity above apply. No PowerShell7.4 runtime,
physical Windows7, scanner/printer or real corpus apply qualification is inferred.
The focused envelope explicitly sets fullWindowsQualification=false. Run03 is
still overall FAIL, including its historical Load NOT_RUN and original failures.

Final required-gates26:50/50 PASS, exit0, in private
`tests/required-gates-final-26.log`. CI now selects `HistoricalSalesMode All`
following observed C/load qualification. Final privacy inventory:216 eligible
files plus10 OOXML parts, clean; the external qualification card names the
applicable skill portably without a user-profile path. Final code SHA, CI All,
Security Supply Chain, Release Pack and exact separate QA payload remain pending.

## First main candidate hosted qualification (2026-11-05 UTC)

Main/code SHA77427d1f943c271e040dd5a67f58c34a3d2b83c1 was pushed without force,
with branch rules read as empty and remote baseline unchanged. Clean fingerprint
3100112310734093d2e5137be1993171473e09342081fdd2c3c9a79054c7f1a4.
CI41955182242 (run022701667Z.56d79e8536c7404da3af0c24916cb4d9) retains FAIL:
Core gate PASS (1085366ms), functional20/20 and historicalSmoke8/8 PASS; foundation
3/4 completed cases PASS, case_shortpath_alias_collide=false, backup case NOT_RUN.
Actual genuine HOT, commit authority and admission passed. Load completed with
measurementCompleted/environmentValid=true but thresholdsPassed=false: apply
18014.0284ms exceeds15000ms. Its795 Dispatcher/398 memory samples are complete,
zero drop/pending/overflow/suspend, p95 .2897ms/max3.4235ms, warm359.8263ms.
Public bundle publication PASS, separately from scenarios FAIL; downloaded GitHub
archive and inner bundle digests verified. Private evidence remains retained.
Security41955182236 PASS (including CodeQL), WPF41955181554 PASS. Downloaded QA
bundle39files, manifest/fixture/closure hashes verified; foreign empty-CWD smoke
run022928230Z.6d6b5892cd06491193d683655047681e8/8+44checks PASS, cleanuptrue/0.
These are attributed to77427d1 only, not a future correction SHA.

Root is correcting two concrete causes: provision a genuine owned8.3 fixture
on an available local NTFS volume if the prepared path has no short alias (absence
is consistent with hosted-volume configuration, not proven by exported detail);
reuse prepared transactional SQL commands and binding/merchant resolution to
remove repeated preparation and identical updates across49k accepted rows.
All variant assertions and15000ms apply threshold remain mandatory. No global
volume setting, budget, permission or productlimit change. Root alone runs builds
and focused regressions; exact final CIAll/security/release/QA payload must follow.

## Prepared-apply correction checkpoint (2026-11-05 UTC)

Only HistoricalSalesImportService.cs and HistoricalSalesAuthorityVariants.cs
changed runtime behaviour after the first hosted failure. The service reuses
five typed SQLite statements within the same transaction, preserves per-row
active/latest-inactive lookup and intra-batch visibility, updates used_at on the
first inserted row of each binding, and resolves immutable merchant evidence in
an operation-local cache bounded at200. Integer/nullable values, checked sums,
source handles, approval/replay/receipt/hooks and commit guard remain unchanged.
The harness always exercises the independently owned local-temp NTFS copy too:
real raw8.3, identical physical file, database_in_use, consistent copy, owner
released, no preparation error. It uses that proof if the prepared fixture lacks
a genuine alias; no global filesystem setting or skipped assertion.

Prepared-apply-regression27:113/113 PASS, zero skipped,38s; all Historical/Getnet,
PosDatabaseAuthority, audit maintenance and native-proof filtered regressions.
Harness28 Release net48/x86 PASS, zero warnings/errors. Focused canonical group
Win7POS.QA.20261105T030459605Z.e0d33b992cf94cf192cc895c56917d80: foundation5/5,
Smoke8/8+44checks, Load11/11 PASS; existing supervisor/Job/context/validators,
all exit0/cleanuptrue/remaining0. Source fingerprint
842ca111714aecfb5928d3929293b17644c6e134a1b6dac79efc7427cba2d428 unchanged.
Wrapper times B24142ms/H26986ms/L18114ms. Load analysis1450.2855ms,
apply2184.1727ms (previous local7862.5155), fullservice2393.6904ms,
backup175.4882ms/coldownership4384.695ms/warm177.6638ms. All three qualification
flags true;280/280 dispatcher and140/140 memory samples, zero pending/drop,
p95 .4894ms/max21.6212ms, peak86450176bytes. No budget increase.

Old Release41955182205 was explicitly cancelled as superseded after CI failed;
its unfinished Core/package are not claimed PASS. Other first-candidate results
remain attributed only to77427d1. Next: final required gates29, clean task-only
main correction commit, new exact-SHA workflows/artifacts. No real apply/Win7.

Required-gates29:50/50 PASS after the prepared-apply/alias correction. Only the
seven task source/docs plus worklog are staged for the correction commit.

## Final software delivery, exact code5c3327b (2026-11-05 UTC)

Code/main5c3327bd320293ad48edc7d9cc01ad2466e591b4, clean source fingerprint
72d047e1665b2dce5308a7d5f68540e5f603cef2da74e8ba090a8e4388e251e9.
CI41959769748 All PASS, runWin7POS.QA.20261105T031014179Z.733127f7e27545a6aac98da382290591:
15/15 steps; Core gate1051290ms; functional20/20, foundation5/37, smoke8/44,
Load1/11. No empty checks or residual Job processes qualify. Load flags alltrue,
analysis2537.2931ms/apply3783.8476ms/service4031.5146ms/backup192.5817ms,
ownershipcold6928.9462ms/warm365.9836ms;506/506 dispatcher+253/253 memory,
zero pending/drop/overflow/suspend, p95 .2572ms/max9.8629ms, peak89022464bytes.
Current public artifact13386211267 downloaded/digest-checked; inner two-file
pair Test-QaPublicBundle PASS, correct run/source/fingerprint. No receipt-derived
Expected context was constructed to manufacture a qualification.

Security41959769752 PASS including CodeQL/dependency/secret/SBOM/reproducibility.
Release41959769763 PASS: Core1715/1715, zero skipped,38m01; gates50/50, CLI,
packaged x86/ValueTuple/provider-consumer closure and integrity/provenance gates.
Version1.0.0-dev.5c3327bd3202. Existing canonical verifier reused:
pwsh -NoProfile -File scripts/win7pos/windows/test-downloaded-release-pack.ps1
-RunId41959769763 -ExpectedCommitSha5c3327bd320293ad48edc7d9cc01ad2466e591b4
-OutputDirectory(new private delivery root)/release-download. Actual argv used
separate parameter/value arguments; this ledger is a sanitized command summary.
PASS:41 product files/110 checksummed, setup/dist/payload correspondence,
SBOM/provenance/attestation, development-unsigned,0 signing phases;
installation NOT_EXECUTED. Artifacts: ReleasePack13386956585, Setup13387265413,
dist13386866791. ZIP SHA256c5dbaa380378d382f5c6a29178e081d444a3c9f57a1864cec8af3e80e53f558c;
installer SHA256f0897aab5a9d33ecdc6a411d1c367c81a8885268d20deca5324e08dc5683394d.

WPF41959770648 PASS with historical_qa_payload=true. QA artifact13385071104,
39 files, closure/manifest/fixture hashes verified after download. Fresh actual
foreign-empty-CWD runWin7POS.QA.20261105T031330057Z.3557fff79f954e9cbe82fa4a1eed9907
PASS8/8+44, source/hash/context bound to downloaded bundle, cleanuptrue/residual0.
Product and QA share33 byte-identical files including Core/Data/WPF. Operational
payload contains no harness, XLSX, database or TRX. QA remains separate.

Actual final product net48/x86 reader: new private console probe references only
the downloaded verified Core/Data DLLs (runtime hashes match product), new
receipt and input hashes independently checked before/after. PASS7/7 unchanged,
private independent financial controls matched, NeedsEvidence=true, CanApply=false,
HasErrors=false, exit0/4313ms. No DB/apply or credentials. Earlier run02 result
remains intact and was not substituted for this actual final reader run.

Correction to earlier cancellation annotation: first Release774 Core1715/1715
actually completed SUCCESS at03:00:46 and CLI began03:00:50, before cancellation
in Inno03:01:15. Thus Core was individually PASS; overall first Release remains
CANCELLED and its incomplete package was never qualified. First CI41955182242
remains FAIL, as do all recorded historical FAIL/TIMEOUT/NOT_RUN checkpoints.
Different hosted Core durations have no established single cause; no timeout,
assertion, permission or filesystem-global setting was relaxed.

All local QA Jobs confirmed cleanup/zero descendants; final reader exited and
MSBuild/C# build servers shut down via the dedicated SDK's standard command.
Private failure roots, W7Q mappings and W7Q83 synthetic copies/provenance are
retained intentionally. No recursive cleanup, unrelated process termination or
original/operational-data mutation. Final documentation delta consists only of
AI_WORKLOG, CODEX_ASUS_TASKS, PLAN, EXECUTION, result and external qualification.
It uses a documentation-only commit with [skip ci] to avoid duplicate full suites;
no required branch protections are bypassed. Tests/packages remain attributed
to code5c3327b; final documentation SHA is reported in the delivery message and
private closeout metadata, and git diff code5c3327b..HEAD verifies this split.

Software delivery is complete. Overall task remains BLOCKED_EXTERNAL for real
Shop/mapping/start/overlap/provider-time evidence and physical Win7 SP1/peripherals.
No prior owner-defer is inherited. Owner card now supplies exact product/QA hashes;
next work requires those external prerequisites, not another implementation tranche.

Final privacy:216 distinct task files,222 content versions including all6 finaldocs,
10 OOXML parts/2 synthetic workbooks; zero actionable privacy findings. Only3 PEM
headers are preexisting synthetic test vectors. Final6-doc delta has0 broken
relative Markdown links and git diff --check PASS, with no runtime/source/config
changes after qualified code5c3327b.


## Checkpoint storico onboarding 2026-11-07 dopo Esc

Main candidato `e832d6a96d5a736145a5da0cfb9ad2dffeba25dd`, feature959396b,
pubblicati. Security42418832124 e WPF42418868960 PASS; QA scaricato40file
con pin forwarder e smoke esterno8/44 PASS. CI42418832135/Release42418832168
ancora in corso al checkpoint: leggere gli stessi run, senza rilanciarli.
Avvio ordinario QA interrotto dall'utente prima dell'osservazione, NOT_RUN.
Nuovo prodotto/reader reale e chiusura documentale restano da completare.
Tutti i processi locali posseduti chiusi; profili/log/prime failure conservati.
Il report onboarding contiene la ripresa precisa; nessuna qualifica reale o
Win7 promossa, Mac PENDING e prove esercente/tempo/overlap assenti.


## Verifica conclusiva onboarding e832d6a — 2026-11-07

- Main pubblicato: feature959396b + closureQAe832d6a, nessuna branch/PR/force push.
  Admin main resta d021a4af, deployment non verificato, handoff invio/risposta PENDING.
- Canonica CI15/15 exit0, functional20/20, foundation5/37, smoke8/44 e load11/11.
  Core/Data1728/1728 e gate50/50 dal log Release dello stesso SHA; la proiezione
  CI espone1653 nomi e non il totale parametrizzato. Load: measurementCompleted,
  environmentValid e thresholdsPassed tutti true, limiti invariati.
- Fingerprint clean c2e98ce089d0b8e4d0b1f4a8a5c5d2bb93ec53fb6be0fc92e91cdbab361d6944.
  RunCI Win7POS.QA.20261107T220502828Z.7c1c98cb41734822b300eccbcdd9e949;
  artifact13593925422 digest ff7058c2be606caf13440d0a89ba886ba74f1e6b57d1d4419eb1a96325795a3e
  verificato, Test-QaPublicBundle e tre validator storici PASS.
- Prodotto scaricato:41file,110checksum,development-unsigned,installation NOT_EXECUTED.
  QA40file/forwarder pinned,34file comuni byte-identici, smoke CWD esterna8/44 PASS
  runWin7POS.QA.20261107T220911950Z.d2c7761c1e1e495da1bc398a7e2de26f.
- Avvio ordinario QA alla ripresa: PASS su profilo vuoto senza credenziali/sessioni,
  osservazione Computer Use e processo posseduto chiuso. Primo NOT_RUN da Esc
  resta distinto e conservato. Nessun bypass o retry live.
- Reader solo DLL prodotto net48/x86:7/7 originali/oracolo integri,exit0,4551ms,
  NeedsEvidence=true/CanApply=false; nessun DB/apply reale. Prove reali mancanti
  confermate dall'utente; login reale5c PASS resta evidence del proprio tentativo409.
- Catture hourly2PASS; import_summary NOT_RUN qa_visual_blank_withheld, nessun PNG.
  Contratto UI5/5 PASS distinto da quella verifica visiva non disponibile.
- Prime failure e checkpoint Esc conservati; correzioni di fixture/contatori/focus,
  stato e ramo generic unsafe documentate nel report, nessun gate/soglia rilassato.
  Root privata onboarding-20261107T212334Z.7cb660d9f0ee487eb524ef62ed9b5767;
  tutti i processi locali posseduti chiusi, dati/log/backup sintetici intenzionalmente
  conservati con mapping, nessuna revoca/cancellazione utente. Hosted cleanup:
  quattro assertion PASS, conteggi per Job non pubblicati, nessuno zero inventato.
- Chiusura solo documentale dopo e832d6a; non richiede suite software duplicate.
  Gate reali/Win7 restano BLOCKED_EXTERNAL e la task non è DONE.


Focused fence green: Core116/116; real loopback8/44 with immutable red case and
fullpositive recovery; localgates50/50. Source frozen. NextexactSHA workflows,
new artifact verification/reader and document-only closeout.


## Verifica finale correttivo d262ccd — 2026-11-07

- Core/Data1734/1734 e gate50/50 dal log Release42432623356;
  CI42432623358 canonica15/15, functional20/20, foundation5/37, smoke8/44,
  load11 con measurementCompleted/environmentValid/thresholdsPassed true.
- Fingerprint6ae00d6f8b1aa6f76a81b97804066a1e5ece2e76a6e419a276ea5a7cd98dff93;
  runCI Win7POS.QA.20261108T010513694Z.48d7f3fff9ca4cbf93dcf90d91296025, artifact13599430953, digestab8822be70a2c71144272486781d0fda080c3d9d91b787c346b654d7ac15b0e0.
  Test-QaPublicBundle e tre validator storici PASS; cleanup quattro assertion PASS,
  nessun conteggio hosted inventato. Security42432623359 e WPF42432636966 PASS.
- Release scaricato41file/110checksum, QA40file e34 comuni identici; stadio
  development-unsigned, installation NOT_EXECUTED. Smoke scaricato8/44 e avvio
  ordinario WPF senza login PASS, processo posseduto chiuso. Reader DLL QA
  verificate uguali al prodotto:7/7 originali/oracolo,4588ms, file invariati,
  NeedsEvidence=true/CanApply=false. Nessun apply reale.
- Il rosso Core1/1 e loopback7/8 e832d6a resta conservato. Nuovo fence persistente
  fino full nonempty atomica, focused Core116/116, loopback8/44 e gate50/50 PASS.
  Nessun cambio alle assertion/budget originari, recovery o contratti cloud.
- import_summary NOT_RUN/unavailable; due chart PASS e contratto UI5/5 distinti.
  Mac invio/risposta PENDING, console/catalog409, prove reali e Win7 aperti.
  Tutti i processi locali posseduti chiusi; profili/log/fixture conservati con owner.
  Aggiornamento documentale separato da d262ccd, nessuna suite software duplicata.


## 8Nov2026 — server409 fix applied, HTTP/UI pending policy

## 10Nov2026 — prova reale5d, race del refresh catalogo

Stessa Execution ACTIVE. Installazione5d8e704 effettiva,41/41 file confrontati
con lo ZIP qualificato,10/10 file profilo invariati prima del login. Utente ha
avviato dal menu Start e inserito personalmente il PIN: shell autenticata,
Online, catalogo vuoto esplicito e nessun popup storico automatico.
Menu→riepilogo→History→Import history apre volontariamente il form vuoto,
titolo/footer contenuti al200%;0file,Analyze/Confirm disabilitati. Escape chiude
la secondaria e il processo19404 resta vivo con la sessione.

Il rientro NON è qualificato: la shell viene portata ai Products in recovery.
Log10:21:02.836 inizio pull periodico,10:21:03.321 recovery catalog_not_sale_safe,
senza completamento del pull. La lettura dello stato shell può osservare la
cancellazione transitoria della prova empty prima della pubblicazione finale;
l'ingresso recovery ferma poi il publisher. Non è dimostrato un difetto di Escape.
Nessun retry live, Apply/Undo o file storico selezionato per questa diagnosi.

Correzione isolata da0378e17 in codex/asus019-catalog-refresh-shell-race: il reader
shell acquisisce il CatalogShopTransitionBarrier già detenuto dal pull. Legge
il risultato completo e rivalida comunque sessione/generazione; una pull fallita
senza nuova prova rimane Recovery, la cancellazione del wait è rispettata.
Non conserva prove invalidabili né abilita vendita/pagamento. Fixture WPF estesa
con refresh sospeso, successo, fallimento e cancellazione; esecuzione PENDING.
Build Release/net48/x86 prodotto+harness PASS0warning; dialog standard39/39 PASS.
Primo build fixture FAIL per binding dynamic non referenziato, conservato:
risolto con DTO tipizzato senza aggiungere dipendenze. Nessun test UI locale
con POS reale aperto; prova nativa demandata alla CI isolata.
UI live lasciata protetta in recovery, installato5d invariato. DPI100/125,
focus visuale affidabile e nuovo rientro richiedono il successore qualificato.

Le righe sotto restano il checkpoint8novembre, non lo stato corrente.

Admin candidate d864c740 / normal merge1a35034a, CI42451262145/CF42451262142 PASS.
67 file / 4.375 test pgTAP, foundation1.131 PASS / 14 SKIP / 0 FAIL, UI48/pairing2. First555f502
preparation hash failure preserved; canonical LF pin fixed without changing SQL/tests.
Migration source20261108051044 applied once; own generated ledger record aligned
atomically, final statement hash byte-identical. Actual pg_proc metadata except
body unchanged. V3 with current real lease returns ok/shop_scoped/done/0counts.
No Worker or Windows code change. App launch rejected by tool policy, no process
and no new pull verified; explicit authorization pending, no alternative launcher.
No Getnet apply/sale/reset/revocation. New report holds evidence and next step.
