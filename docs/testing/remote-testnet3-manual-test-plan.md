# Manual Test Plan — Async Payjoin Plugin on a Remote Testnet3 Server

For a tester validating this plugin against an Internet-accessible BTCPay Server that uses Bitcoin
testnet3.

**Version:** v1 — 190

---

## 0. How to use this plan

### 0.1 Execution tiers

Every case carries one tier. A run may deliberately stop at a lower tier, but skipped higher-tier
cases must not be reported as passed.

| Tier | Meaning | Approval and evidence |
| --- | --- | --- |
| **RO** | Read-only UI or HTTP observation. It creates no store, invoice, session, or transaction. | Normal tester access. Browser screenshot or HTTP response is sufficient. |
| **RW** | Reversible server-state change: dedicated stores/accounts, settings, invoices, API calls, or sessions before broadcast. | Use only run-owned fixtures. Capture before/after state and restore the documented baseline. |
| **TX** | Broadcasts or may broadcast a real testnet3 transaction. | The run owner approves a transaction-count and satoshi budget before the case. Record every txid. |

Classify a case by the maximum credible effect of the request if the guard under test is absent, not
only by its expected response. A POST to a mutation route is therefore at least **RW**, even when the
expected result is 404 and the deliberately safe payload should leave no state.

### 0.2 Evidence strength

Use the strongest available level and record it per case:

1. **Black-box:** rendered UI, fresh authenticated/anonymous browser session, HTTP status/body,
   browser console and network panel, and a public testnet3 explorer.
2. **Cross-surface:** the same state agrees through two independent product surfaces, for example a
   settings save followed by a fresh page load and a Greenfield `GET`.
A success toast is not persistence evidence. For a normal remote settings case, require both a
fresh page load and the Greenfield settings response. These two reads do not prove database shape,
process-restart durability, or absence of a queued disable command; those claims are explicitly out
of scope for this plan.

Tag evidence in the run log as **current-run**, **historical-control**, **source-aided**, or
**harness-limited**. Historical transactions may explain a control but never pass a current **TX**
case. Source inspection may explain an observed route or permission but never replaces black-box
execution. A harness limitation is evidence for `Blocked`, not a product failure and not a Pass.

### 0.3 Remote-server safety rules

1. **Use only run-owned fixtures.** Do not change the settings of a shared demonstration store.
   Prefix disposable stores and invoices with the run ID where the host UI permits it.
2. **Never exercise plugin lifecycle controls.** Verify that the installed-plugin card and recovery
   controls are present, but do not click Disable, Uninstall, Upload, or restart controls as an
   ordinary test step.
3. **A 5xx is a stop signal.** Record UTC time, URL, account role, store/invoice alias, response,
   and the last browser action. Make one read-only health request, end the run, and report the
   result. Without internal access, do not claim that the plugin was not queued for disable.
4. **Do not turn resilience testing into denial of service.** The routine run sends one request per
   negative input. Parallel request storms, black-holed hosts, and repeated relay failures are out
   of scope.
5. **Treat testnet coins as shared state.** Record the approved transaction, payment-value, and fee
   budgets from Section 1.1 before any **TX** case. Do not obtain faucet coins automatically,
   consolidate unrelated UTXOs, or spend funds outside the named test wallets.
6. **Protect secrets.** Never put passwords, seeds, descriptors containing private keys, cookies,
   API tokens, full browser storage, or unredacted request headers in the run log. Generate a fresh
   testnet-only key for cold-wallet cases and destroy its secret material after the run according to
   the run's credential policy.
7. **External failure is not automatically a plugin defect.** Record DNS, TLS, HTTP status, and
   timing for directories, relays, NBXplorer, and the public explorer. Repeat only once with a
   known-good control endpoint.
8. **Pace mutations.** Leave at least 4–6 seconds between UI/API writes and invoice-creation actions.
   If the host returns 429, stop the sequence, wait for the server's cooldown (at least five minutes
   when no `Retry-After` is supplied), then retry exactly once at normal pace. A 429 caused by an
   automation burst is a harness result unless ordinary paced use reproduces it.

### 0.4 Result vocabulary

- **Pass:** the required evidence directly proves the expected result.
- **Fail:** the product was reached with a valid fixture and contradicted the expected result.
- **Blocked:** the case was attempted but its fixture, permission, dependency, budget, or required
  black-box evidence was unavailable or inconclusive.
- **Skipped:** deliberately outside this run's declared tier or change scope.

`Blocked` and `Skipped` are not Pass. A run is green only for the declared scope and only when every
case in that scope passed.

For a requested **full cycle**, assign Pass, Fail, Blocked, or Skipped to every numbered case. Do not
describe a cycle as complete or green while advanced signer, concurrency, partial-payment, refund,
responsive, or second-browser cases remain unclassified or Blocked.

### 0.5 Evidence bundle

For each case retain only what is needed:

- UTC start/end time, case ID, build/version, tester, browser, account role, and fixture aliases;
- screenshot or concise DOM/text capture for visual assertions;
- URL, method, status, duration, redacted request body, and response body for HTTP assertions;
- console errors and failed network requests for plugin pages and checkout;
- invoice ID, sender-session ID if visible, txid, and a testnet3 explorer link for **TX** cases;
- settings snapshot before the case, after the case, and after restoration for **RW** cases;

Maintain a per-case result record inside the run's evidence bundle in the run owner's chosen format;
this plan deliberately does not prescribe or link a separate template. Every one of the 190 parent
IDs must receive a result for a full cycle. Record defined subcases and their evidence references too,
then derive aggregate tier totals from those case results rather than entering unauditable totals.

The 190 parent IDs are acceptance assertions, not 190 independent scenario executions. `RT-BI1`–
`RT-BI9` are derived roll-ups: retain their IDs and statuses, but derive them from their named source
cases plus the final evidence reconciliation in Section 16. They never authorize another mutation or
broadcast.

### 0.6 Matrix subcases and parent roll-up

Stable parent IDs remain the unit counted by this plan. Use the following suffixes in the per-case
result record and bug reports so a partial matrix failure can be rerun without inventing a new
top-level case:

| Parent | Required subcases |
| --- | --- |
| `RT-X4` | `.a` unknown normal ID; `.b` path traversal; `.c` encoded NUL; `.d` 500-character ID; `.e` SQL-like; `.f` HTML-like |
| `RT-S11` | `.a` fee `0`; `.b` fee `250000` |
| `RT-S12` | `.a` fee `1`; `.b` fee `100000` |
| `RT-A4` | `.a` missing directories; `.b` missing relays |
| `RT-A5` | `.a` empty directories; `.b` empty relays |
| `RT-A6` | `.a` HTTP URL; `.b` relative URL; `.c` null; `.d` mixed valid/invalid array |
| `RT-A7` | `.a` below minimum; `.b` above maximum; `.c` minimum boundary; `.d` maximum boundary |
| `RT-A11` | `.a` provisioned session becomes temporarily unavailable; `.b` settled invoice; `.c` expired invoice; `.d` unknown invoice |
| `RT-A13` | `.a` missing token GET; `.b` malformed/expired token GET; `.c` insufficiently scoped GET; `.d` view-only token with baseline-equivalent `PUT` |
| `RT-C9` | `.a` Lightning tab; `.b` non-New on-chain invoice |
| `RT-C15` | `.a/.b` 375 px light/dark; `.c/.d` 768 px; `.e/.f` 1280 px; `.g/.h` 1920 px |
| `RT-U1` | `.a` overview; `.b` settings; `.c` checkout; `.d` sender sessions; `.e` wallet-send offer, each in light and dark |
| `RT-U2` | `.a` 375 px; `.b` 768 px; `.c` 1280 px; `.d` 1920 px across all plugin surfaces |

Parent status is **Pass** only when every required subcase passes. Roll up with precedence
`Fail > Blocked > Skipped > Pass`; a non-Pass parent cannot be used to claim full coverage.

### 0.7 Repeatable oracles and timeboxes

- Replace adjectives with recorded measurements. **Promptly** means within `PAGE_RENDER_TIMEOUT`;
  state changes use `STATE_PROPAGATION_TIMEOUT`; external confirmation uses
  `TESTNET_CONFIRMATION_TIMEOUT` from Section 1.1.
- A **controlled failure** has the case's allowed status/body, no 5xx or secret/stack trace, no
  persisted mutation, no transaction, and a passing post-case health check.
- **Legible/responsive** means no clipped or overlapping text, no unintended horizontal page scroll,
  every action remains reachable, the full QR is visible, focus is visible, and status meaning does
  not rely on colour alone. Record the effective CSS viewport, not only the requested setting.
- **Matches host conventions** means the plugin control is compared with a named neighbouring BTCPay
  control for label placement, focus, validation, disabled state, and keyboard behavior; record the
  chosen control in evidence.
- If an external event never occurs before its timebox, mark the case **Blocked: external
  dependency**. If the event is observed but the plugin does not react within
  `STATE_PROPAGATION_TIMEOUT`, mark it Fail.

### 0.8 Full-cycle acceptance and release readiness

Keep these as two separate gates:

| Gate | Pass criterion |
| --- | --- |
| **Full-cycle acceptance green** | All 190 numbered parent assertions and all required subcases are Pass. |
| **Release-ready** | Full-cycle acceptance is green, every release-blocking contract decision is resolved, and every accepted waiver is explicit and still valid. |


---

## 1. Environment and fixtures

### 1.1 Run variables

Record these before testing. Values shown for the default deployment are hints, not assertions.

| Variable | Value to record |
| --- | --- |
| `RUN_ID` | UTC date plus tester alias, for example `2026-08-22-alice` |
| `BASE_URL` | |
| Bitcoin network | Must be **testnet3**; record the host's exact label and network configuration |
| BTCPay version | Footer and deployment metadata; do not assume the previous run's value |
| Plugin product version | `/server/plugins` |
| Plugin source revision | Record only if the remote UI/API exposes it; otherwise `unknown` and never inferred from the normalized product version |
| External explorer | A testnet3 explorer, for example `https://mempool.space/testnet/` |
| `MAX_TX_COUNT` | Maximum broadcasts the run permits, including Stop/fallback/refund and the defect allowance of concurrency cases |
| `MAX_PAYMENT_SATS` | Sum of invoice, overpayment, fallback, and refund amounts for every broadcast-capable action; count each possible broadcast once |
| `MAX_TOTAL_FEE_SATS` | Sum of miner fees permitted across all run broadcasts |
| `MIN_CONFIRMED_LIQUIDITY_SATS` | Preflight confirmed balance needed to fund selected inputs, receiver contributions, change, and the fee ceiling; this is a fixture requirement, not consumed budget |
| `PAGE_RENDER_TIMEOUT` | Default 5 s for same-origin shell/controls; external availability may complete later under its own case timeout |
| `STATE_PROPAGATION_TIMEOUT` | Default 2 min after a mempool, confirmation, invoice, or settings event is visibly observed |
| `MEMPOOL_APPEARANCE_TIMEOUT` | Default 2 min after the product exposes a txid or claims broadcast; absence from the explorer/mempool is Fail |
| `TESTNET_CONFIRMATION_TIMEOUT` | Default 60 min for one confirmation; no confirmation by then is Blocked external dependency |
| `INVOICE_EXPIRY_TIMEOUT` | Default 5 min after the invoice's advertised expiry time for terminal UI/API propagation |
| Browser scope | Smoke: Chromium is sufficient. Full cycle: Chromium plus Firefox or WebKit |


`MAX_PAYMENT_SATS` counts the invoice amount again when a fallback, overpayment, or refund can create
a separate broadcast. It excludes change and the receiver's ownership-preserving contribution/cold
self-transfer, which are recorded separately as liquidity movements; every miner fee counts once in
`MAX_TOTAL_FEE_SATS`. Before a full cycle, list every broadcast-capable case, its normal count, its
worst-case defect count, value, and fee ceiling. If the approved totals cannot cover that worksheet,
mark the affected cases Skipped before starting rather than exhausting the budget mid-run.

The following is the baseline full-cycle broadcast worksheet when the documented fixture reuse is
followed. Replace every `V…` with the approved low value and every `F…` with its fee allowance before
testing; do not treat the symbols as permission to spend an unspecified amount.

| Scenario / shared cases | Normal broadcasts | Payment-value budget | Fee budget |
| --- | ---: | ---: | ---: |
| Fresh BIP78 control and checkout-paid lifecycle RT-C10, reused by RT-H3 | 1 | `V-C10-BIP78` | `F-C10-BIP78` |
| Confirmed-input transition RT-F8 | 1 | `V-F8` | `F-F8` |
| Primary BIP77: RT-P2–P6, RT-SP1–SP4, RT-SP6, RT-H3, RT-BI2 | 1 | `V-PRIMARY` | `F-PRIMARY` |
| Second sequential invoice required by RT-P8 | 1 | `V-P8-2` | `F-P8-2` |
| Fresh Standard control RT-P9 reused by RT-H3 and RT-L4 | 1 | `V-STANDARD` | `F-STANDARD` |
| Cold routing RT-P10 | 1 | `V-COLD` | `F-COLD` |
| Two-submit concurrency RT-P11, expected exactly-once result | 1 | `V-P11` | `F-P11` |
| Exact partial/overpayment RT-H8: `P + Q = A + δ` | 2 | `P + (A - P + δ)` | `F-H8-1 + F-H8-2` |
| Refund RT-H9 | 1 | `V-REFUND` | `F-REFUND` |
| Partial fixture shared by RT-Y6 and RT-L7 | 1 | `V-PARTIAL` | `F-PARTIAL` |
| Wallet Manager flow RT-SD17 | 1 | `V-SD17` | `F-SD17` |
| Explicit fallback RT-SP5 | 1 | `V-SP5` | `F-SP5` |
| Receiver-unavailable fallback RT-SP8 | 1 | `V-SP8` | `F-SP8` |
| External-signer completion RT-SP10–SP11 | 1 | `V-SP11` | `F-SP11` |
| Awaiting-signature stop RT-SP12 | 1 | `V-SP12` | `F-SP12` |
| Two independent sessions RT-SP14 | 2 | `V-SP14-A + V-SP14-B` | `F-SP14-A + F-SP14-B` |
| No-new-broadcast guards RT-P7 and RT-SP16 | 0 | defect reserve only | defect reserve only |
| **Normal full-cycle total** | **18** | **sum of the expressions above** | **sum of the expressions above** |

Set `MAX_TX_COUNT` to at least 19 for this worksheet: 18 expected broadcasts plus one duplicate or
fallback defect containment slot shared by RT-P7, RT-P11, RT-SP4, RT-SP8, RT-SP12, and RT-SP16.
Reserve the largest matching payment amount and fee too. The first unexpected extra broadcast ends
the run, so the reserve is not permission to continue through multiple money-safety failures. If
fixtures are not reused exactly as listed, recalculate all three budgets upward before starting.
The three fresh current-run controls for RT-H3 are exactly RT-P9 Standard, RT-C10 BIP78, and the
primary RT-P2 BIP77 transaction. If RT-C10 cannot use a verified BIP78 URI, add a dedicated BIP78
broadcast, raise the normal total to 19, and set `MAX_TX_COUNT` to at least 20 rather than silently
using a historical control.

### 1.2 Isolated stores

Create or obtain stores dedicated to the run. A store that is not provably isolated blocks every
case that changes it.

| Alias | Required state | Used by |
| --- | --- | --- |
| **Store R** | Receiver store: native-segwit hot wallet, Async Payjoin enabled, at least two confirmed testnet3 UTXOs. | receiver checkout, real payjoin, accounting |
| **Store S** | Sender store: a different hot wallet with several confirmed testnet3 UTXOs and permission to create wallet transactions. | sender hot-wallet flow and payer for Store R |
| **Store CFG** | Disposable configuration store with a BTC wallet and at least one confirmed UTXO. No unrelated invoices or users. | settings, degraded directory/relay, API writes |
| **Store N0** | Fresh store with no BTC wallet. | wallet-missing overview and fallback |
| **Store NU** | BTC wallet with no confirmed inputs. If funded, its funding transaction remains unconfirmed until its cases finish. | no-confirmed-input fallback |
| **Store C** | Native-segwit, single-signature (`1-of-1`) watch-only wallet whose account public key/descriptor is imported into BTCPay while the server holds no private key. The tester controls the matching testnet-only external signer and can fully sign both pending PSBTs. Fund it within the run budget. | sender two-round external-signing path |

Use different wallet material for Stores R and S. Otherwise a transaction can look like a payjoin
without proving that two independent participants contributed inputs.

Store C deliberately specifies one signer and a `1-of-1` policy so quorum cannot make the expected
lifecycle ambiguous. The two signatures in RT-SP9–SP11 are two **rounds over different transaction
versions**, not two multisig cosigners:

1. Round one fully signs the original/fallback PSBT. Before it is signed, the sender row and BTCPay
   pending-transaction surface show **Waiting for signature**, and nothing is sent to the directory.
2. Once round one is fully signed, the Payjoin session starts without broadcasting the fallback.
   A receiver proposal is a different PSBT and creates a second **Waiting for signature** request.
3. Round two fully signs that proposal PSBT. It then broadcasts exactly once and the sender reaches
   **Completed (payjoin)**.

A hardware or multisig wallet may be tested as an additional variant, but it cannot replace Store C
unless the run log specifies its script type, quorum, every cosigner, who controls each signer, and
how the PSBT becomes fully signed in both rounds.

### 1.3 Accounts and API credentials

| Alias | Required access |
| --- | --- |
| **Server Admin** | Dedicated tester-controlled account authorized by the server-level `CanModifyServerSettings` policy. It is used only for the read-only `/server/plugins` recovery-surface checks; never operate lifecycle controls. |
| **Owner** | Owns all run stores and may modify settings and create wallet transactions. |
| **Manager** | Store R member who may view store settings but may not modify them or create wallet transactions. |
| **Wallet Manager** | Store S member with permission to create wallet transactions but without `CanModifyStoreSettings`. This deliberately exercises the sender/result-page permission boundary. |
| **Payer** | May create Store S wallet transactions but need not modify store settings. |
| **Guest** | Store R member without `CanViewStoreSettings`; no membership in Store CFG. |
| **API-View** | Greenfield token scoped to Store CFG with view-settings and view-invoices only. |
| **API-Modify** | Greenfield token scoped to Store CFG with view/modify-settings and view-invoices. |
| **API-Receiver** | Greenfield token scoped to Store R with view-settings and view-invoices only. |

Create credentials manually and keep them outside the repository and run log. Use a private or
ephemeral browser profile for unauthenticated and role-isolation checks; signing out of one tab is
not proof that another tab has no session.

### 1.4 Baseline configuration

Capture the actual baseline with the UI and Greenfield API before changing anything. Recommended
baseline for Stores R and CFG:

- Async Payjoin enabled;
- directories `https://payjo.in/` and `https://lets.payjo.in/`;
- relays `https://pj.benalleng.com/`, `https://pj.bobspacebkk.com/`, and
  `https://payjoin.achow101.com/`;
- cold wallet empty;
- maximum fee rate empty;
- built-in BIP78 fallback state recorded separately;
- no attention records and no non-terminal sender sessions unless a case explicitly owns them.

Store the redacted JSON returned by `GET /api/v1/stores/{storeId}/payjoin/settings` as the canonical
remote snapshot for Stores R, S, and CFG. After every configuration section, restore the affected
snapshot with the UI or API, then verify it through both surfaces.

Compare endpoint lists as parsed absolute HTTPS URIs. Host/scheme case, a default port, and a
trailing slash are normalization details unless they change the destination; list membership and
meaning must survive. The deployed UI has been observed to add trailing slashes on a no-edit save,
so raw-byte equality is not a valid persistence criterion.

### 1.5 Declared-scope fixture preflight

Do not declare a full cycle until every row below is verified. A missing row is a fixture failure,
not a product failure, and prevents a green full cycle before mutations begin.

| Prerequisite | Required evidence before execution |
| --- | --- |
| Server-level role | Server Admin signs in and can render `/server/plugins` under `CanModifyServerSettings`; record the role and successful read without clicking any lifecycle control. |
| Store C topology | Record `1-of-1`, native-segwit watch-only import, external signer custody, one small confirmed UTXO, and a dry verification that the signer recognizes Store C's derivation without signing or broadcasting a test payment. |
| H3 controls and budget | The worksheet names RT-P9 Standard, RT-C10 BIP78, and RT-P2 BIP77, with enough approved value and fees for all three fresh controls. |
| Timing-sensitive full-cycle cases | If RT-SP2/RT-SP4 are in scope, prepare the sender page, sessions page, wallet/outpoint view, explorer/mempool observation, and duplicate-submit tab before the first POST. Capture synchronized UTC timestamps. If no non-terminal sample can be observed, the case is Blocked rather than inferred from a terminal result. |


The measured **read-only smoke** performs no setup mutation, so it additionally requires these
pre-existing, tester-owned fixtures:

| Alias | Required state and permitted observation |
| --- | --- |
| `RO-CHECKOUT` | A payable Store R invoice whose Async checkout is already armed, with its anonymous checkout URL and stable BIP21 recorded. Observe only; do not pay, expire, refresh into a mutation, or create a replacement during the smoke. |
| `RO-WALLET-OFFER` | A valid testnet3 BIP77 BIP21, normally from `RO-CHECKOUT`, that can be loaded into Store S's wallet-send page to render the injected offer without final submission. |
| `RO-SENDER-HISTORY` | At least one run-owned terminal sender row suitable for table/status/privacy checks. |
| `RO-SIGNING` | A run-owned Store C first-round pending transaction whose expiry is later than the smoke, used only to render and focus the signing link required by RT-U6. Do not sign, cancel, Stop, or broadcast it during the smoke. |

Creating or arming any of these is **RW fixture preparation outside the measured RO scope**. Record
that preparation, run the reset checkpoint, and start the read-only smoke only after the aliases are
stable. If a required fixture is absent, narrow the declared smoke scope or mark its dependent case
Blocked; never manufacture it while claiming an RO-only run.

### 1.6 Reset checkpoint

Run this checkpoint after settings/fallback, receiver-payment, lifecycle, and sender-payment
sections. If any row fails, subsequent dependent cases are **Blocked** until it is repaired.

| Check | Expected remote evidence |
| --- | --- |
| Store R / S / CFG settings | Fresh UI load and Greenfield `GET` exactly match the captured baseline. |
| Store R / S balances | Enough confirmed testnet3 coins remain within the approved budget. |
| Store N0 | Still has no BTC wallet. |
| Store NU | Still has no confirmed input; recreate it if a block changed the fixture. |
| Sender sessions | No unexpected `Pending` or `Waiting for signature` row. Do not click Stop merely to make the table tidy without accounting for the broadcast. |
| Pending transactions | Only those owned by an active sender case. |
| Invoices | Every run-created invoice is labelled in the run log; terminal state agrees with its case. |
| Attention rows | No unexplained row. Preserve a screenshot and invoice reference for the run log rather than mutating it casually. |
| Server health | Anonymous home/login and authenticated plugin overview answer; no new browser console error. |
| Transaction budget | Every broadcast txid, payment value, fee, and ownership-preserving receiver/cold movement is recorded; `MAX_TX_COUNT`, `MAX_PAYMENT_SATS`, and `MAX_TOTAL_FEE_SATS` remain within approval. |

### 1.7 Sanity gate

- **RT-E1 [RO]** `BASE_URL` answers over HTTPS without a certificate warning; HTTP, if exposed,
  redirects to the same HTTPS origin and does not serve an authenticated page in cleartext.
- **RT-E2 [RO]** The footer and wallet address format identify Bitcoin testnet, and an explorer link
  points to testnet3. Stop if a payment address starts with a mainnet-only prefix or an explorer
  opens a mainnet transaction page.
- **RT-E3 [RO]** As Server Admin, `/server/plugins` renders under the server-level
  `CanModifyServerSettings` policy and lists **Async Payjoin**, its actual version, description, and
  `BTCPayServer: >= 2.4.0`. Record whether the host exposes Disable and/or Uninstall, but click
  neither. For a full cycle, inability to verify this permission fails fixture preflight before any
  mutation; it is not deferred until RT-N13.
- **RT-E4 [RO]** Record the product version and any source revision the remote deployment itself
  exposes. If it exposes no commit, record `source revision unknown`; do not infer one from a
  matching semantic or assembly version.
- **RT-E5 [RO]** The selected Store R sidebar shows **Async Payjoin** under Plugins and the store
  settings navigation shows **Async Payjoin** and **Send Async Payjoin** for Owner.
- **RT-E6 [RO]** Store R's wallet UI shows at least two confirmed native-segwit UTXOs; record their
  outpoints without exposing wallet secrets.
- **RT-E7 [RO]** Store S has confirmed spendable coins from a different wallet and no outpoint is
  shared with Store R.
- **RT-E8 [RO]** Overview, settings, sender-sessions page, invoices, and wallet send page render in
  one authenticated session with no plugin console error or failed same-origin request.
- **RT-E9 [RW]** The baseline settings snapshot is captured through UI and API for Stores R, S, and
  CFG, and the two surfaces agree before testing starts.
- **RT-E10 [RO]** An anonymous browser can reach the public landing/login surface while the
  authenticated browser remains healthy; no store name, ID, balance, or configuration is exposed
  before authentication.

---

## 2. Remote exception containment and forbidden cheat surfaces

Run this early. These cases deliberately remain low volume. After each response, perform one
read-only health check; never loop a failing input.

- **RT-X1 [RW]** `POST /plugins/payjoin/run-test-payment` with JSON containing a high-entropy,
  guaranteed-unknown `RUN_ID` invoice ID is absent on remote testnet3: 404 (or the host's equivalent
  route-not-found response), no JSON application response, payment, or session. Never use a real
  invoice: if the cheat guard is broken this anonymous route can broadcast. Any non-404 application
  response fails the deployment-surface assertion; stop after the health check.
- **RT-X2 [RW]** From Owner's authenticated session, submit `POST
  /plugins/payjoin/seed-attention-record` with a valid same-origin antiforgery token and a different
  guaranteed-unknown invoice ID. Expected: route-not-found, no attention row, and no store-state
  change. A route-present "invoice not found" application response is a safe Fail that proves the
  cheat surface leaked. Do not use a real invoice or weaken the deployment to cheat mode.
- **RT-X3 [RO]** `/docs` and the OpenAPI document expose neither cheat route nor their request and
  response models as callable public operations.
- **RT-X4 [RO]** Send one request each to the anonymous payment-url route using an unknown normal ID,
  an encoded path traversal string, an encoded NUL, a 500-character ID, SQL-like text, and
  HTML-like text. Every response is 400/404-class, contains no stack trace or store data, and the
  server remains healthy.
- **RT-X5 [RW]** On Store CFG, a settings POST without a valid antiforgery token is rejected before
  persistence. A fresh UI load and API `GET` still equal the pre-case snapshot.
- **RT-X6 [RW]** An unauthenticated POST to `/stores/{storeId}/payjoin`,
  `/plugins/payjoin/bridges/{invoiceId}/retry`,
  `/stores/{storeId}/payjoin/send/from-wallet`, or
  `/stores/{storeId}/payjoin/send/{senderSessionId}/cancel` redirects to login or returns 401/403;
  it never executes the action. Use guaranteed-unknown store/session/bridge IDs and bodies that are
  invalid even without authorization so the negative check cannot mutate a real fixture.
- **RT-X7 [RO]** Negative responses contain no exception details, connection strings, filesystem
  paths, access tokens, wallet descriptors, or unrelated identifiers.
- **RT-X8 [RO]** After the negative set, a fresh anonymous health request plus authenticated overview,
  settings, and sender-page requests all succeed and the plugin navigation remains present. Report
  this strictly as black-box health; it does not prove internal queue or log state.

---

## 3. Navigation and access control

- **RT-N1 [RO]** Sidebar **Plugins → Async Payjoin** opens `/plugins/payjoin` with heading
  **Async Payjoin** for the currently selected store.
- **RT-N2 [RO]** Store settings navigation **Async Payjoin** opens
  `/stores/{storeId}/payjoin`; **Send Async Payjoin** opens
  `/stores/{storeId}/payjoin/send` for Owner.
- **RT-N3 [RO]** Overview **Open Settings** targets the store named in the overview card, not a
  different store that happens to be present in another tab.
- **RT-N4 [RO]** Visiting `/stores/{id}` changes the selected store used by `/plugins/payjoin`; the
  overview header and Open Settings target change together.
- **RT-N5 [RO]** With no selected store, `/plugins/payjoin` redirects and reports
  **You need to select a store first.** It does not choose an arbitrary store.
- **RT-N6 [RO]** Manager can view the overview and Store R settings navigation, but sees no Save
  button and no **Send Async Payjoin** navigation item. Retry visibility is not inferred from an
  empty attention table and remains outside this case without a natural row.
- **RT-N7 [RO]** Guest sees neither Async Payjoin navigation item. Direct GET of Store R settings
  returns 403 naming the missing view-settings permission and reveals no configuration values.
- **RT-N8 [RO]** Guest direct GET of Store CFG, where Guest has no membership, returns 403/404
  according to host policy and reveals neither existence-sensitive configuration nor store data.
- **RT-N9 [RW]** From Manager's already-rendered settings form, submit a recognizable changed value
  with the form's own antiforgery token. Only a 403 naming the missing modify-settings permission is
  conclusive; 400/antiforgery is Blocked. Fresh UI and API reads prove no value changed.
- **RT-N10 [RO]** Manager direct GET of `/stores/{storeR}/payjoin/send` is forbidden because the
  sessions page requires modify-settings permission.
- **RT-N11 [RW]** A user who can modify Store S wallet transactions but cannot view another store's
  settings cannot use sender start/cancel routes against that other store. Submit only a deliberately
  invalid BIP21 and guaranteed-unknown session ID so a broken permission guard cannot broadcast. The
  response contains no session or wallet details.
- **RT-N12 [RO]** Signed-out requests to overview, settings, and sender sessions return login/401/403
  as appropriate and preserve a safe return URL without exposing the prior user's content.
- **RT-N13 [RO]** Server Admin can render `/server/plugins` under `CanModifyServerSettings` and sees
  the installed-plugin recovery surface; a separate authenticated store-only account cannot reach
  that server-level surface. Verify labels, version, and the authorization boundary, but do not click
  Disable, Uninstall, or Upload. Missing Server Admin credentials are a preflight fixture failure and
  make the case Blocked; never request operator assistance during execution.

---

## 4. Overview page (`/plugins/payjoin`)

- **RT-O1 [RO]** Header card shows the selected store name and ID; changing the selected store
  updates both. An unnamed disposable store renders **Unnamed Store** without layout breakage.
- **RT-O2 [RO]** Enabled Store R with configured endpoints and confirmed inputs shows green
  **Basic prerequisites present** and receiver inputs **Present**.
- **RT-O3 [RO]** Store NU shows amber **Additional requirements pending**, confirmed inputs absent,
  and names the effective fallback without claiming endpoint reachability.
- **RT-O4 [RO]** Store N0 has no wallet but overview and settings still render; status explains why
  Async Payjoin is unavailable rather than throwing.
- **RT-O5 [RO]** Default checkout mode agrees with enabled state and built-in BIP78 availability:
  Async Payjoin when active, Payjoin v1 when disabled with v1 effective, otherwise Standard Bitcoin.
- **RT-O6 [RO]** Fallback target is one step below the default mode and disappears when the default
  is already Standard Bitcoin.
- **RT-O7 [RO]** Configuration lists every directory and relay once, cold-wallet state, default
  checkout mode, and fallback target. Long URLs wrap without horizontal page scroll.
- **RT-O8 [RO]** Basic-prerequisite rows agree with the status badge and configuration card for
  directory, relay, network/wallet, and confirmed inputs.
- **RT-O9 [RW]** Disabling Async Payjoin on Store CFG produces a neutral **Disabled** state before
  any prerequisite result and names the actual checkout fallback. Restore the baseline afterward.
- **RT-O10 [RO]** Loading overview makes no client-side browser request to a directory or relay host;
  browser traffic stays same-origin/static and text explicitly says reachability is not checked.
  This cannot prove the absence of server-to-server traffic and must not be reported as such.
- **RT-O11 [RO]** With no natural failed/armed-expired bridge, the attention section is absent rather
  than an empty card.

`RT-O12`–`RT-O14` are intentionally reserved and are not cases. During remote adaptation, the old
O12 privacy assertion was consolidated into RT-O10, while the old O13/O14 attention fixtures required
cheat/internal access. The IDs are not reused so historical reports and the local-plan mapping remain
unambiguous.

- **RT-O15 [RO]** Status badges remain distinguishable in light and dark themes without relying on
  colour alone.
- **RT-O16 [RO]** Overview emits no plugin console error, failed same-origin request, mixed content,
  or browser security warning.

---

## 5. Store settings (`/stores/{storeId}/payjoin`)

Run every modifying case on Store CFG. Capture the pre-case snapshot, submit once, verify with a
fresh UI load plus Greenfield `GET`, and restore before the next case unless cases explicitly form a
pair.

- **RT-S1 [RO]** Page loads the current values of enabled, directories, relays, cold wallet, and max
  fee rate, and they agree with API `GET`.
- **RT-S2 [RW]** Save with no edits shows success and produces no semantic drift in a fresh UI/API
  read. Canonical trailing slashes are allowed when parsed endpoints remain equivalent.
- **RT-S3 [RW]** Toggle Async Payjoin off and on as a pair. Each value persists and overview/checkout
  changes accordingly; finish enabled.
- **RT-S4 [RW]** A non-HTTPS directory line reports its line number and HTTPS-only error; nothing is
  persisted.
- **RT-S5 [RW]** A non-HTTPS or relative relay line receives the equivalent field-specific error and
  leaves settings unchanged.
- **RT-S6 [RW]** Several invalid directory and relay lines report every invalid line in one response,
  not only the first.
- **RT-S7 [RW]** Empty and whitespace-only directory fields each report at least one directory is
  required and preserve the previous persisted list.
- **RT-S8 [RW]** Empty and whitespace-only relay fields each report at least one OHTTP relay is
  required and preserve the previous persisted list.
- **RT-S9 [RW]** Duplicate/case-only URLs, blank lines, and surrounding whitespace normalize to one
  canonical entry per URL after save.
- **RT-S10 [RO]** Max fee rate `0` is blocked by the HTML control (`min=1`). Record the control
  attributes; do not treat client validation as server validation.
- **RT-S11 [RW]** Bypass only the client constraint on the disposable form and submit `0` and
  `250000`; server validation rejects both and persists neither.
- **RT-S12 [RW]** Boundary values `1` and `100000` each persist and round-trip through UI and API.
- **RT-S13 [RW]** Clearing max fee rate persists JSON `null` and the help text continues to describe
  automatic network estimation. Before Save, inspect the actual control value and require it to be
  empty; if automation `fill("")` leaves the old value, use Select All + Backspace and re-check.
- **RT-S14 [RW]** Cold-wallet garbage reports **Invalid wallet format**, does not leak parser internals
  beyond a safe validation message, and persists nothing.
- **RT-S15 [RW]** A fresh testnet-only tpub or public descriptor normalizes and persists; overview
  changes to **Configured**. Never paste private key material.
- **RT-S16 [RW]** A validation error preserves the tester's submitted text in the rendered form while
  the persisted API state remains the pre-case snapshot.
- **RT-S17 [RW]** Bad directory, relay, fee, and cold-wallet values in one submit display all relevant
  field errors together and do not partially save valid fields.
- **RT-S18 [RO]** Checkbox, labels, help text, error placement, keyboard toggle, and focus order match
  the named neighbouring BTCPay store-settings controls recorded in evidence and are usable without
  a mouse, using the Section 0.7 convention checklist.
- **RT-S19 [RW]** Persistence matrix: save distinctive valid values in all five fields, close the tab,
  open a fresh authenticated session, and compare the form and API with the submitted normalized
  values. Restore the baseline through the API and verify through the UI.
- **RT-S20 [RW]** UI values written by API `PUT` appear after a fresh page load, and a UI save appears
  in API `GET`; there is no separate or stale settings copy.

---

## 6. Receiver checkout — Async Payjoin available

Create a fresh low-value Store R invoice for this section. Use an anonymous private browser for the
checkout and a separate authenticated browser for administration.

- **RT-C1 [RW]** New on-chain invoice renders the **Async Payjoin / Standard Bitcoin** mode switch
  and shield indicator without requiring an authenticated checkout user.
- **RT-C2 [RW]** Async Payjoin is initially selected. Its BIP21 is a testnet address with the exact
  amount, `pjos=0`, and an encoded `pj=` URL containing BIP77 session parameters.
- **RT-C3 [RW]** Clipboard copy and **Pay in wallet** href are byte-for-byte equal. The QR payload is
  semantically the same BIP21 after parsing: scheme/address and Bech32 payload casing may differ,
  while amount, `pjos`, decoded `pj=` value, and every query key/value must agree; none points to
  mainnet. Do not compare the uppercased QR `data-qr-value` as a raw string.
- **RT-C4 [RW]** **Pay in wallet** title identifies an Async Payjoin-capable BIP21.
- **RT-C5 [RW]** Switching to Standard Bitcoin changes QR, clipboard, and href together, removes the
  Async indicator, and the plain/v1 URI contains no stale BIP77 endpoint.
- **RT-C6 [RW]** Displayed amount equals the amount in both BIP21 variants and the invoice API value.
- **RT-C7 [RW]** Reloading the checkout reuses the same receiver session: Async BIP21 remains stable
  and no duplicate visible behavior appears. RT-BI1 records this black-box invariant; database
  uniqueness remains out of scope.
- **RT-C8 [RW]** Browser network capture shows no overlapping anonymous payment-url requests. An
  `Active` response triggers checkout-model refresh and polling stops once `payjoinPaymentUrl`
  appears; `Unavailable` with `retryable:false` stops polling; a retryable response follows the
  bounded exponential backoff (2 s base, 30 s cap) rather than a tight loop. Async-disabled checkout
  makes no such request.
- **RT-C9 [RW]** Lightning tab and non-New invoice states contain no Async Payjoin controls.
- **RT-C10 [TX]** On a fresh invoice, first verify that the non-BIP77 checkout URI contains a BIP78
  `pj=` endpoint without BIP77 fragment/session parameters, then pay through that BIP78 path. After
  the invoice becomes paid, checkout refresh removes payment controls and does not arm another
  receiver session. Preserve this fresh transaction as RT-H3's BIP78 control. If the URI is plain
  rather than BIP78, do not relabel it: provision and budget a separate fresh BIP78 control.
- **RT-C11 [RW]** After an unpaid invoice expires, checkout refresh removes Async Payjoin controls and
  the anonymous endpoint reports a non-payable result.
- **RT-C12 [RO]** Checkout console contains no plugin error, unhandled promise rejection, failed
  same-origin request, mixed content, or secret-bearing log.
- **RT-C13 [RO]** Mode switch is keyboard reachable, has coherent roles/states, announces its active
  option, and does not trap focus. The checkout Copy control has a non-empty accessible name from
  text, `aria-label`, or `title`.
- **RT-C14 [RO]** All plugin-injected strings participate in the host localization mechanism. In a
  non-English locale, record plugin strings separately from host strings; untranslated **Async
  Payjoin**, **Standard Bitcoin**, or **Bitcoin payment mode** amid translated host UI is a product
  localization gap, not proof that the locale switch failed.
- **RT-C15 [RO]** At 375, 768, 1280, and 1920 CSS pixels in light and dark themes, buttons remain
  usable, QR stays complete, long URLs do not escape, and there is no horizontal page scroll.

---

## 7. Receiver checkout fallbacks and dependency degradation

Use Store CFG, take a baseline snapshot, create a new invoice per case, and restore immediately.
Every degradation case makes one checkout request only and restores the baseline immediately. The
result proves customer-visible black-box fallback behavior, not internal exception containment.

- **RT-F1 [RW]** Async Payjoin disabled: checkout offers only the host's effective BIP78/plain
  payment, with no Async switch or BIP77 request.
- **RT-F2 [RO]** Store N0 with no BTC wallet still renders settings/overview; a checkout cannot
  advertise Async Payjoin. Any attempted checkout follows the host's wallet-not-configured/setup
  path with no plugin control, plugin 5xx, stack trace, or console exception; record the exact host
  status/message rather than "fails gracefully".
- **RT-F3 [RW]** Store NU with only unconfirmed coins shows amber prerequisites and checkout falls
  back without attempting to contribute an unconfirmed merchant input.
- **RT-F4 [RW]** Configure only `https://payjoin-directory-unreachable.invalid/`. Checkout shell
  renders its primary QR/href within `PAGE_RENDER_TIMEOUT`; the first payment-url response is
  `Unavailable`; the customer keeps the BIP78/plain fallback; server and plugin remain visibly
  healthy. Close the checkout before its 2-second retry timer fires, restore the baseline, and stop
  the run on any 5xx.
- **RT-F5 [RW]** Configure one unreachable and one known-good directory. A new invoice reaches the
  healthy directory without editing the store again, proving that one bad configured directory does
  not make the whole set unavailable. Do not assert which directory is tried first because route
  order is deliberately selected at runtime.
- **RT-F6 [RW]** Configure one unreachable relay among healthy relays. Async checkout remains
  available, so merely having one bad configured relay does not force customer-visible fallback.
  Repeat once with a second invoice and record timing; do not infer which relay was attempted,
  internal relay parking, or server-side request counts from browser traffic.
- **RT-F7 [RW]** Configure only one unreachable relay. The first payment-url response is
  `Unavailable`, checkout retains BIP78/plain, and the public/authenticated health gate still passes.
  Close the checkout before automatic retry and restore immediately.
- **RT-F8 [TX]** Spend Store R's last confirmed receiver contribution input in a run-owned payment.
  Overview moves **Present → Pending**, and a newly created invoice falls back to BIP78/plain while
  only unconfirmed receiver coins exist. After that transaction confirms, overview returns to
  **Present** and a fresh invoice can advertise BIP77 again. Record separately that the deployed
  BIP78 receiver may use an unconfirmed contribution whereas BIP77 deliberately requires a confirmed
  input; do not treat those two prerequisite policies as identical. If no confirmation arrives by
  `TESTNET_CONFIRMATION_TIMEOUT`, mark Blocked external dependency; after an observed confirmation,
  failure to recover within `STATE_PROPAGATION_TIMEOUT` is Fail.

---

## 8. Receiver end-to-end payment with real testnet3 coins

This section replaces the local plan's cheat-mode payer with Store S or another explicitly approved
BIP77-capable testnet3 wallet. Every case is bounded by `MAX_TX_COUNT`, `MAX_PAYMENT_SATS`, and
`MAX_TOTAL_FEE_SATS`.
Before broadcasting, write the expected invoice amount, Store R receiver outpoints, Store S sender
outpoints, and estimated fee into the run log.

- **RT-P1 [RW]** Create a fresh Store R on-chain invoice for the approved low value. Checkout
  advertises Async Payjoin, and both Store R and Store S still have distinct confirmed inputs before
  payment.
- **RT-P2 [TX]** Pay RT-P1 through Store S's **Send as async payjoin** path or another named BIP77
  sender. The sender creates one visible session and the receiver invoice progresses without a fake
  payment control.
- **RT-P3 [TX]** The invoice reaches Settled/Paid and exposes one settlement txid; Store S's sender
  session reaches **Completed (payjoin)** with the same txid. Once either surface claims broadcast,
  the txid must appear in the explorer/mempool within `MEMPOOL_APPEARANCE_TIMEOUT`.
- **RT-P4 [TX]** Decode the transaction in the external testnet3 explorer. It has at least two inputs,
  including one pre-recorded Store S input and one pre-recorded Store R receiver-contribution input.
  Merely seeing two inputs without ownership evidence is inconclusive.
- **RT-P5 [TX]** Identify the receiver-owned output through wallet/descriptor ownership evidence,
  not by requiring the original invoice address: output substitution may use another receiver-owned
  hot-wallet address even without cold routing. The receiver output equals the receiver contributed
  input plus the invoice amount, and the invoice's credited value equals the invoice amount.
- **RT-P6 [TX]** Before arming the invoice, configure and record an explicit Store R cap `C` that is
  compatible with the approved fee budget. Decode the final transaction and record `fee_sats`,
  `vsize`, and `feerate = fee_sats / vsize`; require positive fee/vsize, finite arithmetic,
  `feerate <= C`, `fee_sats <=` the per-transaction fee allowance, and no budget overrun. Restore the
  baseline cap afterward. Do not use the subjective phrase "sane for testnet3" as the oracle.
- **RT-P7 [TX]** A second attempt to pay the already settled invoice cannot create a new payable
  Async URI, sender session, or second settlement transaction.
- **RT-P8 [TX]** Two distinct invoices paid sequentially produce distinct receiver/sender sessions and
  distinct txids; neither invoice displays the other's amount, address, or transaction.
- **RT-P9 [TX]** Select Standard Bitcoin on a fresh invoice and pay the plain/v1 URI. The invoice
  settles normally, no receiver payjoin claim is shown, and the result is not labelled Async Payjoin.
- **RT-P10 [TX]** With a fresh testnet-only cold-wallet public descriptor configured on Store CFG,
  complete one Async Payjoin and verify the substituted settlement output belongs to that descriptor.
  Its value must equal receiver contributed input plus invoice amount; the hot wallet delta is the
  negative contributed input and the cold wallet delta is the full substituted output. Restore the
  empty cold-wallet baseline immediately afterward.
- **RT-P11 [TX]** On one fresh low-value invoice, issue exactly two near-simultaneous sender submissions
  only if the remaining transaction budget can absorb the defect. Expected: one live sender session
  and at most one broadcast payment. More than two submissions is out of scope.

Run the reset checkpoint before continuing.

---

## 9. Receiver accounting, history, labels, and receipt

Use the successful RT-P2 transaction as the primary fixture so every surface refers to one known
invoice and txid.

- **RT-H1 [RO]** Invoice detail reports Settled, paid value equals amount due, and exactly one
  on-chain payment row refers to the RT-P2 txid.
- **RT-H2 [RO]** The payment row identifies the payjoin nature consistently with the host/plugin
  vocabulary and does not mislabel a plain RT-P9 payment as Async Payjoin.
- **RT-H3 [RO]** Payment row **Index** resolves to a real key path or documented cold-wallet result,
  not an unexplained **Unknown** for an ordinary hot-wallet receiver output. Compare exactly three
  fresh current-run controls: RT-P9 is Standard, RT-C10 is BIP78, and RT-P2 is BIP77. Standard and
  BIP78 should resolve an index; BIP77 resolving **Unknown** is a failing differential result, not an acceptable Payjoin label. Historical payments cannot fill a missing leg.
- **RT-H4 [RO]** Store R wallet history labels the transaction **Async Payjoin** plus the invoice;
  label filtering finds it, while RT-P9 retains the host's plain/v1 classification.
- **RT-H5 [RO]** Compute each tracked wallet's exact transaction delta as
  `sum(owned outputs) - sum(spent owned inputs)`. Without cold routing, the Store R UI delta equals
  that hot-wallet value and invoice credit equals the invoice amount. With cold routing, the hot
  delta is exactly the negative contributed input and the cold delta is `contributed input + invoice
  amount`. Record sender/receiver change and miner fee separately; do not use "approximately invoice
  amount" as the oracle.
- **RT-H6 [RO]** The invoice's visible Events table contains no plugin exception/error and its event
  order agrees with the final paid state. This does not substitute for unavailable server logs.
- **RT-H7 [RO]** Receipt page renders, identifies the paid invoice and amount, and links the same
  settlement transaction without exposing sender-session internals.
- **RT-H8 [TX]** Before broadcasting, choose and record integer satoshi values `A`, `P`, and `δ` with
  `0 < P < A` and `δ >= 1`; set the invoice amount to `A` and the second payment to
  `Q = A - P + δ`. Approve two broadcasts, payment value `P + Q = A + δ`, and both miner fees. After
  payment one, UI plus Greenfield `GET` show `paidAmount = P`, remaining due `A - P`, a non-terminal
  invoice, and `additionalStatus = PaidPartial`; convert the API amount to sats before comparing it
  with `P`. After payment two and the store's required confirmations, they show `paidAmount`
  equivalent to `A + δ` sats, `status = Settled`,
  `additionalStatus = PaidOver`, overpaid amount `δ`, and both recorded txids. No Failed attention
  row may appear merely because paid value exceeded the due value.
- **RT-H9 [TX]** Refund a run-owned Async-Payjoin-settled invoice through the normal BTCPay refund
  flow. The refund uses testnet3, is accounted against the correct invoice, and does not mutate the
  original settlement record or sender session. Record the refund txid separately.
- **RT-H10 [RO]** For RT-P10, receipt and invoice UI remain internally consistent even if their
  displayed destination is the original invoice address while the chain output is the cold-wallet
  substitute. Record this known product distinction rather than misreporting the transaction.

---

## 10. Greenfield API

Use Store CFG and tokens scoped exactly as described in Fixtures. Run write cases as a single group:
capture baseline, perform negative requests once each, perform one valid matrix write, and restore.
Mask the `Authorization` header in every artifact.

Vary one invalid dimension per request. The API may validate fail-fast and return only the first
error; aggregated errors are required only where a case explicitly tests multiple bad lines inside
the same field. After each rejection, prove the entire persisted object is unchanged.

Surfaces under test are `GET/PUT /api/v1/stores/{storeId}/payjoin/settings` and
`GET /api/v1/stores/{storeId}/invoices/{invoiceId}/payjoin/payment-url`.

- **RT-A1 [RO]** `GET /api/v1/stores/{storeId}/payjoin/settings` with API-View returns 200 JSON in
  camelCase and matches a fresh settings page.
- **RT-A2 [RO]** Response carries `payjoinV2Enabled`, every directory and relay, cold-wallet public
  value/null, and `maxFeeRateSatPerVb`; no secret or unrelated store field is present.
- **RT-A3 [RW]** API-Modify `PUT` with a valid distinctive body returns the normalized saved body;
  subsequent GET and UI agree. Restore and verify the baseline.
- **RT-A4 [RW]** Missing `directoryUrls` and missing `ohttpRelayUrls` each return 422 field-required
  validation and leave the baseline unchanged.
- **RT-A5 [RW]** Empty directory or relay arrays return 422 **At least one … is required** and do not
  partially save the other fields.
- **RT-A6 [RW]** `http://`, relative, null, and mixed valid/invalid URL arrays return 422 listing each
  offending value; only absolute HTTPS URLs are accepted.
- **RT-A7 [RW]** Fee values below 1 or above 100000 return 422 with wording consistent with the UI;
  boundary values round-trip successfully when sent in a valid body.
- **RT-A8 [RW]** Invalid `coldWalletDerivationScheme` returns 422 **Invalid wallet format** without
  persisting any part of the request.
- **RT-A9 [RO]** API-View and API-Modify requests to a store outside token scope are 403; an invented
  store ID follows host policy without leaking whether a real inaccessible store exists.
- **RT-A10 [RW]** `GET /api/v1/stores/{storeR}/invoices/{newInvoice}/payjoin/payment-url` with a
  properly scoped API-Receiver token returns 200 and the same active BIP21 used by checkout. JSON
  exposes the granular `bip21`, `status`, `unavailableReason`, and `retryable` contract; Active has
  no unavailable reason and is not retryable.
- **RT-A11 [RW]** Exercise only states with a settled contract: an already-provisioned BIP77 session
  that later loses confirmed inputs returns 200 with the documented plain fallback BIP21, granular
  non-Active status/reason, and `retryable:true`; settled/expired invoices return 404
  `payment-url-not-payable`; an unknown invoice returns 404 `invoice-not-found`. 
- **RT-A12 [RO]** With API-View authorized for Store CFG, place an invoice ID from Store R under
  Store CFG's route. No invoice content may cross the store boundary. Accept 403 or 404 according to
  whether host authorization or controller matching runs first; require the same non-enumerating
  outcome for a real cross-store ID and an invented ID. RT-A9 separately proves a route store outside
  token scope is forbidden.
- **RT-A13 [RW]** Missing, malformed, expired, view-only-for-a-write, and insufficiently scoped tokens
  return 401/403 as appropriate; no HTML login page is mistaken for a JSON API success. For the
  write subcase, submit a valid body semantically identical to the captured baseline so a broken
  authorization guard still cannot change configuration; prove the blob-equivalent API state after.
- **RT-A14 [RO]** `/docs` contains a PayJoin section whose routes, verbs, permissions, schemas,
  nullable fields, and validation limits agree with real responses.
- **RT-A15 [RO]** Swagger/OpenAPI does not publish `run-test-payment` or `seed-attention-record`, and
  remote calls to those paths remain absent as proved by RT-X1/RT-X2.

---

## 11. Anonymous checkout endpoint

Route: `GET /plugins/payjoin/invoices/{invoiceId}/payment-url`. Use invoice IDs created for this run;
never enumerate other users' invoices.

- **RT-Y1 [RW]** In a fresh anonymous browser, a payable New Store R invoice returns 200 without
  cookies or authentication with `status:"Active"` and `retryable:false`. Checkout then refreshes
  its server model and displays the Async BIP21; the anonymous response itself contains no BIP21.
- **RT-Y2 [RO]** Unknown, settled, and expired invoice IDs return 404-class responses with no store
  name, amount, configuration, or exception details beyond what the payable resource requires.
- **RT-Y3 [RW]** JSON contains exactly the camelCase fields `status` and `retryable`. Active maps to
  `"Active"`; every internal unavailable reason collapses to `"Unavailable"`, while the boolean
  controls whether checkout retries. Content type and character encoding are correct.
- **RT-Y4 [RW]** Exactly two simultaneous first-time GETs for one fresh invoice both complete with
  the same `{status:"Active",retryable:false}` result; checkout and authenticated Greenfield GET
  converge on one usable BIP21 without customer-visible duplicate sessions. Higher concurrency and
  load/rate-limit testing are out of scope.
- **RT-Y5 [RO]** Response reveals neither BIP21 nor invoice/store internals: no receiver address,
  amount, BIP77 endpoint, settings, balance, xpub/descriptor, relay list, user identity, other invoice
  ID, unavailable reason, or stack trace. Only `status` and `retryable` are present.
- **RT-Y6 [TX]** After a controlled partial payment changes the remaining amount, the endpoint either
  returns `Unavailable` or becomes Active only after checkout/Greenfield expose a newly valid BIP21
  whose amount equals the amount still payable. It never reports Active while checkout serves the
  original stale Async amount.
- **RT-Y7 [RO]** Anonymous requests do not set an authenticated session cookie, redirect to an
  unrelated origin, or allow dynamic availability status to be served stale from a public shared
  cache contrary to host policy. Record the actual cache headers.

---

## 12. Receiver session lifecycle — black-box view

- **RT-L1 [RW]** First checkout load arms one stable Async BIP21; reloads and Greenfield GET continue
  to return that BIP21, while the anonymous route consistently returns Active without exposing the
  URI. Addresses and endpoints do not rotate customer-visibly.
- **RT-L2 [RW]** An unpaid short-lived invoice expires, loses Async controls, and returns non-payable
  from both authenticated API and anonymous endpoint.
- **RT-L3 [RO]** Ordinary unpaid expiration without an expected final transaction creates no visible
  attention row on overview.
- **RT-L4 [TX]** Paying a fresh invoice through Standard Bitcoin retires its Async offer on refresh
  and does not create a Failed attention row.
- **RT-L5 [RW]** Arm an invoice, disable Async Payjoin on Store CFG, and reload checkout in a new
  anonymous session. New invoices stop advertising Async immediately; record the existing armed
  invoice's observed behavior without claiming unavailable internal cleanup. Restore baseline.
- **RT-L6 [RW]** Stores R and CFG maintain isolated settings and invoice payment URLs. Changing CFG
  never changes R's existing/new BIP21, overview, or sender session list.
- **RT-L7 [TX]** After a partial payment makes the armed session amount differ from amount due,
  checkout drops or replaces the stale Async URI and retains a correct standard-payment path.
- **RT-L8 [RO]** Back/forward navigation, a fresh anonymous profile, and a hard reload never resurrect
  an Async offer for a settled or expired invoice from browser cache.

Run the reset checkpoint before sender testing.

---

## 13. Sender UI, permissions, and input validation

These cases cover the sender feature absent from the local plan. Unless a case says **TX**, stop
before clicking the final **Send as async payjoin** submission.

- **RT-SD1 [RO]** Owner sees **Send Async Payjoin** in Store S navigation; its page has heading
  **Async Payjoin Payments**, explanatory text, and a **Send** link to Store S's BTC wallet send page.
- **RT-SD2 [RO]** With no sessions, the page says **No async payjoin payments yet**. With fixtures, it
  shows only Store S sessions ordered newest first.
- **RT-SD3 [RW]** Pasting a plain BIP21 with no `pj=` into the wallet send page never displays the
  async offer or Async submit button.
- **RT-SD4 [RW]** A BIP78-style `pj=` URL without BIP77 fragment/session parameters remains on the
  host's synchronous path and does not display the Async sender offer.
- **RT-SD5 [RW]** A valid testnet BIP77 BIP21 whose `pj=` endpoint contains fragment/session
  parameters displays the explanatory Async offer and **Send as async payjoin** button.
- **RT-SD6 [RW]** When RT-SD5 is active, the plugin clears/hides core's synchronous PayJoin field so
  the same v2 endpoint cannot accidentally enter the v1 path, while preserving its own hidden BIP21
  value for the Async submit.
- **RT-SD7 [RW]** A valid single destination with matching testnet address and amount is accepted by
  client validation and reaches the final confirmation boundary without creating a session yet.
- **RT-SD8 [RW]** A second non-empty destination is rejected with the one-destination explanation
  before a session, pending transaction, or broadcast is created.
- **RT-SD9 [RW]** Empty extra destination rows are ignored and do not turn a valid single payment into
  a multi-destination rejection.
- **RT-SD10 [RW]** Editing destination after BIP21 resolution is rejected as a destination mismatch;
  repasting the link restores the canonical value.
- **RT-SD11 [RW]** Editing amount after BIP21 resolution is rejected as an amount mismatch; no sender
  session appears.
- **RT-SD12 [RW]** **Subtract fees from amount** is rejected because the receiver expects the exact
  BIP21 amount.
- **RT-SD13 [RW]** Labels entered for the valid destination survive the Async sender path and attach
  to the destination/wallet history after a later successful **TX** case.
- **RT-SD14 [RW]** A user without wallet-transaction permission cannot start or stop a session even
  if they can view settings. Use a deliberately invalid BIP21 for start and a guaranteed-unknown
  session ID for stop so a broken guard cannot broadcast; Payer can perform wallet actions only in
  stores where that permission is granted.
- **RT-SD15 [RO]** Session table renders created time, truncated-but-recoverable destination, sats,
  status, transaction/failure, signing link when relevant, and Stop only for cancellable states.
- **RT-SD16 [RO]** Sender page and injected wallet-send offer are keyboard usable, translated through
  host conventions, responsive at the standard widths, and pass the measurable Section 0.7
  light/dark, focus, clipping, overflow, and reachability checks.
- **RT-SD17 [TX]** As **Wallet Manager**, start one valid Async Payjoin from the wallet send page.
  Authorization must remain coherent through the post-submit redirect: the user can see the created
  session/result or a purpose-built wallet result page. A successful start followed by 403 on
  `/stores/{storeId}/payjoin/send` is a permission mismatch, even if the background
  payment later completes.

---

## 14. Sender end-to-end and lifecycle

Every broadcast-capable action is **TX**. In particular, **Stop the payjoin broadcasts the plain
fallback when one exists**; it is not a harmless delete button.

RT-SP2 and RT-SP4 require evidence from a genuinely live sender session. Pre-arm the sender/session,
wallet/outpoint, and explorer views and prepare the second submission before the first POST; use
synchronized UTC timestamps rather than navigating manually after the fact. A terminal result does
not retroactively prove the missing Pending interval. If the receiver responds before any
non-terminal observation can be captured, mark the affected case Blocked. These timing-sensitive
cases are deliberately absent from transaction smoke in Section 20.3.

- **RT-SP1 [TX]** From Store S hot wallet, submit the RT-P1 BIP21 once. One sender session appears
  with the right destination/amount and progresses from Pending to a terminal state.
- **RT-SP2 [TX]** Before the receiver proposal or fallback deadline, the external explorer shows no
  payment tx and the selected inputs are not spent; the UI says the payment completes in background.
  Evidence must include a timestamped non-terminal sender row plus a wallet/outpoint or mempool read
  taken during that interval; a screenshot captured only after completion is insufficient.
- **RT-SP3 [TX]** When the receiver responds, sender reaches **Completed (payjoin)** and displays the
  same txid that settles Store R's invoice; explorer proves both parties contributed inputs.
- **RT-SP4 [TX]** Submitting the same BIP21 again while its session is live produces no second live
  session, pending transaction, or broadcast. The UI returns a non-5xx, user-visible
  duplicate/already-in-flight result with no stack trace or secret; record its exact text/status and
  timestamp within the non-terminal interval. Do not retry after the first live-window attempt.
- **RT-SP5 [TX]** Explicitly click **Stop the payjoin** on a fresh pending hot-wallet session. The
  plain fallback is broadcast once, status becomes **Completed (fallback)**, and the tx pays the
  original BIP21 destination/amount without claiming payjoin.
- **RT-SP6 [TX]** Select explicit Store S coins before Async submission. The final payjoin or fallback
  spends only selected sender outpoints, apart from behavior the host clearly documents in advance.
- **RT-SP7 [RW]** A rejected destination/amount/multi-output/subtract-fee submission creates no row in
  sender sessions and leaves the previously selected coins spendable on a fresh wallet page.
- **RT-SP8 [TX]** Arrange a run-owned receiver session whose contribution becomes unavailable before
  proposal completion. Sender ends through one controlled fallback, not a stuck Pending row or two
  broadcasts.
- **RT-SP9 [RW]** Store C's first Async submission routes to the normal pending-transaction signing
  screen and sender table shows **Waiting for signature** with a working **Sign the transaction** link.
- **RT-SP10 [TX]** After the first off-server signature, session starts without premature broadcast.
  When a proposal returns, the normal BTCPay signing surface requests the second signature.
- **RT-SP11 [TX]** Supplying the second valid signature completes and broadcasts the payjoin once;
  session becomes **Completed (payjoin)** with the explorer txid.
- **RT-SP12 [TX]** Stopping an awaiting-signature session follows the documented safe path: pending
  signing artifact no longer drives a payjoin, the plain fallback broadcasts at most once when valid,
  and the terminal row explains the result.
- **RT-SP13 [RO]** Closing the browser and opening a fresh authenticated session preserves visible
  sender-session history and terminal statuses. This proves remote UI persistence, not process
  restart durability.
- **RT-SP14 [TX]** Two run-owned sender sessions to different invoices progress independently. A
  failure or cancellation in one does not change the other's destination, amount, status, or txid.
- **RT-SP15 [RO]** Selecting Store R, CFG, or C never exposes Store S's sender rows; direct URLs obey
  store permissions and do not accept a session ID from another store.
- **RT-SP16 [TX]** Terminal Completed/Failed rows have no Stop control and cannot be resurrected by
  reload, back navigation, or one deliberate POST to the old cancel route. Expected: no state change
  and no broadcast. Because a regression could broadcast the fallback after terminal payjoin, this
  case consumes the run's money-safety defect reserve and must run only while that reserve remains.
- **RT-SP17 [RW]** Changing Store S relay settings affects only later sender relay attempts and does
  not rewrite destination/amount/history of an existing terminal session. Restore baseline.
- **RT-SP18 [RO]** A visible sender failure is concise and safe: no PSBT, raw transaction, private
  descriptor, token, filesystem path, or stack trace is printed in the sessions table.

Run the reset checkpoint before cross-cutting checks.

---

## 15. Cross-cutting UI, browser, and privacy

- **RT-U1 [RO]** Overview, settings, receiver checkout, sender sessions, and injected wallet-send
  offer pass every Section 0.7 legibility check in light and dark themes; status is conveyed by
  text/icon as well as colour. Record RT-U1.a–e separately.
- **RT-U2 [RO]** At 375, 768, 1280, and 1920 CSS pixels, no plugin page has accidental horizontal
  scroll, clipped action, overlapping text, or unreachable table content. Record the measured CSS
  viewport from the page, not only the automation setting; if the harness cannot change it, mark the
  affected widths Blocked.
- **RT-U3 [RO]** Visible plugin strings use the host localization path. Changing locale does not
  break route generation, numbers, dates, status meaning, or injected JavaScript labels.
- **RT-U4 [RO]** Terminology is consistent: **Async Payjoin** for BIP77, **Payjoin v1 (BIP 78)** for
  the synchronous fallback, and **Standard Bitcoin** for plain payment. Sender terminal states do
  not call a fallback transaction a payjoin.
- **RT-U5 [RO]** Across one clean navigation of every plugin surface, browser console contains no
  unhandled exception and network panel contains no unexplained 4xx/5xx, mixed content, CORS error,
  redirect loop, or request to an unrelated origin.
- **RT-U6 [RO]** Keyboard-only use reaches navigation, settings controls, checkout switch, wallet
  Async offer, signing link, and safe non-broadcast actions in a logical order with visible focus.
- **RT-U7 [RO]** After logout, Back and Forward do not reveal cached store settings, balances,
  invoices, sender destinations, or session tables; protected requests require authentication again.
- **RT-U8 [RO]** For a full cycle, repeat the read-only smoke in Firefox or WebKit when Chromium is
  primary (or Chromium when another engine is primary). Dates use the browser's locale/timezone
  without changing stored order or future/past meaning, and BIP21/clipboard behavior is equivalent.
  This case is explicitly Skipped for a declared Chromium-only smoke, never silently Pass.

RT-U1 and RT-U2 are cross-surface roll-ups. Reuse current-run evidence only when it records the exact
surface, effective CSS viewport, theme, and Section 0.7 oracle required by the destination subcase:

| Roll-up leg | Reusable source evidence | Evidence still required when absent from the source |
| --- | --- | --- |
| RT-U1.a overview | RT-O15 light/dark status evidence | Remaining Section 0.7 legibility checks for overview |
| RT-U1.b settings | RT-S18 keyboard/focus/convention evidence | Settings light/dark legibility and non-colour status evidence |
| RT-U1.c checkout | The matching light/dark legs of RT-C15 | Nothing for a leg whose exact theme evidence is complete |
| RT-U1.d sender sessions | Surface-separated RT-SD16 light/dark evidence plus RT-SD15 table evidence | Any theme or status leg not captured there |
| RT-U1.e wallet-send offer | Surface-separated RT-SD16 light/dark evidence | Any theme leg not captured there |
| RT-U2 checkout widths | The matching 375/768/1280/1920 RT-C15 legs | Any effective viewport not proved by those captures |
| RT-U2 sender/wallet widths | Surface-separated RT-SD16 captures at the same effective widths | Any surface or width missing from RT-SD16 |
| RT-U2 overview/settings widths | No automatic full reuse from RT-O15 or RT-S18 | Measure every required width unless another named current-run case contains the exact evidence |

Refer to the reused artifact from both case records; do not repeat the action merely to create a
second screenshot. Conversely, a generic desktop screenshot, a requested-but-unverified viewport,
or a source case that covered only keyboard behavior cannot auto-Pass RT-U1 or RT-U2.

---

## 16. Black-box invariants

These are the strongest invariants available without database, process, or filesystem access. They
must not be reported as proof of the underlying relational schema. All nine are **derived roll-up
assertions**: no new fixture, mutation, session, or broadcast is executed for Section 16. Derive each
status from the listed source cases and the final reconciliation; a non-Pass source rolls up with the
Section 0.6 precedence. A final read or arithmetic audit may add evidence, but it is not a new product
scenario.

| Derived assertion | Required source evidence and final reconciliation |
| --- | --- |
| RT-BI1 | RT-C7, RT-Y4, and RT-L1, plus one final UI/authenticated/anonymous convergence read of their shared invoice fixture |
| RT-BI2 | RT-SP4, RT-P7, and RT-P11, plus final sender-row, pending-transaction, invoice, and explorer reconciliation |
| RT-BI3 | RT-P3, RT-SP3, and RT-H1/H4/H7 for the same invoice and txid |
| RT-BI4 | RT-P8 and RT-SP14, plus the final run-wide invoice/session-to-txid worksheet |
| RT-BI5 | RT-N6–N13, RT-L6, RT-A12, and RT-SP15, plus the final cross-store read |
| RT-BI6 | RT-SP7, RT-SP12, and RT-SP16, plus the final reservation/action-state read |
| RT-BI7 | RT-E9, every section restoration artifact, and the final reset checkpoint against Stores R/S/CFG baselines |
| RT-BI8 | Every broadcast-capable case in scope and the completed transaction/value/fee worksheet |
| RT-BI9 | RT-E8, RT-X8, RT-C12, RT-O16, and RT-SD15/SD16, plus the final anonymous/authenticated/API health pass using the named RO fixtures |

- **RT-BI1 [RW]** Repeated UI and authenticated Greenfield reads for one New invoice converge on one
  active BIP21, while repeated anonymous responses converge on Active status only; no
  customer-visible duplicate receiver session appears.
- **RT-BI2 [TX]** Two allowed submissions of the same sender BIP21 yield at most one live session and
  one broadcast txid; a terminal retry cannot create another payment.
- **RT-BI3 [RO]** A settled invoice has one on-chain payment row for the recorded settlement txid and
  the receipt/wallet/sender surfaces agree on that txid.
- **RT-BI4 [RO]** No txid produced by the run is presented as the settlement of two distinct invoices.
  Verify every run txid against invoice, sender table, wallet history, and explorer.
- **RT-BI5 [RO]** Settings, receiver BIP21s, attention UI, sender rows, wallet history, and permissions
  remain isolated by store throughout the run.
- **RT-BI6 [RW]** A terminal or rejected sender session has no active Stop/signing action; a fresh
  wallet page no longer treats its inputs as reserved unless the chain actually spent them.
- **RT-BI7 [RW]** Final UI and API settings for Stores R, S, and CFG exactly equal their captured
  baselines. Any difference makes the run incomplete even when all feature assertions passed.
- **RT-BI8 [TX]** Every transaction broadcast or possibly broadcast by the run has a recorded purpose,
  invoice/session alias, payment amount, fee, ownership-preserving receiver/cold movement, and
  explorer txid; transaction count, payment-value sum, and fee sum independently do not exceed their
  approved budgets.
- **RT-BI9 [RO]** Final anonymous health, authenticated overview, settings, sender sessions, checkout,
  and API-view requests all succeed with no new browser console error or visible plugin disappearance.


---


## 17. Current plugin surface coverage

Use this table during review of the plan itself. A current route or UI extension missing from the
table is a plan defect.

| Product surface | Primary cases |
| --- | --- |
| `header-nav` extension (`Async Payjoin`) | RT-E5, RT-N1, RT-N6–N8 |
| `store-nav` extension (settings/sender) | RT-N2, RT-N6, RT-SD1, RT-SD14, RT-SD17 |
| Overview and empty attention state | RT-O1–O11, RT-O15–O16; bridge Retry is out of scope without a natural fixture |
| Store settings GET/POST `/stores/{storeId}/payjoin` | RT-S1–S20, RT-N9, RT-X5–X6 |
| `checkout-bitcoin-post-content` and `checkout-end` extensions | RT-C1–C15, RT-F1–F8, RT-L1–L8 |
| Anonymous payment-url route | RT-X4, RT-Y1–Y7 |
| Greenfield settings and invoice payment-url | RT-A1–A15 |
| `onchain-wallet-send` extension | RT-SD3–SD13 |
| Sender start `/stores/{storeId}/payjoin/send/from-wallet` | RT-SD7–SD14, RT-SD17, RT-SP1–SP4 |
| Sender sessions page | RT-SD1–SD2, RT-SD15–SD16, RT-SP1–SP18 |
| Sender cancel `/stores/{storeId}/payjoin/send/{senderSessionId}/cancel` | RT-SP5, RT-SP12, RT-SP16 |
| Receiver background flow | RT-P1–P11, RT-H1–H10, RT-L1–L8 |
| Sender background/signature flow | RT-SP1–SP18 |
| Cheat-only routes | RT-X1–X3, RT-A15 |

---

## 18. Smoke subsets

### 18.1 Read-only smoke

Run in order using `RO-CHECKOUT`, `RO-WALLET-OFFER`, `RO-SENDER-HISTORY`, and `RO-SIGNING` from
Section 1.5. The measured smoke performs no mutation and creates no invoice, session, pending
transaction, or broadcast. Fixture preparation is separately recorded RW work and is not silently
included in the RO result. RT-U1.c/e, the signing-link leg of RT-U6, and checkout/session portions of
RT-BI9 are Blocked if their named pre-existing fixture is unavailable.

| # | Cases | Purpose |
| --- | --- | --- |
| 1 | RT-E1–E3, RT-E5, RT-E8, RT-E10 | Correct HTTPS/testnet deployment and visible plugin surfaces |
| 2 | RT-X3–X4, RT-X7–X8 | Published surface, low-volume negative inputs, and visible health without mutation |
| 3 | RT-N1–N8, RT-N10, RT-N12–N13 | Navigation, store selection, and role boundaries |
| 4 | RT-O1–O8, RT-O10–O11, RT-O15–O16 | Overview state without mutation |
| 5 | RT-S1, RT-S10, RT-S18 | Settings render and client/accessibility contract |
| 6 | RT-A1–A2, RT-A9, RT-A14–A15 | Read API shape, scope, and docs |
| 7 | RT-SD1–SD2, RT-SD15–SD16 | Sender navigation and session table |
| 8 | RT-U1–U7 | Cross-cutting browser/UI/privacy pass in the primary browser; add RT-U8 only when the declared smoke scope includes a second engine |
| 9 | RT-BI5, RT-BI9 | Store isolation and final visible health |

### 18.2 Reversible remote smoke

Requires Store CFG and settings API tokens, but no broadcast:

1. RT-E9 baseline capture.
2. RT-X1–X2 with guaranteed-unknown IDs, followed by RT-X8 health.
3. RT-S19 persistence matrix, then restore and prove RT-BI7.
4. On one active invoice, run RT-C1–C8 and RT-C12–C15 before any terminal state change.
5. On a second invoice, check the Lightning half of RT-C9, expire it, then run RT-C11 and the
   non-New half of RT-C9. Wait no longer than `INVOICE_EXPIRY_TIMEOUT` after advertised expiry.
6. RT-F1–F3 only; degraded external-host cases are omitted from smoke.
7. RT-A3–A8 followed by exact baseline restoration.
8. RT-A13 authentication/authorization matrix with baseline-equivalent write payload.
9. RT-Y1–Y5 on run-owned invoice IDs.
10. RT-SD3–SD14, using safe invalid/unknown values for RT-SD14 and stopping before any valid final
    Async submission.
11. RT-BI1, RT-BI7, and RT-BI9.

### 18.3 Transaction smoke

Requires an approved budget and distinct Stores R/S:

1. Capture Store R's fee-cap baseline, configure the explicit `C` required by RT-P6, then run RT-P1
   and preserve its BIP21.
2. Perform one submission that satisfies both RT-P2 and RT-SP1. Record the submit timestamps and
   session ID, but do not require the smoke to capture a transient Pending state.
3. Let the original session finish. Its one terminal result satisfies RT-P3 and RT-SP3 together.
4. Run RT-P4–P6 against that transaction, including the explicit cap and fee arithmetic of RT-P6,
   then restore the fee-cap baseline.
5. Run RT-H1–H2 and RT-H4–H7 against the same invoice/txid. RT-H3 is not part of this one-broadcast
   smoke because it requires separate fresh Standard and BIP78 controls.
6. Derive RT-BI3 and RT-BI8. Run RT-E8, RT-X8, RT-C12, RT-O16, and RT-SD15–SD16 as the final
   read-only health inputs, then derive RT-BI9 and run the reset checkpoint for the affected Stores
   R/S and fee-cap baseline.

RT-SP2, RT-SP4, and derived RT-BI2 are intentionally outside transaction smoke because a fast
receiver can make their live-session window unobservable. RT-H3 is excluded because its other two
protocol controls are absent; RT-BI4 and RT-BI7 are excluded because this subset does not execute
their multi-invoice/session and three-store baseline sources. The timing-sensitive cases remain
full-cycle assertions under the timestamped live-window procedure in Sections 1.5 and 14. Do not
submit the BIP21 again during transaction smoke, either before or after RT-P3/RT-SP3. This subset
authorizes one payment broadcast, not a duplicate or second payment attempt.

---

## 19. Run log

The per-case result record in the evidence bundle is the primary coverage record; its storage format
is intentionally left to the run owner. Record every parent ID in scope, every required subcase,
status, evidence reference, fixture/browser/role, and note. The table below is only a summary derived
from those case records and cannot establish coverage by itself. A run may have separate summary rows
for read-only, reversible, and transaction smoke performed by different testers or on different
days.

| Date (UTC) | `RUN_ID` | Base URL | Network | BTCPay version | Plugin version | Source revision/unknown | Tester | Tier | Fixtures | Cases | Pass | Fail | Blocked | Skipped | Tx count / payment sats / fee sats | Evidence bundle | Baseline restored |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |


---

## 20. Known limitations and interpretation notes

- The installed-plugin UI may display a normalized assembly version such as `0.3.2.0` while the
  source project uses a prerelease version. That is not source-revision evidence.
- Without internal access, a healthy page after a 5xx cannot prove that no disable command was
  queued. This plan therefore stops on the first 5xx and reports only observed black-box health.
- Failed and armed-Expired bridge rows cannot be produced safely through the remote production-like
  surface. Attention rendering and Retry remain untested unless a natural run-owned fixture appears;
  do not enable cheat mode or edit persistent state to manufacture one.
- The anonymous payment-url controller currently documents a need for server-side rate limiting.
  This plan caps concurrency at two and makes no rate-limit or abuse-resistance claim.
- Checkout polling has a source-defined ceiling of 130 attempts with exponential delay capped at
  30 seconds. Remote cases verify early backoff, non-overlap, Active model refresh, and the
  non-retryable stop signal; they do not keep a deliberately failing page open for the full ceiling.
- Testnet3 block intervals, fee estimates, directory availability, and relay availability vary. Use
  the Section 1.1 timeboxes: absence of the required external event is Blocked, while failure to react
  within `STATE_PROPAGATION_TIMEOUT` after an observed event is Fail.
- A native browser prompt used by **Paste BIP21** was not operable through the test harness. The
  payment path was exercised through an isolated authenticated HTTP form submission instead. Mark a
  prompt-only attempt harness-Blocked; do not classify the unsupported automation primitive as a
  product defect.
- Verify form control values before Save. In the reference run, automation `fill("")` did not clear
  some controls, while Select All + Backspace did. The product passed once the DOM value was truly
  empty.
- The reference harness could not change the effective CSS viewport despite requesting widths, so
  responsive cases remained Blocked. A screenshot at an unchanged viewport is not multi-width
  evidence.
- API-key deletion through BTCPay's core modal posted to `/UIManage/deleteapikeypost` and returned
  404. Cleanup succeeded with a one-shot authorized key. Track this as host/infrastructure behavior,
  not an Async Payjoin defect, unless plugin code becomes involved.
- **Stop the payjoin** is a broadcast action, not deletion. Every case that clicks it is **TX** and
  consumes the transaction budget.
- Fresh-session persistence does not prove process-restart durability or database invariants. The
  run report must use the exact black-box wording from Sections 0.2 and 16.

### 20.1 Confirmed remote issues and contract decision

| ID | Cases | Observation on 2026-08-22 | Classification / retest oracle |
| --- | --- | --- | --- |
