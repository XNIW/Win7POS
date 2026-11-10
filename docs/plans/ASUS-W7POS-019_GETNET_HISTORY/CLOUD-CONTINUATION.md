# ASUS019 — continuazione client cloud, Execution isolata

Il mandato dell'utente del9novembre abilita le implementazioni B/C senza
operatore. Questa continuazione usa un ramo isolato da0378e17 e non modifica
il candidato5d8e704, il pacchetto qualificato, l'installato a8286c6 o i profili.
Consegna/UAC/login/DPI restano per la sessione con l'utente. Stessa ASUS019,
nessun DONE o riapertura delle task storiche.

Admin B/C0: PR149. Ledger, worker e workbench: PR150, head1bc98c7 al checkpoint.
Worker distinto dal processo HTTP; nessuna applicazione SQL, grant o deploy live.
Il piano canonico Admin ASUS019 definisce C3 export/mapping/provenance, C4 replica
storico dedicata e C5 unica writer authority. Non usare outbox vendite o prodotti
artificiali. Prove V1 locali migrate come legacy_attested, mai provider_verified.

Client in implementazione: export da backup prodotto verificato con manifest
e checksum; mapping locale/globale esplicito; replica separata e consultabile
offline con watermark/stale; divieto import locale quando l'autorità non è
verificabile. Cutover soltanto con inventario completo, freeze, drain, reconcile
e convergenza di tutti i device, inclusi annullamenti. Vecchi client offline o
non disponibili impediscono l'abilitazione web; non possono ricevere divieti
remoti. Il Win7 fisico già indisponibile resta BLOCKED_TARGET, non viene richiesto
di nuovo. C provider moderno MISSING e CSV BLOCKED_INPUT restano distinti.

Nessuna nuova prova runtime è attribuita finché non eseguita. Export/migrazione
e replica vengono provati su fixture sintetiche, mai sul registro reale importato.
Le ricevute originali e gli importi restano intatti; nessun Apply/undo degli originali.

## Checkpoint implementazione10novembre, futuro client separato

Export da backup verificato e recupero dell'archivio esatto, ack di freeze
device autenticato, cache dedicata SQLite0016 con staging e pubblicazione atomica,
replica snapshot/delta con cursori/checksum, annullamenti e ripresa. I report
scelgono una sola fonte storica e non raddoppiano locale+cloud; la copia offline
è esplicitamente l'ultima completa. Ricevute locali originali restano consultabili
come provenienza. Nessuna attribuzione al cassiere corrente. Grant server fresco,
limitato nel tempo e controllato con orologio monotono per ogni nuovo Apply/Undo
locale; offline/authority frozen o web non consentono scritture locali. Replay
della ricevuta già committata resta una lettura. Il rollback web rimane congelato.

UI nel dialog esistente: prepara/recupera export, sincronizza storico, IT/ES/EN/ZH;
owner/footer/centering invariati. Build source identity fornita dalla toolchain
Release Pack, mai dedotta dal codice staff o inventata da un client locale.
Registro/prepared export/cache restano separati da vendite native, stock e outbox.
La verifica native-outbox è nel livello Data, nessuna query WPF.

15 prove mirate export/recupero/replica/report PASS, WPF net48/x86 build PASS e
dialog standards39/39 PASS. Prima suite Core1757:1735 PASS/22 FAIL, ricevuta
conservata; cause includono fixture ferme a0015, recognizer della nuova baseline,
prerequisito CLI e boundary WPF/Dapper. Corretti senza modificare checksum0001–15
o allentare guard. Nuovo gruppo116:115 PASS/1 FAIL, ultima asserzione del numero
di migrazioni ripristinate aggiornata15→16. Suite Local finale da eseguire.

Admin PR151 head35f3312: SQL72file/4587 assertion PASS,59 nuove;
CI42770536504 e Cloudflare42770536548 SUCCESS. API/UI e follow-up dei totali
review/nome postazioni sono una candidata successiva e non ereditano quei PASS.
Nessun deployment, SQL live, UAC, avvio POS, login o operazione sul registro reale.
Nuovo client non impacchettato/installato; qualificazione WPF completa, DPI e
hardware Win7 restano NOT_RUN/BLOCKED_TARGET. Nessun DONE.
