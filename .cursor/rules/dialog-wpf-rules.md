# Dialog WPF Rules

## Trigger

Questa regola si applica quando tocchi:

- `DialogShellWindow`
- `WindowSizingHelper`
- `MonitorHelper`
- `DialogOwnerHelper`
- `*Dialog.xaml`
- `*Dialog.xaml.cs`

## Prima di modificare

- Leggi `docs/DIALOG_STANDARD.md`.

## Regole

- Non usare `Loaded` per sizing o positioning.
- Non introdurre `Left` / `Top` custom.
- Usa `DialogOwnerHelper.GetSafeOwner()` per owner top-level.
- Usa `OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()` per nested dialogs.
- `AddWorkAreaClampHook` solo nella base class.
- Usa gli shared dialog styles per titolo/footer.

## Patch policy

- Preferisci patch piccole e locali.
- Non riscrivere interi file senza motivo.
- Riporta solo build/check/errori essenziali.
