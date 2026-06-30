# Win7POS production smoke checklist

Questa checklist e una verifica operativa esterna. Non e considerata eseguita finche non viene completata su Windows 7 reale o VM equivalente con periferiche e rete rappresentative.

Per il gate specifico i18n runtime, usare anche `docs/QA/WIN7POS-I18N-RUNTIME-VALIDATION.md` e lo script `scripts/win7pos/windows/run-i18n-runtime-validation.ps1`.

## Ambiente

- [ ] Windows 7 SP1 reale o VM equivalente, preferibilmente x86/x64 con runtime legacy realistico.
- [ ] .NET Framework 4.8 installato.
- [ ] Build Win7POS `Release` x86 installata o copiata da drop verificato.
- [ ] `C:\ProgramData\Win7POS` scrivibile dall'utente POS.
- [ ] `C:\ProgramData\Win7POS\pos.db` assente o backup esplicitamente approvato per test distruttivi.
- [ ] `C:\ProgramData\Win7POS\logs\app.log` esiste dopo avvio.
- [ ] Log ruotati se `app.log` supera la retention prevista.
- [ ] `C:\ProgramData\Win7POS\pos-admin-web.config` contiene solo `AdminWebBaseUrl=<https-url-base>`.
- [ ] Nessun `WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB=1` in scenario release/staging.

## Admin Web staging

- [ ] Admin Web staging raggiungibile via HTTPS dal PC Windows 7.
- [ ] TLS/API funzionano senza prompt browser o certificati non attendibili.
- [ ] Shop test attiva e staff POS attivo con credenziale temporanea nota.
- [ ] Device POS non sospeso/revocato lato Admin Web.
- [ ] Catalogo shop contiene almeno un prodotto vendibile con stock noto.

## Primo collegamento online

- [ ] Avvio con DB vuoto.
- [ ] Inserimento `shop_code`, `staff_code`, PIN/password.
- [ ] L'URL server resta in impostazioni avanzate, non nel flusso cassiere normale.
- [ ] First login completa senza mostrare token/sessioni.
- [ ] Mirror locale operatore creato.
- [ ] Catalog pull iniziale completa.
- [ ] Status strip mostra online o ultimo contatto server.
- [ ] `app.log` non contiene token, PIN/password o secret.

## Offline dopo attivazione

- [ ] Disconnettere rete dopo collegamento riuscito.
- [ ] Riavviare Win7POS.
- [ ] Login operatore locale consentito con mirror esistente.
- [ ] Status strip mostra offline/pending senza bloccare la cassa.
- [ ] Vendita offline completata e salvata.
- [ ] Ricevuta/stampa o salvataggio file funziona senza internet.
- [ ] Outbox pending visibile nello stato sync.

## Disconnessione durante sync

- [ ] Creare almeno una vendita con rete instabile.
- [ ] Interrompere rete durante sales sync.
- [ ] Chiudere l'app durante o subito dopo il tentativo.
- [ ] Riaprire l'app.
- [ ] Outbox riprende senza duplicare la vendita.
- [ ] Se il server aveva gia ricevuto il batch, il retry viene trattato come duplicate/ack e non genera doppio stock.

## Ritorno online

- [ ] Ripristinare rete.
- [ ] Heartbeat torna positivo.
- [ ] Sales sync invia batch pending.
- [ ] Status strip passa da pending/retrying a synced o richiede attenzione.
- [ ] Admin Web `/shop/sync` mostra ultimo batch POS reale.
- [ ] Admin Web non mostra dati finti o azioni mutanti non implementate.

## Conflict / failed_blocked / needs attention

- [ ] Simulare o usare dataset che produce conflict/validation_failed/failed_blocked.
- [ ] POS conserva la vendita localmente.
- [ ] Status strip mostra `Richiede attenzione` o equivalente.
- [ ] Tooltip indica che la vendita e salvata localmente e che non bisogna cancellare dati.
- [ ] Admin Web Sync Recovery mostra audit failure/stock warning se lato server disponibile.
- [ ] Manager/assistenza riceve client batch/sale id abbreviati o log tecnico redatto.

## Restore DB

- [ ] Creare backup manuale da UI.
- [ ] Eseguire restore da backup scelto.
- [ ] Verificare creazione automatica di `pos_pre_restore_yyyyMMdd_HHmmss.db`.
- [ ] Se il pre-backup fallisce, il restore non procede.
- [ ] Integrity check viene mostrato dopo restore.
- [ ] Status strip richiede revisione sync dopo restore.
- [ ] Outbox non viene cancellata.
- [ ] Vendite gia acked non vengono marcate nuovamente senza ack server.
- [ ] Dopo ritorno online, retry resta idempotente.

## Periferiche

- [ ] Stampante reale provata, se disponibile.
- [ ] Stampante PDF/salva file provata se manca hardware reale.
- [ ] Scanner barcode provato, se disponibile.
- [ ] Tastiera Enter/Esc nei dialog principali verificata.
- [ ] Nessuna finestra modale blocca retry sync automatico.

## Raccolta evidenze

- [ ] Screenshot primo collegamento.
- [ ] Screenshot status offline/pending.
- [ ] Screenshot conflict/needs attention.
- [ ] Screenshot restore completato con pre-backup.
- [ ] `app.log` raccolto e controllato per redazione segreti.
- [ ] Report con data, build, Windows version, stampante/scanner usati e Admin Web target.

## Stato

Hardware verification remains `EXTERNAL_NOT_RUN` finche questa checklist non viene completata su hardware/VM reale.
