# TODO

This file is the ordered execution plan for this repository.

It is meant for contributors working from the plugin repo alone. Everything needed to understand the current direction is summarized here directly.

## Alpha Release Checklist (High-Level)

- [ ] session persistence
- [ ] history integration
- [ ] payjoin-cli sender/receiver e2e test integrated
- [ ] a demo video
- [ ] mainnet successes

## Project Goal

Build a BTCPay Payjoin plugin that:

- integrates into the normal BTC invoice checkout path
- can be enabled per store
- emits Payjoin-capable payment URLs with safe fallback to plain BIP21
- survives BTCPay restart during an active receiver negotiation
- is structurally sound enough to keep building on

The current plan is intentionally phased:

- close the current foundation work
- harden, automate, and reduce dependency risk
- extend toward more treasury-oriented behavior

## Current Baseline

The active foundation work is split into two review branches:

- `chavic/persist-receiver-sessions`: durable receiver-session persistence and replay behavior
- `chavic/checkout-model-seam`: checkout URL ownership work stacked after persistence

The persistence branch should be reviewed first. The checkout branch depends on it and should be validated after persistence behavior is stable.

## What Is Already Done

These items are complete enough that they should not be reopened without a concrete bug or architecture decision:

- Valera's plugin was adopted as the working base instead of starting greenfield
- durable DB-backed receiver-session persistence was implemented
- receiver sessions now persist event history and close-request state
- contributed receiver input identity is persisted so replay can resume after restart
- the database is now authoritative for receiver-session state
- BTC checkout ownership for Payjoin URL fields was moved into BTCPay's payment-method checkout model seam
- the old `checkout-end` mutation path is no longer the source of truth
- unit coverage was added for persistence behavior and checkout URL merge behavior

## Ordered Remaining Work

### 1. Finish Receiver Session Persistence Review

Receiver-session persistence should remain database-authoritative. The store should avoid memory-first mutation patterns because a failed write can leave detached in-process state inconsistent with durable state.

- keep `PayjoinReceiverSessionState` scoped as an immutable snapshot/DTO
- keep event sequencing protected by durable database constraints
- avoid process-local locking as the primary consistency mechanism
- keep terminal session cleanup explicit and persisted
- keep focused unit coverage for replay, event append sequencing, and cleanup behavior

OHTTP long-poll timeout diagnostics are tracked separately in <https://github.com/ValeraFinebits/btcpayserver-payjoin-plugin/issues/17>. That issue should be handled as poller diagnostics and timeout behavior, not as part of the core persistence branch unless it blocks replay correctness.

### 2. Validate Restart And Replay

Validate the persistence branch against a live BTCPay instance before treating it as ready to merge:

- a Payjoin-enabled BTC checkout creates or reuses a receiver session correctly
- an active negotiation can be started
- BTCPay can be restarted during the active negotiation
- the receiver session reloads and replays deterministically enough after restart
- a successful Payjoin removes the active session
- a non-Payjoin terminal invoice path also cleans up a waiting session

### 3. Keep Checkout Work Separate

The checkout model seam should stay on its stacked branch until the persistence branch is accepted or otherwise stable enough to build on. It should not be used to hide persistence defects.

- verify that Payjoin-capable BTC checkout URLs are emitted from the checkout model seam
- preserve safe fallback to plain BIP21
- cover both `pj` and `pjos` URL fields where relevant
- cover API-issued invoice/payment URLs as well as the browser checkout path
- keep merchant-facing checkout toggles explicit, with Payjoin enabled by default when the store supports it

## Follow-Up Work After The Current Foundation Pass

This next phase is about making the current base easier to validate, maintain, and operate.

### 1. Fork Alignment

- inventory the plugin's required `rust-payjoin` FFI surface
  - direct close-request support
  - receiver outputs or original proposal PSBT access
  - selected receiver input identification for `proposal.TryPreservingPrivacy(receiverInputs)`
  - selected-input metadata support needed to replace BTCPay-specific persisted metadata
- compare that surface against the current local `rust-payjoin` line
- decide whether alignment should happen immediately or in stages
- execute the chosen alignment plan
- revalidate the plugin after the alignment lands

### 2. Automation

Turn the high-value manual cases into reliable automated coverage:

- stable Payjoin URL generation
- fallback to plain BIP21
- restart-safe receiver replay
- cleanup on invoice state transitions
- successful session completion and removal
- configured cold-change routing

### 3. Correctness Hardening

Tighten the remaining rough edges in the receiver path:

- seen-outpoint tracking
- better fee-range sourcing
- better receiver-owned script handling
- removal of temporary input-contribution shortcuts
- tighter tracking of truly contributed receiver inputs
- remove the receiver-output fallback to `invoice.Due` and source those values from the incoming proposal
- validate that the invoice amount matches the receiver amount in the incoming proposal before replacement outputs are built
- simplify receiver proposal signing, PSBT normalization, and signing-context ownership if the poller flow keeps growing
- clarify the proposal-normalization pipeline if receiver-input finalization and sender-input cleanup continue to diverge

### 4. Diagnostics

Improve observability so failures are easier to understand:

- replay failure reasons
- cleanup reasons
- initialization fallback reasons
- integration-test failure visibility
- merchant-facing failure reasons for payjoin setup, fallback, and cleanup paths
- payer-facing failure reasons when payjoin negotiation is rejected, abandoned, or falls back to plain BIP21
- clearer explanation of when payjoin was unavailable versus when plain BIP21 was intentionally used

### 5. Shipping Cutoff

Decide which remaining issues are must-fix before broader shipping claims and which can remain temporary debt.

- decide whether falling back to unconfirmed merchant coins is acceptable when advertising payjoin availability
- decide whether the OHTTP key cache lifetime should remain fixed or become configurable
- remove the test endpoint from `UIPayJoinController` before making broader shipping claims

## Later Treasury-Oriented Work

This later phase is where the plugin should start taking on more treasury-oriented behavior.

Planned work:

- define the minimum acceptable treasury story for this phase
- strengthen cold-output and descriptor-oriented behavior
- clarify hot-wallet versus cold-wallet role boundaries
- improve accounting and reconciliation support where justified
- retire earlier compromises that should not survive treasury-facing usage

## Guardrails

- do not confuse "cold-change routing exists" with "full treasury architecture is solved"
- if you are working without access to planning materials, record meaningful results in PR descriptions, PR comments, or checked-in repo docs so the next contributor does not have to rediscover them
