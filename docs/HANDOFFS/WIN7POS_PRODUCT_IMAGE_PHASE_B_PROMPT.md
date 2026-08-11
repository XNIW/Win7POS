# Ready-to-run prompt: Win7POS product images Phase B

Use this prompt only after the final Mac cleanup is complete and its runtime
lease is no longer active.

---

TASK: WIN7POS PRODUCT IMAGES — ONLINE PHASE B

Prerequisites:

1. Confirm the final Mac cleanup is complete.
2. Confirm no cleanup runtime lease or destructive cleanup job is active.
3. Re-fetch Win7POS and re-audit the current Android, iOS, Admin, schema, RLS,
   Storage, and `product-image-v1` contract revisions. Do not assume the Phase
   A revisions are still current.
4. Start from the merged/approved Phase A foundation or explicitly reconcile
   its draft PR; do not silently copy it onto a divergent branch.
5. Obtain normal authorization before any staging authentication or mutation.
   Never place credentials, DPAPI material, signed URLs, or tokens in source,
   logs, prompts, fixtures, or cache metadata.

Scope:

- implement the current Admin intent/finalize/read-URLs/remove contract;
- keep signed URLs in memory and use versioned cache identities;
- integrate catalog primary-image synchronization and tombstones;
- enable bounded list thumbnails with progressive local cache behavior;
- enable editor choose/replace/remove with local preview;
- define offline queue/retry and compare-and-swap conflict handling;
- validate owner/shop authorization, RLS, private bucket behavior, and exact
  cleanup/orphan policy;
- keep product save and article-sync semantics unchanged except where a
  separately reviewed current contract explicitly requires integration;
- run WPF runtime smoke, physical Windows 7 SP1 x86 validation, authorized
  public staging acceptance, normal CI, and review;
- open a normal PR and merge only after all required approvals.

Required safety checks:

- verify the target environment and account before every online operation;
- use synthetic/local tests before staging;
- never persist signed/upload URLs;
- never decode unbounded full-resolution images for product lists;
- never delete a storage object from an unvalidated or client-provided path;
- make replacement transactional so a failed upload/finalize cannot remove the
  prior valid image;
- record and reconcile pending cleanup without hiding failures.

Read first:

- `docs/plans/WIN7POS_PRODUCT_IMAGE_CROSS_PLATFORM_AUDIT.md`
- `docs/HANDOFFS/WIN7POS_PRODUCT_IMAGE_PHASE_A_READY.md`
- `docs/DIALOG_STANDARD.md` before changing any dialog

Deliver a source-backed contract delta, implementation, tests, physical Win7
results, staging evidence, cleanup reconciliation, and a normal reviewed PR.

---

This document intentionally contains no host, account, credential, token,
signed URL, private manifest content, or staging command.
