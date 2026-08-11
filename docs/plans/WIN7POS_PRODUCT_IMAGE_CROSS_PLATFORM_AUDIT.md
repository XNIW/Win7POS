# Win7POS product image cross-platform audit

Status: frozen for Win7POS Phase A on 2026-07-30.

This is a source-only, read-only audit. No staging host, authenticated API,
Supabase CLI, Storage, Cloudflare, application executable, UI smoke harness,
DPAPI profile, or private cleanup manifest was contacted or opened.

## Audited current revisions

Repository names were discovered from existing Win7POS documentation and then
verified against their current public `main` branches:

| Platform | Repository | Audited `main` |
| --- | --- | --- |
| Android | `XNIW/MerchandiseControlSplitView` | `4b2b4a93dd5d4db7d1cfb83e897aa5cbac40366e` |
| iOS | `XNIW/iOSMerchandiseControl` | `c1b7b706c5f05cd7e8dda74cea1122f6483df7ec` |
| Admin | `XNIW/merchandise-control-admin-web` | `e1783f57509c8011902c1f076d3b1f5ee2e56309` |

All three revisions contain byte-compatible
`contracts/product-image-v1.json` contracts and matching valid/invalid
fixtures. The contract file, current implementations, Admin API routes, and
relevant public schema/storage migrations were inspected. Historic Win7POS
readiness notes were not treated as current authority.

## Confirmed common contract

### Identity and cardinality

- Contract ID is `merchandise-control.product-image.v1`, version `1`.
- A product has zero or one current primary image. Replacement creates a new
  immutable image version; it does not add a second ordered gallery image.
- `versionId` is the image/version identity used by clients. There is no
  separate portable `imageId` in the v1 client contract.
- Client scope is the account scope plus `shopId`, `productId`, `versionId`,
  and `variant`.
- Product-domain synchronization exposes only
  `primaryImageVersionId` and `primaryImageUpdatedAt`. It does not expose
  storage paths, signed URLs, upload URLs, raw bytes, or local paths.
- `primary` is implicit because only one ready version can be current.
  `displayOrder`, gallery ordering, and multiple-primary selection are not v1
  fields.

### Variants and wire representation

- Required variants are `main` and `thumb`.
- Both persisted variants are strict JPEG (`image/jpeg`).
- The original user file is not persisted.
- Canonical object paths are exactly:

  `shops/{shopId}/products/{productId}/primary/{versionId}/{main|thumb}.jpg`

- Paths are immutable per version. Traversal, empty components, alternate
  separators, or noncanonical substitutions are invalid.
- Canonical JPEG requires SOI and terminal EOI with no trailing data, permits
  only canonical JFIF APP0 metadata, and rejects comments and APP1–APP15.
- Alpha is composited on white, output is normalized to sRGB, and reliable
  source orientation is normalized before encoding.

### Input and output limits

| Item | Confirmed v1 value |
| --- | ---: |
| Source maximum bytes | 25 MiB |
| Source maximum pixels | 64,000,000 |
| Admin/Android accepted source formats | JPEG, PNG |
| iOS accepted source formats | JPEG, PNG, HEIC |
| Main maximum side | 1600 px |
| Main minimum scheduled side | 640 px |
| Main JPEG qualities | 82, 76, 70 |
| Main target / hard maximum | 750 KiB / 1 MiB |
| Thumb maximum side | 384 px |
| Thumb minimum scheduled side | 128 px |
| Thumb JPEG qualities | 75, 68, 60, 52 |
| Thumb target / hard maximum | 90 KiB / 90 KiB |
| Side reduction factors | 1, .85, .72, .61, .52, .44, .40 |

Dimensions preserve aspect ratio and never require an upscale. The thumb is
derived from the normalized main image, not independently from an unbounded
original.

### API and signed-URL policy

The Admin contract currently defines:

- `POST /api/shop/product-images/intent`
- `POST /api/shop/product-images/finalize`
- `POST /api/shop/product-images/read-urls`
- `POST /api/shop/product-images/remove`

The `product-images` bucket is private. Read and upload access uses short-lived
signed URLs; URLs are held in memory only and are never cache identities or
durable fields. Read URL TTL is 300 seconds with a 30-second client safety
window. A read batch is at most 16 products, read-request concurrency is 2,
and download concurrency is 4.

### Replacement, remove, and cleanup

- Intent validates current owner/shop/product access, allocates an immutable
  version, canonical paths, expected metadata, and signed upload leases.
- Finalize downloads and independently validates each uploaded JPEG before
  the version becomes ready.
- Finalize atomically supersedes the previous ready version, promotes the new
  one, and updates the product's primary image reference/timestamp.
- Remove uses expected-version compare-and-swap semantics, clears the product
  primary reference, marks the version removed, and attempts object deletion.
- Failed object deletion is recorded as pending cleanup rather than pretending
  success.
- Public cleanup functions accept only canonical, database-derived paths and
  separately prepare superseded-version and orphan cleanup work. Cleanup
  outcomes and redacted metrics are recorded.
- Clients purge replaced or removed versions by account/shop/product/version
  scope. A failed replacement must not destroy a previously valid local entry.

### Authorization and privacy

- Server routes revalidate an authenticated actor and active owner/shop
  relationship for every operation; product read/write permission is checked
  according to the action.
- Personal-account and platform-admin actors are represented by the Admin
  authorization layer.
- Image-version tables have RLS enabled and forced. Direct table grants are
  revoked from public, anonymous, and authenticated roles; server-side
  service-role access is used.
- Storage reads are allowed only for authenticated actors passing the
  shop-access predicate. Bucket policy does not make objects public.
- Logs, sync payloads, disk indexes, and cache keys must not persist signed
  URLs, upload URLs, access tokens, or raw credentials.

### State, placeholder, and errors

The common state model covers no image, loading, loaded, unavailable/offline,
invalid/corrupt, and generic error. Client implementations expose an explicit
retry where online reads are enabled. Placeholder copy is state-specific and
must not be presented to accessibility tools as a real product image.

Network, validation, authorization, conflict, corrupt-content, and unavailable
states remain distinguishable. Corrupt bytes are rejected before they are
committed to a valid cache entry.

## Platform implementations and differences

### Android

- Product and remote-row models contain only
  `primaryImageVersionId`/`primaryImageUpdatedAt`.
- Source format is detected from bytes, not filename. JPEG and PNG are
  accepted; HEIC is not part of the Android contract.
- The processor uses bounded platform decode/downsampling, orientation
  normalization, white compositing, sRGB output, and strict metadata removal.
- Cache root is under the app no-backup area. Confirmed budgets are 8 MiB
  memory and 64 MiB disk, with bounded entries/files and atomic receipt-based
  commits.
- Cache paths are scope-derived and hardened against links/traversal. The
  index can be rebuilt, LRU eviction is deterministic, and account/shop/
  product/version purges are explicit.
- List display is progressive thumb then main, visible work can be cancelled,
  and editor actions include local choose/camera/replace/remove.

### iOS

- The same product reference fields and account/shop scope gate are used.
- JPEG, PNG, and HEIC source import are supported; animated sources are
  rejected. The persisted wire variants remain JPEG.
- ImageIO performs downsample/orientation work before normalized output.
- The implementation uses bounded memory and disk caches under the system
  caches directory, excluded from backup. The audited implementation uses a
  128 MiB / 4096-entry disk bound; the shared contract also specifies a
  48 MiB / 100-entry memory bound.
- Cache files are atomically committed and hardened against link/path attacks.
  Signed URL leases stay in memory.
- List loading is progressive and visibility-aware. Editor flows include
  camera/library/replace/remove with accessible labels and retry states.

### Admin Console and Admin/Supabase

- The editor provides a local object-URL preview before upload; object URLs
  are released. Offline state may show cached content, but upload/remove is
  disabled while offline.
- Product list/editor components implement explicit placeholder, progress,
  unavailable/error, replacement, and remove-confirmation states.
- Browser cache budget is 32 MiB / 256 entries with separate lease bounds.
- Database table `inventory_product_image_versions` records immutable version
  status (`pending`, `ready`, `superseded`, `removed`, `failed`), previous
  version, canonical main/thumb paths, expected and verified hashes/bytes/
  dimensions/MIME, timestamps, and cleanup status.
- `inventory_products` holds the nullable current version reference and image
  update timestamp. A partial unique constraint permits only one ready image
  version per product.
- Storage bucket `product-images` is private, accepts only `image/jpeg`, and
  has a 1 MiB object limit. Server-side image transformations are not relied
  on.

## Unresolved or intentionally unverified

- Physical Windows 7 SP1/x86 WIC behavior has not been validated in Phase A.
- HEIC decoding is not a shared requirement and is not proven safe on stock
  Windows 7; Win7POS Phase A therefore accepts only JPEG/PNG sources.
- No staging runtime, deployed API response, signed URL, RLS session, storage
  object, or cleanup worker execution was inspected. Source code is the only
  authority used here.
- The private final-cleanup manifest and its current lease were deliberately
  not inspected.
- Exact online retry timing, telemetry, and operator-facing conflict recovery
  for Win7POS require a Phase B decision after cleanup.
- A future gallery, display ordering, alternate crops, WebP/AVIF, or public
  URL policy is not part of v1 and must not be inferred.

## Frozen Win7POS Phase A contract

Phase A implements the smallest compatible offline model:

- immutable `ProductImageIdentity`:
  account scope, shop ID, product ID, version ID;
- `ProductImageVariant`: `Main` or `Thumb`;
- strict JPEG `ProductImageMetadata`: MIME, bytes, width, height, SHA-256;
- `ProductImageReference`: identity, variant, metadata, optional image update
  timestamp;
- deterministic cache key from account/shop/product/version/variant only;
- exact canonical object-path validator, used as contract validation only;
- explicit display and validation states;
- local `IProductImageStreamProvider` returning bytes, with no HTTP type;
- byte-magic source detection for JPEG/PNG;
- bounded local WPF decode and local preprocessing;
- a Win7/x86 preprocessing default of 16,000,000 source pixels, stricter
  than the portable 64,000,000-pixel contract ceiling because legacy WIC
  codecs are not proven to avoid a full native source allocation;
- cache under `%LOCALAPPDATA%\Win7POS\ImageCache`, outside install and POS data
  directories, with a conservative configurable default of 32 MiB / 256
  entries and a replacement-safe floor of 3 MiB / 2 entries.

New versions are persisted as staged entries without deleting a promoted
previous version. A local request-order sequence is persisted with each entry;
version order is never inferred from optional timestamps or random UUIDs.
Eviction spends staged entries before promoted fallbacks, including after
restart. During replacement admission, the candidate and its same-variant
fallback are protected while unrelated LRU entries remain evictable, so the
configured hard limit cannot freeze replacement. Startup reconciles an
interrupted promotion by retaining the highest persisted sequence for the
variant. Only a successful decoder receipt may be followed by explicit
per-variant promotion/invalidation. Main and thumb are promoted independently,
preserving the prior valid variant if a JPEG passes structural checks but the
Win7 decoder rejects it.

No product DB column, schema migration, product save behavior, article-sync
outbox, catalog exactness rule, or online URL/API/storage behavior is added.
The WPF surface is compiled but disabled by a constant-false Phase A flag.

## Deferred online Phase B

After final Mac cleanup completes, Phase B must re-audit the then-current Admin
contract and implement:

- authenticated intent/finalize/read/remove calls;
- signed-URL memory leases and private Storage transfers;
- product primary-image synchronization and exact tombstone behavior;
- list thumbnail loading and editor choose/replace/remove activation;
- offline queue, retry, compare-and-swap conflict, and error UX policy;
- RLS and owner/shop authorization validation;
- exact superseded/orphan cleanup agreement;
- WPF runtime smoke, physical Windows 7 validation, and public staging
  acceptance;
- normal PR, CI, review, and merge.

Phase B must not reuse stale signed URLs or assume the source revisions above
are still current.
