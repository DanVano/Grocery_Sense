# Grocery Sense Architecture Skeleton

Purpose: port map for a future .NET MAUI Blazor Hybrid app. This file documents the current Python/Tkinter prototype, its module I/O, data model, app flow, and port recommendations.

Source checked: `src/Grocery_Sense`, `tests`, `CLAUDE.md`, `module_breakdown.md`, `FUTURE_FEATURES.md`, and `C:\Users\skftw\.claude\plans\please-do-a-full-greedy-graham.md`.

## Product Shape

Grocery Sense is a local-first family grocery savings app prototype:

1. User imports receipt photos.
2. Azure Document Intelligence reads receipt data.
3. Ingest pipeline maps dirty receipt/flyer text to canonical items.
4. SQLite stores stores, items, receipts, prices, shopping list rows, flyers, and household preferences.
5. Price intelligence computes normalized prices, price history, alerts, deals, and basket optimization.
6. Tkinter screens expose receipt import, shopping list, family requests, preferences, deal feed, basket optimizer, budgets, store planning, and item management.

Current runtime: Python desktop app, local SQLite, no server, no real Flipp provider yet.

Target runtime: .NET MAUI Blazor Hybrid for iOS/Android, C# app code, SQLite local store, Azure Document Intelligence retained.

## Current Layering

Intended rule from `CLAUDE.md`:

```text
ui -> services -> data/repositories -> data/connection + data/schema -> domain/models
              -> integrations
              -> config
              -> recipes
```

Actual code mostly follows this, with important exceptions:

- Several Tk windows call repositories directly: store management/settings, item manager, receipt import/browser, price history, deal feed, flyer import, family requests.
- `azure_docint_client.py` is both external OCR client and receipt ingest/data writer.
- `FlyersRepo` applies preference filtering, so repository layer contains some app logic.

Port rule: do not carry these leaks into Blazor. Put Blazor pages behind app services/facades. Repos stay SQLite-only.

## App Startup Flow

Entry points:

- `python -m Grocery_Sense.ui.tk_main`: real GUI.
- `python -m Grocery_Sense.main`: backend smoke test only.

`GrocerySenseApp.__init__()`:

1. Builds Tk root.
2. Creates service instances: shopping list, meal suggestions, price history, weekly planner, flyer scheduler.
3. Schedules async DB init with `after(200, _init_db_async)`.
4. Starts DB init on daemon thread.
5. After DB ready: starts flyer scheduler, refreshes family request badge, checks price drop alerts.
6. All worker results return to UI with `widget.after(...)`.
7. `WM_DELETE_WINDOW` stops `FlyerSyncScheduler`.

Port equivalent:

- MAUI app startup initializes SQLite with async `IAppStartup.InitializeAsync()`.
- Blazor pages bind to scoped/singleton app services.
- Replace Tk `after()` with `MainThread.BeginInvokeOnMainThread` or component `InvokeAsync`.
- Replace `threading.Timer` with a small hosted/background scheduler abstraction only if mobile lifecycle needs it. Otherwise run sync on app resume/manual button.

## Data Store

SQLite path today: `src/Grocery_Sense/data/db/grocery_sense.db`.

Connection invariants:

- All DB access should use `get_connection()` or `connection_scope()`.
- `PRAGMA foreign_keys = ON` per connection.
- WAL and `synchronous=NORMAL` enabled.
- Integrity check runs once per DB path.
- Tests redirect DB through `connection._TEST_DB_PATH`.

Port equivalent:

- Use `Microsoft.Data.Sqlite`.
- Bundle SQLite via `SQLitePCLRaw.bundle_e_sqlite3`.
- Keep raw SQL. No ORM needed yet.
- Centralize connection creation and pragmas in one `SqliteConnectionFactory`.

## Tables

Core and feature-created tables after `create_tables()`, `_migrate()`, `UnitNormalizationService.ensure_schema()`, and `FlyersRepo.ensure_schema()`:

- `stores`: store profile, favorite/active/shop-here flags, distance, Flipp ID. Missing `UNIQUE(flipp_store_id)`.
- `items`: canonical item catalog, category, default unit, tracked flag, notes. `canonical_name` unique.
- `receipts`: receipt header, store FK, purchase date, totals, source, file path, Azure request ID.
- `receipt_line_items`: receipt rows, item FK nullable, quantity, unit price, line total, discount, confidence.
- `receipt_raw_json`: raw Azure JSON per receipt.
- `receipt_file_hashes`: file hash dedupe per receipt.
- `receipt_signatures`: merchant/date/total dedupe per receipt.
- `deleted_receipt_backups`: JSON snapshot for receipt undo.
- `prices`: item/store price observations from receipts, flyers, manual entry, normalized price columns.
- `flyer_sources`: older flyer source table tied to `prices.flyer_source_id`.
- `flyer_batches`: imported flyer batch metadata.
- `flyer_assets`: uploaded flyer files/pages.
- `flyer_raw_json`: raw flyer OCR JSON.
- `flyer_deals`: normalized flyer deal rows.
- `item_aliases`: dirty text -> item mapping. FK has no cascade.
- `shopping_list`: household list rows, planned store, member attribution, soft delete/check-off.
- `member_requests`: secondary family member meal/item requests.
- `user_profile`: older profile table, mostly superseded by JSON config.
- `schema_version`: numeric core migration marker.

FK behavior:

- `prices.item_id/store_id/receipt_id/flyer_source_id`: cascade.
- `receipt_line_items.receipt_id`: cascade.
- `receipt_line_items.item_id`: set null.
- `shopping_list.item_id/planned_store_id`: set null.
- `item_aliases.item_id`: no action. Known gap.
- Flyer batch -> assets/raw/deals: cascade; asset -> deal: set null.

Migration model:

- Core DDL lives in `data/schema.py`.
- Feature DDL lives in `UnitNormalizationService.ensure_schema()` and `FlyersRepo.ensure_schema()`.
- Some Azure support DDL is duplicated/retained in `azure_docint_client.py`, but core schema now creates those tables.
- Migrations are additive or rebuild via `__new` copy/swap.

Port recommendation: create a numbered migration ledger before or during the C# port. Keep raw SQL, but stop scattering DDL across services once port begins.

## Domain Models

`domain/models.py` dataclasses:

- `Store`: `id`, `name`, `address`, `city`, `postal_code`, `flipp_store_id`, `is_favorite`, `priority`, `shop_here`, `is_active`, `notes`, `distance_km`.
- `Item`: `id`, `canonical_name`, `category`, `default_unit`, `typical_package_size`, `typical_package_unit`, `is_tracked`, `notes`.
- `Receipt`: `id`, `store_id`, `purchase_date`, `subtotal_amount`, `tax_amount`, `total_amount`, `source`, `file_path`, `image_overall_confidence`, `keep_image_until`, `azure_request_id`.
- `PricePoint`: `id`, `item_id`, `store_id`, `source`, `date`, `unit_price`, `unit`, `quantity`, `total_price`, `receipt_id`, `flyer_source_id`, `raw_name`, `confidence`, `norm_unit_price`, `norm_unit`.
- `PriceStats`: `item_id`, `store_id`, `min_price`, `max_price`, `avg_price`, `count`.
- `ShoppingListItem`: `id`, `display_name`, `quantity`, `unit`, `item_id`, `planned_store_id`, `added_by`, `added_at`, `is_checked_off`, `is_active`, `notes`, `category`, `added_by_member_id`.

C# port: use `record` or small immutable DTOs for query results; use mutable command models only at UI form seams.

## Main Data Flows

### Receipt Import

`ReceiptImportWindow` -> `ingest_receipt_file_outcome()` -> `AzureReceiptClient.analyze_receipt_file()` -> Azure prebuilt receipt -> `ingest_analyzed_receipt_into_db()` -> stores/items/receipts/receipt_line_items/prices/raw JSON/dedupe tables.

Load-bearing details:

- File hash dedupe happens before Azure call.
- Signature dedupe catches rescans of same merchant/date/total.
- `replace_existing=True` deletes and re-ingests.
- Unit normalization and multibuy parsing happen during ingest.

Port split:

- `AzureReceiptOcrClient`: external API only.
- `ReceiptIngestionService`: dedupe, mapping, DB writes.
- `ReceiptImportPage`: file picker, progress, cancel/retry.

### Flyer Import And Sync

Manual: `FlyerImportWindow` -> `FlyerIngestService.ingest_assets()` -> `FlyerDocIntClient` -> `FlyersRepo`.

Scheduled: `FlyerSyncScheduler` -> `run_sync()` -> `FlippClient.fetch_flyers_for_store()` -> `FlyersRepo.insert_deals()`.

Current `FlippClient` returns `[]`. Pipeline exists, provider does not.

Port split:

- Keep provider behind `IFlyerProvider`.
- Start with stub or manual flyer import.
- Do not fake deals.

### Shopping And Planning

Shopping list text -> `ShoppingListService` -> `shopping_list_repo`.

Planning:

- `PlanningService.build_plan_for_active_list()` estimates store assignment and costs.
- `BasketOptimizerService.optimize()` uses active list, store filters, flyer/current/history prices, distance/gas cost, preferences.
- `apply_optimizer_plan_to_active_list()` writes planned stores back to active list rows.

Port priority: this is core product value. Port after schema/repositories and receipt ingest.

### Preferences

`config_store.py` owns `user_config.json`.

Rules:

- Master profile is household baseline.
- Secondary hard excludes downgrade to soft excludes.
- Allergies are household-wide hard excludes.
- Strong soft consensus drives `*` markers.
- `PROFILE_VERSION` migration happens on config load.

Port recommendation: move JSON config to SQLite or keep JSON only for local settings. Household members/preferences are domain data; SQLite makes sync/backups/migrations easier.

### Price Intelligence

Core modules:

- `UnitNormalizationService`: unit math and normalized columns.
- `MultiBuyDealService`: bundle/promo parser.
- `PriceHistoryService`: item history, baselines, deal classification.
- `PriceDropAlertService`: staple/current quote scan and alert persistence.
- `DealsService`: cache-backed external deal search shape, mostly future-facing.

Port priority: port these before the UI gets rich. The UI is only useful if price math is trusted.

## UI Screen Map

- `tk_main.py`: shell, menus, inline shopping list/meal/weekly plan dialogs, startup jobs.
- `receipt_import_window.py`: folder scan, threaded Azure receipt ingest, raw JSON reprocess.
- `receipt_browser_window.py`: receipt list/filter/delete/undo/raw JSON.
- `stores_management_window.py`: CRUD stores, favorite/active flags.
- `store_settings_window.py`: shop-here flag and distance.
- `store_plan_window.py`: current plan output from `PlanningService`.
- `basket_optimizer_window.py`: optimize active list and apply fast/savings plan.
- `deal_feed_window.py`: active flyer deals, preference-aware filtering, add selected deal to list.
- `flyer_import_window.py`: manual flyer PDF/image import.
- `price_history_window.py`: item/store price stats.
- `price_drop_alerts_window.py`: current alerts and add-to-list action.
- `budget_window.py`: monthly spend/budget and gas cost.
- `item_manager_window.py`: search items, tracked flag, default unit, rename/merge.
- `preference_window.py`: full household preference editor.
- `preferences_wizard_window.py`: guided preference setup.
- `family_requests_window.py`: parent review queue.
- `list_audit_window.py`: active list price audit.

Port mapping: each becomes a Blazor route or modal. Start with 5 routes: Receipts, List, Deals, Plan, Preferences. Keep admin screens later.

## Public I/O Inventory

Return type `Any` means unannotated in current Python, not unknown behavior.

### `config/config_store.py`

- `default_member_profile() -> Dict[str, Any]`
- `ensure_member_profile_defaults(profile: Dict[str, Any], role: str) -> Dict[str, Any]`
- `atomic_write_json(path: Path, data: Any, **dump_kwargs: Any) -> None`
- `load_config() -> UserConfig`
- `save_config(cfg: UserConfig) -> None`
- `list_members() -> List[HouseholdMember]`
- `get_member(member_id: int) -> Optional[HouseholdMember]`
- `get_primary_member() -> HouseholdMember`
- `get_master_member() -> HouseholdMember`
- `get_active_member() -> HouseholdMember`
- `set_active_member_id(member_id: int) -> None`
- `set_primary_member_id(member_id: int) -> None`
- `add_member(name: str, role: str='secondary') -> HouseholdMember`
- `rename_member(member_id: int, new_name: str) -> None`
- `delete_member(member_id: int) -> bool`
- `get_member_profile(member_id: int) -> Dict[str, Any]`
- `save_member_profile(member_id: int, profile: Dict[str, Any]) -> None`
- `is_master(member_id: int) -> bool`
- `get_household_allergies() -> Set[str]`
- `get_postal_code() -> str`
- `get_store_priority() -> List[str]`
- `cache_get(key: str, max_age_days: int=7) -> Optional[Any]`
- `cache_set(key: str, value: Any, max_age_days: int=7) -> None`
- `reset_secondary_member_to_household_baseline(member_id: int) -> bool`
- `get_user_profile() -> Dict[str, Any]`

### `data/connection.py`

- `current_db_path() -> Path`
- `get_db_path() -> Path`
- `get_connection() -> sqlite3.Connection`
- `connection_scope() -> Iterator[sqlite3.Connection]`
- `reset_integrity_cache() -> None`

### `data/schema.py`

- `create_tables(conn: sqlite3.Connection) -> None`
- `initialize_database() -> None`

### Repositories

`stores_repo.py`

- `create_store(name: str, address: Optional[str]=None, city: Optional[str]=None, postal_code: Optional[str]=None, flipp_store_id: Optional[str]=None, is_favorite: bool=False, priority: int=0, notes: Optional[str]=None) -> Store`
- `get_store_by_id(store_id: int) -> Optional[Store]`
- `list_stores(only_favorites: bool=False, order_by_priority: bool=True, limit: Optional[int]=None, include_archived: bool=False) -> List[Store]`
- `set_store_favorite(store_id: int, is_favorite: bool, priority: Optional[int]=None) -> None`
- `set_store_shop_here(store_id: int, shop_here: bool) -> None`
- `set_store_distance_km(store_id: int, distance_km: Optional[float]) -> None`
- `update_store_address(store_id: int, address: Optional[str]=None, city: Optional[str]=None, postal_code: Optional[str]=None) -> None`
- `update_store(store_id: int, name: str, address: Optional[str]=None, city: Optional[str]=None, postal_code: Optional[str]=None, flipp_store_id: Optional[str]=None, is_favorite: bool=False, priority: int=0, notes: Optional[str]=None) -> None`
- `set_store_active(store_id: int, is_active: bool) -> None`
- `delete_store(store_id: int) -> None`
- `upsert_store_from_flipp(name: str, flipp_store_id: str, address: Optional[str]=None, city: Optional[str]=None, postal_code: Optional[str]=None) -> Store`

`items_repo.py`

- `create_item(canonical_name: str, category: Optional[str]=None, default_unit: Optional[str]=None, typical_package_size: Optional[float]=None, typical_package_unit: Optional[str]=None, is_tracked: bool=True, notes: Optional[str]=None) -> Item`
- `get_item_by_id(item_id: int) -> Optional[Item]`
- `get_item_by_name(canonical_name: str) -> Optional[Item]`
- `list_all_item_names() -> List[Tuple[int, str]]`
- `list_items(include_untracked: bool=False) -> List[Item]`
- `set_item_tracked(item_id: int, is_tracked: bool) -> None`
- `get_items_by_ids(item_ids: List[int]) -> Dict[int, Item]`
- `get_items_by_names(names: List[str]) -> Dict[str, Item]`
- `update_item_notes(item_id: int, notes: Optional[str]) -> None`

`items_admin_repo.py`

- `ItemsAdminRepo.ensure_schema() -> None`
- `ItemsAdminRepo.search_items(query: str='', limit: int=250) -> List[ItemRow]`
- `ItemsAdminRepo.get_item(item_id: int) -> Optional[Dict[str, Any]]`
- `ItemsAdminRepo.toggle_tracked(item_id: int) -> int`
- `ItemsAdminRepo.set_default_unit(item_id: int, default_unit: Optional[str]) -> None`
- `ItemsAdminRepo.rename_item(item_id: int, new_name: str) -> None`
- `ItemsAdminRepo.merge_items(target_item_id: int, source_item_id: int, keep_source_as_alias: bool=True) -> None`

`item_aliases_repo.py`

- `ItemAliasesRepo.get_by_alias(alias_text: str, conn: Optional[sqlite3.Connection]=None) -> Optional[ItemAlias]`
- `ItemAliasesRepo.upsert_alias(alias_text: str, item_id: int, confidence: float=1.0, source: str='manual', conn: Optional[sqlite3.Connection]=None) -> None`
- `ItemAliasesRepo.mark_seen(alias_text: str, conn: Optional[sqlite3.Connection]=None) -> None`
- `ItemAliasesRepo.list_all() -> List[ItemAlias]`

`prices_repo.py`

- `add_price_point(item_id: int, store_id: int, unit_price: float, unit: str, quantity: Optional[float]=None, total_price: Optional[float]=None, raw_name: Optional[str]=None, confidence: Optional[int]=None, source: str='manual', date: Optional[str]=None, receipt_id: Optional[int]=None, flyer_source_id: Optional[int]=None) -> int`
- `get_prices_for_item(item_id: int, store_id: Optional[int]=None, since_days: int=365, limit: Optional[int]=None) -> List[PricePoint]`
- `get_most_recent_price(item_id: int, store_id: Optional[int]=None) -> Optional[PricePoint]`
- `get_price_stats_for_item(item_id: int, store_id: Optional[int]=None, since_days: int=365) -> PriceStats`
- `add_price_points(rows: List[Tuple]) -> None`
- `list_unit_prices(item_id: int, store_id: Optional[int]=None, since_days: int=180, sources: Optional[List[str]]=None, receipt_only: bool=False, limit: Optional[int]=None) -> List[float]`
- `get_usual_unit_price(item_id: int, store_id: Optional[int]=None, receipt_only: bool=True, min_samples: int=4, since_days: int=180) -> Tuple[Optional[float], int, str]`
- `get_six_month_low_unit_price(item_id: int, store_id: Optional[int]=None, since_days: int=183) -> Tuple[Optional[float], Optional[str]]`
- `get_last_seen_at_or_below(item_id: int, *, store_id: Optional[int]=None, price_ceiling: float, since_days: int=183) -> Optional[str]`
- `get_usual_unit_price_batch(item_ids: List[int], receipt_only: bool=True, min_samples: int=4, since_days: int=180) -> Dict[int, Tuple[Optional[float], int, str]]`
- `get_six_month_low_batch(item_ids: List[int], since_days: int=183) -> Dict[int, Tuple[Optional[float], Optional[str]]]`
- `get_last_seen_at_or_below_batch(item_id_to_ceiling: Dict[int, float], since_days: int=183) -> Dict[int, Optional[str]]`
- `get_active_flyer_unit_price(item_id: int, store_id: int) -> Optional[float]`
- `list_staple_item_ids(since_days: int=90, min_distinct_receipts: int=3, min_line_items: int=4) -> List[Tuple[int, int, int]]`
- `get_best_current_quote_for_item_store(item_id: int, store_id: int) -> Optional[Dict[str, Any]]`
- Batch readers: `get_most_recent_prices_by_store_batch`, `get_most_recent_prices_global_batch`, `get_active_flyer_prices_batch`, `get_price_stats_batch`, `get_recent_avg_unit_price_by_store_batch`, `get_recent_avg_unit_price_global_batch`, `get_purchase_cadence_batch`.

`receipts_repo.py`

- `ensure_receipt_support_tables() -> None`
- `list_recent_receipts(limit: int=50, offset: int=0, store_id: Optional[int]=None, since: Optional[str]=None, until: Optional[str]=None) -> List[Dict[str, Any]]`
- `get_receipt(receipt_id: int) -> Optional[Dict[str, Any]]`
- `list_receipt_line_items(receipt_id: int) -> List[Dict[str, Any]]`
- `get_receipt_raw_json(receipt_id: int) -> Tuple[Optional[str], Optional[str]]`
- `get_month_spend(year_month: str) -> Dict[str, Any]`
- `get_spend_trend(months: int=12) -> List[Dict[str, Any]]`
- `delete_receipt_cascade(receipt_id: int) -> None`
- `delete_receipt_with_backup(receipt_id: int) -> int`
- `restore_receipt_from_backup(backup_id: int) -> Tuple[int, List[Tuple[str, str]]]`
- `list_deleted_backups(limit: int=25) -> List[Dict[str, Any]]`

`shopping_list_repo.py`

- `list_active_items(store_id: Optional[int]=None, include_checked_off: bool=False) -> List[ShoppingListRow]`
- `list_all_items() -> List[ShoppingListRow]`
- `get_item(row_id: int) -> Optional[ShoppingListRow]`
- `bulk_add_items(rows: List[Tuple]) -> int`
- `add_item(display_name: str, quantity: float=1.0, unit: str='', category: str='', notes: str='', added_by: Optional[str]=None, added_by_member_id: Optional[int]=None, planned_store_id: Optional[int]=None, item_id: Optional[int]=None) -> int`
- `set_checked_off(item_id: int, checked: bool) -> None`
- `delete_item(item_id: int) -> None`
- `clear_all_items() -> None`
- `clear_checked_off_items() -> None`
- `clear_planned_store_ids_for_active_items(include_checked_off: bool=False) -> int`
- `set_planned_store_id(item_id: int, planned_store_id: Optional[int]) -> None`
- `bulk_set_planned_store_ids(assignments: List[Tuple[int, Optional[int]]]) -> int`
- `bulk_set_planned_store_ids_by_item_id(assignments: List[Tuple[int, Optional[int]]], active_only: bool=True) -> int`

`flyers_repo.py`

- `compute_sha256(data: bytes) -> str`
- `FlyersRepo.ensure_schema() -> None`
- `FlyersRepo.upsert_store(name: str) -> int`
- `FlyersRepo.list_stores() -> List[StoreRow]`
- `FlyersRepo.create_flyer_batch(store_id: int, valid_from: Optional[str], valid_to: Optional[str], source_type: Optional[str]=None, source_ref: Optional[str]=None, note: Optional[str]=None, status: str='active') -> int`
- `FlyersRepo.create_batch(source: str, store_id: int, flyer_name: str='', valid_from: str='', valid_to: str='', status: str='active') -> int`
- `FlyersRepo.set_batch_status(flyer_id: int, status: str) -> None`
- `FlyersRepo.add_asset(flyer_id: int, asset_type: str, path: str, sha256: Optional[str]=None) -> int`
- `FlyersRepo.add_raw_json(flyer_id: int, raw_json: str, sha256: Optional[str]=None) -> int`
- `FlyersRepo.add_deal(...) -> int`
- `FlyersRepo.add_deals(deals: List[Dict[str, Any]]) -> int`
- `FlyersRepo.insert_deals(batch_id: int, store_id: int, deals: List[Dict[str, Any]]) -> int`
- `FlyersRepo.list_deals_for_flyer(flyer_id: int, apply_preferences: bool=True, include_soft_excluded: bool=True, include_disallowed_oils: bool=False, limit: int=5000) -> List[Dict[str, Any]]`
- `FlyersRepo.list_active_deals(store_id: Optional[int]=None, store_ids: Optional[List[int]]=None, on_date: Optional[str]=None, as_of: Optional[str]=None, limit: int=5000, preferences_aware: bool=True, apply_preferences: Optional[bool]=None, include_soft_excluded: bool=True, filter_disallowed_oils: bool=False, include_disallowed_oils: Optional[bool]=None) -> List[Dict[str, Any]]`

`member_requests_repo.py`

- `add_request(member_id: Optional[int], member_name: str, kind: str, label: str, item_row_ids: List[int]) -> int`
- `get_request(request_id: int) -> Optional[MemberRequestRow]`
- `list_unreviewed() -> List[MemberRequestRow]`
- `list_all(limit: Optional[int]=None) -> List[MemberRequestRow]`
- `count_unreviewed() -> int`
- `mark_reviewed(request_id: int) -> None`
- `mark_all_reviewed() -> None`

### Services

`unit_normalization_service.py`

- `UnitNormalizationService.ensure_schema() -> None`
- `get_item_default_unit(item_id: int) -> Optional[str]`
- `set_item_default_unit_if_missing(item_id: int, observed_unit: str) -> None`
- `normalize(item_id: int, unit_price: float, observed_unit: str, description: Optional[str]=None) -> NormalizedPrice`
- `guess_unit_from_text(text: str) -> str`

`multibuy_deal_service.py`

- `MultiBuyDealService.adjust(description: str, quantity: Optional[float], unit_price: Optional[float], line_total: Optional[float], discount: Optional[float]) -> DealAdjusted`

`price_history_service.py`

- `PriceHistoryService.get_or_create_item(canonical_name: str, category: Optional[str]=None, default_unit: Optional[str]=None) -> Item`
- `ensure_item_exists(canonical_name: str) -> Item`
- `record_price_from_receipt(...) -> int`
- `record_price_from_flyer(...) -> int`
- `record_manual_price(...) -> int`
- `get_item_stats(item_name: str, window_days: int=180) -> Optional[Dict[str, Any]]`
- `get_baseline_price(item_name: str, window_days: int=90) -> Optional[float]`
- `get_baseline_prices(item_names: List[str], window_days: int=90) -> Dict[str, Optional[float]]`
- `stats_for_item_by_store(item_id: int, store_id: int, window_days: int) -> Dict[str, Any]`
- `classify_deal(item_name: str, candidate_unit_price: float, window_days: int=180) -> Dict[str, Any]`
- `describe_item_history(item_name: str, window_days: int=365) -> str`

`price_drop_alert_service.py`

- `PriceDropAlertService.refresh_engine_alerts(staples_only: bool=True) -> int`
- `compute_engine_alerts(staples_only: bool=True) -> List[Dict[str, Any]]`
- `get_alerts(limit: int=250) -> List[PriceDropAlert]`
- `get_open_alerts() -> List[Dict[str, Any]]`
- `dismiss_alert(alert_id: int) -> None`
- `scan_recent_receipts(days: int=21) -> int`
- `get_price_drop_alert_service(log=None) -> PriceDropAlertService`

`deals_service.py`

- `group_deals_by_store(deals: List[Deal]) -> Dict[str, List[Deal]]`
- `choose_stores_min_trips(by_store: Dict[str, List[Deal]], allow_singleton_for_meat: bool=True, store_priority: Optional[List[str]]=None) -> List[str]`
- `collect_favorite_ingredients(favorite_recipes: List[Dict[str, Any]]) -> List[str]`
- `rank_recipes_by_deals(favorite_recipes: List[Dict[str, Any]], deals: List[Deal], max_recipes: int=9) -> List[Dict[str, Any]]`
- `search_deals(term: str, postal_code: Optional[str]=None, max_age_days: int=7, locale: str='en-CA') -> List[Deal]`
- `suggest_stores_for_term(term: str, postal_code: Optional[str]=None, max_age_days: int=7) -> List[str]`

`shopping_list_service.py`

- `ShoppingListService.add_items_from_text(text: str, planned_store_id: Optional[int]=None, added_by: Optional[str]=None, member_id: Optional[int]=None) -> List[ShoppingListRow]`
- `summarize_list_for_display(include_checked_off: bool=False) -> str`
- `get_active_items(store_id: Optional[int]=None, include_checked_off: bool=False) -> Any`
- `add_single_item(name: str, quantity: Optional[float]=None, unit: str='', planned_store_id: Optional[int]=None, notes: Optional[str]=None, added_by: Optional[str]=None, added_by_member_id: Optional[int]=None, item_id: Optional[int]=None, auto_map: bool=False) -> int`
- `soft_delete_item(item_id: int) -> None`
- `check_off_item(item_id: int, checked: bool=True) -> None`
- `clear_all_checked_off() -> None`
- Module wrappers: `get_active_items`, `get_all_items`, `add_item`, `set_checked_off`, `delete_item`, `clear_all_items`, `clear_all_checked_off`, `clear_planned_stores_for_active_list`, `apply_optimizer_plan_to_active_list`.

`planning_service.py`

- `PlanningService.build_plan_for_active_list(max_stores: int=3, days_back: int=180, history_limit: int=12) -> Dict[str, object]`

`basket_optimizer_service.py`

- `phrase_safe_hit(text: str, term: str, safe_phrases: Optional[List[str]]=None) -> bool`
- `BasketOptimizerService.optimize(mode: str='two_store') -> BasketOptimizationResult`
- Support helpers choose single/two stores, load flyer prices, apply preference annotations, and compute price picks.

`meal_suggestion_service.py`

- `MealSuggestionService.suggest_meals_for_week(profile: Optional[Dict[str, Any]]=None, target_ingredients: Optional[Iterable[str]]=None, max_recipes: int=6, recently_used_recipe_ids: Optional[Iterable[Any]]=None) -> List[SuggestedMeal]`
- `format_meal_explanation(...) -> str`
- `explain_suggested_meal(meal: SuggestedMeal) -> str`

`weekly_planner_service.py`

- `WeeklyPlannerService.build_weekly_plan(num_recipes: int=6, target_ingredients: Optional[Iterable[str]]=None, recently_used_recipe_ids: Optional[Iterable[Any]]=None, persist_to_shopping_list: bool=False, planned_store_id: Optional[int]=None, added_by: Optional[str]=None, map_ingredients: bool=True) -> WeeklyPlan`
- `summarize_weekly_plan(plan: WeeklyPlan) -> list[str]`

`ingredient_mapping_service.py`

- `IngredientMappingService.invalidate_choices() -> None`
- `flush_learned_aliases() -> None`
- `map_to_item(raw_text: str) -> MappingResult`

`flyer_ingest_service.py`

- `FlyerIngestService.ingest_assets(store_id: Optional[int], valid_from: Optional[str], valid_to: Optional[str], file_paths: List[str], raw_json_dir: str, source_type: str='manual_upload', source_ref: Optional[str]=None, note: Optional[str]=None, try_item_mapping: bool=True) -> FlyerIngestResult`
- `ingest_dealrecords_json(store_id: Optional[int], valid_from: Optional[str], valid_to: Optional[str], dealrecords_path: str, source_type: str='manual_upload', source_ref: Optional[str]=None, note: Optional[str]=None, try_item_mapping: bool=True) -> FlyerIngestResult`

`flyer_sync_service.py`

- `needs_sync() -> bool`
- `run_sync(force: bool=False) -> FlyerSyncResult`

`flyer_sync_scheduler.py`

- `FlyerSyncScheduler.start() -> None`
- `request_sync() -> None`
- `stop() -> None`

`preferences_service.py`

- `EffectivePreferences.is_hard_excluded(ingredient: str) -> bool`
- `EffectivePreferences.soft_excluders(ingredient: str) -> List[str]`
- `EffectivePreferences.secondary_soft_excluder_count(ingredient: str) -> int`
- `EffectivePreferences.is_strong_soft_excluded(ingredient: str) -> bool`
- `EffectivePreferences.soft_protein_excluders(protein: str) -> List[str]`
- `EffectivePreferences.secondary_soft_protein_excluder_count(protein: str) -> int`
- `EffectivePreferences.is_strong_soft_protein_excluded(protein: str) -> bool`
- `EffectivePreferences.protein_weight(protein: str) -> float`
- `EffectivePreferences.is_oil_allowed(oil: str) -> bool`
- `compute_effective_preferences() -> EffectivePreferences`
- `get_meal_profile() -> Dict[str, Any]`
- `get_star_excluders(name: str, eff: Optional[EffectivePreferences]=None) -> List[str]`
- `get_soft_exclude_marker(name: str, eff: Optional[EffectivePreferences]=None) -> str`
- `annotate_name_with_star(name: str, eff: Optional[EffectivePreferences]=None) -> str`
- `annotate_protein_with_star(protein: str, eff: Optional[EffectivePreferences]=None) -> str`
- `get_household_hard_excludes(eff: Optional[EffectivePreferences]=None) -> List[str]`
- `get_household_baseline_profile() -> Dict[str, Any]`
- `get_member_profile(member_id: int) -> Dict[str, Any]`
- `get_effective_edit_state_for_member(member_id: int) -> Dict[str, Any]`
- `validate_add_exclude(member_id: int, value: str, exclude_kind: str) -> Tuple[bool, str]`
- `reset_secondary_member_to_household_baseline(member_id: int) -> bool`
- `protein_groups() -> Dict[str, List[str]]`

`family_requests_service.py`

- `pick_meal(member_id: int, recipe_name: str) -> Optional[MemberRequestRow]`
- `pick_item(member_id: int, text: str, quantity: float=1.0, unit: str='each') -> Optional[MemberRequestRow]`
- `pickable_recipes() -> List[str]`
- `unreviewed_count() -> int`
- `list_unreviewed() -> List[MemberRequestRow]`
- `mark_reviewed(request_id: int) -> None`
- `remove_request(request_id: int) -> None`

`list_audit_service.py`

- `audit_active_list(window_days: int=USUAL_LOOKBACK_DAYS) -> Dict[str, Any]`

`budget_service.py`

- `get_budget_status() -> Dict[str, Any]`
- `get_trend(months: int=12) -> List[Dict[str, Any]]`
- `save_monthly_budget(amount: Optional[float]) -> None`
- `save_gas_cost_per_km(rate: float) -> None`
- `get_gas_cost_per_km() -> float`

`db_maintenance_service.py`

- `backup_database(dest_dir: Optional[Path]=None) -> Path`
- `export_to_csv(dest_dir: Path) -> List[Path]`
- `export_to_json(dest_dir: Path) -> List[Path]`

`demo_seed_service.py`

- `reset_all_demo_data() -> None`
- `seed_demo_data(reset_first: bool=True, n_price_points: int=200, days_back: int=90, seed: int=42) -> Dict[str, int]`

### Integrations

`azure_docint_client.py`

- `AzureReceiptClient.__init__(endpoint: Optional[str]=None, api_key: Optional[str]=None, locale: str='en-US') -> None`
- `AzureReceiptClient.analyze_receipt_file(file_path: str | Path, max_attempts: int=3, base_delay: float=2.0, max_retry_after: float=60.0) -> Tuple[str, Dict[str, Any]]`
- `AzureReceiptClient.analyze_and_save_json(file_path: str | Path, raw_json_dir: str | Path) -> AzureReceiptResult`
- `ingest_analyzed_receipt_into_db(file_path: str | Path, operation_id: str, analyze_result: Dict[str, Any], saved_json_path: Path, store_match_threshold: int=85, file_hash: Optional[str]=None) -> int`
- `ingest_receipt_file_outcome(file_path: str | Path, raw_json_dir: str | Path='azure_raw_json', locale: str='en-US', store_match_threshold: int=85, replace_existing: bool=False) -> IngestOutcome`
- `ingest_receipt_file(file_path: str | Path, raw_json_dir: str | Path='azure_raw_json', locale: str='en-US', store_match_threshold: int=85) -> int`

`flyer_docint_client.py`

- `FlyerDocIntClient.__init__() -> None`
- `analyze_layout_file(file_path: str | Path) -> AzureLayoutResult`

`flipp_client.py`

- `FlippClient.fetch_flyers_for_store(store_name: str, postal_code: str) -> List[Dict[str, Any]]`

### Recipes

- `Recipe.id() -> Any`
- `Recipe.name() -> str`
- `Recipe.ingredients() -> List[str]`
- `Recipe.tags() -> List[str]`
- `RecipeEngine.load_all_recipes(force_reload: bool=False) -> List[Dict[str, Any]]`
- `RecipeEngine.filter_recipes_by_ingredients_and_profile(include_ingredients: Iterable[str], profile: Optional[Dict[str, Any]]=None, max_results: int=10) -> List[Dict[str, Any]]`
- `RecipeEngine.get_recipe_by_name(name: str) -> Optional[Dict[str, Any]]`
- Module wrappers mirror the same three methods.

### UI Entrypoints

- `PreferencesWindow` helper widgets expose `inner()`, `set_validate_add(...)`, `set_values(...)`, `get_values()`, and `set_enabled(...)` for internal list editing.
- `PreferencesWizardWindow` helper widgets expose the same list-editor methods plus read-only `set_values(...)`.
- `ReceiptBrowserWindow.refresh_receipts() -> None`
- `ReceiptBrowserWindow.view_raw_json() -> None`
- `ReceiptBrowserWindow.delete_selected_receipt() -> None`
- `ReceiptBrowserWindow.undo_last_delete() -> None`
- `open_basket_optimizer_window(master=None, log=None) -> BasketOptimizerWindow`
- `open_budget_window(master=None, log=None) -> BudgetWindow`
- `open_deal_feed_window(master=None, log=None) -> DealFeedWindow`
- `open_family_requests_window(parent, log=None, on_change=None) -> None`
- `open_flyer_import_window(parent, log=None) -> None`
- `open_item_manager_window(parent, log=None) -> None`
- `open_list_audit_window(master=None, log=None) -> ListAuditWindow`
- `open_preferences_window(master=None, log=None) -> PreferencesWindow`
- `open_preferences_wizard_window(master=None, member_id=None, log=None) -> PreferencesWizardWindow`
- `open_price_drop_alerts_window(master=None, log=None) -> PriceDropAlertsWindow`
- `open_price_history_window(master=None) -> PriceHistoryWindow`
- `open_receipt_browser_window(parent, log=None) -> None`
- `open_receipt_import_window(parent, log=None) -> None`
- `open_store_plan_window(parent, log=None) -> None`
- `open_store_settings_window(master=None, log=None) -> StoreSettingsWindow`
- `open_stores_management_window(parent, log=None) -> None`
- `tk_main.main() -> None`
- `main.run_smoke_test() -> None`

## Known Gaps And Risks

1. Flipp provider is a stub. Deal sync has no real provider data.
2. UI currently bypasses services in multiple windows. Port should correct this.
3. `azure_docint_client.py` mixes OCR client and DB ingest. Split in C#.
4. Schema is split across core and feature modules. C# port needs a migration ledger.
5. `item_aliases.item_id` does not cascade. Decide intended delete behavior before port.
6. `stores.flipp_store_id` lacks uniqueness. Real provider sync can double-count stores.
7. `user_profile` table and JSON household config overlap. Pick one durable source.
8. Receipt undo is JSON snapshot/re-ingest, not full transactional restore.
9. Azure dependency is not installed in this environment, so integration imports fail unless requirements are installed.
10. No packaging. Python app depends on `src/` being importable.

## C# / MAUI Blazor Port Shape

Minimal target projects:

```text
GrocerySense.App        MAUI Blazor UI
GrocerySense.Core       domain records, app services, price math, planning
GrocerySense.Data       SQLite connection, migrations, repositories
GrocerySense.Integrations Azure OCR and future flyer provider clients
GrocerySense.Tests      unit/integration tests with temp SQLite
```

Lazy version: one MAUI solution with folders matching those projects first. Split projects only when compile boundaries help.

Recommended C# seams:

- `IReceiptOcrClient`
- `IFlyerProvider`

Register repositories and app services as concrete classes. Add an interface only when a second implementation or test fake requires one.

Python -> C# mapping:

- `@dataclass` -> `record` or `record struct`
- `Dict[str, Any]` -> typed record where stable, `Dictionary<string, object?>` only at OCR/raw seams
- `sqlite3.Row` -> typed mapper methods over `SqliteDataReader`
- `connection_scope()` -> `await using SqliteConnection` plus explicit transaction where needed
- `threading.Thread` + `after()` -> `Task.Run` only for CPU/file work, `await` for I/O, `InvokeAsync` for UI
- `requests` -> `HttpClient`
- `rapidfuzz` -> `FuzzySharp` or simplest local scoring port if dependency cost is too high
- JSON config atomic write -> one `AtomicJsonStore`

## Port Sequence

1. Freeze Python behavior with tests around price math, ingestion helpers, preferences, recipe filtering, meal-suggestion scoring, basket optimizer, and schema migrations.
2. Create C# domain records and SQLite schema/migration runner.
3. Port repositories with raw SQL and temp-SQLite tests.
4. Port unit normalization, multibuy parser, preferences, price history, `RecipeEngine`, and `recipes.json`.
5. Split and port receipt OCR + receipt ingestion.
6. Port meal suggestions, shopping list, planning, basket optimizer, and price alerts.
7. Build Blazor pages for receipt import, shopping list, deals, plan, preferences.
8. Add mobile file/photo picker and secure Azure credential setup.
9. Add starter data only after schema/migration runner is stable.
10. Add central crowdsourced backend later. Do not block the local app on it.

## Review Of Existing Port Plan

Plan file reviewed: `C:\Users\skftw\.claude\plans\please-do-a-full-greedy-graham.md`.

What is good:

- One `ARCHITECTURE.md` deliverable is right. No C# scaffold yet.
- SQLite + `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` is the right port base.
- Keeping Azure Document Intelligence is fine.
- Raw repository layer can port close to line-for-line.
- Calling out schema split and UI screen mapping is necessary.

What to improve:

1. Add an "actual vs intended layering" section. The current plan repeats the intended layering but misses UI -> repo and integration -> DB leaks.
2. Split `azure_docint_client.py` during port. Keeping it as one class in C# will bake in the current mixed responsibility.
3. Add migration ledger as port work, not future cleanup. Mobile app upgrades need deterministic migrations.
4. Decide JSON config vs SQLite for household preferences before port. Mobile sync/backup favors SQLite.
5. Start Blazor with 5 routes, not 19 windows. Directly cloning every Tk window wastes time.
6. Mark provider gaps plainly: Flipp unavailable, central price DB future, starter data local-only.
7. Define DTO contracts for app services before UI. This prevents Blazor pages from binding to repository rows.
8. Add mobile-specific seams: file/photo picker, secure credential storage, app lifecycle sync, offline queue if uploads appear later.
9. Keep repository interfaces out until needed. One SQLite implementation does not justify interface boilerplate.
10. Add golden tests from Python before porting math. Unit normalization and multibuy parsing are where silent money bugs live.

Top recommendation: port behavior from the service layer inward first, not screen-by-screen. The money-saving logic is the product; Blazor can be thin once services are stable.
