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

- `master` on this fork is `846f177`
- `valera/master` is ahead of this fork's `master`
- `chavic/persist-receiver-sessions` contains the durable receiver-session persistence work
- `chavic/checkout-model-seam` stacks the checkout ownership work on top of persistence and is the current combined validation branch
- `chavic/payjoin-artifact-freshness` is local-dev tooling only and is not part of the current shipping path

Current review branches and PRs:

- fork draft PR `#1`: `chavic/persist-receiver-sessions` -> `master`
- fork draft PR `#3`: `chavic/checkout-model-seam` -> `chavic/persist-receiver-sessions`
- upstream draft PR `ValeraFinebits/btcpayserver-payjoin-plugin#6`: `chavic/persist-receiver-sessions` -> `master`

Current local validation on `chavic/checkout-model-seam`:

- command:
  - `dotnet test BTCPayServer.Plugins.Payjoin.sln -c Debug`
- result:
  - `BTCPayServer.Plugins.Payjoin.Tests`: 18 passed
  - `BTCPayServer.Plugins.Payjoin.IntegrationTests`: 22 skipped

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

The remaining blocker is not architecture anymore. It is live validation of the combined path on `chavic/checkout-model-seam`.

Receiver-session orchestration in `PayjoinReceiverPoller` is still too fragile beyond single test purchases because session handling remains sequential. This should be treated as a blocker in the active work.

### 1. Prepare The Local Test Environment

- make sure BTCPay is loading this plugin build
- make sure the local regtest or Docker-backed harness is working
- make sure only the Payjoin plugin is staged in the local plugin load path if other local plugins could interfere
- if bindings are missing, generate them:
  - `bash rust-payjoin/payjoin-ffi/csharp/scripts/generate_bindings.sh`

### 2. Run The Combined Live Validation

Run the live test on `chavic/checkout-model-seam` and verify the full path:

- a Payjoin-enabled BTC checkout emits a Payjoin-capable payment URL
- the plugin creates or reuses the receiver session correctly
- an active negotiation can be started
- BTCPay can be restarted during the active negotiation
- the receiver session reloads and replays deterministically enough after restart
- a successful Payjoin removes the active session
- a non-Payjoin terminal invoice path also cleans up a waiting session

### 3. If The Live Validation Fails

- fix the bug on `chavic/checkout-model-seam`
- rerun:
  - `dotnet test BTCPayServer.Plugins.Payjoin.sln -c Debug`
- rerun the live validation
- keep the fixes on the stacked branch until the combined path is clean

### 4. If The Live Validation Passes

- record the result in a durable place that other reviewers can see
- update upstream PR `ValeraFinebits/btcpayserver-payjoin-plugin#6` with the validation result
- decide whether the checkout seam should be proposed upstream as its own follow-up stacked PR
- only after that treat the current foundation work as closed

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
