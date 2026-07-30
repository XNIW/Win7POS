# Win7POS product image Phase A ready

Status: Phase A implementation is committed and published in a draft PR; it
must not merge until final Mac cleanup is complete.

## Safety statement

This phase was offline-only with respect to product-image runtime services:

- Win7POS was not launched.
- UiSmokeHarness was not launched.
- No staging host was contacted.
- No login, device registration, catalog pull, or mutation ran.
- The `asus-staging` DPAPI profile was not used.
- No Admin, Supabase, Storage, Cloudflare, schema, or RLS state was modified.
- The private cleanup manifest was neither opened nor modified.
- Win7POS `main` was not changed or merged.
- The audit used only public Git/GitHub source reads; implementation used only
  local restore/build, synthetic unit tests, and static gates. Delivery is
  limited to this feature-branch push and its draft PR.

## Audited contracts

The full source-backed matrix is in
`docs/plans/WIN7POS_PRODUCT_IMAGE_CROSS_PLATFORM_AUDIT.md`.

- Android `XNIW/MerchandiseControlSplitView`:
  `4b2b4a93dd5d4db7d1cfb83e897aa5cbac40366e`
- iOS `XNIW/iOSMerchandiseControl`:
  `c1b7b706c5f05cd7e8dda74cea1122f6483df7ec`
- Admin `XNIW/merchandise-control-admin-web`:
  `e1783f57509c8011902c1f076d3b1f5ee2e56309`

All three confirm the same v1 private-storage model: zero/one primary image,
immutable `versionId`, strict JPEG main/thumb variants, canonical
shop/product/version paths, product sync limited to version/timestamp, signed
URLs held only in memory, and bounded caches.

## Implemented Core/Data foundation

- pure immutable Core identities, references, variant/display/validation
  states, canonical SHA-256 cache key, exact object-path policy, and local
  stream-provider interface;
- strict JPEG/PNG byte inspection with bounded size/pixel checks, safe EXIF
  orientation read, strict JPEG terminal/metadata validation, and metadata
  removal;
- local cache under `%LOCALAPPDATA%\Win7POS\ImageCache`;
- configurable 32 MiB / 256-entry conservative defaults, two concurrent
  producers, a 3 MiB / 2-entry replacement-safe configuration floor, hard
  configuration ceilings, deterministic LRU, total on-disk
  accounting (payloads, metadata, index, and temporary files), per-version
  invalidation, same-key request coalescing, per-consumer cancellation, atomic
  temp/rename commits, stale temp cleanup, corrupt-index rebuild, and restart
  recovery;
- persisted staged/promoted state, request-order stage sequences that do not
  infer ordering from timestamps or UUIDs, and independent main/thumb
  promotion;
- generic eviction prefers expendable staged entries before promoted
  fallbacks; while admitting a replacement it protects the candidate and its
  same-variant fallback, evicting unrelated LRU entries while staying within
  the configured hard limit;
- reparse/junction rejection before cache-root creation, no-follow Win7 root
  lock opening, fail-closed bounded directory scans, traversal-safe deletion,
  and an exclusive root lock that is released only after initialization and
  producers drain;
- same-key flights remain registered through completion after the last
  consumer cancels, preventing overlapping retry producers;
- explicit per-variant promotion/invalidation occurs only after that variant
  decodes successfully, preventing a structurally valid but WIC-invalid JPEG
  from poisoning either fallback variant;
- startup reconciles an interrupted promotion by retaining only the promoted
  entry with the highest persisted stage sequence for each product/variant;
- no URL, token, HTTP client, database blob, schema migration, or product
  persistence change.

The image cache is intentionally outside the application install folder and
outside the production-like POS data/backup directory.

## Implemented WPF/local preprocessing foundation

- WPF `BitmapImage` decode uses bounded `DecodePixelWidth`/`DecodePixelHeight`,
  `BitmapCacheOption.OnLoad`, immediate stream disposal, and `Freeze()`;
- list and editor profiles use separate 128 px / 512 px defaults;
- concurrency and weak memory entries are bounded; same-key decode work is
  coalesced and failures are returned as display states;
- local JPEG/PNG preprocessing detects actual format from bytes, reports
  original diagnostics, applies reliable orientation, composites alpha on
  white, avoids upscale, generates strict JPEG main/thumb candidates, removes
  unnecessary metadata, and never overwrites the source; its Win7/x86 default
  rejects sources above 16,000,000 pixels even though the portable contract
  ceiling is 64,000,000;
- product-list template and editor preview presenter compile with localized,
  accessible status text and no focus for noninteractive imagery;
- the Phase A image feature flag is a compile-time `false`, so no live list
  column/editor image action is enabled.

Implemented UI strings are English, Spanish, Italian, and Simplified Chinese:
no image, loading image, image unavailable, invalid image, and product image
preview. Phase B action copy is specified in the handoff prompt but is not
reserved as unused runtime resources.

## Automated validation

Focused current results:

- Core/Data image tests: 42 passed, 0 failed (contract, validation, traversal,
  canonical keys, strict bytes, coalescing, eviction, replacement, interrupted
  write, corrupt index/timestamps, fail-closed scan overflow, stray paths,
  accounting, producer bounds, concurrent read/write and promotion races,
  persisted fallback pressure/restart, no-follow root locking, lifecycle
  drain, cancellation retry coalescing, and architectural boundaries).
- WPF x86/net48 imaging tests: 19 passed, 0 failed.
- Synthetic WPF coverage includes bounded decode of a 1600×900 main image,
  one-flight repeated requests, 120 concurrent thumbnail requests constrained
  to two decoders and 24 weak memory entries, corrupt input, queued
  cancellation, explicit memory trimming, JPEG/PNG preprocessing, no file
  lock/overwrite, and bounded 2400×1800 preprocessing.
- Measured on the local development machine in Release/x86: the 120-request
  synthetic thumbnail batch completed in 985 ms with maximum observed decode
  concurrency 2 and 24 retained weak-cache entries; the 2400×1800 synthetic
  preprocess completed in 52 ms and produced a 38,958-byte main plus a
  5,280-byte thumb.

These are deterministic local synthetic measurements. They are not a claim of
physical Windows 7 validation.

Final local gates:

- complete Core/Data test project: 788 passed, 0 failed;
- WPF Release/x86/net48 imaging project: 19 passed, 0 failed;
- WPF Release/x86 build: succeeded with 0 warnings / 0 errors;
- solution Release build: succeeded with 0 warnings / 0 errors;
- dialog standards: 34/34, all pass;
- product free-text, keyset paging, architectural boundaries, and Phase A
  static gates: all pass;
- `git diff --check`: pass;
- Gitleaks 8.30.1 working-tree scan: no leaks;
- independent final current-diff review: P0 0, P1 0, P2 0; every finding from
  earlier review waves was fixed and re-reviewed.

## Delivery

- Branch:
  `codex/asus-product-image-foundation-offline-20260730`
- Base:
  `f34308b24fd30d0b85845429f1ece97cc5106c6d`
- Implementation commit:
  `b03d8aaa860cce9ddfc5b38431b5059a90cab35a`
- Draft PR:
  `https://github.com/XNIW/Win7POS/pull/72`
- Merge status: not merged; auto-merge must remain disabled.
- Required PR banner:
  `DO NOT MERGE UNTIL FINAL MAC CLEANUP IS DONE.`

## Deferred Phase B and prerequisites

Use `docs/HANDOFFS/WIN7POS_PRODUCT_IMAGE_PHASE_B_PROMPT.md` only after:

1. final Mac cleanup is confirmed complete;
2. its runtime lease/destructive worker is confirmed inactive;
3. current Admin/image contract, schema, RLS, Storage, Android, and iOS
   revisions are re-audited;
4. online/staging authorization is explicitly granted;
5. a physical Windows 7 SP1 x86 validation machine is available.

Phase B must add authenticated fetch/upload/finalize/remove, catalog image
synchronization, live list/editor integration, offline queue/retry/conflict
policy, RLS/private-storage validation, exact cleanup/orphan reconciliation,
runtime smoke, authorized public staging acceptance, and normal PR/CI/merge.

Physical Windows 7 status: pending.
