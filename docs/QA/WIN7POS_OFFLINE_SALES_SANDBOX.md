# Win7POS offline sales QA sandbox

## Purpose

Use this workflow to exercise the real Win7POS sales and payment UI without an
Admin Web connection. It creates a new synthetic database and a short-lived QA
authorization lease. It must never reuse a shop, Epson validation or production
data directory.

## Start

Close any running Win7POS instance, then run from the repository root:

```powershell
pwsh -File scripts/start-offline-sales-qa.ps1
```

The launcher:

- creates a unique directory below `C:\POSData\Win7POS-QA`;
- refuses an existing non-empty directory or a reparse-point target;
- builds and runs the non-shipping UI smoke harness seed;
- seeds 48 synthetic products and an empty sales register;
- creates one shop-bound synthetic remote operator mirror and an authorization
  lease valid for at most 12 hours;
- starts Win7POS with `--safe-start` and an Admin Web endpoint restricted to
  loopback;
- disables sales sync, automatic receipt/fiscal printing and the cash drawer.

To reopen the same synthetic run during its existing authorization lease, close
Win7POS and explicitly pass its directory:

```powershell
pwsh -File scripts/start-offline-sales-qa.ps1 `
  -DataDir C:\POSData\Win7POS-QA\Offline-Sales-YYYYMMDD-HHMMSS `
  -ResumeExistingSandbox
```

Resume mode requires the original non-empty sandbox marker and data files below
`C:\POSData\Win7POS-QA`, rejects reparse points and runs a database-read-only,
fail-closed check that saved printer names, receipt output, cash-drawer output,
the synthetic shop identity, the authorization lease and the fiscal lock remain
valid. A unique verifier exit code prevents a stale harness from being accepted.
It does not reseed, replace or renew the lease, and does not intentionally alter
existing sales, stock or outbox rows; normal application startup may run
idempotent schema migrations and write diagnostics. Safe Start and the
loopback-only endpoint remain mandatory. Use it only to continue the same
current QA run; it is never a way to reuse a shop, Epson validation or production
data directory.

`-SkipBuild` is intended only for an already validated local build; the launcher
still requires the verifier's current success code and fails closed for a stale
or incompatible harness.

In the normal access form keep shop code `QA-SHOP`, enter staff code `qa-admin`
and the existing synthetic QA PIN manually, then choose **Sign in**. The local
loopback request fails closed and the normal offline-mirror flow verifies the
PIN, lease, catalog and shop binding before opening POS. Do not choose **Local
recovery sign-in**: local recovery intentionally remains a restricted maintenance
surface and can never promote itself to sales access.

Test products use barcodes `QA000001` through `QA000048`. Cash, card and mixed
payments can be committed; their sales, stock movements and outbox rows remain
inside the sandbox database.

## Validated runtime matrix

The interactive run in
`C:\POSData\Win7POS-QA\Offline-Sales-20260718-220237` completed three sales:

- manual-price cash: CLP 3,432;
- `QA000002` card: CLP 550;
- `QA000003` mixed: CLP 300 cash plus CLP 275 card.

The final read-only audit found 3 sales, 3 lines, 2 expected stock movements and
3 sales-outbox rows. Gross was CLP 4,557; cash CLP 3,732; card CLP 825; change
zero. Every outbox row remained `pending` with zero attempts and no server ID.
The manual-price line correctly had no stock movement; `QA000002` and
`QA000003` each decremented stock once. Foreign-key checks and SQLite
`quick_check` passed.

Sales Register previews and Daily Close reproduced the same totals and did not
change sales, stock, outbox or fiscal state. No print, PDF, spooler or drawer
activity occurred. A live UI review also caught and corrected the shared
ComboBox template so the Sales Register operator filter renders its friendly
display name instead of the CLR type name.

## Hardware

Receipt printing and the cash drawer are disabled in every new sandbox. Enable
them manually only when a physical hardware test is explicitly intended. The
shop name and receipt footer identify the data as QA / non-fiscal.

## Limits

This workflow validates local POS behavior only. It does not validate Admin Web
authentication, reconnect, server acknowledgement, revocation or remote
idempotency. Those scenarios require an authenticated staging account for the
same shop.

After the lease expires, create a new sandbox by running the launcher again. Do
not copy or renew `pos-trusted-device.json`, and do not point the launcher at an
existing physical QA database.
