# V3 pre-Phase-0 — backend closeout plan

Written 2026-07-11. Question answered: **is any backend code from any implementation plan unfinished,
blocking the v3 Android/iOS platform Phase 0?**

## Verdict: NO backend code blocks Phase 0

Verified against the tree at 19-commits-ahead of origin on `V2_Features_Implementation_Phase2`:

- **416 tests green, 0 skipped** (`dotnet test`, run unpiped 2026-07-11).
- **`dotnet build GrocerySense.Integrations` — 0 warnings / 0 errors** (the project `dotnet test` never
  compiles; re-verified after the Flipp provider landed).
- **No new reflection-based JSON** in the food-savings commits (`FlippClient` parses via `JsonDocument`,
  AOT-safe; migration 6 is plain DDL). The Android AOT landmine (`V2_FOLLOWUPS.md` §4 #7) is not tripped.
- Every implementation plan is closed: v1 PORTING Phases 0–9 ✅ · V2_PLAN Phases 1/2/4/5/6 ✅ · July
  code-review findings #1–#15 + T1 ✅ (`archive/CODE_REVIEW_FINDINGS_2026_07.md`) · security-review CSV
  hardening ✅ · SyncCompleted/Phase-8-hook plan ✅ (executed, archived) · all nine food-savings recs ✅
  (`673d2bd`, `688c403`, `e676b4f`, `d0ac9f0`).

## Closeout tasks before cutting the v3 line (do in order, ~minutes each)

- [ ] **Push the branch**: `git push origin V2_Features_Implementation_Phase2` (19 commits, origin is behind;
      everything exists only on this machine until pushed).
- [ ] **Merge → `main` + tag the v2 code baseline**:
      `git checkout main && git merge --no-ff V2_Features_Implementation_Phase2 && git tag -a v2 -m "v2 feature-complete baseline" && git push origin main v2`
      (mirrors the v1 baseline convention from V2_PLAN Phase 0; the *release* tag waits for the signed APK).
- [ ] **Cut the v3 branch** from `main` (suggested: `V3_Platform_Phase0`).

## Explicitly NOT blocking — and why (don't re-add these to the critical path)

| Item | Why it waits |
|---|---|
| Phase 3 threshold tuning | Needs the physical 6-month receipt backfill first (user task; no corpus = nothing to tune — fabricating verdicts violates fail-loud). |
| §2 on-device UI verification (`V2_FOLLOWUPS.md`) | Needs the Android build that Phase 0 produces — it's Phase 0's exit gate, not its entry gate. |
| Receipt-scan-on-ingest alerts + local notifications | Deferred by decision (v1 grill Q8, `V2_FOLLOWUPS.md` §5). The interesting half (`Plugin.LocalNotification`, Android 13+ `POST_NOTIFICATIONS`) is only testable on device — build it DURING/after the Android phase, and make the backfill batch path skip it (`ImportBatchAsync` comment). |
| `Array.Empty<T>()` → `[]` sweep | Cosmetic, optional (archived plan Task 3). Skip. |
| Keystore / JDK 17 / SDK 36 / Azure budget cap | These ARE Phase 0 — user/environment actions, commands in `V2_PLAN.md` Phase 0 and `V2_FOLLOWUPS.md` §1. |

## Phase 0 itself (for reference, after closeout)

- **Android**: JDK 17 (`winget install Microsoft.OpenJDK.17`) → elevated `sdkmanager` for
  `platforms;android-36` + `build-tools;36.0.0` → `dotnet build GrocerySense.App -f net10.0-android` →
  keystore → signed `dotnet publish -c Release` → on-device smoke of every route (§2 list) → sideload ring.
- **iOS**: blocked on hardware — a Mac build host (Xcode) + Apple Developer account. Nothing buildable on
  this PC alone; decide Mac/cloud-Mac before scheduling any iOS work. The shared C#/Blazor code is
  platform-neutral; the work is toolchain + head config + device smoke, not a rewrite.
