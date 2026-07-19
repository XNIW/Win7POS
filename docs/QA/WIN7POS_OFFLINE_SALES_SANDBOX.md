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

In the normal access form keep shop code `QA-SHOP`, enter staff code `qa-admin`
and the existing synthetic QA PIN manually, then choose **Sign in**. The local
loopback request fails closed and the normal offline-mirror flow verifies the
PIN, lease, catalog and shop binding before opening POS. Do not choose **Local
recovery sign-in**: local recovery intentionally remains a restricted maintenance
surface and can never promote itself to sales access.

Test products use barcodes `QA000001` through `QA000048`. Cash, card and mixed
payments can be committed; their sales, stock movements and outbox rows remain
inside the sandbox database.

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
