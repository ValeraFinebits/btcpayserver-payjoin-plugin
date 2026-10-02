# Smoke Test Plan — Async Payjoin Plugin on a Remote Testnet3 Server

**Version:** v1

This is the short live smoke test. It covers the two
product flows that matter here -- Async Payjoin (BIP77) and its Payjoin v1
(BIP78) fallback-using.

## Run contract

Fill these values before starting:

|Value|Meaning|
|-|-|
|`RUN\_ID`|Unique label; include it in both invoice order IDs|
|`BASE\_URL`|URL of the QA-controlled BTCPay/testnet3 server|
|`RECEIVER\_STORE`|QA-owned testnet3 store receiving both invoices|
|`SENDER\_STORE`|Separate QA-owned testnet3 store/wallet sending both payments|
|`AMOUNT`|Testnet3 amount selected for this run|
|`SETTLEMENT\_POLICY`|`blocking` or `non-blocking`|

### Preconditions and safety

* The QA engineer has admin access to the test server, both stores, and their
Bitcoin wallets.
* The complete environment uses testnet3. The sender wallet contains
QA-controlled test coins sufficient to execute the test.
* The receiver has at least one spendable testnet3 input suitable for Payjoin.
Generate/fund the required wallets before the run if necessary.
* The receiver overview reports `Basic prerequisites present`,
`Receiver inputs: Present`, `Default checkout mode: Async Payjoin`, and
`Fallback target: Payjoin v1 (BIP 78)`.
* Capture the receiver's Async-enabled state and directory/relay URLs as the
run baseline. Do not modify settings while executing the smoke test.
* Create two new invoices. The expected flow contains two broadcast actions:
one BIP77 and one BIP78. Count an action when its final broadcast-capable
button is activated, even if the response times out.

## Checkout contract

1. Create invoice A in `RECEIVER\_STORE` for `AMOUNT` with order ID
`<RUN\_ID>-BIP77`. Open its public checkout through `BASE\_URL`.
2. Wait up to 30 seconds for the Async controls and URL to appear. The first
render may show the BIP78 fallback while the plugin bootstraps; do not fail
the run during this window.
3. In Async mode, inspect one QR or **Pay in wallet** BIP21. Confirm the
testnet3 destination and amount, `pj=` containing a BIP77 mailbox URL with
its fragment, and `pjos=0`.
4. Switch invoice A to **Standard Bitcoin**. Confirm the same destination and
amount, `pj=https://…/BTC/pj`, and no `pjos=0` or BIP77 mailbox fragment.
5. Switch back to Async and reload once. Use the current Async link for the
BIP77 payment; do not reuse a stale link from an earlier render.
6. Create invoice B with order ID `<RUN\_ID>-BIP78`, open its checkout, select
Standard Bitcoin, and capture its current BIP78 link for the second payment.

If a product-generated link is wrong before signing, record `Fail: checkout contract` and do not broadcast that payment. If the QA browser or wallet
cannot expose or operate an otherwise valid link, record `Blocked: harness`
and capture enough evidence to reproduce the limitation.

### BIP77 -- invoice A

1. In `SENDER\_STORE` → **Bitcoin → Send**, open invoice A's current Async
BIP21. If **Paste BIP21** cannot accept input in the test browser, open the
same send page with its URL-encoded `?bip21=` parameter; treat this only as
a harness workaround.
2. Before signing, verify destination, amount, testnet3 network, sender
balance, and displayed fee. Stop if any value is wrong.
3. Activate **Send as async payjoin** once. Set
`BROADCAST\_ACTION\_COUNT=1`.
4. Reload the sender-session page periodically until `Completed (payjoin)` or
the currently documented successful terminal state appears. The initial
notice may show a fallback txid; use the final txid from the completed row.
5. Verify one final txid in invoice A, the sender wallet, and the receiver
wallet. Confirm the expected receiver payment and inspect the transaction
with the QA server's own testnet3 tooling if additional reconciliation is
needed. Wait for settlement only as required by `SETTLEMENT\_POLICY`.

### BIP78 -- invoice B

1. Start only after the BIP77 action has a known reconciled outcome and the
receiver still has at least one suitable spendable testnet3 input. If
necessary, prepare another receiver input before continuing.
2. In the sender wallet, open invoice B's Standard BIP21 and verify its
destination, amount, testnet3 network, sender balance, and fee before
signing.
3. Select **Broadcast (Payjoin)** once. Set
`BROADCAST\_ACTION\_COUNT=2`.
4. Verify one final Payjoin txid in invoice B and both wallet histories, then
verify settlement according to `SETTLEMENT\_POLICY`.

Invoice B may also show a separately labelled **Original transaction**. It is
an intermediate BIP78 artifact, not a duplicate payment; count only the final
Payjoin transaction. If the broadcast response times out, use the invoice,
wallet histories, server logs, and testnet3 transaction data to reconcile the
existing attempt before deciding the result. Never click Broadcast again for
the same attempt.

## Result

```text
RUN\_ID:
BASE\_URL:
RECEIVER\_STORE:
SENDER\_STORE:
AMOUNT:
BTCPay / plugin versions:
Testnet3 node / backend:
Receiver readiness: PASS / BLOCKED / FAIL
Checkout contract: PASS / BLOCKED / FAIL
BIP77 invoice / session / final txid:
BIP77 settlement: PASS / BLOCKED / NOT RUN
BIP78 invoice / final txid:
BIP78 settlement: PASS / BLOCKED / NOT RUN
BROADCAST\_ACTION\_COUNT: 0/2, 1/2, or 2/2
Overall: PASS / BLOCKED / FAIL
Settings equal baseline: YES / NO
Evidence / notes:
```

`PASS` requires receiver readiness, the checkout contract, both final txids,
and the selected settlement policy. Use `BLOCKED` for a missing local fixture,
harness limitation, unavailable testnet3 backend, or confirmation that cannot
currently be obtained. Use `FAIL` for an observed product or deployment
mismatch. Keep one screenshot, DOM observation, log extract, or transaction
observation for each failed or blocked check; correlate evidence by invoice ID
and full txid, never by amount or shortened txid.

## Handoff and out of scope

* Restore nothing unless the smoke test itself changed it; receiver settings,
directory/relay configuration, stores, and wallets should remain equal to
the captured baseline.
* Test coins, stores, invoices, and wallets may remain in the QA environment
for later investigation. Prepare or replenish fixtures as needed for later
runs.
* If a broadcast action was activated, continue reconciling its existing
invoice/session/txid and never rebroadcast the same payment attempt.
* Anonymous authorization matrices, unauthenticated JSON contracts,
directory/relay outage simulation, expiry/replacement, cold wallets,
refunds, over/underpayment, concurrency, external signers, responsive
layouts, and full fee/audit policy coverage belong in the local manual plan
or a dedicated test.

