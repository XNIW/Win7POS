# Win7POS product-image readiness

Status: `DESIGN_READY / IMPLEMENTATION_NOT_STARTED`

Scope: follow-up feature after PR #7 and the later PR-B reconciliation. This
document defines a lightweight Windows 7-safe contract only. PR #7 must not add
image fields, database migrations, downloads, or product-image UI.

## Cross-client contract prerequisite

Admin, Android, iOS, and Win7POS must agree on one versioned product-media
contract before implementation starts. The minimum read model is:

- `image_url`: absolute HTTPS URL for a display image;
- `thumbnail_url`: optional, preferred URL for a small list/POS thumbnail;
- `image_version`: opaque value that changes whenever the remote image changes;
- `image_updated_at`: optional UTC diagnostic timestamp;
- explicit deletion: both URLs empty/null with a changed `image_version`.

URLs are transport references, not product identity. Clients must key cached
entries by stable product identity plus `image_version`, never by the display
name. The server should provide JPEG or PNG and a small thumbnail suitable for
POS display. WebP may be offered to newer clients, but Win7POS does not require
it and must receive a JPEG/PNG fallback.

Before runtime work, the four clients must publish fixtures for: no image, valid
thumbnail, full-image fallback, changed version, removed image, unsupported
content type, oversized response, offline cached image, and broken URL.

## Windows 7 storage and limits

- Do not store image BLOBs in SQLite.
- Store cache files below the active POS data root (normally ProgramData/POSData),
  never under Program Files and never beside the executable.
- Use a dedicated, non-executable cache directory with opaque hashed filenames;
  do not derive a path from a URL or product name.
- Suggested initial cache budget: 128 MiB total, configurable downward, with a
  hard upper bound of 256 MiB.
- Suggested per-file maximum: 1 MiB for thumbnails and 4 MiB for a full display
  image; reject larger responses before decode.
- Suggested decoded target: at most 512 x 512 pixels for a detail view and at
  most 160 x 160 pixels for POS/list thumbnails.
- Maintain a small metadata index containing product key, version, file length,
  media type, dimensions, last access, and verification outcome. It may be a
  separate bounded table or atomic sidecar metadata, but never the image bytes.
- Apply least-recently-used cleanup after a successful write and at a low-impact
  maintenance point. Never clean the cache in the scan or payment critical path.
- Use atomic temporary-file-to-final-file replacement. A failed or interrupted
  download must not replace the last verified cached image.

## Download and decode pipeline

1. Render a lightweight placeholder immediately.
2. Look for the exact product/version in the local cache.
3. Decode and downsample off the UI thread to the requested display size.
4. Freeze the WPF bitmap before returning it to the UI thread.
5. If the exact version is absent and the workstation is online, enqueue a
   bounded, cancellable download outside the POS scanner/payment path.
6. Validate HTTPS scheme, response status, declared content type, magic bytes,
   byte limit, and decoded dimensions before promoting the cache entry.
7. Cancel both download and decode when product selection or virtualized item
   ownership changes. A late completion must be version-fenced and must not set
   an image on a recycled row.
8. Show a stable broken-image state after validation or decode failure; do not
   retry continuously. Retry only after backoff, a version change, or an explicit
   user refresh.

Use WPF/Win7-native JPEG and PNG decoding. Decode with cache-on-load semantics so
file handles close promptly, and downsample during decode rather than allocating
the full source bitmap first.

## Offline and invalidation behavior

- Offline mode may show only the last verified cache entry for the requested
  version. If it is unavailable, retain the placeholder; image absence never
  blocks product lookup or a sale.
- A changed `image_version` invalidates the prior entry logically. The old file
  may remain until LRU cleanup but must not be shown as the new version.
- Remote deletion immediately returns the placeholder and makes prior versions
  ineligible for display; physical cleanup remains deferred and bounded.
- Do not prefetch the full catalog. An optional future prefetcher may load a
  small, bounded set of visible or recently used thumbnails only when POS load,
  network policy, and cache budget permit.
- Product lists must retain WPF virtualization. Image work must follow the
  visible row lifecycle and must not keep ViewModels, controls, or bitmaps alive.

## Critical-path and privacy rules

- No image network call, disk cleanup, or full-size decode in barcode scanning,
  cart mutation, payment, sale commit, receipt rendering, sync idempotency, or
  drawer/printing paths.
- Product images are untrusted remote content. Permit only the agreed HTTPS
  origins or a documented redirect policy; reject executable/polyglot content
  and media-type mismatches.
- Do not log signed URLs, query tokens, response bodies, or local user paths.
  Diagnostics may record product ID, opaque version, byte count, media type,
  cache outcome, and a sanitized failure code.
- Cache/export/backup policies must explicitly state whether product images are
  excluded. Default: cache is disposable and excluded from database backup.

## Acceptance gates for the future feature

- Contract fixtures pass identically in Admin, Android, iOS, and Win7POS.
- Win7POS net48/x86 builds and runs with no new package required solely for image
  decoding.
- 20 rapid selection changes show only the final product image and leave no
  handler, task, stream, file handle, window, ViewModel, or bitmap residue.
- A long virtualized list does not download all product images and does not lose
  recycling.
- Offline startup, scanner lookup, payment, receipt printing, and sync behavior
  remain unchanged with an empty, full, corrupt, or read-only cache.
- Cache limits, LRU cleanup, version change, remote deletion, cancellation,
  placeholder, broken image, invalid type, oversized file, and privacy logging
  all have deterministic automated coverage.
- Physical Windows 7 validation covers JPEG/PNG decode, DPI scaling, low-memory
  behavior, offline cache, and slow/unavailable network.

Implementation remains blocked until the cross-client contract and fixtures are
approved. It must be delivered as an independent feature after PR #7; it must
not be folded into receipt/recovery work or PR-B migrations.
