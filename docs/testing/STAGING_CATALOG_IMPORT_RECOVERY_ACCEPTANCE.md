# Live catalog import and recovery acceptance

This is a new opt-in continuation of the canonical article runner. Prepare the
current qualified Release Pack and publish the ordinary 22-field article READY
on Admin main first. Keep the QA shop, manifest, contributor controls and staging
deployment qualified for the complete run. The scope remains
`qa-articles-zero-sales`; there are no sales, payment or hardware actions.

```powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosCatalogImportRecoveryAcceptance.ps1 `
  -Profile asus-staging -ReadinessRunId $freshReadyRunId `
  -ReleasePackRunId $qualifiedReleaseRunId
```

The wrapper selects the new `-CatalogImportRecovery` switch on
`Invoke-Win7PosStagingAcceptance.ps1`. All existing exact-main/clean checkout,
profile, published READY, package verification, DLL binding, build/test/gate,
redaction and host lease checks apply. The article suite runs once. The U2 phases
`prepare`, `resume`, `continue` use distinct net48/x86 processes under that same
lease and total timeout budget. Before each phase the runner rechecks identical,
still-valid READY and bound package DLLs. The ordinary article result and live U2
result remain separate files.

The executable U2 cases are:

- Ordinary 5,000-row import with dense Unicode in both product names, forcing
  partitioning by bytes as well as rows. Production preview, builder, SQLite
  applier, serializer, credentials, HTTP client and Admin responses are used.
- An explicitly **seeded local legacy uncertain** 5,000-row original. Its seeded
  dispatch metadata is QA input, not proof of a historical/live original send.
  The large original is uploaded through the real bounded multipart protocol,
  authoritatively queried and retired, then recovered through its persisted plan.
- Draft persistence followed by process restart and local-only preparation,
  explicit discard, invalid preview and commit revalidation, corrected draft,
  and verified backup before the application transaction.
- One actual accepted response for the exact owned second recovery part is lost
  locally after parsing the real server reply. The first part remains ACKed;
  the parent remains partial. Another process verifies progress and immutable
  SQLite identities/payload hashes and respects real retry deadlines before
  completing every remaining ACK.
- A separate one-row ordinary original is retired through Admin, then its exact
  old identity is submitted live. An explicit retirement rejection is required;
  an unrelated HTTP/authentication error cannot pass this check. Its replacement
  is subsequently reconciled so this case does not leave an artificial barrier.
- A separate compact 1,001-row seeded uncertain original creates a two-part
  predecessor. Its first 1,000 rows receive a real live ACK. Explicit committed
  plan retirement preserves those rows as `accepted_plan_part` and fences the
  remaining one-row identity; its late `/apply` must receive an explicit fence
  rejection. A new independent ordinary import on that owned row, using the same
  authenticated shop/device, changes retail/purchase from 1,200/900 to 1,300/1,000.
  Its real ACK and authoritative lookup become `accepted_contributor`, a distinct
  coverage kind. Quantity stays 1.25 in the importer, frozen intent and cloud;
  no quantity edit, stock-set assumption or additional stock delta is introduced.
  The successor carries exactly 1,000 + 1 rows, has zero children, and closes only
  after actual complete Admin registration. The persisted preparation is observed
  read-only before normal closure clears it; its exact captured bytes/IDs are
  idempotently registered again and its coverage digest is preserved. A real
  replay of the accepted predecessor part must return the same immutable receipt
  IDs. Stock, prices and SQLite/history fingerprints remain unchanged across
  both replays. No second device or fabricated server acceptance is used.
- Terminal-cursor cloud readback compares every owned product ID, barcode,
  Unicode name, stock and price with SQLite, and every mapped price ID/type/value
  with cloud history. SQLite integrity, unique maps, unchanged economic/history
  fingerprints across ACK/retry, membership and final queues are checked.

The internal process-local observer keeps the existing real transport. It
records complete serialized request UTF-8 byte counts and SHA256, route,
operation/part identity, attempt metadata and actual HTTP response observations.
It stores no request body, response body or credentials. Every observed request
must fit 524,288 bytes and every item-bearing request at most 1,000 rows. HTTP
digests describe the current transport envelope; immutable payload identities
are separately checked in SQLite. The lost-part replay must keep its exact route,
schema, plan and part digest. Raw body equality is additionally required while
the credential binding digest is unchanged; a valid credential rotation is
explicitly reported and does not change that immutable operation identity.
Existing foreign import obligations cause a
failure before seeding or draining. No pending row, retry deadline or lease is
cleared to make the run pass.

New plans also reserve the current Admin RPC's separate 524,288-byte JSONB item
storage budget. This conservative bound includes the 13 normalized ordinary
keys, explicit absent/null fields, object/array entries and alignment, rounded
numeric storage, and product-name fallback duplication. Correction nested maps
and masks have their own bound. Non-ASCII display values reserve at most three
UTF-8 bytes per accepted UTF-16 code unit at the field's Admin limit, covering NFC
expansion across OS/runtime Unicode versions; short non-ASCII names can therefore
produce more parts than an HTTP-only planner. No payload text is normalized or
changed by sizing. Facts report `SqlNormalizedItemsUpperBoundBytes`, not a measured
database byte count. The live receipt requires this bound for every dispatched
ordinary/recovery/contributor operation; seeded legacy originals remain explicit
inputs. Already persisted plans retain their exact identities and bytes.

The receipt `catalog-import-recovery-live-result.json` explicitly lists these
remaining qualifications as not covered by this minimum extension:

- Linked `/import-correction` of a deferred edit on an already accepted row:
  the case above corrects both prices through a new ordinary contributor and
  proves succession; it does not edit the carried accepted predecessor rows.
- General concurrency between independent devices and mobile sessions.
- Product dialog focus, keyboard/scanner, Enter/Esc and four-language visuals:
  these remain the existing WPF U2 suite and physical observation, separate from
  this service-level live path.
- A process crash during SQLite commit: the owned isolated crash regression is
  a different case from the lost live HTTP response above.
- A complete uniform 59,999-data-row run and workload-specific resource peaks.
- Mobile propagation/latency and physical Windows 7/peripherals/installer.

The harness creates 11,002 synthetic QA products and verifies their 22,004
effective server price IDs. The contributor inserts exactly two additional local
history entries once. The two old local price-history IDs for its seeded legacy
row (1,200/900) were never sent or accepted remotely and stay unmapped. They must
be the only unmapped IDs in the complete owned dataset and keep their exact
original identity, values and null old prices. They are explicitly preserved in
the report/manifest, including explicit unchanged before/after contributor-ACK
identity snapshots; every other local history ID is compared with the cloud.
`U2-CLEANUP-MANIFEST.json`
contains their exact server product/price IDs and operation identities, shop,
device, run ID, prefix, relevant recovery plan IDs and those two local-only
unaccepted history IDs. No cloud cleanup is performed automatically. Preserve
this manifest and audit; use the authorized Admin cleanup procedure with these
exact identities. A service-level U2 PASS never substitutes for the qualifications
above, physical Win7, production approval or cleanup completion.
