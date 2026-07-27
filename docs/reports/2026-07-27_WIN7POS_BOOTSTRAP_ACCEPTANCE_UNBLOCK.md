# Win7POS staging bootstrap acceptance unblock — 2026-07-27

Status: `CODEX_MAC_HANDOFF_READY`.

This is an evidence and design record for the bootstrap unblock. It is not a
catalog acceptance closeout and does not transition `ASUS-W7POS-013` to `DONE`.

## Frozen failed-run evidence

- Frozen copy: `C:\Dev\_codex-evidence\win7pos-bootstrap-unblock-20260727\failed-run-frozen`.
- Failed call interval: `2026-07-27T18:09:10.3837602Z` to
  `2026-07-27T18:09:11.1719193Z`.
- The original report had `bootstrapCode=failure`, no catalog pages and no
  local catalog rows. Its generic `bootstrap_failure` did not retain the HTTP
  status, response receipt fact or transport root code.
- The profile-value scan recorded no profile-value hit. That check was not
  reached in the original control flow and is not evidence about credential
  validity.

The read-only staging audit correlation for the three-minute window around the
call found no first-login, device, session, credential or bootstrap record.
No audit request identifier is reproduced here. This places the observed
failure in classification A: client transport, edge response or harness. It
does not prove a credential, device-approval or server-application failure.

## Manual WPF versus acceptance harness

| Field | Manual WPF | Harness before fix | Match | Action |
| --- | --- | --- | --- | --- |
| Base URL | validated `PosAdminWebOptions` | protected profile, same validation | yes | retain |
| Shop/staff/credential | common request DTO; values present | common request DTO; values present | yes | retain |
| JSON, content type, method, route, User-Agent | `PosAdminWebClient` | same `PosAdminWebClient` | yes | retain |
| AppVersion | production WPF assembly | UiSmokeHarness assembly | no | use `PosApplicationVersion` |
| Device identifier | persistent normal POS data directory | recreated when isolated directory moved | no | v2 DPAPI QA profile |
| Display name | production machine-derived format | machine-derived per run | incomplete | persist v2 QA profile value |
| Outer bootstrap timeout | six minutes | ten minutes | no | use six minutes |
| DB/settings | production initialization | isolated initialization | intentional | retain isolation |
| First-login contract, trusted session, operator mirror, catalog start | `PosOnlineBootstrapService` | same service | yes | retain |
| Device lifecycle | persistent device identity | transient device identity | no | stable, dedicated QA identity |

No profile value, token, complete shop/staff identifier or response body is
included in this matrix.

## Implemented diagnostic boundary

`PosOnlineResult`, `PosOnlineBootstrapResult` and the acceptance report now
retain only bounded facts: failure stage, root code, HTTP status, retryability,
authentication result, device approval state, redacted technical identifiers,
sanitized exception type, request-response receipt, and the first-login/trust/
catalog progress flags. Error bodies, cookies, tokens, credentials and full
identifiers are not copied to the report.

An HTTP response without an application `code` is now classified as a bounded
HTTP root (`http_401`, `http_403`, `http_409`, `http_5xx` or `http_<status>`),
rather than the ambiguous `failure`. DNS, TLS, network, timeout and invalid
response remain separate offline-testable roots.

## Stable QA identity

The `asus-staging` profile was migrated in place from schema v1 to v2 through
DPAPI CurrentUser with the existing restricted ACL. The migration generated one
dedicated QA device identifier and display name without requesting or emitting
credential values. Its report retains only a truncated fingerprint. Re-running
the safe test reuses that fingerprint; deleting/recreating the isolated SQLite
directory no longer changes the identity sent by the harness.

The new `Test-Win7PosStagingCredential.ps1` performs no network operation and
reports only schema, allowlisted host, presence/length/format checks, expiry,
ACL state and a truncated device fingerprint.

## Remaining acceptance gate

Local verification is complete: the Core suite (675 tests), WPF and harness
`net48`/`x86` builds, solution build, 44 required gates, dialog standards,
credential-vault self-test, profile validation, bootstrap contract smoke and
diagnostics-matrix smoke all passed. An independent re-review found no
remaining P0, P1 or P2 issues. The smoke invocation uses the `x86` harness
with its required isolated `--data-dir`; it makes no network request.

The code was normally merged as PR #48 at `39733f45a3982aa69dee7777a86bc88ed2cd6fbe`.
The sole authorized follow-up acceptance ran once at `2026-07-27T19:31:27Z`.
It reached staging and completed first login, device trust and trusted-session
persistence, then failed during catalog pull with `HTTP 503`,
`failureStage=catalog_pull`, and `rootCode=http_5xx`. It is retryable from a
transport perspective, but no retry is authorized by this task.

Read-only audit correlation records `pos.auth.first_login.success` and
`pos.device.trusted` at `2026-07-27T19:31:27Z`; it contains no catalog success
or catalog failure audit event. The redacted acceptance evidence is
`C:\Dev\_codex-evidence\win7pos-staging-acceptance-20260727-193121`.
The server/edge catalog-pull owner handoff is
`docs/HANDOFFS/2026-07-27_WIN7POS_STAGING_BOOTSTRAP_FAILURE.md` and the
matching external Mac prompt is stored next to the frozen evidence. No secret,
response body, complete technical identifier, production system or catalog data
is included in either handoff.
