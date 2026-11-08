# ASUS-W7POS-019 — correzione del catalogo HTTP 409 unmapped

> Prosecuzione corrente8novembre: Windows1823697 è qualificato e installato,
> Retry completo verificato su fixture; [report end-to-end](ASUS_W7POS_019_RETRY_E2E_RESULT.md).
> L'autorizzazione al normale avvio era già concessa, ma il nuovo tentativo è
> stato nuovamente rifiutato dalla policy dello strumento. Non è più una
> richiesta di autorizzazione pendente. Nessun nuovo apply SQL o deploy in
> questa prosecuzione; la prova RPC sotto mantiene la propria attribuzione.

Aggiornamento: 8 novembre 2026, America/Santiago. Scope pubblico NEW_SHOP_019.
Stato: FIX_APPLIED_RPC_VERIFIED. NORMAL_POS_ACCESS_NOT_RUN_POLICY.
La correzione server è pubblicata e applicata; il percorso HTTP/UI normale
non è ancora qualificato. Nessuna nuova task, import Getnet o qualifica Win7.

## Causa dimostrata e correzione

Il primo accesso reale precedente aveva creato trust e sessione validi. Nel
progetto effettivo la Shop è active, la lease corrente è valida e il ruolo è
pos_admin; non esistono mapping o righe nelle quattro classi catalogo.
Il resolver installato restituiva legacy_owner_bridge/null/blocked. L'audit
correlato conferma unmapped allo stage manifest; il primo errore e tutti i sei
eventi storici sono conservati, senza un nuovo tentativo HTTP per diagnosticarli.

Il corpo installato coincideva esattamente con Admin main d021a4af:
9ef09cf7740e3e9c8e18e9dfa986fee0. La migration forward cambia soltanto il resolver:
shop attivo, zero righe e nessuna storia di bridge risolve lo scope canonico
shop_scoped sul proprio UUID. Mapping incompleto o disabilitato non diventa
native-empty; legacy verificato e cataloghi non vuoti mantengono il contratto.
Nessun owner dedotto, articolo fittizio, nuova tabella o grant.

Lease, lock di scope, controllo integrità, revision/snapshot/scope-key e fence
di pubblicazione restano attraversati. Il server produce il manifest dalle query
reali; non converte un errore in un payload vuoto. Il client Windows d262ccd e
il suo vincolo full-required sono invariati; uno zero non abilita le vendite.

## Identità e prove

| Ambito | Esito verificato |
| --- | --- |
| PR Admin | [143](https://github.com/XNIW/merchandise-control-admin-web/pull/143), candidata d864c740652e721559b6341c995aa5416e5f4b76, review statica indipendente senza rilievi |
| Merge normale |1a35034a490cfcbe108a7680ac3a0eb65d6c0d60, tree identico a candidata e merge-ref PR79006cebe |
| CI post-merge |[CI42451262145](https://github.com/XNIW/merchandise-control-admin-web/actions/runs/42451262145) e [Cloudflare42451262142](https://github.com/XNIW/merchandise-control-admin-web/actions/runs/42451262142) PASS; deploy Worker skipped |
| Database |67 file / 4.375 test pgTAP PASS, incluse28 nuove assertion; nessuna delle4347 precedenti rimossa |
| Foundation/UI sintetica |1.131 PASS / 14 SKIP / 0 FAIL; UI 48 PASS, pairing 2 PASS; Next/OpenNext/smoke Cloudflare PASS |
| Artifact DB |13605977380; SHA256 aced9fbd9b9396f9f19113679c445db86efa336bb99814f2f72cc76d8462c2ec verificato |
| Migration |20261108051044_asus_019_native_empty_catalog_scope.sql, SHA256 af69667d6e8e9988a833c4cb2b818c4378061cbc6869c4821b29aa5e64e379ce |
| Dopo apply reale |Body9622c4d3a4c793a7803d730b8a8c51d4; tutto pg_proc eccetto prosrc identico al backup live, inclusi OID/owner/ACL |
| Manifest RPC reale v3 |ok,shop_scoped,done,revision0,rows0; summary e windowCounts tutti0,owner null,scope-key legata allo shop |
| Worker effettivo |Version399720d2-c76f-440b-bf08-a4a2bd7da1a9 del15ottobre, URL Supabase pubblico confrontato col progetto; SHA Git non esposto |
| Client riusato |d262ccd già qualificato:1734/1734 Core/Data,50/50gate e quattro workflow PASS; nessuna suite Windows duplicata |
| HTTP/UI normale |NOT_RUN_POLICY, nessun nuovo login/pull o processo avviato dall'operazione negata |

I nuovi test attraversano una lease sintetica reale e verificano lo zero completo,
l'esclusione di una riga effettivamente presente in un altro shop, mapping
incompleti/disabilitati, missing/null/suspended, device/shop errati, cambio
mapping prima della pubblicazione, revoca, expiry e versione credenziale.
Gli esiti CI del merge-ref PR sono accompagnati dal confronto del tree; il
post-merge ha inoltre la propria receipt sull'esatto1a35034a.

## Apply e prime failure conservate

È stata applicata una sola migration via connettore sul progetto verificato.
Il record generato20261108053703 è stato allineato con un UPDATE atomico del solo
nuovo record posseduto alla versione sorgente20261108051044, controllando
nome/digest/assenza di duplicati. La cronologia contiene una sola voce e lo
statementSHA256 coincide con il file; nessun train pendente è stato applicato.
Backup e inversa privata con guard del nuovo corpo sono conservati, non eseguiti.
Advisor sicurezza5prima/5dopo, nessuna voce sul resolver; nessuna attestazione
globale aggiuntiva sul progetto.

PrimaCI555f502: FAIL al preflight per pin CRLF Windows invece del blob LF Git.
Artifact/log del primo GUARD_REJECTED conservati. Corretto il solo pin su nuova
SHA, senza normalizzare il verifier o cambiare SQL/assertion/expected/budget.
Preflight CLI read-only: NOT_RUN per IPv6 non supportato, nessuna mutazione CLI.

Primo preflight di avvio POS: NOT_RUN, manifest conteneva il bare host atteso e
il controllo richiedeva una URI assoluta. Confermati host esatto e assenza di
processi prima di costruire la sola authority HTTPS verificata.
Il successivo comando di avvio è stato respinto dalla policy con blocked by policy,
senza dettaglio ulteriore. Nessun processo/manifest di tentativo creato e nuovi
audit catalogo0 dopo apply. Non è stato usato un launcher alternativo né un retry
dopo quel rifiuto. È pendente l'autorizzazione specifica a un solo avvio normale.

## Limiti e ripresa precisa

Il test RPC non sostituisce il trasporto HTTP o il login UI e non ha simulato
un audit di pubblicazione successo. Dopo autorizzazione: verificare prima
processi/audit per eventuali esiti incerti, quindi ritentare soltanto l'avvio
negato una volta sul prodotto d262ccd e profilo isolato già identificati.
Qualunque credenziale necessaria resta nell'inserimento mascherato dell'utente.
Nessuna automazione dei dialog di autenticazione.

Il caso console staff/overview è distinto da questo409 e resta non qualificato.
Mapping esercente Getnet, inizioPOS/overlap, semantica provider e Win7 fisico
rimangono gate esterni. Nessun import, vendita, reset DB/outbox o revoca account.
I worktree, profilo/trust, log, prime failure e backup tecnici sono mantenuti
intenzionalmente con owner root; processo POS del tentativo negato assente.

Evidence private della prosecuzione: pointer catalog409-current-root.private.txt;
observed-cause.private.json, resolver-before-apply.private.json,
post-apply-metadata-and-policy.private.json, apply-and-rpc-verification.private.json,
ci-postmerge-verified/qualification.json e rollback-exact-resolver.private.sql.
Il [rapporto onboarding](ASUS_W7POS_019_ONBOARDING_EXECUTION_RESULT.md) e
l'[handoff Admin](../plans/ASUS-W7POS-019_GETNET_HISTORY/ONBOARDING_ADMIN_HANDOFF.md)
mantengono la storia e le qualifiche separate.
