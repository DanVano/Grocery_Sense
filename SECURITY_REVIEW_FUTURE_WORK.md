# Security Review — V2_Features_Implementation_Phase2 (2026-07-07)

Scope: full branch diff vs `origin/V2_Features_Implementation_Phase1` (37 files, ~4,200 insertions —
Phases 1/2/4/5/6: ItemsAdminRepo, MemberRequestsRepo, ConfigStore member CRUD, RecipeEngine,
MealSuggestionService, WeeklyPlannerService, FamilyRequestsService, DbMaintenanceService, and the
Items/Family/Meals/Preferences/Receipts UI).

Method: two-pass review — an identification pass over the whole diff plus the downstream helpers the
new code calls with user-controlled input, then an independent false-positive validation pass on each
candidate finding (confidence threshold 8/10 to report).

## Verdict

**No high-confidence vulnerabilities found.** Zero findings survived validation.

## Verified clean (identification pass, ruled out with code inspection)

- **SQL injection** — every new query in `ItemsAdminRepo` / `MemberRequestsRepo` is parameterized,
  matching the existing repo pattern. The three string-interpolated SQL sites are safe:
  - `MergeItems` interpolates table names only from the fixed private `ItemIdTables` array.
  - `DbMaintenanceService.ReadTable` interpolates only from the fixed `ExportTables` whitelist.
  - `BackupDatabase`'s `VACUUM INTO '...'` path is app-generated (cache dir + timestamp) and
    single-quote-escaped.
  - `SearchItems` passes the LIKE pattern as a parameter (`%`/`_` wildcards only broaden the user's
    own search).
- **XSS** — no `MarkupString` / raw-HTML rendering anywhere in the diff; all Razor output auto-escaped.
- **Path traversal** — backup/export paths built entirely from `FileSystem.CacheDirectory` +
  timestamps; `RecipeEngine`'s file-path constructor is test-only (DI registers embedded-resource mode).
- **Unsafe deserialization** — `JsonDocument` + source-gen contexts over plain DTOs;
  `MemberRequestsRepo.DecodeRowIds` accepts only numeric array elements.
- **AuthZ (master vs secondary member)** — any device user can switch "Acting as"; no privilege
  boundary is actually crossed: the app is single-shared-device with no authentication by design, and
  the review queue is documented as advisory ("no approval gate").

## Future implementation (defense-in-depth, not a confirmed vulnerability)

### 1. CSV formula-injection hardening in `DbMaintenanceService` — ✅ DONE 2026-07-09

Implemented in `DbMaintenanceService.CsvSanitizeCell`: TEXT data cells leading with `=`,`+`,`-`,`@`,tab,CR
are prefixed with `'`; headers and numeric `long`/`double` cells are exempt (so negative numbers keep
their `-`). Tests: `Csv_export_neutralizes_formula_injection_in_text_cells`,
`Csv_export_leaves_negative_numbers_uncorrupted`. Original finding below for the record.



- **Where:** `Grocery_Sense/GrocerySense.Core/Services/DbMaintenanceService.cs:136-137` (`CsvEscape`)
- **What:** `ExportToCsv` applies only RFC 4180 quoting. Cells starting with `=`, `+`, `-`, `@`,
  tab, or CR are not neutralized. Exported columns include receipt-OCR-derived text
  (`prices.raw_name` via `ReceiptIngestionService.cs:232-234` → `ReceiptsRepo.cs:238`, and
  `items.canonical_name`), and the advertised workflow (Preferences → Export CSV → share sheet)
  ends with the file opened in Excel/Google Sheets.
- **Why it was filtered out (validation verdict: false positive, 3/10):** the only cross-trust-boundary
  text is the victim's own physical receipt; the attack needs a printed formula to survive OCR intact
  and then defeat modern Excel/Sheets protections (DDE blocked by default, Protected View, HYPERLINK
  click-through). No remote/automated delivery vector in a local-first single-family app. Google VRP
  and similar programs treat this class as the spreadsheet app's responsibility.
- **Cheap fix when convenient:** in `CsvEscape` (data cells only, not headers, and keep numeric
  `long`/`double` cells exempt), prefix fields starting with `=`, `+`, `-`, `@`, `\t`, `\r` with a
  single quote `'` before RFC 4180 quoting — OWASP CSV-injection guidance. ~5 lines, one test.

## Standing notes for future phases (carried context, not findings)

- `/agent`-style network surface does not exist in this app today; if v3 adds sync/accounts (see
  `V2_FOLLOWUPS.md` §5 v3 gate), re-review `member_requests` and ConfigStore member CRUD — the
  "advisory review queue, no approval gate" design is only safe while everything is one trust domain
  on one device.
- OCR backend proxy (`V2_FOLLOWUPS.md` §5 v3 gate) remains the real security workstream for a public release: the Azure
  key must not ship in the app.

---

# Security Review — V3 Mobile (2026-07-18)

Threat model: **client-only with no first-party backend** (terminology adopted 2026-07-23 — not
"local-only": selected receipt/flyer images go to Azure Document Intelligence, the postal code goes to
Flipp; those are the two disclosed egress points), no accounts, BYOK Azure key. No Grocery Sense server
exists.
Source reviews (`security_review_claude_0718.md` findings + `security_review_codex_0718.md` remediation) were condensed into this doc and deleted 2026-07-21.
Branch: `V3_Mobile_Development`. **No critical/high vulnerability under the current threat model** — this
round is defense-in-depth, privacy disclosure, resource bounding, and future-regression prevention.

## Remediation completed (2026-07-18)

| ID | Finding | Status | Where |
|----|---------|--------|-------|
| SEC-01 | iOS Keychain values survive uninstall → clear on true first launch | ✅ Done | `App.xaml.cs` (`ResetIosSecretsOnFirstLaunch`, iOS-only; `Preferences` marker) |
| SEC-02 | Unbounded picker / Flipp input | ✅ Done | `Core/BoundedFileCopy.cs` (20 MiB + ext allowlist) in `Receipts.razor`/`Deals.razor`; `FlippClient.cs` (4 MiB body, `MaxDepth=32`, ≤2000 items/flyer, ≤1000 chars/field, 20 s read cap) |
| SEC-03 | Network disclosures incomplete | ✅ Done | Caption disclosures on `Preferences.razor`/`Receipts.razor`/`Deals.razor` (postal→Flipp, files→Azure). No new consent state. |
| SEC-04 | Temp/duplicate sensitive artifacts linger | ✅ Done | `DbMaintenanceService.CleanupShareArtifacts` (24 h, name-scoped) on startup; failed flyer copies deleted; disk raw-JSON write removed — SQLite `flyer_raw_json` is now the sole raw copy (the disk copy was write-only, never read back) |
| SEC-05 | High/critical NuGet advisories don't fail the build | ✅ Done | `Directory.Build.props` — `NuGetAudit` + `NuGetAuditMode=all`, `NU1903`/`NU1904` as errors |

## Verification evidence (2026-07-18)

- `dotnet test GrocerySense.Tests` → **482 passed, 0 failed, 0 skipped** (incl. new `BoundedFileCopyTests`, Flipp bound tests, `CleanupShareArtifacts` test).
- `dotnet build GrocerySense.App -f net10.0-windows10.0.19041.0` → **Build succeeded, 0 errors** (1 pre-existing unrelated CS8604 warning in `ReceiptsRepo.cs:139`).
- `dotnet list package --vulnerable --include-transitive` on Data / Integrations / Core / Tests → **no vulnerable packages**.
- **Not verified on-device:** iOS Keychain uninstall/reinstall (SEC-01), 20 MiB rejection UX, failed-import cleanup, disclosure copy — need a real iOS/Android build; the Windows dev-harness can't reproduce Keychain uninstall semantics.
- **Android Release not validated:** blocked by `XA5207` (Android API 36 `android.jar` not installed). No Android manifest/Release claim made.

## Accepted risks / no-action (this round)

- **Local data not additionally encrypted** (SQLite, `user_config.json`, receipt/flyer images). OS sandbox + device data protection are the primary controls. SQLCipher deliberately NOT added (native-dep + key-recovery cost; leaves images/config outside the DB; no protection vs live rooted use). Revisit for regulated data / managed shared devices / explicit rooted-forensic requirement.
- **No certificate pinning** — platform trust kept; pinning Flipp/Azure is brittle vs cert rotation.
- **Secure-transport defaults kept** — iOS ATS active (no weakening exception), Android API 36 `usesCleartextTraffic=false`, Azure endpoint guard requires HTTPS+Azure host, Flipp fixed HTTPS base URL.

## Secrets history scan (2026-07-23, hardening branch P1-6)

**Result: no secrets found in the current tree OR the full commit history as of 2026-07-23.**

- Method: full-history dump (`git log --all -p`, all branches, ~35 MB) scanned locally with targeted
  regexes — key/secret/password/token assignment patterns, 84-char Azure-style base64 keys, private-key
  PEM headers, GitHub/OpenAI token prefixes, cognitiveservices URLs with embedded keys. One raw match:
  an inline SVG `data:` URI (base64 `PHN2Zy…` = `<svg`), verified false positive.
- Honest scope: this was a targeted regex pass, not a full-ruleset scanner (gitleaks is not installed on
  the dev machine). The CI pipeline (`.github/workflows/ci.yml`, added same day) runs version-pinned
  gitleaks over the full history (`fetch-depth: 0`) on every push, which is the authoritative scan going
  forward.
- If a future scan hits: rotate the key at Azure (Portal → the Document Intelligence resource → Keys →
  Regenerate), update SecureStorage on devices via Preferences, then rewrite/invalidate the leaked
  commits before any push to a shared remote.

## Conditional release gate — SEC-06 (developer-owned shared Azure key)

BYOK (each user owns key+quota) stays valid. If a **developer-owned/shared** key is ever distributed in the
client: **STOP** and build a backend first — server-side Key Vault/managed identity (no shared key in the app),
auth, per-user/install rate+spend limits, MIME/byte/page/operation allowlists, logs excluding document
content/credentials/full postal codes, short-lived deletion of uploads+OCR output, and abuse tests.
