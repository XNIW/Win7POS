# Win7POS staging QA credential vault

The staging QA profile is deliberately outside Git at
`C:\ProgramData\Win7POS\QaSecrets\<profile>.dpapi`. It uses DPAPI
`CurrentUser`, with inheritance removed and access restricted to the current
Windows user and `SYSTEM`.

One local operator initializes it with hidden prompts:

```powershell
pwsh -NoProfile -File scripts\qa\Set-Win7PosStagingCredential.ps1 -Profile asus-staging
```

Run the test-only acceptance harness without supplying a credential in chat,
arguments, environment variables, logs, or screenshots:

```powershell
pwsh -NoProfile -File scripts\qa\Invoke-Win7PosStagingAcceptance.ps1 -Profile asus-staging
```

Before the first invocation, build the test-only WPF harness in Release x86:

```powershell
& 'C:\Dev\dotnet10\dotnet.exe' build tests\Win7POS.Wpf.UiSmokeHarness\Win7POS.Wpf.UiSmokeHarness.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86
```

The runner requires a clean checkout whose `HEAD` exactly equals
`origin/main`, builds with `C:\Dev\dotnet10\dotnet.exe`, and generates one
logical run ID in the form
`ASUSART_POST_PR68_<UTC_TIMESTAMP>_<RANDOM>`. It archives any previous isolated
data directory before starting; it never performs an automatic or blind retry.
Evidence is written beneath
`C:\Dev\_codex-evidence\win7pos-final-post-pr68-<RUN_ID>`.

One logical run contains two bounded harness processes. The `prepare` process
performs first login/catalog, disables the article lane, persists the synthetic
create plus dependent edit, writes a restart checkpoint, and exits with the
dedicated restart code. The wrapper verifies that the process is gone and
starts the `resume` process against the same data directory. Resume attaches
the persisted trusted session without repeating first login, proves the two
outbox rows survived the process boundary, and completes the mutation matrix.
Only a request proven to have reached staging counts as a logical run.

The harness decrypts the profile only inside its process and calls the
production first-login, offline-authorization, catalog, local operator,
article repository/outbox, scheduler, ACK and canonical-pull paths. It accepts
only the verified HTTPS staging hostname and uses only:

```text
C:\POSData\Win7POSFinalArticleSyncAcceptance
```

Every staging article created by the harness is synthetic and mapped to the
run ID. The scenario covers offline create, a pre-ACK dependent edit, harness
restart, verified category/supplier references, prices, signed manual stock,
duplicate, deactivate/reactivate, replay, payload mismatch, stale conflict,
fair progress of an unrelated product, canonical readback, zero echo and
sales-lane isolation. A retry-wait row or HTTP/transport failure stops the run;
the harness does not make the same request again automatically.

Redacted evidence is written to:

```text
C:\Dev\_codex-evidence\win7pos-final-post-pr68-<RUN_ID>
```

`CLEANUP-MANIFEST.json` is the only evidence file that carries exact
synthetic remote/client IDs and barcodes. It contains no credential, session
token, PIN, request body, or pre-existing product data. The companion
`NEXT-CODEX-MAC-FINAL-CLEANUP.md` is generated with an exact, ready-to-run
Admin cleanup prompt. No sale, refund, void, receipt print, scanner or drawer
operation is performed.

The profile is never accepted through arguments or environment variables.
Remove the local profile when no longer needed:

```powershell
pwsh -NoProfile -File scripts\qa\Remove-Win7PosStagingCredential.ps1 -Profile asus-staging
```

Run the credential vault self-test with synthetic values only:

```powershell
pwsh -NoProfile -File tests\qa\Win7PosQaCredentialVaultSelfTest.ps1
```
