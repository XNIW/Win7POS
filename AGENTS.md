# Agent Notes

## Repo context

- WPF
- `net48`
- `x86`
- Windows 7 first

## Dialog rule

Prima di toccare uno di questi file o pattern, leggere `docs/DIALOG_STANDARD.md`:

- `*Dialog.xaml`
- `*Dialog.xaml.cs`
- `DialogShellWindow`
- `WindowSizingHelper`
- `MonitorHelper`
- `DialogOwnerHelper`

## Guardrails

- No `Loaded` per positioning/sizing.
- No `Left`/`Top` custom nei dialog.
- Owner via `DialogOwnerHelper.GetSafeOwner()`.
- Nested dialogs via `OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()`.
- Clamp hook solo nella base class.
- Footer buttons/title con le shared resources dialog.

## Docs

- `docs/DIALOG_STANDARD.md` – standard dialog WPF (leggere prima di toccare dialog).
- `docs/FORMATO_LOG_DEBUG.md` – formato log e guida al debug.
- `docs/AI_WORKLOG.md` – cronologia sintetica sessioni AI.
- `docs/reports/` – report storici (analisi, cleanup, revert).
- `docs/plans/_archive/` – piani eseguiti.

## Validation

- `pwsh -File scripts/check-dialog-standards.ps1`
- `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
