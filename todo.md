# TODO

This file is the ordered execution plan for this repository.

Status: `[x]` done, `[~]` in progress or needs verification, `[ ]` open.

## Beta Checklist (High-Level)

- [x] session persistence
- [x] history integration
- [x] payjoin-cli sender/receiver e2e test integration
- [x] a demo video
- [x] mainnet successes
- [ ] review by a well-known Bitcoiner

## Project Goal

Provide per-store Async Payjoin support for receiving invoice payments and wallet-side sending, with safe fallback, durable sessions, and correct settlement accounting.

## Current Baseline

Work from `master` unless a PR says otherwise. Prioritize payment safety, then FFI adaptation and beta validation. Other follow-ups remain in GitHub issues.

## Completed Baseline

- [x] DB-backed receiver events, close requests, contributed-input identity, replay, and terminal cleanup; restarts verified manually
- [x] per-store checkout/API Payjoin URLs (`pj`/`pjos`), safe BIP21 fallback, and wallet-side sending (#113)
- [x] receiver ownership and seen-input checks, native input selection and identification, fee sourcing, validation against the pinned session amount, and contributed-input signing with native PSBT normalization
- [x] automated coverage for URL generation/fallback, receiver replay, invoice-state cleanup, session removal, and cold-change routing

## Ordered Remaining Work

### 1. Payment Safety `[~]`

- [ ] preserve sender reservations after exposed-payment cancellation and retry fallback after signing-request expiry (#260)
- [ ] fix sender fee rounding, validate originals before disclosure, and retain fallback rejection reasons (#227)
- [~] complete typed FFI POST error handling and POLL/POST recovery tests; session retention is already implemented (#198)
- [ ] adopt upstream receiver proposal safety fixes with native/C# regression tests (#257, rust-payjoin #2047)
- [ ] bound anonymous bootstrap work and isolate ownership limits per store (#138, #234)

### 2. rust-payjoin API Adaptation `[~]`

- [ ] choose and implement the FFI 0.26 adaptation approach, preserving input restrictions and settlement tracking after expiry/restart (#261)
- [ ] regenerate matching native/C# artifacts and revalidate the plugin after alignment
- [ ] replace committed-output event JSON parsing with typed FFI access (follow-up)

### 3. Validation `[~]`

- [ ] fix the restart harness and re-enable in-flight receiver and sender completion tests (#97)
- [ ] run the beta manual test plan (#152)

### 4. Diagnostics `[~]`

- [~] actionable replay, setup, fallback, cleanup, and integration-test failure diagnostics
- [ ] explain Payjoin availability, rejection, abandonment, and fallback to merchants and payers, distinguishing unavailability from an intentional Standard payment
- [ ] distinguish Payjoin v1 and v2 in transaction labels
- [ ] verify core/v1 payment lifecycle parity for invoice events, watcher updates, webhooks, and notifications (#52)

### 5. Shipping Cutoff `[~]`

- [ ] decide whether the OHTTP key cache lifetime should remain fixed or become configurable
- [~] decide whether `RunTestPayment` should remain limited to cheat mode or be removed before making broader shipping claims
- [ ] reconcile master and RC3 versions, dependencies, and migrations before beta packaging
- [ ] verify RC3-to-beta upgrades with persisted receiver/sender sessions, preserving payments and reservations
- [ ] verify the packaged beta on a clean instance and run release tests; set GitHub `prerelease=true`

## Later Treasury-Oriented Work `[ ]`

- [ ] define treasury scope: hot/cold wallet roles, descriptor support, accounting/reconciliation, and compromises to retire

## Guardrails

- keep receiver state database-authoritative and `PayjoinReceiverSessionState` an immutable snapshot; protect event sequencing with database constraints
- keep Payjoin payment URL ownership in the checkout model
- require confirmed receiver coins before advertising Payjoin availability
- do not confuse "cold-change routing exists" with "the full treasury architecture is complete"
- record meaningful results in PRs or checked-in docs for the next contributor
