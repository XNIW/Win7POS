# Win7POS Bootstrap E Sync Online

Stato: SOFTWARE_READY_EXCEPT_HARDWARE_SMOKE. Smoke finale su Windows 7 fisico resta richiesto.

## Fresh Install Online

1. Win7POS carica Admin Web base URL con priorita env var, config file, default pacchettizzato.
2. Se non esistono operatori locali attivi, apre il first-login online prima del wizard recovery/dev.
3. L'operatore inserisce `shop_code`, `staff_code` e PIN/password; il client non salva il segreto raw.
4. La response first-login viene accettata solo se `ok=true`, device trusted/active, session token/id presenti, shop/staff presenti e policy versionata.
5. Win7POS salva snapshot shop/policy, trust DPAPI e solo dopo crea/aggiorna il mirror staff locale.
6. Se la persistenza locale di trust/mirror fallisce, il trust viene rimosso e il bootstrap non viene dichiarato riuscito.
7. Sales sync pendente viene tentato best-effort; poi parte il catalog full pull iniziale.
8. Il dialog resta bloccante su "Preparazione negozio" finche' il catalogo non diventa sale-safe. La finestra mostra progress bar standard, step accesso/device/operatore/catalogo/finalizzazione, pagina catalogo e contatori cumulativi.
9. Il POS normale non si apre se `pos.catalog.sale_safe_at` non e' stato scritto; retryable/partial non cancella trust e propone retry/uscita.
10. `MainWindow` crea il `PosView` solo dopo gate sale-safe e login operatore: nessun `StartInitialize()` POS viene avviato durante `InitializeComponent`.
11. `--safe-start` disabilita heartbeat/catalog/sales online, ma non permette vendita normale se manca un catalogo gia' sale-safe.

## Catalog Full Pull

- Il request DTO invia `limit=1000`, coerente con il massimo accettato da Admin Web.
- Il bootstrap usa `MaxBootstrapCatalogPullPages=120`; il refresh background usa un cap piu corto.
- Dopo ogni pagina, Win7POS salva cursor, `last_sync_at`, `last_has_more`, versione catalogo e diagnostica.
- Se il cap termina con `HasMore=true`, il run salva:
  - `pos.catalog.bootstrap_status=partial_has_more`;
  - `pos.catalog.last_error=has_more_not_drained`;
  - cursor gia ricevuto, senza cancellarlo.
- Il run successivo riparte dal cursor salvato e puo completare il drain.
- Il completamento sale-safe scrive:
  - `pos.catalog.sale_safe_at`;
  - `pos.catalog.initial_completed_at` solo la prima volta.
- Sale-safe viene promosso solo quando il pull drena tutte le pagine (`HasMore=false`), applica prodotti/prezzi necessari, non riceve `auth_denied` e non incontra errori SQLite critici. Una risposta completa senza prodotti remoti attivi resta retryable (`no_catalog_products`).
- Product tombstone remoto marca il prodotto inattivo e `remote_deleted_at`; non fa purge.
- Category/supplier tombstones sono osservati e loggati come diagnostica, ma non applicati alle tabelle locali perche' oggi non hanno remote id/tombstone columns.
- Il limite category/supplier tombstone non blocca sale-safe: le righe prodotto restano la fonte vendibile, product tombstone e' soft-delete reale, e il check statico vieta purge distruttivo del catalogo/outbox.
- Se ci sono movimenti stock locali con outbox `pending`, `retry` o `failed_blocked`, il catalog pull preserva `stock_qty` locale.
- I prezzi arrivati prima del prodotto non vengono piu scartati: Win7POS li mette in `remote_catalog_pending_prices`, usa `remote_price_id` come idempotency key quando presente e riprova l'applicazione dopo gli upsert prodotti. Il replay drena batch multipli e usa un prodotto canonico per `remote_product_id`, cosi' un catalogo grande non puo' diventare sale-safe lasciando prezzi risolvibili in coda.
- L'upsert prodotto canonicalizza `remote_product_id`: se un prodotto remoto cambia barcode, Win7POS evita duplicati attivi e disattiva eventuali vecchie righe attive dello stesso prodotto remoto.

## Avvio Giornaliero

1. Startup inizializza/migra SQLite; se questa fase fallisce l'app non apre il POS.
2. Prima del login resta il gate sale-safe: se manca `pos.catalog.sale_safe_at`, si rientra nel flusso bloccante di preparazione catalogo.
3. Dopo login operatore, ma prima di creare `PosView`, `MainWindow` apre `PosStartOfDaySyncDialog`.
4. `PosStartOfDaySyncService` restituisce `StartOfDaySyncResult` con `CanOpenPos`, `ShouldContinueInBackground`, `RequiresOperatorAction`, motivo bloccante, contatori outbox e stato catalogo.
5. Il preflight verifica in ordine: DB locale/sale-safe, restore review, outbox blocked, stato auth denied persistito, sessione trusted, heartbeat, outbox, sales sync bounded, catalog delta bounded.
6. Timeout: totale circa 28s; heartbeat 4s; sales sync 8s; catalog delta 12s.
7. Se tutto termina velocemente, il dialog si chiude e il POS apre normalmente.
8. Se rete/catalog delta/sales retry sono lenti ma `sale_safe_at` esiste e non ci sono criticita', `CanOpenPos=true`, `ShouldContinueInBackground=true`: l'operatore puo continuare e `QueueBackgroundOnlineRefresh` riprende heartbeat/sales/catalog in background.
9. Se heartbeat/sales/catalog ricevono `auth_denied`, il trust viene cancellato, lo stato viene marcato `failed_auth_denied`/`auth_denied` e il POS non apre finche non viene ricollegato.
10. `--safe-start` salta il preflight online giornaliero ma resta soggetto al gate sale-safe gia descritto.

### Quando Bloccare

- Catalogo locale non sale-safe o nessun catalogo locale valido.
- Migrazione/DB locale fallita.
- Device/session revocati (`auth_denied` / `failed_auth_denied`).
- Outbox vendite `failed_blocked`.
- Restore DB con `pos.restore.needs_sync_review=true`.

### Quando Continuare In Background

- Rete temporaneamente assente o heartbeat timeout.
- Catalog delta lento, timeout o `partial_has_more` con catalogo precedente gia sale-safe.
- Sales sync retry/timeout non critico.
- Admin Web non configurato o sessione trusted mancante, se il catalogo locale e' gia sale-safe e non esiste uno stato auth denied persistito.

## Status Strip

Priorita' summary: auth denied/ricollegamento, vendite blocked/restore review, vendite retry, vendite pending, ultimo errore sales, catalogo in preparazione/aggiornamento/parziale, sync in corso, catalogo pronto/online ok. `catalogo pronto` non deve coprire vendite blocked/retry/pending o revoca sessione.

## Vendita Offline -> Reconnect

1. La vendita viene salvata localmente in SQLite.
2. Nella stessa transazione vengono scritti righe vendita, movimento stock locale e `sales_sync_outbox`.
3. Al reconnect, Win7POS prende massimo 25 outbox items per run; il repository forza comunque un massimo 50.
4. Retry usa backoff bounded; dopo 12 tentativi l'item diventa `failed_blocked`.
5. Ack `duplicate`, `acked`, `synced` o `idempotent` e' accettato come successo.
6. `conflict` o `validation_failed` passa a blocked/review.
7. L'outbox non viene cancellata da cleanup distruttivi; gli stati restano visibili nello status.

## Restore E Backup

- Restore DB e' bloccato se il DB corrente ha outbox `pending`, `retry` o `failed_blocked`.
- Prima di sovrascrivere il DB, Win7POS esegue `PRAGMA wal_checkpoint(FULL)` e crea un pre-backup `pos_pre_restore_*`.
- Dopo la copia del backup selezionato, esegue `DbInitializer.EnsureCreated`, `PRAGMA integrity_check`, salva source/pre-backup/integrity e marca `pos.restore.needs_sync_review=true`.
- Start-of-day blocca l'apertura POS quando `pos.restore.needs_sync_review=true`; lo status strip mostra restore review come stato di attenzione.
- Backup manuale esegue `PRAGMA wal_checkpoint(FULL)` prima della copia file.
- Nessuna routine Win7POS fa `DELETE`, `DROP` o `TRUNCATE` di `sales_sync_outbox` per risolvere errori.

## Failure Matrix

| Caso | Stato locale | Azione |
| --- | --- | --- |
| First-login invalid response | no trust nuovo | mostra errore, riprovare |
| Trust/mirror persistence failure | trust cleared | controllare DB/permessi file, riprovare |
| Catalog timeout iniziale senza sale-safe | `failed_retryable` | POS non entra in vendita normale; retry dal dialog preparazione |
| Catalog timeout dopo sale-safe precedente | `failed_retryable` | POS resta usabile; status visibile e retry background |
| Catalog cap con `HasMore=true` senza sale-safe | `partial_has_more`, cursor salvato | POS non entra in vendita normale; retry dal dialog preparazione |
| Catalog cap con `HasMore=true` dopo sale-safe precedente | `partial_has_more`, cursor salvato | POS resta usabile con status "Download interrotto/ripresa" |
| Catalog auth denied | `failed_auth_denied`, trust cleared | ricollegare POS online |
| Prezzo remoto prima del prodotto | pending SQLite locale | replay automatico quando arriva il prodotto; idempotenza con `remote_price_id` |
| Safe-start senza sale-safe | no apertura POS | mostra catalogo incompleto e chiude; non inizializza `PosView` |
| Start-of-day sale-safe + rete lenta | `ShouldContinueInBackground=true` | l'operatore puo continuare; sync prosegue background |
| Start-of-day auth denied persistito o nuovo | `failed_auth_denied`/`auth_denied`, trust cleared | blocco apertura POS; ricollegare device |
| Start-of-day outbox blocked | `failed_blocked` | blocco apertura POS; review/supporto |
| Sales network/server retryable | outbox `retry` | backoff automatico |
| Sales conflict/validation | outbox `failed_blocked` | review/supporto, non cancellare DB/outbox |
| Restore DB | `pos.restore.needs_sync_review` | verificare sync prima di chiudere intervento |

## Rischi Residui Fuori Scope

- Admin Web deep paging catalog pull: risolto nel software. Il server mantiene cursor `catalog-v1` e contratto API invariati, sceglie scope deterministico (`shop_scoped` o `legacy_owner_bridge`) e usa `range(from, to)` bounded invece di `range(0, offset + limit)`. Check: `npm run check:pos-catalog-paging`.
- Category/supplier tombstone applicativo resta diagnostic-only perche' lo schema locale Win7POS non ha remote id/tombstone columns per quelle tabelle. Un'applicazione reale richiede task schema/UI separato; intanto non c'e purge e non blocca sale-safe.
- Backup/restore via copia SQLite resta una procedura manutentiva locale: ora usa WAL checkpoint, pre-backup, integrity check e blocco start-of-day post-restore. Una UI di revisione dettagliata dell'outbox del backup selezionato puo essere un miglioramento futuro, non un blocker software corrente.

## Da Provare Su Windows 7 Fisico

- Fresh install online con catalogo grande reale o simulato.
- Avvio offline dopo bootstrap e login mirror staff.
- Vendita offline, reconnect, duplicate retry e blocked conflict.
- Revoca device/session da Admin Web e ricollegamento.
- DPAPI CurrentUser con utente Windows reale di cassa.
- Rete instabile, TLS 1.2, stampante/scanner e tempi UI su hardware target x86.
