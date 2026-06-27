# Implementation Notes

Running log of non-obvious decisions made during the Python → C# port. One section per task.
Cross-reference: `PORTING.md` (playbook), `CONTRACT_AUDIT.md` (Port/Replace/Defer ledger).

---

## Phase 2 · Task 10 — ConfigStore

- **Path:** ctor takes the app-data dir; DI derives it from the db path's directory (config json sits
  beside `grocery.db`). No source-relative path — mobile revokes those.
- **Scope (per CONTRACT_AUDIT lines 142–144):** implemented the stub's exact surface = the v1
  single-profile subset.
  - **Deferred to v2:** all multi-member CRUD (add/rename/delete/primary, member switching).
  - **Deferred to consumers:** `get_user_profile` (only consumer is MealSuggestion, Phase 7/v2),
    `get_postal_code` / `get_store_priority` (trivial reads, add when Phase 4 needs them).
  - **Kept structural, deferred field-level:** the 50-line `ensure_member_profile_defaults` sanitizer →
    `EnsureHousehold` only fixes structure; per-field sanitization moves to PreferencesService Phase 3
    (audit marks it Replace there).
- **GasCostPerKm** kept in the record (cut by the optimizer redesign) but never surfaced —
  normalized-valid, not deleted. Deleting it is a Models change for later.
- **Skipped `sort_keys`** (no STJ built-in; only affects diff stability, not behavior).
