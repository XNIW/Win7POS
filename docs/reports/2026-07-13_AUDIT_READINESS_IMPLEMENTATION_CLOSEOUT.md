# Audit readiness implementation closeout — 2026-07-13

## Scope and Git provenance

- Repository: `XNIW/Win7POS`
- Base branch: `feature/ui-ux-products-settings-unification`
- Remediation branch: `fix/audit-readiness-bootstrap-ci-ui-security`
- Initial SHA: `034ac9566429f1531f7adee9c4adc4afe05ab35e`
- Initial `origin/main`: `353b7f752db61bed77ae1eadd3207b841eb4c961`
- Sync result: the expected snapshot was still current; feature was 3 commits ahead and 0 behind main.
- Code-closure SHA before this report: `d88a112`
- Final SHA: the commit containing this report; the authoritative full value is recorded by `git rev-parse HEAD` and by the final pack `VERSION.txt`.

No reset, clean, stash, push, merge or PR operation was performed.

## Original findings and pre-patch proof

| Finding | Pre-patch proof | Classification |
|---|---|---|
| Fresh-install local recovery unreachable | Isolated empty DB plus unreachable loopback Admin Web ended at `network_error` / missing offline mirror; no recovery action was shown and the app closed on cancel. | Runtime/product bug |
| Denial/recovery decision ambiguous | Network fallback and first-run eligibility were interleaved in the WPF dialog, without a pure policy boundary. | Security invariant at risk |
| Six gates failed | `first-login-sale-safe`, `online-bootstrap`, `online-client`, `online-linking`, `security-hardening`, `supplier-excel-wizard`. | Five stale/contradictory checkers plus one packaging mismatch; recovery reachability was a real bug |
| CI gate drift | CI ran only a subset of the locally audited gates. | CI/provenance mismatch |
| Release support files duplicated | Workflow and Windows builder generated three different inline documents. | Packaging/provenance mismatch |
| SDK nondeterministic | Workflows selected `10.0.x`; no `global.json`. | Toolchain mismatch |
| Toast severity inferred from translated text | Italian/Spanish/Chinese messages without the searched English fragments could auto-dismiss as non-errors. | Product/accessibility bug |

Baseline gate result: 7 PASS, 6 FAIL. Baseline WPF Release x86 build passed. The isolated reproduction used only a test data directory and loopback endpoint; supplied test identifiers and credentials are intentionally omitted from this report and were absent from the sanitized logs.

## Implemented strategy

The startup decision is now split into a pure Core policy, one targeted Data snapshot, and a thin WPF layer. Local admin creation and its two security events run in one immediate SQLite transaction. The unified POS access dialog is retained; `FirstRunSetupDialog` is reachable only as an explicit eligible recovery child.

### Startup and recovery matrix

| State / result | Decision |
|---|---|
| Empty DB + successful online bootstrap + sale-safe catalog | Normal POS; no local recovery |
| Empty DB + missing server / DNS / TLS / network / timeout / temporary server failure | Primary retry online; secondary explicit local recovery |
| Empty DB + invalid credentials / 401 / 403 / device / policy / contract / invalid authenticated response | Denied; no mirror fallback and no local admin recovery |
| Active remote mirror + network unavailable | Existing offline mirror login; no admin creation |
| Existing rows, all disabled | Restore/Admin Web guidance; not a fresh install |
| Active local recovery user | Unified local login path |
| Authenticated user + catalog not sale-safe | Recovery shell; `PosView` is not created |
| Recovery catalog verified sale-safe | Controlled start-of-day transition to normal POS |

Recovery shell exposes Products/Import when the operator retains catalog permission, server retry, language, backup/restore and diagnostics. Sales, payment, refund, void and cash drawer remain unreachable until verification. The persistent banner and placeholder do not grant catalog permission to a remote role that lacks it.

## Security and data invariants

- Online denial is classified before any transient/offline recovery decision.
- Zero users is rechecked inside the same immediate transaction as the administrator insert.
- User insert and both security audit rows roll back together on failure.
- Catalog approval requires an active product with barcode, name and positive sale price; marker and audit are atomic.
- PIN/password values are neither returned nor logged; request/local variables and `PasswordBox` controls are cleared in `finally`.
- Queries introduced by this remediation are parameterized; startup uses one aggregate snapshot instead of `ListAsync()`.
- Existing TLS 1.2, URL guards, DPAPI CurrentUser token protection, role mapping, operator switch and permission-denial behavior remain gated.
- Release validation rejects source, PDB, DB, token/service-role markers and production/secret-like config.

## Tests added

- Recovery policy: transient failures, missing server, invalid credentials, HTTP 401/403, device/policy/contract/response denial, remote mirror, disabled users and local login.
- Shell policy: unsafe catalog remains recovery; sale-safe catalog exits recovery.
- Data: targeted bootstrap snapshot, concurrent first-admin race, audit rollback, local catalog approval.
- Notice policy: Info 4 s, Success 3 s, Warning 8 s/manual dismiss, Error persistent/manual dismiss.

The negative tests reproduce the previously unsafe or ambiguous states; before the patch the policy/data APIs and atomic transaction did not exist, so these tests could not pass. The corresponding positive tests preserve legitimate retry, offline mirror, local recovery and sale-safe exit behavior.

## Files and architecture changed

- `Win7POS.Core`: explicit recovery/access/shell and notice policies.
- `Win7POS.Data`: targeted user-state queries, atomic first-run administrator and atomic local catalog approval.
- `Win7POS.Wpf`: unified access recovery actions, controlled recovery shell/settings, typed accessible toast.
- `scripts`: corrected structural gates, canonical gate aggregator, deterministic builder and shared release support writer.
- `.github/workflows`: exact SDK and canonical gate/release validation parity.
- `README.md`: actual explicit recovery and catalog-lock behavior.

No Android/Kotlin or external component was modified.

## Validation results

| Command / check | Result | Essential result |
|---|---|---|
| `C:\Dev\dotnet10\dotnet.exe --info` | PASS | SDK 10.0.301 selected by `global.json` |
| `dotnet restore Win7POS.slnx` | PASS | All projects up to date |
| `pwsh -NoProfile -File scripts/check-required-gates.ps1` | PASS | 15/15 canonical gates PASS |
| `dotnet build Win7POS.slnx -c Release --no-restore` | PASS | 0 warnings, 0 errors |
| `dotnet test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release --no-restore` | PASS | 58 passed, 0 failed, 0 skipped |
| `dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release --no-restore -- --selftest --keepdb` | PASS | Sale/refund/import/apply and Chinese selftest PASS |
| `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore` | PASS | net48 x86, 0 warnings, 0 errors |
| `dotnet list package --vulnerable --include-transitive` | PASS | No vulnerable packages in all projects |
| `dotnet list package --deprecated` | PASS | No deprecated packages in all projects |
| `git diff --check` | PASS | No whitespace errors |
| Clean Windows release builder | PASS | Dist recreated; provenance and payload guards passed |
| Release pack completeness (`-WriteManifests`) | PASS | Required files and manifests present; forbidden payload absent |
| Win7 runtime release validation | PASS | net48/x86 runtime layout and PE validation passed |
| Online linking validator on `dist\Win7POS` | PASS | Shared docs and `VERSION.txt` provenance passed |
| Inno Setup installer | PASS | Inno Setup 6.7.3 compiled `installer/output/Win7POS-Setup.exe` successfully |

`VERSION.txt` is generated only by `write-release-support-files.ps1` and records full/short SHA, ref, UTC timestamp, configuration, platform, SDK, clean/dirty state and CI run number when applicable. The final pack is generated after the report commit so its `CommitSHA` equals final `HEAD`; the exact value is intentionally taken from the artifact rather than duplicated inside its own commit.

## UI/UX and screenshots

Automatic structural validation passed for 1024-minimum layout constraints, shared dialog sizing/ownership rules, virtualization, keyboard commands, localization keys, disabled states, double-submit prevention and accessible toast dismiss. English, Spanish, Italian and Chinese entries are present for all new operator-facing paths.

- Pre-patch access screenshot: observed during the isolated baseline; not persisted in the repository.
- Products screenshot: **NOT RUN / not attached**. The available desktop automation policy prohibits automating authentication dialogs, and no already-authenticated test window was available. Creating a production-binary login bypass solely for a screenshot was rejected as a security regression.
- Manual 1024×768 / 1366×768 at 100% / 125% DPI: **NOT RUN** on this Windows 10 builder.

## Smoke matrix

| Scenario | Result | Evidence / limitation |
|---|---|---|
| Fresh install offline, pre-patch | MANUAL PASS (reproduction) | Empty isolated DB created; network failure led to missing mirror and no recovery, confirming the bug |
| Fresh install offline, post-patch policy/data | AUTOMATIC PASS | Recovery eligibility, atomic admin and recovery-shell tests pass |
| Online denied | AUTOMATIC PASS | Negative invalid-credential/401/403/device/policy/contract tests; gate confirms no fallback/recovery |
| Offline mirror | AUTOMATIC PASS | Policy chooses mirror login and forbids admin creation |
| All users disabled | AUTOMATIC PASS | Data snapshot and policy regression test |
| Catalog unsafe / safe transition | AUTOMATIC PASS | Recovery/exit policy plus atomic catalog verification test |
| Final interactive Windows UI scenarios | NOT RUN | Authentication UI was not automated; requires operator-driven smoke |
| Windows 7 SP1 VM/physical smoke | PENDING HARDWARE | No Windows 7 target was connected |
| Printer 58/80 mm and spooler | PENDING HARDWARE | No printer connected |
| Barcode scanner keyboard input | PENDING HARDWARE | No scanner connected |
| Cash drawer | PENDING HARDWARE | No configured drawer connected |

## Residual risks

1. Operator-driven post-patch UI smoke at both target resolutions/DPI values remains required.
2. Windows 7 SP1 runtime compatibility must still be confirmed on the target OS.
3. Printer, scanner and cash-drawer behavior remains hardware-dependent.
4. The generated installer has not yet been installed and exercised on a clean Windows 7 target.
5. CI workflows were structurally validated locally but not executed remotely because no push/PR was authorized.
