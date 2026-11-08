# ASUS-W7POS-019 — Retry, qualifica software e gate end-to-end

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
