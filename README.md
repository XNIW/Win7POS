# Win7POS

POS (Point of Sale) per Windows 7 / .NET Framework 4.8, architettura x86. Valuta: CLP (pesos cileni).

## Build (x86)

```bash
# Dalla root del repository
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release
```

Il progetto è configurato con `PlatformTarget=x86` e `TargetFramework=net48`. L’eseguibile si trova in `src/Win7POS.Wpf/bin/Release/net48/`.

## Dati e log in produzione

In ambiente Windows l’applicazione usa la cartella comune:

- **Root dati:** `C:\ProgramData\Win7POS`
- **Database SQLite:** `C:\ProgramData\Win7POS\pos.db`
- **Log:** `C:\ProgramData\Win7POS\logs\app.log`
- **Backup:** `C:\ProgramData\Win7POS\backups`
- **Export:** `C:\ProgramData\Win7POS\exports`
- **Ricevute (PDF/copie):** `C:\ProgramData\Win7POS\receipts` (se abilitato)

In caso di errore, controllare i log in `C:\ProgramData\Win7POS\logs\app.log`.

## Stampante

- Dal menu laterale: **Stampa** → **Impostazioni stampante** per scegliere la stampante e le opzioni (copie, stampa automatica, salvataggio copia).
- **Stampa ultima ricevuta** per ristampare l’ultimo scontrino.
- Per test rapidi è possibile usare “Salva in file” o una stampante PDF.

## Backup e restore del database SQLite

- **Backup:** menu **Database** → **Backup database**. Il file viene salvato in `C:\ProgramData\Win7POS\backups` con nome tipo `pos_backup_yyyyMMdd_HHmmss.db`.
- **Restore:** menu **Database** → **Manutenzione database** (o percorso equivalente per il restore). Copiare il file `.db` di backup nella cartella desiderata e, dall’app, selezionare il restore da quel file (l’app sovrascrive il `pos.db` corrente con il file scelto). Eseguire sempre un backup prima di un restore.

**Manuale (senza UI):** copiare `C:\ProgramData\Win7POS\pos.db` in un luogo sicuro per il backup. Per il restore, chiudere l’applicazione, sostituire `pos.db` con il file di backup e riavviare.
