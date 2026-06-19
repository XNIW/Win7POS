# Win7POS VM Control Bridge

## Stato TASK-034

`TASK-034` mette il bridge VM in pausa: `PAUSED_VM_SETUP_REQUIRED`.

Non avviare bridge, job, UTM, VM o smoke Win7 reale in TASK-034. Il bridge resta un piano di ripresa per quando una `WinPOS-Builder` con toolchain Windows e una `Win7POS-Test` con runtime .NET Framework 4.8 saranno pronte e collegate a una cartella condivisa.

## Scopo

Le fasi precedenti hanno preparato build e smoke test, ma dal Mac corrente non sono visibili hypervisor, CLI VM, VM Windows o drop gia prodotto. Codex puo lavorare solo con risorse realmente accessibili: senza `UTM.app`, `utmctl`, una VM importata o una cartella condivisa montata, non puo avviare Windows, leggere schermi o raccogliere output.

Questa fase prepara un ponte operativo per rendere controllabile la Builder VM Windows 10/11 senza installare Codex dentro Windows 7.

## Opzioni di controllo

### Codex nella Builder VM

Opzione piu diretta quando l'utente puo installare o usare Codex dentro `WinPOS-Builder`.

- Usare solo Windows 10/11 Builder.
- Eseguire restore, build, package e checksum.
- Non installare Codex dentro `Win7POS-Test`.
- Esportare il drop verso Mac o cartella condivisa.

### Bridge con cartella condivisa

Opzione consigliata quando Codex resta sul Mac.

- Il Mac scrive job allowlistati in una cartella condivisa.
- La Builder VM esegue `start-builder-bridge.ps1`.
- Windows legge job da `inbox`, esegue solo azioni consentite e scrive log/output in `outbox`.
- Codex legge `outbox`, decide il passo successivo e prepara il drop per Win7.

### Hypervisor CLI

Usare solo se esiste davvero una CLI come `utmctl`, `prlctl`, `vmrun` o `VBoxManage`.

- Sono ammessi solo help/list/start non distruttivi.
- Non fare restore snapshot automatico.
- Non cancellare snapshot o dati.
- Non dichiarare VM controllabile se la CLI non elenca la VM.

### Computer Use su finestra VM

Utile solo se la finestra VM e visibile e controllabile dal Mac.

- Puo aiutare con click, terminale e screenshot.
- Non sostituisce la cartella condivisa per log/drop.
- Non deve inserire password, licenze, token o dati reali.

## Piano consigliato per Mac Apple Silicon

1. Installare o rendere disponibile un hypervisor scelto, per esempio UTM, Parallels, VMware Fusion o VirtualBox, senza farlo automaticamente da Codex.
2. Creare o importare `WinPOS-Builder` con Windows 10/11.
3. Installare nella Builder VM solo gli strumenti necessari: Visual Studio o Build Tools, MSBuild, .NET Framework 4.8 targeting pack, restore NuGet via MSBuild, Inno Setup opzionale.
4. Creare o importare `Win7POS-Test` con Windows 7 SP1 x64.
5. Tenere Win7 pulita, isolata e con snapshot baseline manuale.
6. Abilitare una cartella condivisa tra Mac e Builder VM.
7. Mappare la cartella condivisa nella Builder VM come `C:\Win7POSBridge` oppure passare `-BridgeRoot`.
8. Avviare nella Builder VM:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\bridge\start-builder-bridge.ps1 -BridgeRoot C:\Win7POSBridge -Watch
```

9. Dal Mac inviare job:

```bash
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job env-report --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-dry-run --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job build-release --execute
scripts/win7pos/vm/send-builder-job.sh --bridge-root <shared-bridge-on-mac> --job package-drop --execute
```

10. Leggere log e zip da `outbox`, validare il drop sul Mac e preparare `.win7pos-vm/drop`.

## Cosa fa l'utente una sola volta

- Installare/configurare l'hypervisor.
- Creare/importare le VM.
- Configurare cartelle condivise.
- Installare tool build nella Builder VM.
- Avviare manualmente il bridge nella Builder VM.
- Copiare il drop nella VM Win7 o rendere visibile la cartella condivisa.

## Cosa puo fare Codex dopo

- Scoprire host/CLI/VM con `discover-vm-host.sh`.
- Inviare job build allowlistati con `send-builder-job.sh`.
- Leggere log, checksum e screenshot dalla cartella condivisa.
- Validare il drop sul Mac.
- Preparare `.win7pos-vm/drop`.
- Guidare smoke Win7 assistito e raccogliere evidence.

## Limiti sicurezza

- Il bridge non esegue comandi arbitrari.
- Nessun servizio Windows viene installato.
- Nessun registry viene modificato.
- Nessun dato reale del negozio deve entrare nel drop o nei report.
- Non usare internet in Windows 7 se non e necessario e approvato.
- Non usare Windows 7 come ambiente agente.
- Non fare restore snapshot automatico.
- Non correggere business code durante build/smoke evidence.

## Verdict quando non esiste una VM

Se discovery non trova hypervisor, CLI o file VM, il verdict operativo e:

```text
VM_SETUP_REQUIRED
```

In quel caso il prossimo passo e configurare manualmente un hypervisor e almeno la Builder VM, poi rieseguire:

```bash
scripts/win7pos/vm/discover-vm-host.sh
```
