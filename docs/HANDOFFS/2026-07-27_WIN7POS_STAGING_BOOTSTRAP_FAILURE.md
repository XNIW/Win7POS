# Win7POS staging catalog-pull 503 — Mac handoff

Status: `READY_FOR_MAC`.

## Bounded acceptance evidence

- Win7POS main SHA: `39733f45a3982aa69dee7777a86bc88ed2cd6fbe`.
- Acceptance completed: `2026-07-27T19:31:27.9819345Z`.
- Outcome: `bootstrap_catalog_pull_http_5xx`.
- Failure stage / root: `catalog_pull` / `http_5xx`.
- HTTP status: `503`.
- Request reached server: `true`.
- Device approval state: `approved`.
- First login / trusted session / catalog start: `true` / `true` / `true`.
- Catalog pages / rows: `0` / unavailable because the request failed before a
  valid catalog page was received.
- Redacted client request ID: `sha256:081732bebab8`.
- Redacted edge correlation ID: `sha256:c24e0c989466`.
- Server request ID: not supplied by the response.
- Evidence directory:
  `C:\Dev\_codex-evidence\win7pos-staging-acceptance-20260727-193121`.

## Read-only audit correlation

The `±3 minute` staging audit window contains only:

| Timestamp UTC | Event key | Result | Severity |
| --- | --- | --- | --- |
| 2026-07-27T19:31:27.242812Z | `pos.auth.first_login.success` | `success` | `info` |
| 2026-07-27T19:31:27.242812Z | `pos.device.trusted` | `success` | `info` |

No catalog success or failure audit event is present in the window. This
confirms that authentication, dedicated-device approval and client bootstrap
were successful; the `503` belongs to the server/edge catalog-pull path.

## Requested Mac action

1. Correlate the redacted edge ID and timestamp with staging catalog-pull
   worker/edge logs.
2. Diagnose and fix the staging-only `503`; do not alter production, catalog
   data, Android/iOS, or the Win7POS client as part of this handoff.
3. Deploy the server-side fix to staging and validate the endpoint with the
   existing bounded observability/audit workflow.
4. Return `READY_FOR_ASUS_BOOTSTRAP_ACCEPTANCE` with the fixed staging SHA,
   bounded audit result and no secret or full identifier.

The single ASUS acceptance authorized by this task has already been consumed.
Do not trigger another ASUS acceptance without explicit user authorization.
