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

The harness decrypts the profile only inside its process, calls the production
first-login/bootstrap/catalog services once, logs in the mirrored local
operator through the production session service, and captures only the
post-login read-only products view. It accepts only the verified HTTPS staging
hostname, uses the fixed isolated data directory
`C:\POSData\Win7POSAutomatedStagingAcceptance`, and writes redacted evidence
under `C:\Dev\_codex-evidence`. It never accepts secrets through arguments or
environment variables. Remove the local profile when no longer needed:

```powershell
pwsh -NoProfile -File scripts\qa\Remove-Win7PosStagingCredential.ps1 -Profile asus-staging
```

Run the credential vault self-test with synthetic values only:

```powershell
pwsh -NoProfile -File tests\qa\Win7PosQaCredentialVaultSelfTest.ps1
```
