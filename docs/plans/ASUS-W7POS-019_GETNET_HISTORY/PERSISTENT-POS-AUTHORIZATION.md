# ASUS-W7POS-019 — autorizzazione ordinaria persistente

Requisito esplicito dell'utente del 12 novembre 2026, nella stessa Execution:
l'uso ordinario del POS non deve scadere né interrompere l'operatore per il
semplice trascorrere del tempo. L'eventuale verifica di licenza del dispositivo
è futura. Questo requisito sostituisce il limite locale di dodici ore e il
vincolo di continuità temporale fra riavvii Windows per il nuovo modello.

La prova reale aa4 installata è FAIL di accesso: first-login va in timeout;
il fallback locale incontra offline_attestation_required, presentato da una
traduzione generica come autorizzazione scaduta. Non è una prova di PIN errato.
Le qualifiche automatiche precedenti restano attribuite ai propri sorgenti.

Il nuovo grant locale v6 nasce solo dopo una risposta online valida e resta
legato a utente Windows/macchina mediante DPAPI, al profilo, alla generazione,
al negozio/dispositivo/operatore e alla versione delle credenziali. Il PIN
locale rimane necessario al login e allo sblocco. Stato alterato, generazione
diversa e revoca nota continuano a essere respinti. Un testimone indipendente
dal backup del profilo, ora persistente in HKCU, conserva hash, receipt e
tombstone; la sua assenza non genera autorità tramite una lettura offline.
Il ritorno a un vecchio backup non deve ripristinare un grant revocato.

Client e server negoziano esplicitamente `localAuthorizationPolicy:
persistent-pos-v1`. La nuova transazione V4 delega l'emissione a V3 con gli
stessi lock, verifiche PIN/versione/stato e protezioni; promuove soltanto la
credenziale e la sessione appena emesse, nella stessa transazione. Il massimo
UTC finito portabile `9999-12-31T23:59:59.999999Z` rappresenta assenza di scadenza
ordinaria nelle colonne esistenti, non una data commerciale o di licenza.
L'heartbeat esistente conserva quel valore e restituisce la scadenza effettiva.
Non serve un nuovo endpoint di rinnovo o un PIN recuperabile. Revoca, blocco,
cambio credenziale e invalidazioni note restano controlli distinti dai timeout.

Una scadenza staff impostata esplicitamente rimane un cap di autorità; nessuna
data staff viene rimossa o estesa. Un risultato con limite finito non seleziona
v6, ma conserva il percorso bounded compatibile. La modalità permanente locale
richiede sia l'echo della policy sia l'assenza di limite finito nella ricevuta.
MainWindow non avvia il timer di scadenza per il grant permanente. Il testimone
v6 usa HKCU persistente, le letture ordinarie non riscrivono il registro e una
nuova emissione invalida anche il testimone v5 precedente.

Le ricevute legacy v1–v5 non diventano perpetue mediante un flag di JSON.
Il percorso di aggiornamento deve richiedere nuova autorità online oppure
verificare integralmente la precedente prova indipendente prima di migrare.
La compatibilità bounded resta coperta da prove esplicite, separata dal nuovo
comportamento ordinario. Non modificare le prove legacy per simulare un PASS
della modalità persistente.

Accettazione: login valido, permanenza oltre i limiti precedenti senza logout,
riapertura offline e riavvio Windows, PIN errato respinto, revoca/cambio
credenziale/profilo respinti, backup vecchio e witness mancante respinti,
riconnessione cloud senza ridurre l'autorità permanente, timeout server non confuso
con revoca, cassa/carrello/focus conservati. Prove Core e WPF distinte, build
net48/x86, gate Windows e pacchetto successore propri prima dell'installazione.

Stato: IMPLEMENTING. Gruppo Core corrente 46/46 PASS; build WPF net48/x86 PASS.
Smoke bounded/persistente e processi CLR distinti PASS anche con il controllo
sui cap staff. Il suo primo rerun FAIL usava ancora la ricevuta sintetica breve:
corretto soltanto il fixture permanente al valore wire reale, preservando le
prove bounded. Rerun separato `wpf-negotiated-cap.private.log` PASS. MainWindow
sintetica PASS in quattro aperture, due permanenti senza timer di scadenza.
Check statico autorità e standard dialog ALL PASS. Il primo Core completo
resta 2034 PASS/1 FAIL per CLI non compilata; dopo build CLI, quel caso e 45
prove auth PASS46/46, senza trasformare il primo run in un full-suite PASS.
I FAIL intermedi restano nell'evidence privata `C:/Dev/U19PersistentPosAuth01`.
SQL, CI, Release e ASUS Integrated12/Final60 sono gate propri del successore.
Nessun nuovo pacchetto installato: l'installato reale resta aa4ec25. Il profilo
reale format4 richiederà un primo accesso online valido per ottenere v6.
