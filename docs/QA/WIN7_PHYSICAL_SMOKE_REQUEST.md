# Windows 7 physical smoke request

Status: `WIN7_PHYSICAL_MACHINE_REQUIRED_WITH_ARTIFACT_AND_SCRIPT_READY`

Date: 2026-07-06 UTC
Branch: `fix/win7pos-hardening-phase3`
Head SHA: `46fcd681fcb74fc66cdca3979b8e350172430fb3`

## Required target

- Windows 7 SP1 real machine or VM.
- .NET Framework 4.8 installed.
- Microsoft Visual C++ Redistributable 2015-2022 x86 installed.
- Network access to Admin Web staging:
  `https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev`
- A printer or virtual printer for receipt/PDF checks, if available.
- Barcode scanner, if available.

## Current discovery result

This Codex session did not have a Windows 7 target attached:

- Hyper-V `Get-VM`: unavailable.
- VirtualBox `VBoxManage`: unavailable.
- VMware `vmrun`: unavailable.
- Checked local paths: `C:\VMs`, `C:\VirtualMachines`, `D:\VMs`,
  `C:\Users\xniw9\VirtualBox VMs`, and
  `C:\Users\xniw9\Documents\Virtual Machines`.

## Artifact to use

Use the GitHub artifact from Release Pack run `28762470861`.

| Artifact | ID | Digest |
| --- | --- | --- |
| `Win7POS-Setup` | `8098062159` | `sha256:f4fe4a6e0937738414c110caa5d7fef5cd490003781bbcd8f40198af3f37fccb` |
| `Win7POS-dist` | `8098062657` | `sha256:2dc7273ea26aa6b0c25b2817f98fce5938632403511929d9832c2d8a4dac2680` |
| `Win7POS-ReleasePack-x86` | `8098063084` | `sha256:35d39fd02d970b6d0b410e416a8fa16f4728bbc5f519379448a65520bef4a8e7` |

Downloaded and validated on the ASUS host:

- `C:\Temp\Win7POS-gh-artifacts-28762470861\Win7POS-dist`
- `C:\Temp\Win7POS-gh-artifacts-28762470861\Win7POS-Setup\Win7POS-Setup.exe`
- `C:\Temp\Win7POS-gh-artifacts-28762470861\Win7POS-ReleasePack-x86\Win7POS_20260706_0142.zip`

File hashes after extraction/download:

- `Win7POS-Setup.exe` SHA256:
  `B00F89309B0AD10E9E166AA496EE33759DAF78381FDDE7F15A29AE5DD315A8D4`
- `Win7POS_20260706_0142.zip` SHA256:
  `BAB640096BF3C1E9FCD7802E9E0A981ED7B84DD9DC9869371FAF11D24A8F010E`

## Prepare target folder

Copy the dist artifact to:

```powershell
C:\Win7POSTest\drop\Win7POS
```

Create the data directory:

```powershell
New-Item -ItemType Directory -Force -Path C:\Win7POSTest\data
```

Optional staging URL config:

```powershell
"AdminWebBaseUrl=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev" |
  Set-Content -Encoding ASCII C:\Win7POSTest\data\pos-admin-web.config
```

## Prereq command

Run on Windows 7:

```powershell
powershell -ExecutionPolicy Bypass -File C:\Win7POSTest\drop\Win7POS\scripts\win7-smoke\check-win7-prereqs.ps1 `
  -AppDir C:\Win7POSTest\drop\Win7POS `
  -DataDir C:\Win7POSTest\data `
  -AdminWebBaseUrl https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev
```

Expected result:

```text
=== RESULT: ALL PASS ===
```

## Manual smoke checklist

Run the checklist in `docs\WIN7_PRODUCTION_SMOKE_CHECKLIST.md`.

Minimum required checks:

- Startup: `Win7POS.Wpf.exe` opens without crash.
- Database: DB create/migrate completes in `C:\Win7POSTest\data`.
- First-login staging: synthetic shop/staff credentials only.
- Supplier Excel import: `.xls` preview/apply.
- Supplier Excel import: `.xlsx` preview/apply.
- Step 4 apply: catalog import outbox row is created.
- Catalog import sync: outbox reaches `acked` after staging E2E credentials are available.
- Offline sale: sale saved locally without network.
- Reconnect sales sync: pending sale syncs without duplicate.
- Restore guard: unresolved outbox prevents unsafe restore.
- Printer: Notepad test print.
- Printer: receipt/PDF flow, if printer is available.
- Scanner: barcode scan, if scanner is available.

## Evidence to attach

Attach only non-secret evidence:

- Windows version / SP1 proof.
- .NET Framework 4.8 proof.
- VC++ x86 prereq result.
- `C:\Win7POSTest\data\logs\app.log`
- `C:\Win7POSTest\data\logs\physical-smoke.txt`, if using the bridge scripts.
- Screenshot of startup shell.
- Screenshot or log of Excel import apply.
- Request IDs for first-login/catalog/sales sync, with tokens redacted.
- Printer name used for receipt/PDF smoke.

Do not attach:

- session tokens;
- device tokens;
- service-role keys;
- DB passwords;
- real shop/customer data.

## Owner action

The owner or QA operator with access to a Windows 7 SP1 target must execute this smoke.

After completion, update the delivery report with:

- `WIN7_PHYSICAL_MACHINE_REQUIRED_WITH_ARTIFACT_AND_SCRIPT_READY` -> `PASS_WIN7_PHYSICAL_SMOKE`
  if all required checks pass.
- Exact failing step and log path if any check fails.
