# ASUS-W7POS-019 — Retry, qualifica software e gate end-to-end

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

## Latest combined UX gate — ACTIVE

The old chain was reproduced: historical child Close preserves the login;
Cancel of the underlying unaccepted login closes MainWindow and its login-owned
historical session. No evidence attributes direct process exit to child Close.
New login transfers a version-fenced authenticated session exactly once, including
verified-empty shops without history permission. MainWindow defers commercial VM
until authoritative sale safety and start-of-day gates. History is voluntary in
the existing report. Report numbers are unavailable until a current calculation.

Focused real-MainWindow fixture PASS (13.297s): empty login, three import close
paths (button/X/Escape), same PID/HWND/session/filter/model, report Escape/cart/
scanner focus, restart without history permission, full authoritative transition
and ready login/scanner. Separate geometry22observations10.870s and integrated
sale/retry/receipt/restart1.904s PASS. Required gates50/50 PASS. New source is still
local; canonical Windows All, exact-SHA package/install and actual NEW_SHOP_019
navigation/review-only remain required. No original Apply/undo repeated.

Evidence pointers: ux-navigation-03-root.private.txt, ux-navigation-02-root.private.txt,
ux-core-initial-root.private.txt, ux-required-gates-02.private.log under existing
private resume. nav01 TIMEOUT60s retained (modal observer fixture,11positive
asserts); unchanged process budget after observer fix. Previous a828 validation
below remains attributed to a828. Physical Win7/second monitor absent; modern C
and CSV input still unavailable. Restored native200% already observed; candidate
100/125/1024×768 and installed200% remain pending, not inherited PASS.

## Stato corrente — 9 novembre 2026, a8286c6 qualificato e installato

Ultimo requisito UX in Execution: dopo accesso valido entrare nella shell anche
con catalogo autorevolmente vuoto, vendite ancora negate; storico volontario
dal riepilogo e chiusure secondarie senza logout/uscita. Gate UX ACTIVE, oltre
alla geometria. La patch locale non è pubblicata: HEAD/main c54be47, installato
a8286c6 ricontrollato, nessun runtime attivo. Snapshot privato pre-shell-ux
conserva il delta geometria prima delle modifiche sovrapposte. Il vecchio percorso
login/workspace sotto è evidence della vecchia UX, non il nuovo criterio PASS.

Addendum geometria ACTIVE nella stessa Execution: centro della work area,
owner autentico, titolo/footer raggiungibili anche a200% e con annidamenti.
Display200% ripristinato dall'utente e form vuoto osservato; pulsanti ancora
visibili, decentramento osservato senza attribuire un clipping non dimostrato.
POS chiuso ordinariamente prima di fixture/harness. Il successore geometria
richiede nuova qualifica; a828 non chiude questo nuovo gate. Sugli originali
si eseguirà soltanto review del reimport, senza nuovo Apply o undo.

Correzione condivisa implementata, nuovo candidato da congelare/qualificare;
installato operativo ancora a8286c6. Il focused geometria separato dal worker
storico passa22misure in10.065s processo, con owner e card reali della catena
login loopback → workspace → import → conferma → messaggio/parent. Cancel
scrive zero, conferma una sola operazione; traversal, ritorno all'owner,
ridimensionamento NOMOVE e trascinamento non ricentrato PASS. Sei expected
rettangolari indipendenti coprono origini negative, taskbar superiore/laterale,
work area ridotta,1024×768 richiesto e1024×600 best effort. Sono simulazioni,
non una certificazione fisica multi-monitor. EN/IT/ES/ZH osservate.

Il primo rosso misura overlay fuori centro di424/136pixel, import oltre il
bordo utile e footer del messaggio lungo fuori superficie. Il primo fix
intermedio contieneva ma non centrava il non-overlay: WPF applicava
SizeToContent dopo la posizione iniziale alla dimensione minima. Il successore
misura prima di show, fissa le dimensioni iniziali entro il cap e lascia il
contenuto variabile nello scroll interno. Il clamp ora tratta anche NOMOVE e
NOSIZE, riprende i campi non definiti dal vero HWND e ricontiene senza
ricentratura continua. Overlay sempre sulla work area; owner autentici invariati.

| Superficie sintetica al200%,192DPI | Work area pixel | Card/finestra pixel `(x,y,w,h)` | Centro / bordi / footer / primo HWND |
| --- | --- | --- | --- |
| POS access | 0,0,2880,1704 | 880,409,1120,886 | PASS; offset0/0 |
| Historical workspace | stessa | 815,558,1250,588 | PASS; offset0/0 |
| Historical import vuoto/analizzato | stessa | 720,332,1440,1040 | PASS; offset0/0, footer fuori dal corpo scorrevole |
| Confirm import / cancel | stessa | 352,572,2176,560 | PASS; offset0/0, nested owner conservato |
| Messaggio lungo | stessa | 1000,48,880,1608 | PASS; messaggio scorrevole, pulsante esterno |
| LanguageSettings, quattro lingue | stessa | 1010,628,860,448 nel primo caso | PASS; misure per lingua nella receipt privata |
| DailyReport | stessa | 720,332,1440,1040 | PASS; contenuto/report sintetico |

Expected monitor acquisito dal test via GetMonitorInfo, distinto dall'helper
di produzione. Primo HWND effettivo catturato al primo WINDOWPOSCHANGED visibile;
superficie overlay misurata con RequireOverlayCard/PointToScreen. Pixel fisici,
DIP relativi, owner, monitor e footer sono distinti nella receipt. Tolleranza
centro2pixel. Il prodotto registra una sola osservazione first_render redatta
nel log, senza testi o dati finanziari, per verificare anche l'installato.

Build Release x86 senza warning e guard50/50 PASS. I primi gate45/50 e49/50
erano aspettative letterali obsolete CenterOwner e fixture functional20: sostituite
con policy comune e contratto21, mantenendo tutte le prove precedenti e aggiungendo
geometria come worker separato entro60s. Nessun aumento del budget storico60s
o del passo90s. Recovery sul nuovo codice passa30.160s dopo sostituzione della
propria vecchia aspettativa owner con work area/card/CTA più forti. La prima
failure di questa prosecuzione era tale aspettativa, non il vecchio weakref.
Il precedente FAIL locale19/20 resta conservato: nessuna causa del weakref
dichiarata risolta da un verde diverso o retry invariato. I primi errori di
compilazione/invocazione del test, prima dell'avvio, sono conservati privati.
Restano da eseguire qualifica exact-SHA,100/125 del nuovo codice, installazione,
riapertura reale e verifica200% finale. Gate UI complessivo ancora ACTIVE.

Stessa ASUS019/R3, ACTIVE; software REVIEW. Codice/pacchetto
`a8286c669a7c0d8ec4f82e1e7a31fc1b5d46eed3`, versione
`1.0.0-dev.a8286c669a7c`, net48/x86, development-unsigned, su Windows11
Home Single Language10.0.26300 x64. Il successore cambia soltanto la fixture
rispetto al fix ca4e437: tutte le assertion e il budget processo60s restano
invariati. Il fix mantiene il piano durante la conferma, nega la rientranza e
respinge una review modificata mentre il popup è aperto. La regressione
riproduce lo stesso errore postcommit; lo stack originario live non è disponibile.

| Superficie | Expected / osservato ed evidence |
| --- | --- |
| Qualifica exact-SHA | [CI42673051923](https://github.com/XNIW/Win7POS/actions/runs/42673051923), [Security42673051757](https://github.com/XNIW/Win7POS/actions/runs/42673051757), [Release42674141171](https://github.com/XNIW/Win7POS/actions/runs/42674141171), [WPF42675026366](https://github.com/XNIW/Win7POS/actions/runs/42675026366) SUCCESS; canonica15/15, functional20/20, foundation5/37, smoke8/52, load11/11 con measurementCompleted/environmentValid/thresholdsPassed=true |
| QA scaricato | Artifact13698359462, payload40 verificato; CI artifact13699879208 verificato con validator canonico e harness/WPF byte-identici. Smoke sul PC49.678s,8/52 PASS, exit0, cleanup confermato, nessun orfano; fingerprint e40file invariati |
| Release e installazione | Release41file/110checksum verificati,34file comuni QA/prodotto identici; installato41/41. Setup789da5b9d3b5bd86b052a44292dffb5ac09d98c60c0bd61fc60b6ecc9f7c4f91; ZIP2aa065419d9349f8098113514ad5766ae43786c09058830ecd29f911ae5d226a; EXEfa69762f43d17a44f1a9291827d1848c2f443e1301e6802f2733f39aa1216a6f |
| Conservazione dati | Backup prodotto verificato del ledger committato prima dell'update; intero profilo byte-invariato durante installer, trust e configurazione utente conservati, nessun auto-start. Dopo login, riconciliazione logica esatta con originali e ricevuta PASS, SQLite/FK e invarianti commerciali PASS |
| Avvio ordinario corrente | Collegamento Start Menu esistente → EXE installato/profilo NEW_SHOP_019 verificati prima del PIN → immissione mascherata utente → accesso/empty autorevole/workbench/import PASS. Nessun launcher alternativo o retry dell'avvio negato dallo strumento |
| Admin servito | Percorso B, PR148/9016054, Worker a68ebec2-3c55-46f8-bf4d-502e326187af e deployment1cd437f1-0608-408b-ba42-eb0f9716c7e8 invariati al refresh readonly. Login web corrente: overview staff-unavailable distinta da denial; reader Staff safe autorizzato PASS |
| Originali e prove | Analisi UI7 su1823697 confrontata con oracolo OOXML indipendente; originali invariati. A/B VERIFIED, tre alias distinti; C modern MISSING. Solo legacy4 committati con backup e riconciliazione esatta. Giugno parziale conservato, date assenti non sono zeri |
| Consultazione sul successore | LIVE_REOPEN_PENDING: riapertura ricevuta/report, nuovo CSV e reimport identico da concludere. Le prove prima dell'update restano PASS attribuite a1823697; nessun undo del lotto valido |
| DPI e tastiera | Nativi100% e125% osservati, titolo/footer/form vuoto interamente visibili a2880×1800. Shift+Tab/Enter aprono il file picker, Escape lo chiude senza selezioni; forward Tab/assistività non interamente certificati. Ripristino200% confermato e osservato sul form a828; nuovo candidato ancora da verificare |
| Esterni | CSV_INPUT=BLOCKED_INPUT (solo XLSX ricevuti); moderni richiedono prova C pertinente ai campioni privati già preparati. Win7 fisico/periferiche NOT_RUN/BLOCKED_TARGET già dichiarato; nessuna certificazione trasferita da Windows11 |

Prime failure conservate: ca4e437 CI oltre60s pur con8/52 assert PASS e Load
NOT_RUN; full locale Recovery19/20 e history non partito per path evidence
troppo profondo; crash diagnostico2/5 poi corretto rifiuto ownership W7HP.
Nessun guard o soglia indeboliti, nessun retry Recovery invariato. I primi run
GitHub a828 Release42673052013/WPF42673173639 e WPF42674135048 non hanno
creato job per Internal server error; retry API failed-jobs403 non possibile,
poi invocazioni ordinarie sopra SUCCESS. Non è dimostrata una causa negli input.
Prima UAC annullata prima del lancio: installato/profilo invariati; secondo
consenso utente, installer exit0 e verifica41/41. Nessun FAIL cancellato.

Evidence privata dal pointer residual-current-root.private.txt e dal suo
resume-current-root.private.txt, delivery-a8286c6, downloaded smoke, riconciliazioni
ledger/CSV e receipt originali. Nessun dato finanziario, identificativo bancario,
credenziale, DB o trust pubblicato. La policy POS nativa resta separata per i
report misti; non blocca il report solo Getnet. Storico V1 locale, distinto dal
cloud/mobile. Le sezioni seguenti sono checkpoint storici del proprio candidato.

## Storia — 9 novembre 2026, legacy salvato e correzione in qualifica

Primo candidato ca4e437: CI42667255330 FAIL di durata processo, non di assert
(8/52 PASS, corpo59.625s più avvio/uscita oltre60s). Load NOT_RUN. WPF42667293657
functional20/20, Security42667255175 e Release42667255023 PASS; non installato.
Successore solo test: stessa correzione prodotto, una sola fixture VM per stale
review/commit singolo/rientranza negata. Fresh run06 PASS8/52 in50.329s processo,
cleanup confermato e nessun orfano; soglie e assert invariati. Qualifica e pacchetto
exact-SHA successore aperti. Local full FAIL conservato: Core1734 PASS, Recovery
19/20 (weakref control4/recovery5); storico non partito per root evidence troppo
profonda. Crash scaricato2/5 prima del corretto rifiuto ownership W7HP non canonica;
nessun guard aggirato. Foundation hosted5/37 PASS distinta. Display100% confermato
dall'utente;100/125 nativi e ripristino200% ancora da osservare.

Stessa ASUS019/R3, ACTIVE. Admin148 autenticato PASS sul Worker a68ebec2 e
deployment1cd437f1: overview staff-unavailable distinta da denial e reader
Staff safe autorizzato. Avvio quotidiano dal collegamento installato1823697,
profilo conservato, login mascherato e catalogo autorevole vuoto PASS.

| Prova reale | Risultato e limite |
| --- | --- |
| Analisi originali | Sette XLSX invariati, UI NeedsEvidence disponibile e riepilogo confrontato con oracolo OOXML indipendente; giugno parziale conservato |
| Scope applicabile | Quattro legacy, A/B verificati e CLP esplicito; tre moderni separati e bloccati da C |
| Apply legacy | COMMITTED: backup prodotto verificato, ricevuta originaria e singolo lotto attivo; riconciliazione esatta di identità testuali, file/riga, tempi e importi PASS |
| Errore dopo Apply | NullReferenceException UI osservata e conservata; nessun rollback o retry del lotto valido. Stack live non disponibile |
| Consultazione | Ricevuta, report solo Getnet giornaliero/periodo e dettaglio del giorno selezionato PASS prima della chiusura |
| CSV reale | Snapshot esatto del ledger, metadata REPORT/MOVEMENT coerenti, ID integri e testo protetto dalle formule PASS; VerifiedForScope non certifica completezza degli originali |
| Invarianti | SQLite/FK PASS; vendite native, righe vendita, stock e outbox commerciali invariati; nessuna stampa/fiscale/cassa generata |
| Correzione | HIST02 riproduce rientranza della conferma e stesso errore postcommit. Fix minimo in working tree: piano stabile, seconda attivazione negata e review cambiata respinta; build/dialog standard PASS, qualifica esatta aperta |
| Residui | Nuovo pacchetto qualificato/installato, reopen/dedup effettivi e DPI100/125; C modern MISSING, CSV_INPUT BLOCKED_INPUT, Win7 BLOCKED_TARGET |

Focused run05 PASS8/52,53.276s entro60s, exit0/cleanup confermato/nessun orfano:
conferma singola, rientranza negata e review cambiata senza scritture. Qualifica
canonica e pacchetto exact-SHA ancora da completare.
I run diagnostici01-04 e i primi errori rimangono conservati. Il FAIL HIST08
deriva dal lock image-cache del POS reale concorrente, chiuso normalmente prima
del prossimo run. Nessuna cache/trust o dato cancellato per ottenere PASS.
Display dichiarato200% e2880x1800; nessuna prova100/125 trasferita da render
sintetici. Le qualifiche1823697 sotto restano della baseline installata.

## Storia — 9 novembre 2026, prima dell'analisi e dell'Apply reali

Windows conserva codice/pacchetto/installato1823697, net48/x86 su Windows11
build26300. EXE SHA2564d9abfa0a4b56d1a86d46d16f145156f5fca4253194024beb6e73761c6939157
ricontrollato. Nessuna patch Windows, reinstallazione o nuova suite completa.
Le qualifiche1734/50/15/8-52, reader originali7/7 e CSV50000 restano attribuite
al candidato invariato. Reader non significa analisi UI; CSV50000 è export.

| Risultato richiesto | Osservato / evidence attribuibile | Residuo e chiusura |
| --- | --- | --- |
| Release compatibile | Percorso B: export readonly del Worker realmente servito399720d2, confronto dei percorsi runtime/config e contratti remoti; recuperati reader staff TASK140F e predicate dei ruoli web esistenti | Non è stata pretesa equivalenza dello snapshot8ottobre. Nessun nuovo SQL, grant, RLS o secret put |
| Prima release147 | Mergecdebb26, CI42632336586/Cloudflare42632336513 PASS; Worker5cbf018f pubblicato. Catalogo/workbench POS PASS, console pos_admin FAIL | Due sessioni nuove non scadute revocate dal guard manager-only: prima failure conservata. POS della task fermato prima del rollback pinned399720d2, deployment1c2aaf19 verificato e pubblico PASS |
| Successore148 | Codicecc7b835, merge9016054ab6fea1a8d9ad7e6765255662af5afafe.45mirati PASS; CI42638651781 attempt2 e Cloudflare42638650988 PASS. Post-merge CI42641653938/Cloudflare42641652917 PASS | Primo attempt CI PREFIX_CONTAINER_HEALTH_FAILED prima di bind/apply/test conservato; un solo rerun failed jobs, TASK094 live SKIPPED |
| Artefatto e rollout corrente | Artifact13684508006: source434e312/tree918a24e equivalente a cc7b835/merge9016054;57checksum, lock/config e dry-run no-bundle PASS. Worker a68ebec2-3c55-46f8-bf4d-502e326187af, deployment1cd437f1-0608-408b-ba42-eb0f9716c7e8,100%, tag9016054 verificati, pubblico PASS | Controllo autenticato del successore IN_PROGRESS. Il pubblico non certifica login o import |
| POS installato / storico | Sul primo rollout, avvio normale manuale dopo rifiuto dello strumento; login riuscito, «catalog confirmed empty / history available / sales and payments not ready» osservato, Historical workspace aperto | Riapertura sul successore da osservare. Il log mostra pagina1/products0/hasMoreFalse; non contiene uno status HTTP200 esplicito: non inventarne una ricevuta |
| Avvio quotidiano | Collegamento Start Menu esistente verificato: EXE installato, nessun argomento. WIN7POS_DATA_DIR configurata solo per l'utente corrente sul profilo conservato NEW_SHOP_019; nessun DB/trust spostato | Configurazione persistita PASS; ereditarietà nel processo avviato dal collegamento ancora da verificare prima del PIN. Nessun launcher alternativo o retry automatico del comando negato |
| Analisi sette XLSX | Tutti i sette file nuovamente indicati dall'utente hanno hash uguali al manifest indipendente congelato; nessun CSV ricevuto | Analizza nella UI IN_PROGRESS, nessun PASS trasferito dal reader. Diagnostica/copertura, incluso giugno parziale, da confrontare con oracle privato |
| Getnet A | VERIFIED: dichiarazione contestuale dell'utente che associa gli export al negozio indicato, corroborata da due identità fiscali esportate corrispondenti all'unico shop ufficiale pertinente | Tre alias conservati distinti; non raggruppati dal solo nome o RUT. Fonte e limiti nella scheda privata |
| Getnet B | VERIFIED: attestazione utente «Mai usato prima»; DB isolato vuoto è solo corroborazione | Rappresentare il confine della nuova attivazione9novembre, senza inventare una precedente prima vendita |
| Getnet C | MISSING per i moderni: la colonna FECHA DE VENTA con suffisso Z dichiara UTC lessicale, ma non discrimina la semantica di costruzione dell'export Getnet | Serve riscontro provider/portale/voucher pertinente ai campioni privati già individuati. Non trasferire OneGMR CSV/SFTP o fuso del PC. EXT-TIME moderno distinto dal legacy commerciale locale e dalla policy POS nativa |
| Apply / reopen / consultazione / CSV / dedup | NOT_RUN reale; nessuna importazione effettuata | Verificare separatamente l'eventuale scope legacy con A/B validi. Moderni bloccati da C; Apply soltanto con piano corrente e prove applicabili, backup prodotto e conferma. Non annullare dati validi come cleanup |
| CSV_INPUT | BLOCKED_INPUT: forniti soltanto XLSX, nessun adapter CSV inventato | Per qualificarlo occorre un export Getnet CSV rappresentativo. Nessuna conversione distruttiva degli originali |
| UX Windows11 | Catalogo vuoto e workbench nativi osservati, testi/controlli leggibili sulla superficie disponibile; prima interazione coordinate seguita da apertura modale osservata | Non qualifica l'intero form, DPI100/125 o assistività. Impostazioni non ha esposto finestra controllabile; apertura manuale richiesta, nessuna scala modificata |
| Win7 fisico / periferiche | BLOCKED_TARGET: indisponibilità già dichiarata dall'utente. Kit1823697 conservato invariato | Nessuna nuova richiesta di disponibilità, ricostruzione kit o certificazione Win7 da Windows11 |

La qualifica147 ha mancato la differenza semantica nel resolver dei ruoli:
l'inventario di nomi RPC non provava equivalenza dell'autorizzazione. Il
successore usa il predicate di produzione già condiviso da login/principal,
non aggiunge ruoli. Testano manager/pos_admin/courier e15negativi di scope,
versione, revoca e must-change, oltre ai ruoli esclusi; rosso precedente
conservato. Il verde software148 non cancella il primo FAIL live.

Artefatto148 ZIP SHA2569f5902b105078c35896b531b42e54a6bf30f50a01a6fd026cfa3d238f7c9b01a;
Worker SHA2567d87a0968c9728acdc6993248c794598b94fd7b2fcdc38a0070de97f8e30b08f.
Pack Linux conservato dopo i gate con sole due variabili pubbliche browser
legate al progetto staging verificato. Configurazione portabile solo staging,
deploy CLI no-bundle/keep-vars; nessun workflow_dispatch generico, TASK094 live,
TASK150 secret put, produzione o modifica DB. Binding/variabili conservati.
[PR147](https://github.com/XNIW/merchandise-control-admin-web/pull/147),
[PR148](https://github.com/XNIW/merchandise-control-admin-web/pull/148).

Snapshot readonly9novembre prima della prova corrente: quick_check=ok,
foreign_key_check0, journal_mode=delete; vendite/righe/stock/outbox commerciali0,
ledger storico/binding/lotti/file/operazioni0, solo metadata dataset1.
Sessione/trust possono cambiare con il login: nessuna immutabilità byte del DB.
Backup, originali, trust e prime failure restano conservati. Bytecode test la
cui cancellazione era negata rimane trattenuto, senza tentativi alternativi.

Evidence privata dal pointer residual-current-root.private.txt e dal suo
resume-current-root.private.txt (resume20261109T132559Z). Procedura privata
aggiornata al Worker corrente; nessuna credenziale o somma reale nei documenti.
Nota PR29 unica riusata. Stato ACTIVE con verifica reale in corso, nessun DONE.

## Storia — prosecuzione dell'8 novembre 2026

Windows resta sul codice/pacchetto1823697; nessuna modifica applicativa o
reinstallazione nuova. Le qualifiche1734/50/15/52 e i pacchetti sotto sono
riusati con la loro provenienza. Host riconfermato Windows11 Home Single
Language10.0.26300 x64; prodotto net48/x86, scope pubblico NEW_SHOP_019.

Su esplicita richiesta utente è riuscito un solo nuovo normale Start-Process
dell'EXE installato, sullo stesso profilo isolato verificato. PID/creationTime/
path conservati privatamente. Non sono cambiati launcher, policy, UAC o
protezioni. I precedenti rifiuti rimangono storici, senza attribuirne una causa
a Smart App Control, antivirus o password. La documentazione ufficiale
[Windows sandbox](https://learn.chatgpt.com/docs/windows/windows-sandbox)
descrive diagnosi generali, non prova la causa del precedente blocked by policy.

L'utente ha inserito la password solo nel campo mascherato. Un tentativo
correlato nel log POS: firstLoginSucceeded=yes, trustedSessionPersisted=yes,
Access verified/device linked/operator configured osservati. Catalogo invece
HTTP500/db_failure, pagina1/rows0 e nessuna full committata. Audit remoto dello
stesso shop: reason=catalog_v2_empty_manifest, stage=manifest. Il resolver409
corretto non è stato riapplicato: la nuova causa è il guard HTTP che scarta lo
zero completo. Nessuna cassa o sessione operatore locale conclusa.

| Residuo / expected | Stato osservato | Prerequisito, owner e azione esatta | Criterio di chiusura |
| --- | --- | --- | --- |
| Admin stato pagina: review/merge | PASS PR144, merge919d100; CI42519626751/Cloudflare42519626731 PASS | Review indipendente root del diff preparato, continuità f8ac2fd6→tre file docs19e0b6f→merge verificata; nessun nuovo consenso obbligatorio | Merge e controlli verificati; deploy distinto |
| HTTP: full autorevole vuota | FAIL reale500 preservato; fix PR145 codice3e56a322 qualificato e mergefdb30f4 | Mirati33/33, CI42522680653/Cloudflare42522680527 PASS,1156foundation+14SKIP,67file/4375pgTAP. Solo full fresca terminale shop_scoped senza owner, righe/nove contatori zero; delta/legacy/parziali negati, publication fence intatto | Stesso percorso HTTP200 dopo rollout compatibile; quindi storico, cassa bloccata |
| CI post-merge e diagnostica | f57afa6: CI42526220441 database/Verify e Cloudflare42526220439 PASS, live E2E/deploy SKIPPED | PR146 di sola diagnostica CI: codicef344918, CI42525515132/Cloudflare42525515054 PASS,1157foundation+14SKIP; mergef57afa6. Precedente fdb30f4: prefix bootstrap FAIL due volte prima di bind/apply/test, causa nativa UNKNOWN; un solo rerun mirato, nessun terzo retry invariato. Nessuna nuova TASK146 Admin | Qualifica software chiusa; failure precedenti conservate e rollout distinto |
| Rollout staging | BLOCKED_RELEASE_UNIT; Worker399720d2 del15ottobre invariato | Owner Admin: fornire/riconciliare albero immutabile equivalente alla versione servita, inclusa TASK140F, oppure prova di compatibilità della main completa. Target/progetto/binding metadata verificati, rollback399720d2 pronto. Non manca l'autorizzazione utente | Unità release definita, build/config staging coerenti, pubblicazione Worker attestata, check pubblico e console autenticata |
| Console autenticata | PASS accesso staff; vecchio fallback non autorizzato/righe0/scrittureOff osservato realmente | Non interpretato come incassi zero. PR144 è integrata ma non distribuita | Dopo deploy: indisponibile/deny/errore/zero autorizzato distinti sulla console corretta |
| Avvio e autenticazione POS | PASS nuovo avvio1823697 e prima autenticazione; bootstrap non completo | Profilo/DB/trust/eseguibile verificati, vault dell'altro negozio intatto; niente ulteriori richieste della stessa autorizzazione | Avvio già chiuso; storico operativo dipende dal catalogo |
| Preview sette originali nell'app | NOT_RUN, dipende dal catalogo HTTP | Reader prodotto7/7 precedente riusato; non equivale al passaggio nel POS | Aprire gli stessi originali nella UI autorizzata dopo bootstrap riuscito |
| Prove Getnet A/B/C | MISSING tutte e tre; scheda privata pronta | Tre gruppi esportati e otto campioni con file/riga/timestamp/identificativo privati. Fonte mapping→Shop, prima data effettiva POS/overlap e semantica provider per formati/periodi mancanti. Richiesta unica effettuata; l'utente delega il giudizio ma non fornisce nuovi fatti | Fonti pertinenti VERIFIED, senza deduzioni da nomi, login o data account |
| Apply/cronologia/CSV/idempotenza/undo reali | BLOCKED_EVIDENCE, nessun apply | NeedsEvidence=true/CanApply=false. Esercente/owner documenti completa la scheda privata; backup prodotto e flusso di prova già descritti | Apply autorizzato e confronto con oracolo, reopen/CSV/dedup/undo nel perimetro previsto |
| UI Windows11 | Render dell'intero client sintetico EN/IT osservati a192DPI; campi/summary/footer leggibili nella renderizzazione | Host osservatore privato con40file QA invariati, nuovo DB sintetico, nessun avvio App POS operativo. Screenshot nativa ritagliata e input senza cambi verificabili: nessun PASS di interazione desktop. Impostazioni non espone finestra controllabile; nessuna scala modificata | DPI100/125 effettivi, full form sullo schermo, tastiera/assistività/clipping da completare con operatore |
| Win7/periferiche | BLOCKED: utente conferma target non disponibile | Kit1823697 invariato, SHA256d8544ed496521815bb37187a9dada065d3fa978ff535acf59e955a8b1764410f. Nessun nuovo hardware hash/run assegnato | Owner/macchina SP1/periferiche e sessione; receipt nuove validate sul build host |

La diagnostica CI proietta soltanto sette categorie chiuse nel campo category
esistente. Non pubblica output nativo, argv, path o messaggi eccezione; timeout,
tentativo unico, cleanup e SQL invariati. Test privacy con sentinelle PASS.
La prima prova Windows sul writer Linux O_DIRECTORY resta FAIL di setup test;
corretto il test usando un sink in memoria, senza cambiare il writer Linux.
Chiusura documentale Admin separata6173964, senza delta runtime:
[ricevuta immutabile e handoff](https://github.com/XNIW/merchandise-control-admin-web/blob/617396414cb87c71ab24da7f7cd03b360b0a9701/docs/TASKS/EVIDENCE/TASK-139/asus-019-staff-overview-20261108.md).

Unità di rilascio: il precedente albero equivalente1f0679b2 non attesta il
Worker15ottobre; la main differisce su28 percorsi runtime da quel solo albero
storico. Le quattro RPC U2 risultano presenti anche se le due versioni isolate
non figurano nella migration history: non se ne deduce assenza o compatibilità
di tutto il contratto. Nessun train SQL, generic workflow_dispatch, import
catalogo live, secret put o deploy eseguito in questa prosecuzione.

Le prove disponibili sono state cercate nel manifest, negli originali immutati,
nel messaggio Getnet con sette allegati e nelle fonti provider. Identità fiscale
ufficiale presente/bloccata dalla piattaforma verificata come fatto separato;
non risolve da sola l'associazione degli esportati. La fonte ufficiale
[OneGMR](https://docs.globalgetnet.com/en/products/business-operations/global-merchant-reports)
indica UTC per CSV/SFTP e include il Cile: l'applicabilità a questi XLSX del
portale non è provata, quindi la policy non viene trasferita. Portale Getnet
osservato senza sessione autenticata; nessuna credenziale POS riusata lì.
La policy temporale POS resta un gate separato per report misti, non un
prerequisito universale dell'Apply Getnet.

DB reale dopo il tentativo: quick_check PASS, foreign_key_check0, vendite/righe
vendita e tutte le outbox0, transazioni/batch/file/operazioni storiche0. Dataset
iniziale1 non è un import. Nessuna immutabilità byte dopo il login viene
affermata: sessione/trust possono essere aggiornati dal normale accesso.
Originali7/7 e kit invariati, backup e prime failure conservati. Processi POS
e osservatore EN fermati solo tramite PID/creationTime/path verificati; dispose
della fixture EN non osservato, nessun suo PASS di cleanup interno inventato.
Osservatore IT termina exit0, fixture disposed/holder restored veri e40file
qualificati ricontrollati. Queste osservazioni non sostituiscono QA canonica.
Rimozione del solo bytecode test nel worktree privato rifiutata dallo strumento
con blocked by policy: file trattenuto, nessun altro metodo di cancellazione.

Nota PR29 unica riusata; risposta sull'unità di rilascio ancora PENDING.
TASK146 iOS osservata OPEN/head137afa5e, nessuna build o pulizia mobile.
Stato complessivo ACTIVE con blocchi operativi distinti sopra; nessun DONE.
Evidence corrente dal pointer residual-current-root.private.txt sotto la root
privata ASUS019; gli altri pointer e la consegna1823697 sono preservati.

## Qualifica Windows1823697 riusata — checkpoint precedente

Le sezioni seguenti conservano le prove della prima consegna a1dccdb. I vecchi
stati avvio BLOCKED_POLICY e PR144 in attesa sono superati dai fatti sopra;
ogni altra prova mantiene la propria attribuzione originale.

Aggiornamento 8 novembre 2026. Codice Windows:
`1823697f1f0c965fb8fd9d85259a862160f12c59`, pubblicato normalmente su main.
Scope pubblico NEW_SHOP_019; binding e identificativi effettivi nel manifest privato.
La chiusura documentale è distinta da questa SHA. Stato del nuovo candidato:
SOFTWARE_DELIVERED / REVIEW del software. Stato complessivo: BLOCKED_EXTERNAL; nessun DONE.
Workflow dello stesso codice:
[CI](https://github.com/XNIW/Win7POS/actions/runs/42497670447),
[Security](https://github.com/XNIW/Win7POS/actions/runs/42497670514),
[Release](https://github.com/XNIW/Win7POS/actions/runs/42497670533),
[WPF/QA](https://github.com/XNIW/Win7POS/actions/runs/42497682626).

## Comportamento corretto

Dopo login verificato e catalogo non scaricato, Retry riusa una continuazione
limitata a cinque minuti monotoni, con SecureString readonly. La prova di lease
proviene dall'unico guard già composto da OperatorSession: non autentica un
operatore, non prepara la cache autorizzativa e non emette nuovi grant.
Revoca, scadenza, cambio generation, cancellazione e chiusura invalidano la
continuazione; la credenziale protetta viene distrutta al consumo o al rifiuto.
Il campo mascherato rimane vuoto e non viene eseguito un secondo first-login HTTP.

Connect e Retry condividono la preparazione dello storico dopo una full completa
autoritativa vuota. Lo storico autorizzato si apre; la cassa resta bloccata.
Una full verificata non vuota può completare l'accesso normale. Il fence persistente
d262ccd resta intatto: delta e approvazioni generiche non riabilitano un vecchio
catalogo dopo full empty. Retry prenota subito busy/cancellazione prima degli await,
ignora il secondo click e ricontrolla l'autorità dopo la risposta e prima dell'accesso.

La conclusione del login ricontrolla anche holder, versione, principal e lease.
Errori dopo il mirror login eseguono rollback della sola autorità creata dal
tentativo; il logout condizionale CAS conserva un eventuale operatore sostitutivo.
Il controllo catalogo non disconnette più il holder globale in questo percorso.
Nessun cambio ai contratti cloud/mobile, alla policy economica o alle soglie.

## Prove eseguite e provenienza

Owner dei run Windows: Root, unico writer build/test del checkout. Scope runtime
QA esclusivamente sintetico, endpoint loopback e root nuove; nessuna credenziale
reale usata dagli harness. Host locale Windows11 Home Single Language,
build26300 x64; prodotto e harness net48/x86. Questa è qualifica Windows11.

| Gate | Expected / osservato | Provenienza |
| --- | --- | --- |
| Regressione iniziale sul prodotto d262ccd | FAIL previsto: Retry riceve empty ma storico nascosto;1first-login/2catalog | retry-ui-red-01, DLL pubblicate d262ccd esatte, fingerprint stabile |
| Smoke locale precommit | PASS8scenari/52assert; tutti44originari più8semantici | basee0c2596 + delta congelato, fingerprint9f338e943848e98c5cab298db2641e4309dd7a6f0c9e94962bd665b370114b5d; runWin7POS.QA.20261108T155646352Z.e698ad4d6c254374ab82b5eba7021f44. Le prove esatte1823697 sono CI e QA scaricato sotto |
| Build WPF/harness e dialog | PASS,0warning/0error | build del medesimo focused run, Release/net48/x86 |
| Required gates | PASS50/50, nessun gate/allowlist ampliato | required-gates-02; primo49/50 conservato |
| Review del delta | Nessun finding concreto residuo dopo correzioni | review read-only di normal completion, CAS e proof readonly; nessun build/test attribuito al reviewer |
| CI, Core/Data, foundation e load | PASS15/15, Core1734/1734, functional20, foundation5/37, smoke8/52, load11 (M/E/S=true) | CI42497670447 e Release42497670533 sul codice1823697 |
| Security | PASS | Security Supply Chain42497670514, entrambi i job |
| Build e payload QA | PASS40file, digest e manifest verificati | WPF42497682626, artifact13624267366 |
| QA scaricato, CWD esterna | PASS8/52, exit0, fingerprint stabile;46175.7187ms | runWin7POS.QA.20261108T161042928Z.4be7680489dc43809a1a8461c9815ef9 |
| Originali con nuove DLL | PASS7/7, oracolo e hash attesi, prima/dopo invariati;4487ms, exit0 | driver binario invariato, DLL QA1823697; binding al prodotto PASS34/34 byte-identici al prodotto |
| ReleasePack e installer1823697 | PASS41file/110checksum, development-unsigned,0signing phases | verificatore canonico, run42497670533 |
| Installazione nuova candidata | PASS41/41, registry1.0.0-dev.1823697f1f0c, nessun avvio POS automatico | installazione d262ccd precedente PASS41/41; DB invariato |

Directory installata verificata: `C:\Program Files (x86)\Win7POS`.

SHA256 del ZIP GitHub QA:
`39e53ac2e118015fca072f18a2e23c9f7cd142e83a7068d5f8f82210715a47e0`.
Harness `1db8f0d11ab1dc463e86773ae2fad909dee6b7e34348af0a27b312c9fcf6097e`;
WPF `4d9abfa0a4b56d1a86d46d16f145156f5fca4253194024beb6e73761c6939157`;
Core `0f4e88e440b88f609c2a9aa3f292393e437f4798206313f5f4babeb89cd1b856`;
Data `ee9ce0ac2636a1fcdf6245430c579e46030a2070c63e03ad2b424cefde4bb0a5`.
FixtureSHA256 `72cd6f0afc82e418d5676fc329f61761d224e8aade30da98ada4ad30ee64620e`.
Installer SHA256 `61263381f4587c66d571d260f949a399a8ade10a51fe4a11c718f35c80ab2491`; payloadZIP `68a95a98f81a0b266f58047f024159360f6694de8c7ab86bae03ad49ed545124`. QA ZIP interno `e0de754ca546adf73b9c1e0c75a788255e1ada7a5b5f2b318612154e92ff21fd`, payload-manifest `334bcc226f8a14f3a97c04cda2967447976a47d4e05f4631fa5e0bf0add6210a`.

Comando canonico eseguito dalla CI, con le due root nuove del job:

```powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode All -OutputRoot "$env:RUNNER_TEMP/qa" -PublicOutputRoot "$env:RUNNER_TEMP/qa-public"
```

Il job WPF esegue `pwsh -File scripts/check-dialog-standards.ps1` e build
Release/net48/x86. Download qualificato mediante
`scripts/win7pos/windows/test-downloaded-release-pack.ps1`, RunId42497670533,
ExpectedCommitSha1823697 completo e nuova OutputDirectory privata. I comandi
esatti dei run locali, context fidati, PID/creationTime/path, tempi e log sono
nelle evidence private attribuite. Nessuna suite ripetuta per soli documenti.

## Verifica visiva effettiva

Tre regioni sintetiche registrate PASS nel QA scaricato: hourly_mixed219x92,
hourly_negative219x92 e import_summary861x16. Osservate tramite view_image:
riepilogo leggibile, geometrie positiva/negativa coerenti. La cattura campiona
il visual realizzato in coordinate locali, dopo BringIntoView, sulla superficie
opaca effettiva degli antenati. Fixture ownership, testo allowlisted e rifiuto
blank/privacy restano invariati; nessun testo o PNG sostitutivo fabbricato.
Il precedente import_summary vuoto resta NOT_RUN nella propria evidence.
Questa osservazione non certifica l'intero form a DPI125, lettura assistiva,
localizzazione manuale o hardware Win7. Tastiera/focus/Enter/Escape e restituzione
del focus all'owner sono verificati dal percorso WPF sintetico dello smoke.

## Prime failure conservate

| Tentativo | Esito originario / causa e correzione |
| --- | --- |
| retry-ui-red-01 | Retry empty non abilita History sul prodotto d262ccd. Cleanup secondario con connessione attiva; ora host.StopAsync atteso e errore primario distinto |
| retry-ui-green-01 |47/49: preflight async prima di busy; corretto il race del doppio click |
| retry-ui-green-02 |48/49: generation cambiata durante risposta tratteneva SS fino al timer; ora demand semantico post-await |
| normal-completion-red-01 |49/51: late-authority rosso valido; secondo caso non raggiunto per cursore sintetico ripetuto, senza modificare il guard prodotto |
| normal-completion-red-02 |49/51: entrambi rossi validi, generation tardiva e fault SQLite dopo login; sorgenti intermedi e receipt conservati |
| required-gates-01 |49/50: seconda composizione leaseguard e struttura logout non conforme. Rifattorizzata produzione su unico issuer/proof readonly; gate intatti |
| prima PNG local-coordinate | Cattura binaria nonblank ma trasparente/illeggibile; ora composita sulla superficie reale e ispezionata leggibile |
| verifica CI locale iniziale | Assunto un file opzionale core-tests.public.json non emesso: INVALID_VERIFIER_ASSUMPTION, senza fallimento canonico. Nuova verifica3/3 strict più15/15 PASS;1734 contati nel log Release dello stesso SHA, non inventati dalla proiezione parziale |
| osservatore CSV build/run iniziali | Primo argv MSBuild errato, nessuna compilazione; poi setup rifiutato per output fuori sourceRoot, prima della fixture. Corretti argomento/output senza modificare guard o prodotto; run02 PASS, prime failure conservate |
| installazioni d262ccd1/2 | Consenso Windows annullato anche dopo risposte chat; esiti conservati. Terza apertura esplicitamente richiesta e consenso effettivo: installazione riuscita |

Nessuna soglia o budget30/60 originale ampliato, nessuna assertion originaria
eliminata. I nuovi check distinguono empty/revoca/scadenza/generation/cancel,
normal full positiva, authority tardiva e fault dopo autenticazione.

## Admin e coordinamento

[PR Admin144](https://github.com/XNIW/merchandise-control-admin-web/pull/144):
codicef8ac2fd6beda0e20d2fa4060c2dad45e78bdc001, docs19e0b6f.
CI42493312883/Cloudflare42493312831 PASS:1144foundation/14SKIP/0FAIL,
UI48/pairing2 e67file/4375pgTAP. Merge sintetico56751ed3 con tree identico;
artifact13622139763 digestZIP verificato. Il fallback distingue superficie
staff non disponibile, permesso negato, errore e lettura autorizzata vuota;
nessun nuovo reader, query staff, SQL, grant, merge o deploy. Review/ACK Admin
e rollout restano PENDING; non è una prova della console live.

La [nota PRiOS29 esistente](https://github.com/XNIW/iOSMerchandiseControl/pull/29#issuecomment-6722613449)
è riusata, nessun duplicato. RESUME_STATUS pubblicato
su head3a94b3d7 riconcilia il server409 alle15:10Z; il successivo137afa5e, PR29 ancora aperta, conferma ACTIVE/EXECUTION. TASK146 resta separata;
la sua matrice e le sue integrazioni non qualificano Windows. Reader mobile_sales_read_v1
e storico Getnet V1 locale restano separati. Nessuna build/cleanup mobile eseguita.

## Gate reali e fisici ancora aperti

| Gate | Stato, owner e azione concreta |
| --- | --- |
| Avvio/login/catalogo HTTP/UI reale | BLOCKED_POLICY: autorizzazione già concessa, nuovo Start-Process rifiutato con blocked by policy, processo non nato e nessun nuovo pull osservato. Responsabile ambiente/policy; normale avvio del profilo corretto quando eseguibile, nessun launcher alternativo o ripetizione della stessa domanda |
| Credenziale per normale login | Nessun profilo DPAPI adatto: host/staff combaciano ma Shop diverso; nessun uso o modifica del profilo estraneo. Dopo finestra reale valida occorre un'unica immissione mascherata dell'utente, mai password in chat |
| Preview UI dei sette originali | BLOCKED_POLICY, distinta dal reader7/7 PASS. Non presentata come attraversata nel POS reale |
| Apply reale / riapertura / idempotenza / undo / CSV reale | BLOCKED_EVIDENCE. Responsabile esercente deve fornire mapping esercente→Shop, inizio uso POS/overlap e semantica providerZ/offset con provenienza/copertura. Login/account non sono queste prove. NeedsEvidence=true, CanApply=false, nessun apply operativo |
| Policy temporale nativa | Gate separato per includere POS nei report misti. Non è prerequisito universale dell'Apply Getnet; storico consentito conservato, copertura nativa indisponibile distinta da zero completo |
| DPI100/125, Shift+Tab e assistività manuale | BLOCKED_UI_SURFACE: prova manuale NOT_RUN, provider non espone Settings come finestra controllabile, nessuna scala modificata. Completare con operatore nel contesto fisico della scheda; niente attestazione DPI125 dalla PNG96dpi |
| Win7SP1 / scanner / stampa ordinaria sintetica | BLOCKED_EXTERNAL: macchina e periferiche non disponibili. Stesso pacchetto/context/scheda, net48/x86; niente SDK moderno, CLI net10 o pwsh sul target |

Apply/report/CSV/idempotenza/undo/rollback sono invece eseguiti sulle fixture
sintetiche: nessun effetto commerciale, stock, documento fiscale o stampa introdotto
dall'import/undo. Misura CI reference-Windows completa e validata: analisi1743.4963ms, backup218.9936ms, apply2625.1742ms, commit88.3536ms, query cold277.3629ms/warm255.9188ms, p95 Dispatcher0.2341ms e picco85864448B. measurementCompleted/environmentValid/thresholdsPassed tutti true. Osservatore privato separato con gli stessi binari e50000 movimenti storici sintetici: query365.7015ms, CSV totale5656.8755ms, IO finale12.6125ms entro il budget originario1000ms; Published e50000 righe/token verificati. Nessun budget totale CSV inventato. Processo15928ms/exit0, cleanupSucceeded=true/processRemaining=0, fixture e holder rilasciati; non qualifica Win7 o risorse/foreground come la misura CI.

Run dell'osservazione CSV:
`Win7POS.QA.20261108T170800854Z.10471a489caa468c9beb6afe8a5f6268`.
Il kit trasferibile riusa gli stessi setup/QA e la
[scheda operatore corrente](ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md).
Template di contesto da completare con owner, run/nonce nuovi e identità
hardware approvata: preparazione conclusa, esecuzione fisica NOT_RUN.

## Backup, evidence e cleanup

Backup online verificato con API prodotto, integrity/FK PASS e scope ufficiale
verificato prima dell'installazione; nessun Initialize/migration o copia grezza.
DB del profilo conservato byte-identico dopo installazioni d262ccd/1823697,
QA e osservazione CSV, verificato nuovamente alla chiusura.
Originali e controlli finanziari, hash input e identità rimangono privati.
La nuova ripetizione riusa il driver binario senza leggere/dumpare il Program.cs
privato e lascia invariata la receipt precedente. Tutti i processi QA/reader/osservatore posseduti sono terminati; il supervisore CSV conferma cleanup e zero residui. La canonica PASS include il cleanup obbligatorio; conteggi raw per Job hosted non pubblicati, nessuno zero inventato. Nessuna terminazione per nome di processi utente.

Evidence private mappate da `e2e-current-root.private.txt` sotto
`C:\Dev\_codex-private\ASUS-W7POS-019`; delivery separata corta, nuovo owner/run,
puntatore `delivery-current-root.private.txt`. Profili/trust/backup/originali e
prime failure conservati, nessuna revoca o cancellazione generica. La chiusura
documentale non attribuisce al nuovo SHA i verdi del predecessore.
