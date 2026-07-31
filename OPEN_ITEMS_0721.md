# Grocery Sense — Open Items (consolidated 2026-07-21)

Single tracker for everything still unfinished. Condensed from the stale plan/review files deleted
on 2026-07-21: `V3_Phase0_plan.md`, `V3_PRE_PHASE0_BACKEND_CLOSEOUT.md`, `INFLATION_ADJUSTMENT_PLAN.md`,
`Phase0_changes.md`, `Pony_tail_audit_claude_0718.md`, and the four `*_review_*_0718.md` files — all of
which were fully or ~fully executed (verified against the code, 2026-07-21).

**Current state (updated 2026-07-31):** all v2 + v2-follow-up feature code is done and merged to `main`
(PR #1). **The Android head BUILDS** (JDK 17 + API 36 installed; `dotnet build GrocerySense.App
-f net10.0-android` → 0 errors, 7 pre-existing CS8602 nullable warnings in `AndroidLocalNotifier.cs`).

**Unpushed work sits on `feat/feature-pack-1` (15 commits, off the `hardening/p0-intake-replace-ocr`
tip, which is off `feat/family-food-features`): 608 tests green; Windows + Integrations build 0-error.**
It carries the full `HARDENING_PLAN.md` rev 3.1 (P0-1…P1-6), seven UI/workflow features, a
bugfix/security/perf/refactor pass, and three architecture deepenings. **Push + PR is a [USER]
decision — not done.** The first push also gives the new CI workflow its first run.

Everything still open below is **on-device / user / hardware-gated** — no service or data code blocks
release. Android-only source (`MainActivity` share intake) is **compile-unverified on this machine**:
it is outside the Windows head's TFM, so only an Android build proves it.

**Supersedes** `V2_FOLLOWUPS.md` §1 (platform Phase 0) and §2 (on-device verification) — that file's outer
copy predates the 2026-07-21 Android bring-up. `V2_FOLLOWUPS.md` §3 (known limitations), **§4
(bug-fixing landmines — required reading before touching merge/backfill/export/alert code)**, and §5
(deferred features) stay the reference; `SECURITY_REVIEW_FUTURE_WORK.md` stays the live security-notes doc.

Legend: **[USER]** = user action (secret/portal/hardware) · **[DEVICE]** = needs a running device/emulator ·
**[CODE]** = agent code step · **[BLOCKED]** = waiting on another item.

---

## 1. Android release plumbing — [USER] / [DEVICE]

The Android head compiles; getting a signed build onto a phone does not.

- [ ] **Release keystore** [USER, secret] — `keytool -genkeypair -v -keystore grocerysense-release.keystore
  -alias grocerysense -keyalg RSA -keysize 2048 -validity 10000` in `%USERPROFILE%\keystores\`.
  **Back it up off-machine, and store the keystore recovery info (alias + passwords) with the backup.**
  Lose it → testers uninstall/reinstall and lose local data.
- [ ] **Azure OCR budget alerts** [USER, portal] — CAD $10/mo budget + 50%/100% email alerts, set **before**
  any OCR smoke or the ~50-receipt backfill. **Azure budgets ALERT only — they do not stop spend.** The
  in-app page/batch caps (P0-3: one page per receipt, ≤10 files × ≤10 pages per flyer import, one OCR call
  at a time) are the actual spend control. Verify prebuilt-receipt per-page price while in the portal
  (expected backfill spend ≈ US$1–2).
- [ ] **Signed r1 APK + on-device smoke** [DEVICE] — `dotnet publish -f net10.0-android -c Release`
  (`-p:AndroidPackageFormats=apk`, signing passwords via env vars, never in csproj); `apksigner verify`;
  `adb install -r`; run the §4 smoke matrix on a real Android 13+ phone. First-ever run of the trimmed/AOT
  Release config. Tag `v2.0-android-r1`.
- [ ] **r2 notification smoke + in-place-upgrade proof** [DEVICE] — versionCode → 3, republish, `adb install
  -r` over the now-populated production DB (data must survive; back up via Drive first). Focused smoke:
  grant path (prompt → notification → tap → `/savings`), deny path (revoke → scan → in-app line still shows),
  backfill batch fires no notification and doesn't inflate the next scan's count. Tag `v2.1-android-r2`.

## 2. Physical paper backfill session — [USER] (the linchpin)

Corpus reality (counted 2026-07-11): **50 receipts over the last 12 months**, most crumpled, oldest fading —
**do it soon.** Nothing data-starved (alerts, optimizer, savings, meal-cost, badges, inflation baselines,
budget y/y) turns real until this runs. Tooling ready since Phases 1–2. Full protocol:
`Grocery_Sense/brainstorms/2026-07-11-receipt-backfill-session-grill.md`.

- Flatten overnight → photograph all 50 first (one photo per receipt, whole receipt in frame — hard rule;
  no flash) → import in chunks of 10 oldest-first via Receipts → **Backfill (multiple)** → **confirm each
  date against the paper** (no legible date = skip, no rescue/guessing — the batch path never defaults to
  today, `V2_FOLLOWUPS.md` §4.9) → share-sheet backup after **every** chunk → one cleanup pass after
  (repeat items only, ~20–30 staples; merge dupes via `/items` only).
- Still open from the grill (ended at Q6): merchant list at triage · day-split (rec: Day 1 flatten+photos,
  Day 2 import+cleanup, ~2.5–3 h) · **success criteria** — min bar: every imported receipt has a
  paper-confirmed date, staples mapped correctly, per-chain store rows sane. Keep the box + gallery photos
  until that bar passes.

## 3. Phase 3 real-data tuning — [USER + CODE] · [BLOCKED on §2]

Tune against the real backfilled corpus and the FINAL inflation-adjusted baselines. "No change needed" is a
valid recorded outcome. Record every verdict in `IMPLEMENTATION_NOTES.md`.

- [ ] **Fuzzy** (FuzzySharp 0.78 accept / 0.90 learn) — count mis-mapped vs total in `/items`; <5% keep 0.78,
  >5% revisit (code, test-first). This is the known reliability risk (Python used 0.75 — a 3-pt divergence).
- [ ] **Optimizer** (maxStores 3 / minItemSaving 10% / minStoreSaving $5) — real lists, eyeball moves, adjust
  in-app settings against the inflation-adjusted `usualAvg`.
- [ ] **Alerts** (15% below-usual / 5% near-low / staple gate) — one normal-use week; raise the 15% threshold
  if noisy.

## 4. On-device UI verification debt — [DEVICE]

608 tests pass but **none exercise Razor or the Android platform layer** — all UI + intent behaviour is
unproven at runtime. Verify on device (blocker = crash / data loss / feature unusable / silent-degradation;
degraded = log to a device-polish backlog, doesn't block):

**New surfaces from the hardening/feature branch (verify these first — several replace flows you may
have smoke-tested before):**
- **Share intake state machine** (P0-2, Android-only code — first Android build also proves it
  compiles): share while a batch is pending/importing → **loud reject, zero copies**, original batch
  intact · >10 URIs → 10 copied + disclosed reject · error-only batch shows **Dismiss** · kill mid-copy
  → relaunch → the orphan is swept only **past the 24 h age gate** (age the file artificially to test).
- **Atomic replace** (P0-1): backfill chunk with Replace ON, **skip a duplicate at the date dialog** →
  the original receipt is still listed (this is the bug that motivated the work) · a forced conflict
  reads as "replace conflict — originals kept", never as "duplicate"/"failed".
- **Spend caps** (P0-3): 25-file backfill pick → clean reject before any OCR call · chunk of 10 OK ·
  multipage TIFF bills one page (check the Azure portal's page count after a scan).
- **Restore** (P1-5 + the new UI): Receipts → **Recently deleted** → Restore (conflicts disclosed) ·
  share-sheet backup → **second emulator / wiped app** → restore-from-backup in the **startup error
  shell** → data visible after the cold-start swap (this is the backfill gate's exit test) · kill the
  app mid-swap → next launch recovers deterministically.
- **Flipp sync semantics** (P1-4): airplane-mode sync → visible failure, `NeedsSync` still true, next
  resume retries · the last-failure line persists on Deals after the snackbar is gone.
- **New feature surfaces:** item price-history panel · budget by-store table · watch-hit Add-to-list ·
  list-row edit dialog (qty/unit/notes) · add-field **autocomplete** (picked suggestion maps, free text
  and comma multi-add still work) · **share list as text** through the OS share sheet.

- **Backfill batch**: multi-pick, per-receipt date-confirm dialog, missing-date entry, cancel mid-batch,
  summary counts, retention (only imported images kept), store + date-range filter narrows/clears honestly.
- **Items / Meals / Family**: `/items` search/rename/merge-confirm + receipt-line Fix dialog · `/meals`
  Suggest, per-serving cost, Add-to-list, My-recipes CRUD · `/family` acting-as, picks, Keep/Remove, nav
  badge, member CRUD.
- **Data & backup**: share-sheet backup (fiddly Android file-provider bit) + CSV/JSON export; restore on a
  **second device/emulator** (`adb push` the backup .db, no in-app restore).
- **Savings / Deals / food-savings recs**: persisted alerts + dismiss, alert/deal Add-to-list rows (qty +
  note, mapped under store in Shop Mode, unmapped as disclosed text row); **live Flipp sync** on Wi-Fi AND
  cellular (first real network path); Shop Mode store groups + Buy/Stock-up/Wait badges, multi-buy chips,
  pantry hints + budget check, comparator page, swap chips; ranked kid picks + on-sale chips, restock draft,
  Trip-check dialog.
- **Android workflow shell** (`V3_Mobile_Development`): bottom-bar **safe-area / gesture-bar inset** +
  keyboard overlap; FAB above the bar; active-route tinting; Plan-trip preview→confirm→Shop-Mode; Shop Mode
  surviving a tab-away; Home resume card vs a real half-finished shop; Scan FAB camera→ingest; **hardware
  Back** walks in-app history / exits at root; **share target** (intent filter in the sheet, cold vs warm
  delivery, content-URI grants, confirm banner, oversize/bad shares as disclosed rejects).
- **Known gap (deferred):** a MudBlazor dialog/drawer creates no Back-history entry, so hardware Back
  navigates the page behind it instead of closing the overlay — needs a managed open-overlay bridge.

## 5. iOS / Apple heads — [USER hardware] · [BLOCKED on Mac + §2/§3]

Needs a **Mac build host (Xcode)** + Apple Developer account ($99/yr) — impossible on this PC alone. Shared
C#/Blazor code is platform-neutral; the work is toolchain + head config + device smoke, not a rewrite.
Dan wants to revisit. Gate: physical Mac (used M-series class) + Stages 2 & 4 complete.

- [x] **B1 source-gen sweep** [CODE] — **the 8-site watch-list is converted** (re-verified 2026-07-31 by
  sweeping `JsonSerializer.(Serialize|Deserialize)` across the tree): every shipping call site now passes a
  source-gen context — `ConfigStore` → `UserConfigJsonContext` (covers `FoodInflationByYear`; the
  polymorphic member profile goes through `ProfileDictionaryConverter`), `ReceiptsRepo` →
  `ReceiptSnapshotContext`, `UserRecipesRepo` → `StringListJsonContext`; the receipt/flyer ingest and both
  Azure clients build their dictionaries with `Utf8JsonWriter`/`JsonDocument` (`RawJson.ToJsonString`)
  rather than reflecting over `Dictionary<string, object?>`. The only reflection-based STJ left is in
  `GrocerySense.Tests` fixtures, which never ship. **Residual risk is verification, not conversion:** iOS
  full AOT has no JIT fallback, so this still has to be *proven* on a real AOT build (B3/B4) — and any new
  serialized type must add a context (`V2_FOLLOWUPS.md` §4.7).
- [ ] B1 privacy manifest / keychain entitlement / `IosLocalNotifier` — mostly landed (commit `eb50682`);
  finish + verify on device.
- [ ] B2 first Mac build → simulator → device Debug · B3 Release AOT (MtouchInterpreter contingency for
  MudBlazor/Blazor JIT) · B4 device smoke (multi-pick PHPicker contingency if Photos unreachable) ·
  B5 TestFlight **internal testers only** (keeps unofficial-Flipp ToS away from Apple review).

## 6. Security — on-device verification + standing gate

Feature/hardening code done (SEC-01…05, 2026-07-18); details in `SECURITY_REVIEW_FUTURE_WORK.md`.
**Second hardening pass done 2026-07-23** on `feat/feature-pack-1` — the full `HARDENING_PLAN.md`
rev 3.1: atomic receipt replacement · bounded share intake + DB-aware orphan sweep · Azure
spend/resource bounds (page caps, batch caps, one-at-a-time OCR gate, response + field guards) ·
correct Flipp sync semantics (committed-success throttle, Retry-After, per-store retention) · staged
cold-start restore + newer-schema guard · CI (unpiped `dotnet test` + unsigned Android Release compile
+ pinned full-history gitleaks). **Full-history secret scan run 2026-07-23: no secrets in tree or
history** (method + rotation steps recorded in `SECURITY_REVIEW_FUTURE_WORK.md`); CI's gitleaks job is
the authoritative recurring scan from here.

- [ ] **On-device verification** [DEVICE] — iOS Keychain uninstall/reinstall (SEC-01), 20 MiB reject UX
  (SEC-02), disclosure copy + failed-import cleanup (SEC-03/04); re-validate Android **Release** security
  (the old XA5207 blocker is gone now the head builds).
- [ ] **SEC-06 conditional gate** (standing) — BYOK is fine. If a **developer-owned/shared** Azure key is
  ever shipped in the client: **STOP** and build a backend first (server-side Key Vault, auth, per-user
  rate/spend limits, allowlists, content-excluding logs, short-lived deletion, abuse tests).

## 7. Performance residual — [DEVICE profile first]

The perf plans (Claude + Codex, 2026-07-18) are **~90% shipped** as `perf:` commits: nocase item index,
bounded item-search + receipt-summary aggregation, batched optimizer history, narrowed swap candidates,
paged deal rows, reused mapping connections. What's left is deliberately measure-first:

- [x] **Home no longer materializes the deal table for a count** (2026-07-31) — the landing screen ran
  `ListActiveDeals` (up to 5 000 rows) just to show a number; `FlyersRepo.CountActiveDeals` is a real
  `COUNT` with the same predicate, agreement pinned by test.
- [ ] **Profile on an Android Release/AOT build with a seeded DB** — the whole plan gates on this and it
  hasn't run (query plans captured, on-device traces were blocked; now unblocked). Then stop.
- [ ] Only if the numbers say so: **Items-page list virtualization** (`<Virtualize>` — Items still renders
  up to 250 expansion panels; Deals already uses MudPagination) · `[GeneratedRegex]` for the hot static
  regexes (AOT: `RegexOptions.Compiled` is a silent no-op on the trimmed head) · fuzzy-map prefilter
  (correctness-sensitive — keep ingest parity fixtures green; the alias cache may already make it moot).

## 8. Deferred to v3 / future (out of scope by decision)

Reference: `V2_FOLLOWUPS.md` §5 · `CONTRACT_AUDIT.md` · `reference-python/FUTURE_FEATURES.md`.

- **v3 public-store gate:** accounts/auth · multi-device sync · OCR backend proxy + per-user rate limiting ·
  Flipp ToS/legal review (provider is built; legal clearance is what's gated) · proactive push (FCM/APNs) ·
  auto-update channel (Firebase App Distribution / Play) · starter dataset + central crowdsourced price DB.
- **Per-member preference profiles** (merge/consensus/star machinery) — v2 is names-only.
- **Historically-accurate Trip check** (list snapshot, date-valid flyers, usual-excluding-receipt, unit
  normalization) — v2 ships the "right-after-the-trip" MVP only.
- **Inflation adjustment beyond ClassifyDeal + BasketOptimizer `usualAvg`** (MealSuggestion / Watchlist /
  ShoppingInsights / Planning) — revisit after adjusted numbers prove trustworthy; also user-editable bands
  and half-life-as-a-setting.
- **SQLite pragma tuning** (page cache / mmap) — post-backfill on-device profiling gate only; SIGBUS risk.
- **Offer-quantity parsing from promo text** (`TryParseOfferQuantity`) — only if the flyer qty-1 default
  proves annoying in practice.
- **Alias-correction back-creating a price on an originally-unmapped line** — recovery path is re-import
  with `replaceExisting`; a dedicated back-create is out of scope.

---

_This `.md` is under the repo's blanket `*.md` gitignore — `git add -f OPEN_ITEMS_0721.md` to track it._
