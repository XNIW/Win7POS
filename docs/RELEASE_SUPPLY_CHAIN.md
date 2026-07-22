# Win7POS release supply-chain runbook

Status: `REAL_PROTECTED_TAG_SIGNING=BLOCKED_EXTERNAL`.

Repository-local workflows and NON-PRODUCTION self-signed fixtures can verify the
signing wiring, but they are not production signing evidence. A real certificate,
an approved protected-tag run, a reachable RFC3161 authority and post-sign
verification are still required.

## Repository settings

Configure these settings manually; they cannot be enforced by repository files:

- Protect tags matching `vMAJOR.MINOR.PATCH`. The active tag ruleset must target
  exactly `refs/tags/v*.*.*` with no exclusions, restrict creation and deletion,
  and disallow force updates. GitHub's `Restrict creations` rule necessarily uses
  a bypass actor to identify who may create a tag: configure exactly one reviewed
  release-maintainer team, specific user or GitHub App, never an administrator,
  repository-wide role, deploy key, enterprise-wide role or invented identity. The
  protected workflow also requires the tag commit to be contained in `main` and
  the tag version to match `Directory.Build.props` exactly.
- Create the GitHub Environment `win7pos-protected-release`. Permit only protected
  release tags, require at least one release reviewer other than the initiator,
  prevent administrator bypass, and grant environment access only to the signing
  job.
- Keep the signing job at `contents: read`; grant `id-token: write`,
  `attestations: write` and `artifact-metadata: write` only to the separate
  attestation job that never receives signing secrets. Do not grant package,
  issue, pull-request or repository write scopes.
- Store `WIN7POS_SIGNING_PFX_B64` and `WIN7POS_SIGNING_PFX_PASSWORD` only as
  protected-environment secrets. Store the public expected thumbprint as
  `WIN7POS_SIGNING_CERT_THUMBPRINT` and the approved RFC3161 URL as
  `WIN7POS_RFC3161_TIMESTAMP_URL` environment variables. Store the one reviewed
  tag-ruleset bypass actor's positive numeric ID and exact GitHub actor type as
  `WIN7POS_RELEASE_TAG_BYPASS_ACTOR_ID` and
  `WIN7POS_RELEASE_TAG_BYPASS_ACTOR_TYPE`. These are public policy values, not
  signing secrets; a missing, broad or mismatched actor blocks production signing.
- Never echo the secret identifiers, values, imported certificate bytes, password
  or private-key material. Import the PFX into an ephemeral `CurrentUser/My`
  certificate store, pass only its public thumbprint to the signing script, and
  remove every certificate newly imported by that job (including owned private
  keys) in an unconditional cleanup step. The PFX is first inspected with
  ephemeral key storage and is rejected if any contained certificate collides
  with a pre-existing store entry. Pre-existing store entries are never cleanup
  targets; independent ownership and import-outcome markers make missing cleanup
  evidence fail closed.

PR and branch workflows must remain unsigned and must not reference the protected
environment or signing secrets.

The PR, branch and ordinary Release Pack workflows still generate a reproducible
unsigned payload manifest, CycloneDX SBOM, SHA-256 manifest and SLSA/in-toto
statement. Their stage is `development-unsigned`, their `releaseTag` is empty,
and their SHA-derived development version must match the exact checked-out
commit. Ordinary Release Pack is branch-only and explicitly rejects a
`workflow_dispatch` tag ref; only `protected-release.yml` may produce artifacts
for a release tag. Development evidence is not a release signature and cannot be
promoted to the protected-tag stage.

## Pinned signing toolchain

`scripts/win7pos/windows/release-signing-toolchain.json` pins the official
`Microsoft.Windows.SDK.BuildTools` NuGet package, both package digests, package
size, the x64 `signtool.exe` path, executable digest, product version and Microsoft
Authenticode signer. Install it into a run-scoped directory with:

```powershell
pwsh -NoProfile -File scripts/win7pos/windows/install-pinned-signtool.ps1 `
  -ToolDirectory "$env:RUNNER_TEMP/win7pos-signtool" `
  -GitHubEnvironmentFile $env:GITHUB_ENV
```

The installer emits only public tool metadata and `WIN7POS_SIGNTOOL_EXE`; any
package, executable, version or Microsoft-signature mismatch fails closed.

## Protected release

Use this exact order so the installer embeds already-signed application binaries:

1. Build and reproduce the unsigned `Release/x86` payload at the exact protected
   tag commit. Generate the CycloneDX/SPDX SBOM and immutable unsigned payload
   manifest.
2. Generate unsigned checksums and in-toto/SLSA provenance with
   `write-release-integrity-metadata.ps1 -StageName unsigned -FilePrefix
   unsigned-release`. Validate them before any signing mutation.
3. Import the protected certificate without logging it. Run
   `invoke-protected-release-signing.ps1 -Mode Application`; this signs only
   project-owned `Win7POS.*.exe`/`Win7POS.*.dll` files using `/fd SHA256`, `/tr`,
   `/td SHA256`, verifies each signer/signature/timestamp, and writes
   `signing/application-signing-record.json`.
4. Compile a fresh Inno Setup installer from that signed application payload.
5. Run `invoke-protected-release-signing.ps1 -Mode Installer` with the application
   signing record. The script first proves all recorded application binaries are
   unchanged and still valid, then signs and verifies the exact versioned
   installer and writes `signing/installer-signing-record.json`.
6. Create the final ZIP from the signed payload. Regenerate final `release-*`
   checksums, provenance and attestation over the signed payload, ZIP, installer,
   SBOM, original unsigned manifest and both signing records. Never validate a
   signed file against its pre-sign hash.
7. Run `test-protected-release-artifacts.ps1` with both signing records,
   `-ExpectedStage signed`, the public expected signer thumbprint, and
   `-RequireProductionSignatures`. After GitHub creates the two OIDC attestation
   bundles, rerun the validator with only those exact files as
   `-PostMetadataArtifactPath`. Publish only after the checksummed tree plus those
   two post-metadata files form a closed-world match and platform verification
   passes.

Recommended artifact layout:

```text
dist/
  Win7POS/                              signed application payload
  Win7POS-MAJOR.MINOR.PATCH-Setup.exe   signed installer
  Win7POS-MAJOR.MINOR.PATCH-x86.zip     ZIP of signed payload
  unsigned/
    unsigned-payload-manifest.json
    sbom.cdx.json
    unsigned-release-checksums.json
    unsigned-release-provenance.json
    unsigned-release-attestation.intoto.jsonl
  signing/
    application-signing-record.json
    installer-signing-record.json
  release-checksums.json
  release-provenance.json
  release-attestation.intoto.jsonl
  attestations/
    github-provenance.sigstore.json
    github-sbom.sigstore.json
```

`release-provenance.json` and `release-attestation.intoto.jsonl` are identical
in-toto Statement v1 content (pretty JSON and JSONL encodings) with an SLSA v1
predicate. They bind the exact commit, semantic version, protected tag, workflow
run/attempt, unsigned manifest, SBOM and current artifact digests. GitHub's OIDC
artifact attestation remains a separate platform-signed verification layer.

## Failure and fixture policy

Missing certificate, timestamp failure, bad signature, changed payload, version
mismatch, missing SBOM, missing provenance or missing attestation all fail closed.
`test-release-signing-negative.ps1` creates a short-lived certificate named
`Win7POS NON-PRODUCTION Signing Fixture` and exercises both application and
installer signing over disposable files. The certificate deliberately remains
self-signed and untrusted, has no timestamp, and records both
`SELF-SIGNED-UNTRUSTED-NON-PRODUCTION-FIXTURE` and
`NONE-NON-PRODUCTION-FIXTURE`. The fixture validator requires that explicit
state plus an intact Authenticode signer; production validation rejects it and
continues to require a Windows-trusted signer and verified RFC3161 timestamp.
The test removes the exact generated certificate. Its output is wiring evidence only and must never be
relabelled as a production signature or Windows 7 certification.
