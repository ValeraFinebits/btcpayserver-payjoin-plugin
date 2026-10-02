# Manual Test Plan — Async Payjoin Plugin on a Local Dev Server

For a developer testing this plugin by hand against their own BTCPay instance — the regtest,
cheat-mode server described under [Environment](#1-environment). Not a QA script for staging or production: several cases deliberately break the store's
configuration, and one section only makes sense in cheat mode.

Scope: everything a human can reach in the browser, plus the two HTTP surfaces the UI depends on.
Automated coverage lives in `BTCPayServer.Plugins.Payjoin.Tests` / `.IntegrationTests`; this plan
covers what those cannot — rendering, wiring into the host UI, persistence, and the operator's
actual click path.

Case IDs are stable — reference them in bug reports. Cases marked `⚠ BUG-n` are **known failing**;
see [Open issues](#open-issues).

**Version:** v1 — 148 cases

---

## 0. How to test this plugin

Three rules learned the hard way. They change the outcome of a session, not just its tidiness.

### Rule 1 — a success message is not evidence of persistence

"Async Payjoin settings saved." is emitted before anything is verified. **Every settings case must be confirmed in the store blob**, not on screen:

```bash
docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -t -c "SELECT \"StoreBlob\"->'payjoin.settings' FROM \"Stores\" WHERE \"Id\"='<storeId>';"
```

### Rule 2 — an unhandled plugin exception disables the plugin and stops the server

BTCPay's `PluginExceptionHandler` reacts to *any* exception escaping plugin code by queueing
`disable:BTCPayServer.Plugins.AsyncPayjoin` and calling `StopApplication()` 3 s later. In
production that means the merchant loses payjoin until someone re-enables it. So:

- after **every** 5xx from a plugin endpoint, check the queue file — an empty directory is a pass:

```bash
ls "$APPDATA/BTCPayServer/Plugins/"
```

- if `commands` or `disabled` is present, the plugin was auto-disabled. Remove the queued command
  (or re-enable via *Server Settings → Plugins*) before continuing, or later results are invalid.
- a dev server loaded through `DEBUG_PLUGINS` still loads the plugin despite the `disabled` list,
  so **the UI can look healthy while the disable marker is sitting on disk**. Check the file.
- with a debugger attached the handler skips the shutdown — a bug that kills a production server
  will look harmless when tested from Visual Studio. Test at least once without the debugger.

### Rule 3 — keep the node boring

Cheat-mode payments call `sendtoaddress` on bitcoind's *default* RPC endpoint. If the node has more
than one wallet loaded, that call throws, the exception escapes the controller, and Rule 2 fires —
a plugin-disabling failure caused purely by node state. Before a session:

```bash
docker exec btcpayservertests-bitcoind-1 bitcoin-cli -regtest -rpcport=43782 -rpcuser=ceiwHEbqWI83 -rpcpassword=DwubwWsoo3 listwallets
```

Expect exactly `["default"]`. Unload anything else (`unloadwallet <name>`) — including wallets you
create yourself to generate test keys.

---

## 1. Environment

| Item | Value |
| --- | --- |
| Server | `http://localhost:14142` (regtest, `BTCPAY_CHEATMODE=true`, Development/Debug) |
| Plugin build | **Debug** — loaded via `DEBUG_PLUGINS` in `btcpayserver/BTCPayServer/appsettings.dev.json`, pointing at `BTCPayServer.Plugins.Payjoin/bin/Debug/net10.0/…dll`. Build Debug and restart; a Release build changes nothing. |
| Restart | `dotnet run --launch-profile Bitcoin --no-build` from `btcpayserver/BTCPayServer` (log to a file — the crash evidence lives in stdout, nothing is written to disk by default) |
| Backing services | `btcpayservertests-*` docker compose (postgres `39372`, nbxplorer `32838`, bitcoind RPC `43782`) |

### Fixtures

The plan assumes an empty server: create these yourself before starting, and don't rely on anything
left behind by a previous session. Cases refer to them by these names.

| Name | How to create it | Used by |
| --- | --- | --- |
| **Store A** | BTC store with a **hot wallet**, native segwit, at least one *confirmed* UTXO (mine a block after funding). | almost everything |
| **Store B** | A store with **no wallet at all** — create it and stop there. Verify it is still wallet-less at the start of each session (below): a store that has since had a wallet configured looks fine but can no longer produce PJ-O4. | the amber prerequisite state (PJ-O4), the disabled/fallback branches (PJ-O5, PJ-O6), store isolation (PJ-L6), and as the "other store" for PJ-N5 and PJ-A9 |
| **Store C** | A store with a **watch-only** wallet: *Import wallet → Enter extended public key*, using a **freshly generated, never-used** key (`createwallet` → `listdescriptors` → `unloadwallet`, per Rule 3). Fund one of its addresses and **do not mine**. | PJ-F6 |
| **Manager account** | A second user, added to Store A with the **Manager** role (view store settings, but not modify). | PJ-N4 positive half, PJ-N7 |
| **Guest account** | A third user, added to Store A with the **Guest** role (no store-settings permission at all). | PJ-N4 negative half, PJ-N5 |
| **API key** | On your own account: *Account → API Keys → Generate Key*, labelled exactly `payjoin-manual-plan` so the snippet in section 10 can find it unambiguously, with `btcpay.store.canviewstoresettings`, `btcpay.store.canmodifystoresettings`, `btcpay.store.canviewinvoices`, each **scoped to Store A** rather than all stores. The scoping is what makes PJ-A9 mean anything: an all-stores key reaches Store B happily and proves nothing. | section 10 |
| **Seeded settlement records** | Two **fresh** invoices on Store A, each passed to the cheat-mode seeder (below), one as `failed` and one as `expired`. | PJ-O13, O14, O16, O17, N8 |

Three fixtures are perishable, and each fails quietly rather than loudly. **Store B** stops working
for PJ-O4 as soon as anyone gives it a wallet — which happens by accident more often than it sounds,
since a wallet-less store is an odd thing to leave alone on a dev server. Observed in practice on a
run of this plan. When it happens, **create a new wallet-less store** rather than trying to strip the
wallet off the old one: removing a derivation scheme leaves history behind and the store no longer
behaves like a fresh one. Check before trusting it:

```bash
docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -t -c 'SELECT "StoreName", "DerivationStrategies" IS NOT NULL AS has_wallet FROM "Stores";'
```

**Store C** stops being a valid fixture the moment anything mines a
block — its funding transaction confirms and it becomes an ordinary store, so re-fund it before
using it. **Store A** needs its confirmed balance topped up as payjoin tests consume coins.

Creating the two extra accounts requires setting passwords; do that yourself rather than delegating
it, and use throwaway credentials, since this is a local regtest instance.

#### Seeding a settlement record

The attention table's two row kinds cannot be produced by paying anything — see the note above
PJ-O13. A cheat-mode endpoint creates them through the ordinary service methods, so the rows have
the shape the real flow would leave behind:

```bash
curl -s -X POST -H 'Content-Type: application/json' \
  -d '{"invoiceId":"<invoiceId>","kind":"failed"}' \
  http://localhost:14142/plugins/payjoin/seed-attention-record
```

`kind` is `failed` or `expired`. Notes that matter in practice:

- **Seed a fresh invoice.** The endpoint is get-or-create: pointed at an invoice that already has a
  settlement record it reuses that record's deadline, and a stale deadline makes a retried row expire
  again immediately — which looks like a Retry bug and is not one.
- It refuses an invoice whose record is already `Reconciled`, so it cannot rewrite a real settlement.
- Seeded failures carry a `SEEDED:` prefix in the message, visible in the table and the database.
- Like `run-test-payment`, the route only exists in cheat mode and is marked for removal.

Useful queries:

```bash
docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -c 'SELECT "InvoiceId","Status","ExpectedFinalTransactionId","ReconciledAt" FROM "BTCPayServer.Plugins.Payjoin"."AccountingBridges" ORDER BY "UpdatedAt" DESC LIMIT 10;'
```

```bash
docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -c 'SELECT "InvoiceId", count(*) FROM "BTCPayServer.Plugins.Payjoin"."ReceiverSessions" GROUP BY 1;'
```

Bridge `Status`: `0 PendingFallback, 1 PendingFinalTransaction, 2 Reconciled, 3 Failed, 4 Expired`.

### Sanity gate

- **PJ-E1** Server answers on `:14142`; footer reads `Environment: Development (Debug)`.
- **PJ-E2** Startup log contains `Running plugin BTCPayServer.Plugins.AsyncPayjoin - <version>`, and
  `$APPDATA/BTCPayServer/Plugins/` is empty (no stale `disabled`/`commands`).
- **PJ-E3** Sidebar shows **Async Payjoin** under **Plugins**.
- **PJ-E4** Store A has ≥ 1 *confirmed* segwit UTXO.
- **PJ-E5** `bitcoin-cli listwallets` → exactly one wallet (Rule 3).
- **PJ-E6** `payjo.in` / `lets.payjo.in` and the configured OHTTP relays resolve from the host.

### Baseline

Store A settings: enabled = on; directories `https://payjo.in/` + `https://lets.payjo.in/`; relays
`https://pj.benalleng.com/` + `https://pj.bobspacebkk.com/` + `https://payjoin.achow101.com/`;
cold wallet empty; max fee rate empty. **Restore it after any case that changes settings, and
confirm with the blob query** — several cases leave the store in a state that silently breaks later
ones.

### Reset checkpoints

Settings are not the only thing that drifts. Sections 8, 9 and 12 spend coins, leave sessions and
bridges behind, and mine blocks that quietly age other fixtures. With this many cases, a polluted
environment is the largest single source of defects that are not defects — so stop at the end of
**each of sections 8 (payments), 9 (accounting) and 12 (lifecycle)** and clear this checklist before
starting the next one. Anything that fails here makes the following section's results untrustworthy;
record it as **Blocked**, fix the fixture, and re-run.

| Check | Expected | Command |
| --- | --- | --- |
| Store A settings | exactly the baseline above | blob query (Rule 1) |
| Store A balance | enough confirmed coins for the next section's payments | wallet page |
| Store B | still has no wallet | `has_wallet` query in Fixtures |
| Store C | its funding transaction still **unconfirmed** — re-fund if a block was mined | mempool / store balance |
| bitcoind | exactly one wallet loaded | `listwallets` (Rule 3) |
| Plugin queue | empty | `ls "$APPDATA/BTCPayServer/Plugins/"` |
| Sessions | one row per invoice you deliberately left open, none for settled or expired ones | sessions query |
| Bridges | no `Failed` or armed `Expired` rows except the ones you seeded on purpose | attention query |

Invoices themselves are cheap and need no cleanup — leave them. What must not survive a checkpoint is
a **seeded** settlement record you have finished with: retry it until the table is empty, or later
overview cases count rows that belong to an earlier section. Retry is the right tool for this — it
is the product's own path, it empties the table one row at a time, and when the last row goes the
whole *needing attention* section disappears rather than rendering an empty frame. That vanishing
section is itself the checkpoint's pass signal.

---

## 2. Exception containment (run this first — it invalidates everything else)

Rationale in Rule 2. Each case: trigger the failure, then assert (a) a controlled response,
(b) server still answering, (c) plugin queue directory still empty.

- **PJ-X1** `POST /plugins/payjoin/run-test-payment` for an already-settled invoice →
  `{"succeeded":false,…}` with HTTP 200, server alive, no disable queued.
- **PJ-X2** Same for an unknown/garbage invoice id.
- **PJ-X3** Malformed body → 400-class, not 500. Four bodies: `null` and non-JSON → 400 with
  `A JSON body containing an invoiceId is required.`; `{}` → 200 `invoiceId is required`;
  `{"invoiceId":"…"}` unknown → 200 naming the invoice. Server alive, nothing queued.
- **PJ-X4** Node made unusable mid-flight (load a second bitcoind wallet, then run a test payment)
  → controlled failure naming the invoice, server alive. The action catches every non-cancellation
  exception precisely because escaping one costs the whole process.
- **PJ-X5** Directory host unresolvable (see PJ-F4a) → no exception escapes on any page.
- **PJ-X6** Settings POST with a cold wallet that parses but cannot be tracked (NBXplorer down)
  → error rendered on the form, plugin not disabled.
- **PJ-X7** Hostile invoice ids on the *always-on* anonymous endpoint
  (`GET /plugins/payjoin/invoices/{id}/payment-url`): unknown id, `../../etc/passwd`, `%00`,
  a 500-char id, SQL and HTML payloads → 404/400 only, server alive, nothing queued. This endpoint
  is the production-reachable one, so it carries the strictest bar. URL-encode the payloads: an id
  containing a raw space makes curl fail before the request leaves the machine, and the empty result
  reads like a server that died.
- **PJ-X9** The seeder is a second anonymous cheat endpoint, so it carries the same bar: `null`,
  non-JSON, `{}`, an unknown invoice id, an unknown `kind`, and an already-reconciled invoice must
  each produce a controlled response — 400 for an unparseable body, 200 with a reason otherwise —
  with the server alive and nothing queued. The reconciled case is the important one: it is what
  stops a test tool from rewriting real settlement history.
- **PJ-X8** After the whole session: `$APPDATA/BTCPayServer/Plugins/` still empty.


## 3. Navigation and access control

- **PJ-N1** Sidebar → *Plugins → Async Payjoin* opens `/plugins/payjoin`, titled "Async Payjoin".
- **PJ-N2** Store *Settings* group shows **Async Payjoin** → `/stores/{storeId}/payjoin`.
- **PJ-N3** Overview → **Open Settings** targets the *selected* store. Know how to change that
  selection before testing anything store-specific: visiting `/stores/{id}` switches it, while
  `/stores/{id}/invoices` and friends do not. Get this wrong and every overview case silently
  reports on the previous store — the page header names the store, so read it every time.
- **PJ-N4** Nav items absent for a user without `CanViewStoreSettings`. Run it as a pair, or it
  proves nothing: a **Manager** (view, no modify) must see both the sidebar plugin item and the
  store-nav item, and a **Guest** in the same store must see neither while still reaching the store's
  own pages. Verified both ways; the Guest's direct hit on `/stores/{id}/payjoin` also 403s naming
  `btcpay.store.canviewstoresettings`, with no configuration in the error body. Note the host still
  renders an empty **PLUGINS** heading for the Guest — the section survives, only the entries are
  gated.
- **PJ-N5** As the Guest account, `GET` Store B's `/stores/{storeId}/payjoin` — a store it does not
  belong to → **403** naming the missing `btcpay.store.canviewstoresettings`, and no configuration
  values anywhere in the error page.
- **PJ-N6** `GET /plugins/payjoin` with no store selected → redirect + "You need to select a store first."
- **PJ-N7** Save rejected for a view-only user. A hidden button is not authorization, so this case
  must reach the endpoint — and must reach it the way a real browser would, or it proves nothing.

  1. Sign in as the **Manager** and open Store A's `/stores/{storeId}/payjoin`. The page renders
     (view permission), the Save button is absent, the fields are editable.
  2. Change a field to a value you can recognise later, e.g. max fee rate `999`.
  3. Submit **the form that is already on the page**, from the browser console:
     `document.querySelector('form[method="post"]').submit()`. Do not hand-build a request: the form
     carries the antiforgery token and the full field set, and without them the server can reject on
     validation or antiforgery *before* the permission check ever runs.
  4. Expected: BTCPay's **403** page, whose body names the missing
     `btcpay.store.canmodifystoresettings`.
  5. Confirm the store blob still holds the old value.

  **Read the failure mode carefully.** An antiforgery error, a 400, or a redirect back to the form
  are all *inconclusive* — they mean the request died before authorization, so the case is blocked,
  not passed. Only the 403 naming the permission proves the endpoint is guarded.
- **PJ-N8** Overview **Retry** buttons hidden without `CanModifyStoreSettings`. Seed an attention row
  first, or there is no button to hide and the case passes vacuously. Then load the overview as the
  Manager: the row is visible, the Retry control is not.
- **PJ-N9** `/server/plugins` lists **Async Payjoin** with the built version, the description, and
  the dependency range (`BTCPayServer: >= 2.4.0`). Resources currently read "No documentation" —
  the plugin ships no documentation link.
- **PJ-N10** The same page carries the disable/uninstall controls. This is the operator's recovery
  path after a Rule 2 auto-disable, so check it exists and names the plugin correctly *before* you
  need it in anger.

## 4. Overview page (`/plugins/payjoin`)

- **PJ-O1** Header card shows store name + id; unnamed store → "Unnamed Store".
- **PJ-O2** Configured store with confirmed inputs → green **"Basic prerequisites present"**.
- **PJ-O4** Configured but **no confirmed inputs** → amber "Additional requirements pending" plus an
  alert naming the real fallback. Use **Store B**: a store with no wallet reaches this state for
  free, with nothing to drain.
- **PJ-O5** *Default checkout mode* matches state: enabled → "Async Payjoin"; disabled + v1
  effective → "Payjoin v1 (BIP 78)"; else "Standard Bitcoin".
- **PJ-O6** *Fallback target* sits one step below the default mode and disappears when the default
  is already "Standard Bitcoin".
- **PJ-O7** Configuration card lists every directory and relay, one per line, long URLs wrapping.
- **PJ-O8** Cold wallet row reads Configured/Not configured and tracks the settings page.
- **PJ-O9** *Basic prerequisites* card (directory, relay, confirmed inputs) agrees with the
  Configuration card and the badge.
- **PJ-O10** With Async Payjoin **disabled** the page says so plainly: a neutral **Disabled** badge
  and a message naming what checkout serves instead. Assert on a funded store too — the disabled
  state is reported ahead of every prerequisite check, so a missing prerequisite cannot mask it.
- **PJ-O11** Badge colours carry meaning in both themes; a neutral chip must not read as success.
- **PJ-O12** **The page makes no outbound request, and does not pretend to know reachability.**
  Point the directory at an unroutable HTTPS host and reload: the page must still render promptly,
  must **not** claim the endpoints answer, and must **not** emit any request to the directory or the
  relays. Watch the log while loading — no `PayjoinOhttpKeysProvider` or `PayjoinMailroomManager`
  line may appear for a page load; those belong to arming a session and to the poller only. The
  status text says reachability is not checked here, and the prerequisites card says the same. This
  is a privacy constraint, not a gap: admin-triggered probes would emit non-payment traffic timed by
  an operator opening a page, which is exactly what an outside observer can fingerprint.
> **Why these need the seeder.** The table lists two row kinds and neither can be produced by using
> the product: `Failed` requires reconciliation to hit contradictory payment data, and an armed
> `Expired` requires `ExpiresAt` older than the **6-hour** `ArmedBridgeGracePeriod`
> (`PayjoinAccountingBridgeService.cs:87`) — shortening the store monitoring window does not help,
> since the six hours start counting after it. Use the seeder described in Fixtures. The
> reconciliation logic itself is unit-covered (`PayjoinAccountingBridgeServiceTests`,
> `UIPayjoinOverviewControllerTests`); what these cases add is the rendering and the Retry
> round-trip in a browser.

- **PJ-O13** *Settlement records needing attention* appears for `Failed` bridges, and for `Expired`
  bridges that carry an expected final transaction; otherwise absent. Seed one of each and confirm
  both render; with no such bridges the table must disappear entirely. **Check the exclusion too**,
  which ordinary testing produces for free: count the store rows in status 3 or 4 against the rows on
  screen. Unarmed expired records — the normal residue of invoices that were never paid — must be in
  the first number and not the second. A table that matches the raw count is over-reporting.
- **PJ-O14** Attention row shows invoice link, badge, expected txid, failure message, last update.
- **PJ-O15** More than 50 records → "Showing the 50 most recently updated of M records."

  **Not part of a routine run.** The bound and the count are unit-covered
  (`GetRequiringAttentionAsyncBoundsTheListAndReportsTheTotal`); the only thing manual testing adds
  is that one rendered sentence. Run it when the overview's rendering or the attention query changes,
  not on every pass — done by hand it costs more than the rest of section 4 put together.

  When you do run it, script all three phases and click nothing:

  ```bash
  # setup — needs btcpay.store.cancreateinvoice on the fixture key
  for i in $(seq 1 51); do
    ID=$(curl -s -X POST -H "Authorization: token $KEY" -H 'Content-Type: application/json' \
      -d '{"amount":"1.00","currency":"USD"}' \
      http://localhost:14142/api/v1/stores/$STORE/invoices | grep -o '"id":"[^"]*"' | cut -d'"' -f4)
    curl -s -X POST -H 'Content-Type: application/json' \
      -d "{\"invoiceId\":\"$ID\",\"kind\":\"failed\"}" \
      http://localhost:14142/plugins/payjoin/seed-attention-record > /dev/null
  done
  ```

  Then read the sentence once, and cross-check **M** against the database: it counts only
  attention-eligible rows, so raw status 3/4 minus the unarmed expired ones must equal M exactly
  (56 − 3 = 53 on the run that produced this note).

  **Clean up by script too.** Retrying 50-odd rows through the UI is not a test of anything — one
  manual Retry in PJ-O16 already covers the button. Drive the same route in a loop with a session
  cookie and the page's antiforgery token, or reset the seeded records directly; either way the pass
  signal is the same as everywhere else, the *needing attention* section disappearing entirely.
- **PJ-O16** **Retry** → success message naming the invoice and the row leaves the table. The record
  then lands in one of **two** states, and both are correct — check the right one or the case reports
  a phantom bug:
  - a **Failed, unarmed** record — seed it with `kind=failed` — returns to `PendingFallback`, keeps
    its original deadline, and its failure message is cleared;
  - an **Expired, armed** record — seed it with `kind=expired` — returns to `PendingFinalTransaction`,
    keeps its expected final transaction, and has its deadline pushed out by the six-hour armed grace,
    because there is still a signed proposal out there that could confirm. Verified: status 1, armed,
    deadline in the future, message cleared.

  In both cases re-check after a poller tick: a record whose deadline is already in the past will
  simply expire again, which looks like Retry failing and is not.
- **PJ-O17** Retry the same record again, now that it is pending → "The settlement record could not be retried."

## 5. Store settings (`/stores/{storeId}/payjoin`)

Every case ends with the blob query (Rule 1).

- **PJ-S1** Page loads with current values; all five fields round-trip.
- **PJ-S2** Save with no edits → success and no drift. Compare the store blob before and after, not
  the rendered fields: the form re-normalises what it displays, so a round trip that reorders or
  re-cases the stored list can still look identical on screen. Order and trailing slashes must both
  survive.
- **PJ-S3** Toggle **Enable Async Payjoin** off → persists; overview mode changes.
- **PJ-S4** Toggle back on → persists.
- **PJ-S5** Non-HTTPS directory line → `Line N: '<value>' is invalid. Only absolute HTTPS URLs are
  allowed.`, nothing saved, submitted text preserved.
- **PJ-S6** Same for relays.
- **PJ-S7** **Several** invalid lines → *all* are reported, one per line, under the field.
- **PJ-S8** Directory field cleared → "At least one directory URL is required.", nothing saved.
  Set a distinctive value first, or a silent restore of the defaults looks like a no-op.
- **PJ-S9** Same for relays.
- **PJ-S10** Whitespace-only field behaves like the empty field (same message, same outcome).
- **PJ-S11** Duplicates, including case-only (`HTTPS://PAYJO.IN/`), de-duplicate on save.
- **PJ-S12** Blank lines and stray whitespace are ignored, not errors.
- **PJ-S13** Max fee rate `0` → blocked client-side (`min=1`), never posted. The client guard is not
  the only one: the same value sent to the API is rejected 422 (PJ-A7), so a bypassed form cannot
  store a zero cap.
- **PJ-S14** Max fee rate `250000` → "The maximum fee rate must be between 1 and 100000 sat/vB."
- **PJ-S15** Max fee rate `1` and `100000` → **stored in the blob**, not just echoed on screen.
- **PJ-S16** Max fee rate cleared → stored null, help text's "automatic" behaviour applies.
- **PJ-S17** Cold wallet garbage → "Invalid wallet format: …", nothing saved.
- **PJ-S18** Cold wallet valid tpub/descriptor → normalised into the blob, overview "Configured",
  NBXplorer tracks it.
- **PJ-S19** Validation errors preserve the user's submitted text.
- **PJ-S20** Error placement is consistent (message directly under its field).
- **PJ-S21** Bad URL + bad fee + bad cold wallet in one submit → all three surface together, in one
  round. Nothing is persisted — confirm with the blob query. The cold wallet is only *tracked* in
  NBXplorer once the whole submit is valid, so a rejected submit leaves no trace there either.
- **PJ-S22** Checkbox follows host convention (control/label order, keyboard toggle, label click).
- **PJ-S23** **Persistence matrix** — set every field to a distinctive non-default value, save once,
  reload, and diff the blob field-by-field. This is the case that catches silent droppers
  (`PayjoinV2Enabled`, `DirectoryUrls`, `OhttpRelayUrls`, `MaxFeeRateSatPerVb`,
  `ColdWalletDerivationScheme`). Fold PJ-S11 and PJ-S12 into this same submit — put a case-only
  duplicate, a blank line and a padded line in the URL field, and one save proves all three.
  **Never point the cold wallet at Store C key.** Payjoin output would land on it, giving that store
  confirmed coins and quietly destroying the PJ-F6 fixture.

## 6. Checkout — Async Payjoin available

Precondition: baseline, confirmed segwit inputs, fresh **New** invoice.

- **PJ-C1** Mode switch (**Async Payjoin** | **Standard Bitcoin**) + shield indicator render
  directly above the QR.
- **PJ-C2** Async Payjoin preselected; BIP21 carries `pjos=0` and `pj=` pointing at **one of** the
  configured directories — not the local `/BTC/pj` v1 endpoint. Assert membership in the configured
  list, never a specific host: with several directories configured the choice varies between
  invoices, and pinning one produces a failure that is not there.
- **PJ-C3** QR payload, clipboard value and **Pay in wallet** href describe the same BIP21. Read the
  QR payload from the `data-qr-value` attribute rather than decoding the image, and compare
  case-insensitively — the QR carries the address and payjoin URL uppercased for encoding density,
  so a literal string comparison fails on a page that is perfectly correct.
- **PJ-C4** `Pay in wallet` title reads "BIP21 payment link with Async Payjoin support".
- **PJ-C5** Switching to Standard Bitcoin swaps QR and href together and hides the indicator;
  switching back restores it; no layout jump under the cursor. **Read what the second tab actually
  serves** — on a store with BTCPay's own payjoin enabled, assert the tab carries **no** `pj=` in
  either the href or the QR and its title reads plain "BIP21 payment link". Use such a store: on one
  without built-in payjoin the case cannot fail. Casing is the only legitimate href/QR difference.
- **PJ-C6** Displayed amount equals the amount in both BIP21 variants.
- **PJ-C7** Session amount ≠ invoice due → payjoin URL discarded, plain URL kept. **Inducible**: arm
  a session, then use the cheat panel's *Fake a BTC payment* with a smaller amount. The checkout then
  drops the tabs, serves the remaining due, and logs
  `PayJoin payment URL discarded: the receiver session expects a different amount than the invoice
  now asks for`. Verify the amount in the QR equals the *remaining* due, not the original — read that
  figure off the page text rather than a checkout view-model field, whose name differs between BTCPay
  versions and silently yields `undefined` in a comparison that then always fails.
  The server now refuses first — see PJ-Y6 — so the checkout drops the tabs because the endpoint
  reported `Unavailable`, not merely because its own JS noticed.
- **PJ-C8** Reloading checkout reuses the session — exactly one `ReceiverSessions` row per invoice.
- **PJ-C9** Payment-url fetch happens once per load, and not at all when the feature is off.
- **PJ-C10** No payjoin controls on the Lightning tab, or for statuses other than `New`.
- **PJ-C11** After paid/expired, controls disappear on refresh.
- **PJ-C12** Console free of plugin errors.
- **PJ-C13** Mode switch is keyboard reachable with coherent ARIA. It is a toggle button group, not
  a tab widget: assert `role="group"` with an `aria-label` on the container, `aria-pressed` toggling
  on the two buttons, and **no** `role="tab"`, `role="tablist"` or `aria-selected` anywhere in it.
  Tab + Enter is the whole interaction; arrow keys are not part of this pattern and must not be
  expected. A widget that announced tabs it did not implement is what this replaced.
- **PJ-C14** Plugin strings go through the localizer. Every string the checkout script injects — tab
  labels, shield indicator, group `aria-label`, test-payment labels — is now resolved server-side by
  `IStringLocalizer` and looked up in i18next at runtime, rather than hardcoded English in the markup
  it builds. **This makes them localizable, not localized**: the plugin ships no translation
  catalogue, so with `/i/{id}?lang=es-ES` they still render English while the host's own strings
  translate. Assert the wiring (change a key's host translation and see it follow), not the language.
  Ignore the cheat-panel strings; the host leaves those English too.
- **PJ-C15** Dark theme: active tab matches the primary button colour; inactive tab legible.
- **PJ-C16** Mobile (375 px): both buttons fit side by side and the QR stays fully visible.
  "Standard Bitcoin" wraps onto two lines at this width while "Async Payjoin" stays on one — that is
  expected, not a layout break; what would be a break is either button clipping, overflowing, or
  pushing the QR off screen.

## 7. Checkout — fallbacks

- **PJ-F1** Async Payjoin disabled → no switch, no test button, host's plain/v1 BIP21.
- **PJ-F4a** Directory host **unresolvable** — `https://payjoin-directory-unreachable.invalid/`.
  DNS refuses immediately, so this is the fast-fail path.
  `/plugins/payjoin/invoices/{id}/payment-url` → `{"bip21":"bitcoin:…","status":"Unavailable"}`
  **in ≤ 3 s**, and checkout renders the host's BIP 78 URL (`pj=http://…/BTC/pj`) with no switch, no
  shield, no test button, no console error.
- **PJ-F4b** Directory host **black-holed** — an unroutable address such as `https://10.255.255.1/`,
  which swallows the connection instead of refusing it. Same response body and same checkout
  rendering as F4a, but the endpoint returns **after the connect timeout, ~30 s** (accept 20–40 s;
  what actually matters is that it is bounded and does not hang). Two rules for reading this case:
  - Check the **page** before the endpoint. Checkout renders its QR and href server-side, so the
    customer sees a payable invoice immediately; the plugin only ever upgrades an already-rendered
    page. A slow `curl` here is not a broken checkout.
  - A response faster than F4a's, or no response at all, is the failure. The former means the
    timeout was skipped, the latter that it is unbounded.
- **PJ-F5** All OHTTP relays unreachable → same graceful fallback; log records "OHTTP keys are
  unavailable from all configured relays".
- **PJ-F6** Only unconfirmed coins → payjoin not advertised. Overview shows amber *Additional
  requirements pending* with Receiver inputs **Pending** and names the cause ("no confirmed receiver
  inputs … until the wallet has spendable confirmed coins"), and the payment-url endpoint answers
  `status:"Unavailable"` with a plain BIP21.

  Use **Store C**. Watch-only is enough — nothing has to sign for the plugin to decide the wallet
  has no spendable confirmed coins.

  > One trap worth stating plainly: the imported key must be **unused**. Reusing a key that has ever
  > received payjoin output — the cold-wallet cases produce exactly such keys — leaves confirmed
  > UTXOs on it, the page correctly reports *Present*, and the fixture bug reads exactly like a
  > product bug.
  >
  > The legacy-coins half of this case (non-segwit UTXOs rejected by `IsSupportedReceiverCoin`) needs
  > its own fixture: same recipe as Store C, but import a legacy derivation scheme.
- **PJ-F7** Store with no BTC wallet → settings and overview still render, payjoin unavailable.

## 8. End-to-end payment (cheat mode)

- **PJ-P1** Cheat panel shows **Async Payjoin test payment → Run Async Payjoin test payment**,
  spaced consistently with the other cheat controls.
- **PJ-P2** Click → "Running…" → "Done", invoice Settled, confetti.
- **PJ-P3** Amount paid equals the invoice amount exactly.
- **PJ-P4** **It is a real payjoin.** Decode the tx and assert ≥ 2 inputs, one of them the
  receiver's, and a receiver output equal to *contributed input + invoice amount*:

  ```bash
  docker exec btcpayservertests-bitcoind-1 bitcoin-cli -regtest -rpcport=43782 -rpcuser=ceiwHEbqWI83 -rpcpassword=DwubwWsoo3 getrawtransaction <txid> 2 <blockhash>
  ```

  (no `-txindex`: find the block with `getblockhash`/`getblock` first).
- **PJ-P5** Fee rate is sane and respects the store's max-fee-rate cap when one is set.
- **PJ-P6** Two invoices open at once → both settle; one session and one bridge each.
- **PJ-P7** Second run on the same invoice → `succeeded:false` with a message, no double payment,
  no orphan bridge, server alive.
- **PJ-P8** Test payment while payjoin is unavailable → button hidden; direct POST returns
  `succeeded:false` with operator-readable prose naming the invoice, and `payjoin is disabled by
  store settings` when the switch is off. Internal state names leaking into these messages is the
  regression to watch for.
- **PJ-P9** **Cold wallet routing.** Use a **freshly generated** key here as well — a key with prior
  payjoin history already holds outputs, and then every address you check matches for the wrong
  reason. With it set, run a test payment and assert: the
  receiver output pays a *cold-wallet-derived* address (`deriveaddresses` on `0/*` and `1/*`), the
  invoice still settles for the full amount, and the bridge reconciles. Operator-visible
  consequence to verify in the wallet UI: the hot wallet shows a **negative** amount for the
  invoice (its contributed input left for cold storage) — see PJ-H5.
- **PJ-P10** The endpoint exists only in cheat mode (would 404 on a production build).
- **PJ-P11** **Two test payments fired at the same instant on one invoice** — the money-safety case.
  Exactly one succeeds and broadcasts; the other returns a controlled failure; the invoice has one
  payment, the bridge one expected final transaction, and the session is cleaned up once. Which of the
  two wins varies between runs, so assert the counts, not the order. The sharpest single check is
  `SELECT count(DISTINCT "ExpectedFinalTransactionId")` for the invoice — it must be 1, which catches
  a second proposal being armed even if only one of them was ever broadcast. The loser should say a
  payment for this invoice is most likely already in flight — not name an internal state.

## 9. Accounting, history, labels

- **PJ-H1** Invoice Settled, "Paid" equals amount due, one on-chain payment row with the payjoin txid.
- **PJ-H2** The payment row identifies the payjoin nature of the transaction (core marks BIP78
  payments "Payjoin transaction"; async should be at least as informative).
- **PJ-H3** Payment row **Index** shows a real key path, not "Unknown". The path is resolved by
  asking NBXplorer about the settlement script itself, so it answers for whichever tracked wallet
  owns it — the store's or the cold one — without the plugin choosing between them. Expect a
  change-branch path such as `1/12` when output substitution moved the output. Compare against a
  plain payment on another invoice in the same store. Cheapest check is the DB:

  ```bash
  docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -t -A -c "SELECT \"Blob2\"->'details' FROM \"Payments\" WHERE \"InvoiceDataId\"='<invoiceId>';"
  ```
- **PJ-H4** Wallet list labels the tx **Async Payjoin** + **invoice**; the label filter finds it.
- **PJ-H5** Balance delta matches the flow: **+invoice amount** with no cold wallet; **−contributed
  input** with a cold wallet (funds moved to cold storage — expected, but easy to misread as a loss).
- **PJ-H6** Invoice event log free of plugin errors/warnings — read the **Events** table itself, not
  the whole page: an invoice that also carries a seeded or failed settlement record contains the word
  "Failed" elsewhere, and a page-wide text search reports a problem that is not in the log. Note what
  the log *doesn't* say: the only
  payjoin line is core's `BTC-CHAIN: Payjoin is enabled for this invoice` (that's BIP 78). Nothing
  records the async session being armed, proposed or settled, so this log is no help when async
  payjoin misbehaves.
- **PJ-H7** Bridge ends `Status = 2 (Reconciled)` with `ReconciledAt` set. **Invoice Settled does not
  mean bridge Reconciled** — the invoice settles from the mempool while the bridge sits at
  `1 (PendingFinalTransaction)` until the plugin sees the transaction. No block is required, but the
  delay is not fixed — observed anywhere from immediate to about 30 s across runs. Poll the row with
  a timeout instead of asserting a duration; a single read at +5 s reports a failure that is not
  there, and a hardcoded expectation of "~10 s" fails the fast runs.
- **PJ-H8** Receipt page renders and shows the payjoin payment.
- **PJ-H9** Over-paid invoice leaves **no Failed settlement record** — that is the assertion that
  holds however the overpayment arrived, and the only one worth pinning. Do not pin the invoice
  status: a plain overpayment on a default store lands on *Settled (paid over)*, and other
  configurations word it differently. Do not expect the record to reconcile either when the
  overpayment was an ordinary payment: there was no payjoin to settle, so it correctly stays
  `PendingFallback` until its monitoring window closes, and only the session is retired.
- **PJ-H11** **Refund a payjoin-settled invoice.** Reach it at `/invoices/{id}/refund` — the link
  lives on the invoice page and the bare URL redirects if you are not on the right store. *Issue
  Refund* offers the correct paid amount
  (BTC at the paid rate) and creating it produces a pull payment without error — the payjoin payment
  record is consumable by the refund flow. Stop at creation unless you mean to move funds.
- **PJ-H10** **Cold wallet vs. receipt.** With a cold wallet configured, settle an invoice by payjoin
  and assert all four, each of which is a fact about this build:
  1. the invoice address does **not** appear among the settling transaction's outputs;
  2. the receiver output pays an address derived from the configured cold wallet key. Derive a
     **wide** range over both branches — `[0,40]` at least, not `[0,9]`. NBXplorer hands out the
     next unused index on a freshly tracked key, which can already be well past zero, so a short
     range reports "not a cold address" for a payment that went exactly where it should. Compare
     against the derived set rather than assuming a low index;
  3. the invoice page and `/i/{id}/receipt` both print the **invoice** address as "Destination";
  4. the payment's proof points at the output index that (1) says is not the invoice address.

  That the receipt therefore cannot be matched against the chain is real; whether it should be fixed,
  and how, is [QUESTION-2](#open-questions). Do not fail a build over it — fail it if any of 1–4
  stops holding, which would mean the routing or the recording changed.

## 10. Greenfield API

Uses the **API key** fixture. Read it from the database rather than pasting it around, so it never
lands in a transcript or a shell history — and select it **by label**, never `LIMIT 1`. A server with
more than one key will hand you somebody else's, and if that one happens to be broader-scoped,
PJ-A9 passes while proving nothing. Give the fixture key the exact label below and match on it:

```bash
# Exactly one row must come back. Two means a duplicate label, none means the fixture is missing —
# either way stop, because an empty $KEY silently becomes an unauthenticated request.
KEY=$(docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -t \
  -c "SELECT \"Id\" FROM \"ApiKeys\" WHERE \"Label\" = 'payjoin-manual-plan';" | tr -d ' \n')
[ -n "$KEY" ] || { echo "fixture key not found"; return 2>/dev/null || exit 1; }
curl -s -H "Authorization: token $KEY" http://localhost:14142/api/v1/stores/<storeId>/payjoin/settings
```

Everything in this section is a deterministic request/response assertion, so it belongs in
`BTCPayServer.Plugins.Payjoin.IntegrationTests` long-term — the harness there already mints scoped
keys via `TestAccount.CreateClient(permissions…)`. Until it moves, run it by hand.

- **PJ-A1** `GET /api/v1/stores/{storeId}/payjoin/settings` → 200, camelCase, mirrors the UI.
- **PJ-A2** Response carries every persisted setting, `maxFeeRateSatPerVb` included.
- **PJ-A3** `PUT` valid body → 200 and the **blob** matches the response. Check both sides: the
  response is built from the submitted settings, so it can agree with the request while the stored
  row disagrees with each — which is exactly how the dropped fee cap hid.
- **PJ-A4** `PUT` missing `directoryUrls` / `ohttpRelayUrls` → 422 "field is required".
- **PJ-A5** `PUT` empty array → 422 "At least one … is required" (the state the form can't reach).
- **PJ-A6** `PUT` `http://` or relative URLs → 422 listing each offending value.
- **PJ-A7** `PUT` out-of-range fee → 422, same wording as the form.
- **PJ-A8** `PUT` invalid `coldWalletDerivationScheme` → 422 "Invalid wallet format".
- **PJ-A9** Both a store outside the key's scope (Store B) and an invented store id → **403**
  `Insufficient API Permissions`. The authorization layer answers before the plugin's not-found logic
  runs, so there is no `store-not-found` to see and no way to tell a missing store from a forbidden
  one — the desirable outcome, since the API is then not an existence oracle for store ids. Only the
  invented-id half has been exercised so far; the scoped-key half needs the key built as the fixture
  table describes.
- **PJ-A10** `GET …/invoices/{invoiceId}/payjoin/payment-url` for a New invoice → 200
  `{bip21, status:"Active", unavailableReason:null}` — three fields, not two.
- **PJ-A11** Settled/expired invoice → 404 `payment-url-not-payable`.
- **PJ-A12** Invoice requested under a different store's path → **403**, same reason as PJ-A9. What
  matters is that no invoice data crosses stores; the status code comes from the host.
- **PJ-A13** No/insufficient credentials → 401/403.
- **PJ-A14** `/docs` shows the **PayJoin** section and the documented schema matches the real
  payload. `PayjoinStoreSettingsData` must declare **all five** properties — `payjoinV2Enabled`,
  `directoryUrls`, `ohttpRelayUrls`, `coldWalletDerivationScheme`, `maxFeeRateSatPerVb` — and
  `PayjoinPaymentUrlData` all three (`bip21`, `status`, `unavailableReason`). The schema sets
  `additionalProperties: false`, so a field the API accepts but the schema omits is not a
  documentation gap but a contract that contradicts the server: a generated client drops the field
  and a strict validator rejects a request the server would have honoured. That is exactly what
  happened to `maxFeeRateSatPerVb` once, which is why this case diffs mechanically rather than
  eyeballing the page:

  ```bash
  curl -s http://localhost:14142/swagger/v1/swagger.json | jq '.components.schemas.PayjoinStoreSettingsData.properties | keys'
  ```

  Compare that list against a live `GET …/payjoin/settings` response. Both operations and both
  schemas (`PayjoinStoreSettingsData`, `PayjoinPaymentUrlData`) must be present and complete.
- **PJ-A15** The cheat endpoints must **not** appear in the published surface. Neither
  `run-test-payment` nor `seed-attention-record` may show up in `swagger.json`, under any tag: they
  exist only in cheat mode and documenting them would advertise a test affordance as API.

## 11. Anonymous checkout endpoint

- **PJ-Y1** `GET /plugins/payjoin/invoices/{id}/payment-url` unauthenticated → 200 for a New invoice.
- **PJ-Y2** Non-payable/unknown invoice → 404.
- **PJ-Y3** Shape is camelCase `{bip21, status}`; the checkout script's PascalCase fallback stays
  unused (a console warning means the host serializer changed).
- **PJ-Y4** 20 **parallel** first-time calls (`for i in $(seq 1 20); do curl … & done; wait`) →
  exactly one session row, one bridge, one distinct `bip21` across all 20 responses, and no
  unique-constraint or duplicate-key noise in the log. Sequential calls do not exercise
  `PayjoinSessionBuildLock`; fire them at once or the case is worthless.
- **PJ-Y5** **The anonymous endpoint reveals nothing about a store beyond the invoice it was asked
  about.** This endpoint takes no store id and no credentials, so the whole question is what an
  invoice id alone unlocks.

  Fixture and sequence:
  1. On **Store A**, create invoice `INV-A` and load its checkout so a session is armed.
  2. On **Store B**, create invoice `INV-B` (give Store B a wallet only if it needs one to accept an
     invoice; otherwise use **Store C**, which has one).
  3. Call `GET /plugins/payjoin/invoices/{INV-A}/payment-url` **signed out entirely** — a private
     window, or `curl` with no cookie and no token.

  Assert on the response body, which must contain exactly two fields: `bip21` and `status`.

  **What legitimately appears:** the BIP21 itself, and inside it the *one* directory the session was
  armed against — a sender cannot complete a payjoin without it. Do not file that as a leak.

  **What must not appear** — check each, since a leak here is silent:
  - Store A's id or name;
  - the *other* configured directories, or any relay hostname (relay material travels inside the
    `OH1…`/`RK1…` bech32 blobs, never as a readable host);
  - the cold wallet key or the fee cap;
  - any address, amount or id belonging to `INV-B` or any other invoice;
  - session, bridge or transaction identifiers beyond what the BIP21 carries.

  Two things will otherwise waste your time:

  - **Search case-insensitively.** The plugin uppercases the payjoin URL inside the BIP21, so a
    lowercase `grep` for a configured host returns zero against a body that plainly contains it —
    the check passes while proving nothing.
  - **Match whole hosts, not substrings.** With `payjo.in` and `lets.payjo.in` both configured, a
    search for the former hits inside the latter, and one legitimately present directory reads as
    two. Anchor the match or compare the parsed `pj=` host against the configured list.

  Then repeat step 3 for `INV-B` and confirm the same, and confirm neither response differs
  depending on whether you are signed in. Serving the BIP21 to an anonymous caller is the design —
  checkout is public — so this case is about everything *else* staying invisible.
- **PJ-Y6** **The endpoint's amount is trustworthy after a partial payment.** Set up the PJ-C7
  fixture, then call the endpoint directly: it must answer `status:"Unavailable"` with a plain BIP21
  for the *remaining* due — never `Active` with the original amount. The guard is server-side, so
  every consumer is covered, not just the checkout's own JS: the Greenfield payment-url operation,
  a POS integration, and a wallet holding a cached URI all get the safe answer.

## 12. Session lifecycle

- **PJ-L1** One `ReceiverSessions` row after first checkout load; none for invoices never opened.
- **PJ-L2** Invoice expires unpaid → the session row is **removed**, while the bridge stays
  `PendingFallback` until its own `ExpiresAt` — the ~24 h monitoring window, not the 15 min payment
  window. Assert both; a bridge still pending right after expiry is correct. Don't wait 15 minutes:
  the checkout cheat panel's *Expire invoice in … seconds* does it in 15.
  **Removal is not instant and that is not a leak** — closing is event-driven
  (`InvoiceDataChangedEvent` → `RequestClose`) and finished by the poller's 5 s tick, so the
  payment-url endpoint 404s while the row is still there. Budget ~30 s, not ~10: the chain is the
  cheat deadline, then BTCPay noticing the invoice expired, then the event, then a poller tick — and
  only the last of those is the plugin's 5 s loop. Measured 28 s from pressing Expire with a 10 s
  deadline.
- **PJ-L3** Expired bridge without an expected final tx never reaches the attention table.
- **PJ-L4** Invoice paid by plain "Fake a BTC payment" → no Failed bridge. The session is retired
  only once the payment **confirms** (mine a block); immediately after the fake payment the row is
  still there, which is correct, not a leak. Budget ~40 s end to end. The record stays
  `PendingFallback` with no failure afterwards — a plain-paid invoice leaves nothing to reconcile, so
  a pending record is not a health signal on its own, and the absence of a Failed one is the actual
  assertion.
- **PJ-L5** Hard restart with an open payjoin invoice → the session survives and the invoice is still
  payable through it. Order is what makes this a test rather than a formality: arm the session
  **before** killing the process, and after the restart pay that same session without touching the
  endpoint again first. Re-arming after the restart quietly replaces the thing under test. Assert the
  session count for that invoice stays at 1 across the restart — a second row means it was rebuilt,
  not resumed — and that the payment reconciles afterwards.
- **PJ-L7** **Settings changed while a session is armed.** Arm an invoice, then switch Async Payjoin
  off. All four assertions are unambiguous — this case records behaviour, it does not judge it:
  1. the payment-url endpoint returns `status:"Unavailable"` with a plain BIP21 on the next call;
  2. the cheat payment refuses with `payjoin is disabled by store settings`;
  3. the `ReceiverSessions` row for that invoice **still exists**;
  4. the poller still names that invoice in the log on subsequent ticks.

  Whether 3 and 4 *should* hold is [QUESTION-1](#open-questions), not a pass/fail criterion. Repeat
  the whole case with a directory-URL change instead of the toggle.
- **PJ-L6** Stores A and B → settings, sessions and bridges isolated per store. The settings half is
  two commands, not a browser session: `PUT` a distinctive directory list and `payjoinV2Enabled:
  false` to Store B through the API, then read **both** blobs. B changes, A is untouched. Then switch
  the selected store (visit `/stores/{B}`) and confirm the overview reports B's values and B's
  state — this doubles as the disabled-store rendering of PJ-O5, PJ-O6 and PJ-O10 without setting
  anything up twice. Restore B afterwards; only Store A has a prescribed baseline, so a stale
  fixture here misleads the next reader rather than failing loudly.

## 12a. Database invariants

Run this once at the end of a session. It is six counts, all of which must be zero, and it catches
whole classes of bug that no single case looks at — duplicate sessions, orphaned rows, settlements
recorded without the transaction that produced them.

- **PJ-D1** No invoice has more than one receiver session.
- **PJ-D2** No invoice has more than one settlement record.
- **PJ-D3** No `Reconciled` record is missing `ReconciledAt`.
- **PJ-D4** No `Reconciled` record is missing its expected final transaction.
- **PJ-D5** No expected final transaction id appears against two invoices — one payjoin cannot settle
  two invoices, and this is the cheapest place to notice if it ever did.
- **PJ-D6** No session exists whose invoice has no settlement record.

```bash
docker exec btcpayservertests-postgres-1 psql -U postgres -d btcpayserver -c 'SELECT (SELECT count(*) FROM (SELECT "InvoiceId" FROM "BTCPayServer.Plugins.Payjoin"."ReceiverSessions" GROUP BY 1 HAVING count(*)>1) a) AS dup_sessions, (SELECT count(*) FROM (SELECT "InvoiceId" FROM "BTCPayServer.Plugins.Payjoin"."AccountingBridges" GROUP BY 1 HAVING count(*)>1) b) AS dup_bridges, (SELECT count(*) FROM "BTCPayServer.Plugins.Payjoin"."AccountingBridges" WHERE "Status"=2 AND "ReconciledAt" IS NULL) AS no_time, (SELECT count(*) FROM "BTCPayServer.Plugins.Payjoin"."AccountingBridges" WHERE "Status"=2 AND "ExpectedFinalTransactionId" IS NULL) AS no_tx;'
```

## 13. Cross-cutting UI

- **PJ-U1** Light and dark: no unreadable text, no invisible badge.
- **PJ-U2** 375 / 768 / 1280 / 1920 px: no horizontal scroll, no clipped cards, URLs wrap.
- **PJ-U3** All visible strings translatable, including JS-injected checkout strings.
  Known admin-side instance: the store-nav item (`PayJoinStoreNavExtension.cshtml`) carries
  `text-translate="true"`; its sibling sidebar item (`PayjoinHeaderNav.cshtml`) does not — same
  label, two behaviours, one attribute apart.
- **PJ-U4** Terminology consistently "Async Payjoin" everywhere the operator can see it.
- **PJ-U5** No console errors or failed requests on any plugin page — and check the **server** log at
  the same time, by level rather than by keyword. A clean run has zero `fail:` lines from plugin
  namespaces and zero "caused by plugin". With a directory deliberately broken, expect one `fail:`
  per relay from `PayjoinOhttpKeysProvider` plus a summarising `warn:` — see the note below about
  whether Error is the right level for a failure the plugin then handles.
- **PJ-U6** Keyboard-only pass over the settings form and the checkout switch. Also check what a
  screen reader gets: every label carries `for`, and after a failed submit each offending field sets
  `aria-invalid="true"` and points `aria-describedby` at its error list and its help text.

---

## Smoke subset 

When there isn't time for the full plan — a PR check, a pre-demo pass. Run them in order and carry one invoice
through: the invoice you pay at step 5 is the one steps 6 and 12 then inspect. Without that thread,
step 12 sends you hunting for "a settled payjoin invoice" among everything the session produced.

| # | Case | Why it earns a slot |
| --- | --- | --- |
| 1 | PJ-E2, PJ-E5 | Plugin actually loaded, no stale disable marker, one bitcoind wallet |
| 2 | PJ-X3 | A `null` body is one curl and instantly proves whether the server-killer class is back |
| 3 | PJ-S23 | Persistence matrix — one save catches every dropped field |
| 4 | PJ-C2, PJ-C5 | The payjoin URL is right, and the other tab serves what its label claims |
| 5 | PJ-P2, PJ-P4 | The test payment settles *and* is a genuine two-input payjoin |
| 6 | PJ-H1, PJ-H4, PJ-H7 | Settled, labelled, reconciled — the merchant-visible result |
| 7 | PJ-F4a | Unreachable directory degrades to BIP 78 instead of hanging. F4a only — it is the ≤3 s path; F4b costs 30 s of waiting and belongs to the full run |
| 8 | PJ-Y4 | 20 parallel arming calls still produce one session |
| 9 | PJ-P11 | Two simultaneous payments still produce one transaction |
| 10 | PJ-O4, PJ-O10 | Load Store B — both overview edge states in one page load |
| 11 | PJ-C7, PJ-Y6 | Partial payment: the checkout must drop the payjoin URL, and check what the endpoint still serves |
| 12 | PJ-H3 | Payment row Index against a plain payment — one glance, and it is the cheapest data-integrity signal |
| 13 | PJ-X8 | Plugin queue directory still empty when you're done |

## Run log

One row per session. **Blocked is not Failed** and the distinction is the point of this table: a
failed case is a defect in the plugin, a blocked case is a case that could not be run — a broken or
drifted fixture, a missing account, an inconclusive result like the one PJ-N7 warns about. A run with
blocked cases is not a green run, and the blocked count is what tells the next tester where the
environment needs work before their results mean anything.

| Date | Plugin version / commit | Tester | Fixtures at start | Cases run | Pass | Fail | Blocked | Skipped | Bug IDs | Server log |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |

- **Plugin version / commit** — the built version plus the commit the plugin was built from. "Debug"
  alone cannot identify what was tested.
- **Fixtures at start** — `clean` if every Reset checkpoint row passed before starting, otherwise
  name what was off (`Store C confirmed`, `Store B has wallet`).
- **Skipped** — deliberately not run this session (out of scope for the change under test), as
  opposed to Blocked, which was attempted and could not complete.
- **Server log** — path to the captured stdout. Rule 2 evidence lives only there, and a run whose log
  was not kept cannot be re-examined.

## Open questions

Product decisions, not defects. Each names behaviour that is deterministic and covered by a case
above; what is undecided is whether that behaviour is what the plugin wants. Keeping them here
rather than inside the cases is deliberate — a case whose expected result is "decide whether this is
right" gets two testers filing opposite results against the same build.

| ID | Case | Question | Why it matters |
| --- | --- | --- | --- |
| QUESTION-1 | PJ-L7 | Should switching Async Payjoin off retire sessions that are already armed, or let them finish? | Today the switch stops new arming immediately but in-flight sessions keep polling. Either answer is defensible; the operator has no way to know which one they are getting. |
| QUESTION-2 | PJ-H10 | Should a cold-wallet settlement be able to declare its real destination, or is the invoice address the right thing to show? | The receipt shows an address that never appears on chain. Fixing it inside the plugin is not possible — "Destination" comes from the host's payment prompt — so this is either accepted and documented, or raised upstream. |

Resolve one and it turns back into an ordinary case with a fixed expected result.

## Open issues

Empty, and that is the current state rather than an unfinished section: every defect this plan has
found so far has been fixed and verified, so no case carries a `⚠ BUG-n` marker. When one does, it
goes here — a marker without a row, or a row without a marked case, means the document is lying to
the next reader.

| ID | Case | Issue | Severity |
| --- | --- | --- | --- |

Also noted, below bug threshold:

- **A black-holed directory holds the payment-url request for 30 s** (PJ-F4b). Harmless for checkout,
  which renders before the fetch, but a synchronous API consumer waits the full timeout.
- **Relay failures log at Error while being handled** (PJ-U5). Each unreachable relay produces a
  `fail:` line from `PayjoinOhttpKeysProvider`, then a `warn:` summarises the fallback that actually
  happened. Three relays configured means three Error lines per checkout load on a misconfigured
  store — loud for a condition the plugin recovers from by design.
- **Cold-wallet payments show as a large negative wallet amount** with no in-UI explanation (PJ-H5).
- **The shield indicator repeats the active tab's label** directly above it on checkout — harmless,
  but it is the same words twice in 40 px.
