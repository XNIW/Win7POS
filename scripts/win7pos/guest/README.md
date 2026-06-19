# Win7POS-Test Guest Notes

## Ruolo della VM Windows 7

`Win7POS-Test` è solo il target runtime legacy per smoke test. Non ospita Codex, non fa build e non deve contenere Visual Studio o tool di sviluppo salvo runtime strettamente necessari.

## Flusso consentito

1. Ricevere il drop già buildato dalla Builder VM.
2. Copiare il drop in:

```text
C:\Win7POSTest\drop\Win7POS
```

3. Copiare lo script guest in:

```text
C:\Win7POSTest\run-pos-smoke.bat
```

4. Eseguire:

```cmd
C:\Win7POSTest\run-pos-smoke.bat
```

5. Salvare screenshot e log nella cartella condivisa.

## Evidence attesa

- Screenshot della schermata iniziale o dell'errore.
- `C:\Win7POSTest\data\logs\app.log`, se generato.
- Note su runtime installati.
- Note su hardware non testato.

## Limiti

- Non installare Codex dentro Windows 7.
- Non usare internet in Windows 7 se non necessario e approvato.
- Non usare dati reali del negozio.
- Non modificare registry.
- Non cambiare configurazioni hardware POS reali.
- Non fare restore snapshot automatico da script.
- Non creare agenti comandi arbitrari dentro Win7.

Le azioni Win7 restano manuali, assistite via GUI/hypervisor, o limitate allo script `run-pos-smoke.bat`.
