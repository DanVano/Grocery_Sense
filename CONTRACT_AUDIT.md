# Contract Audit — Python public API → C# v1 (PORTING Phase 0.B)

Every public Python API tracked as **Port** (bring as-is), **Replace** (behavior/shape changes in C#), or
**Defer** (v2 — leave unported). Source: `reference-python/ARCHITECTURE.md` Public I/O Inventory, with
`prices_repo` verified directly (the file the scaffold trimmed). Classification follows the locked v1
decisions in `PORTING.md` ("v1 Product Decisions") + `brainstorms/2026-06-24-…md`.

Legend: **Port** = port faithfully · **Replace** = port with changed behavior (see note) · **Defer** = v2, skip now.
"Done?" tracks build progress — all currently ☐.

## v1 critical path (what Phases 2–4 MUST port)
`connection` + `schema(→ledger)` + **all of `prices_repo` (22 fns)** + `items_repo` + `item_aliases_repo` +
`receipts_repo` + `shopping_list_repo` + `stores_repo` + `flyers_repo`(CRUD) → then `unit_normalization`,
`multibuy`, `price_history`, `price_drop_alert`, `ingredient_mapping`, single-profile `preferences`,
`shopping_list_service`, `budget_service`, **redesigned `basket_optimizer`**, `flyer_ingest`, receipt ingest.

---

## Data layer

### `data/connection.py` — Port (foundation)
| API | Status | C# target | Note |
|---|---|---|---|
| `get_connection`, `connection_scope`, `current_db_path`, `get_db_path`, `reset_integrity_cache` | **Port** | `SqliteConnectionFactory` | pragmas (FK/WAL/synchronous/busy_timeout/UTF-8) + per-path integrity check |

### `data/schema.py` — Replace (→ migration ledger)
| API | Status | C# target | Note |
|---|---|---|---|
| `create_tables`, `initialize_database` | **Replace** | `Database` | restructure into a **numbered migration ledger** (schema_version + ordered steps); fold in feature-local DDL Python self-creates |

### `data/repositories/stores_repo.py` — Port (store mgmt is v1)
| API | Status | C# target | Note |
|---|---|---|---|
| `create_store`, `get_store_by_id`, `list_stores`, `set_store_favorite`, `set_store_shop_here`, `update_store_address`, `update_store`, `set_store_active` | **Port** | `StoresRepo` | |
| `set_store_distance_km` | **Defer** | — | distance cut from optimizer (Q12); no v1 consumer |
| `delete_store` | **Defer** | — | test-only in Python |
| `upsert_store_from_flipp` | **Defer** | — | real Flipp provider = v2 |

### `data/repositories/items_repo.py` — Port (all)
| API | Status | C# target | Note |
|---|---|---|---|
| `create_item`, `get_item_by_id`, `get_item_by_name`, `list_all_item_names`, `list_items`, `set_item_tracked`, `get_items_by_ids`, `get_items_by_names`, `update_item_notes` | **Port** | `ItemsRepo` | batch fns chunk at 900 params |

### `data/repositories/item_aliases_repo.py` — Port (fuzzy mapping needs it)
| API | Status | C# target | Note |
|---|---|---|---|
| `get_by_alias`, `upsert_alias`, `mark_seen`, `list_all` | **Port** | `ItemAliasesRepo` | optional `conn` param → deferred-write batching |

### `data/repositories/prices_repo.py` — Port (ALL 22 — load-bearing)
| API | Status | C# target | Note |
|---|---|---|---|
| `add_price_point`, `get_prices_for_item`, `get_most_recent_price`, `get_price_stats_for_item`, `add_price_points` | **Port** | `PricesRepo` | core CRUD + stats |
| `list_unit_prices`, `get_usual_unit_price`, `get_six_month_low_unit_price`, `get_last_seen_at_or_below` | **Port** | `PricesRepo` | "usual"/median + 6-mo-low math |
| `get_active_flyer_unit_price`, `list_staple_item_ids`, `get_best_current_quote_for_item_store` | **Port** | `PricesRepo` | flyer price + staple detection |
| `get_usual_unit_price_batch`, `get_six_month_low_batch`, `get_last_seen_at_or_below_batch` | **Port** | `PricesRepo` | **batch — missing from scaffold** |
| `get_most_recent_prices_by_store_batch`, `get_most_recent_prices_global_batch`, `get_active_flyer_prices_batch`, `get_price_stats_batch` | **Port** | `PricesRepo` | **batch — optimizer/alerts depend on these** |
| `get_recent_avg_unit_price_by_store_batch`, `get_recent_avg_unit_price_global_batch`, `get_purchase_cadence_batch` | **Port** | `PricesRepo` | **batch — cadence feeds stock-up qty** |

### `data/repositories/receipts_repo.py` — Port (Receipts route + Budget)
| API | Status | C# target | Note |
|---|---|---|---|
| `list_recent_receipts`, `get_receipt`, `list_receipt_line_items`, `get_receipt_raw_json` | **Port** | `ReceiptsRepo` | browse/view |
| `get_month_spend`, `get_spend_trend` | **Port** | `ReceiptsRepo` | **Budget v1 depends on these** |
| `delete_receipt_cascade`, `delete_receipt_with_backup`, `restore_receipt_from_backup`, `list_deleted_backups` | **Port** | `ReceiptsRepo` | undo via JSON snapshot |
| `ensure_receipt_support_tables` | **Replace** | `Database` | folds into the migration ledger |

### `data/repositories/shopping_list_repo.py` — Port (all)
| API | Status | C# target | Note |
|---|---|---|---|
| `list_active_items`, `list_all_items`, `get_item`, `add_item`, `bulk_add_items`, `set_checked_off`, `delete_item`, `clear_all_items`, `clear_checked_off_items`, `set_planned_store_id`, `bulk_set_planned_store_ids`, `bulk_set_planned_store_ids_by_item_id`, `clear_planned_store_ids_for_active_items` | **Port** | `ShoppingListRepo` | optimizer writes planned-store back here |

### `data/repositories/flyers_repo.py` — Port CRUD / Replace filtering
| API | Status | C# target | Note |
|---|---|---|---|
| `ensure_schema`, `upsert_store`, `list_stores`, `create_flyer_batch`, `create_batch`, `set_batch_status`, `add_asset`, `add_raw_json`, `add_deal`, `add_deals`, `insert_deals`, `compute_sha256` | **Port** | `FlyersRepo` | CRUD only; `ensure_schema`→ledger |
| `list_active_deals`, `list_deals_for_flyer` | **Replace** | `FlyersRepo` (CRUD) + service | **strip preference filtering OUT of the repo** (layering leak) → into a Deal-feed service using single-profile prefs |

### `data/repositories/member_requests_repo.py` — Defer (v2 family)
| API | Status | C# target | Note |
|---|---|---|---|
| `add_request`, `get_request`, `list_unreviewed`, `list_all`, `count_unreviewed`, `mark_reviewed`, `mark_all_reviewed` | **Defer** | — | family meal-picks = v2 |

### `domain/models.py` — Port (records, already scaffolded)
| API | Status | C# target | Note |
|---|---|---|---|
| `Store`, `Item`, `Receipt`, `PricePoint`, `PriceStats`, `ShoppingListItem` | **Port** | `GrocerySense.Domain/*` | already C# records; money→decimal, dates→DateOnly |

---

## Services (Core)

### Port as-is (core money math + v1 features)
| Module / API | Status | C# target | Note |
|---|---|---|---|
| `unit_normalization_service`: `normalize`, `guess_unit_from_text`, `get_item_default_unit`, `set_item_default_unit_if_missing`, `ensure_schema` | **Port** | `UnitNormalizationService` | core; golden-test |
| `multibuy_deal_service`: `adjust` | **Port** | `MultiBuyDealService` | core; golden-test |
| `price_history_service`: `get_or_create_item`, `ensure_item_exists`, `record_price_from_receipt`, `record_price_from_flyer`, `record_manual_price`, `get_item_stats`, `get_baseline_price(s)`, `stats_for_item_by_store`, `classify_deal`, `describe_item_history` | **Port** | `PriceHistoryService` | |
| `price_drop_alert_service`: `refresh_engine_alerts`, `compute_engine_alerts`, `get_alerts`, `get_open_alerts`, `dismiss_alert`, `scan_recent_receipts` | **Port** | `PriceDropAlertService` | keep 15%/5%/staple defaults; alerts table → migration ledger |
| `ingredient_mapping_service`: `map_to_item`, `flush_learned_aliases`, `invalidate_choices` | **Port** | `IngredientMappingService` | rapidfuzz→**FuzzySharp** (0–100 vs 0.78/0.90) |
| `shopping_list_service`: `add_items_from_text`, `add_single_item`, `get_active_items`, `summarize_list_for_display`, `soft_delete_item`, `check_off_item`, `clear_all_checked_off` (+ module wrappers) | **Port** | `ShoppingListService` | |
| `flyer_ingest_service`: `ingest_assets`, `ingest_dealrecords_json` | **Port** | `FlyerIngestService` | manual flyer import (v1) |
| `flyer_sync_service`: `needs_sync`, `run_sync` | **Port** | `FlyerSyncService` | stub-backed until provider (v2) |
| `budget_service`: `get_budget_status`, `get_trend`, `save_monthly_budget` | **Port** | `BudgetService` | **v1** |
| `budget_service`: `get_gas_cost_per_km`, `save_gas_cost_per_km` | **Defer** | — | gas unused (optimizer redesign) |

### Replace (behavior/shape changes)
| Module / API | Status | C# target | Note |
|---|---|---|---|
| `basket_optimizer_service`: `optimize` | **Replace** | `BasketOptimizerService` | **REDESIGN** — hybrid gate (≥10% / ≥$5), maxStores=3, fewest-stops/best-savings toggle, no trip penalty. Full spec + 8 golden tests in `PORTING.md` Phase 4 |
| `basket_optimizer_service`: `phrase_safe_hit` | **Port** | `BasketOptimizerService` | whole-word match helper (hard-exclude safety) |
| `preferences_service`: `compute_effective_preferences`, `get_household_baseline_profile`, hard/soft/oils accessors | **Replace** | `PreferencesService` (single-profile) | collapse to ONE profile: allergies + hard/soft excludes + oils for the deal filter |
| `preferences_service`: member-merge/consensus/star helpers (`get_star_excluders`, `annotate_*`, `get_effective_edit_state_for_member`, `validate_add_exclude`, `reset_secondary_member_to_household_baseline`, `protein_groups`), `get_meal_profile` | **Defer** | — | multi-member + meal-profile = v2 |
| `flyer_sync_scheduler`: `start`, `request_sync`, `stop` | **Replace** | `FlyerSyncScheduler` | timer → **sync-on-resume / manual button** (mobile background limits) |
| `azure_docint_client` (API half): `AzureReceiptClient.analyze_receipt_file`, `analyze_and_save_json` | **Replace** | `AzureReceiptOcrClient : IReceiptOcrClient` (Integrations) | pure OCR, no DB writes |
| `azure_docint_client` (DB half): `ingest_receipt_file_outcome`, `ingest_analyzed_receipt_into_db`, `ingest_receipt_file` | **Replace** | `ReceiptIngestionService` (Core) | dedupe + mapping + DB writes; whole ingest in a transaction |

### Defer (v2)
| Module | Status | Note |
|---|---|---|
| `planning_service`: `build_plan_for_active_list` | **Defer** | superseded by redesigned `BasketOptimizer`; revisit only if its cost-summary view is wanted |
| `deals_service`: all (`group_deals_by_store`, `choose_stores_min_trips`, `collect_favorite_ingredients`, `rank_recipes_by_deals`, `search_deals`, `suggest_stores_for_term`) | **Defer** | external-search + recipe path; v1 Deal feed sources from `FlyersRepo.list_active_deals`, not this service |
| `meal_suggestion_service`, `weekly_planner_service` | **Defer** | meal planning v2 |
| `family_requests_service` | **Defer** | family picks v2 |
| `list_audit_service`: `audit_active_list` | **Defer** | v2 |
| `db_maintenance_service`: `backup_database`, `export_to_csv`, `export_to_json` | **Defer** | v2 |
| `demo_seed_service`: `reset_all_demo_data`, `seed_demo_data` | **Defer** | v2 |

---

## Integrations
| Module / API | Status | C# target | Note |
|---|---|---|---|
| `flyer_docint_client`: `analyze_layout_file` | **Port** | `FlyerDocIntClient` | layout OCR for manual flyer import |
| `flipp_client`: `fetch_flyers_for_store` | **Port** | `FlippClient : IFlyerProvider` | stays an empty stub; behind the interface |
| `azure_docint_client` | **Replace (split)** | see Services/Replace | OCR→Integrations, ingest→Core |

---

## Config
| Module / API | Status | C# target | Note |
|---|---|---|---|
| `config_store`: `load_config`, `save_config`, `atomic_write_json`, `cache_get`, `cache_set`, `get_postal_code`, `get_store_priority`, `get_household_allergies`, `get_user_profile` | **Port** | `ConfigStore` | atomic JSON; single-profile shape |
| `config_store`: `default_member_profile`, `ensure_member_profile_defaults` | **Replace** | `ConfigStore` | single profile (forward-compatible → v2 "master member") |
| `config_store`: `list_members`, `get_member`, `get_primary_member`, `get_master_member`, `get_active_member`, `set_active_member_id`, `set_primary_member_id`, `add_member`, `rename_member`, `delete_member`, `get_member_profile`, `save_member_profile`, `is_master`, `reset_secondary_member_to_household_baseline` | **Defer** | — | multi-member = v2 |

## Recipes
| Module / API | Status | Note |
|---|---|---|
| `recipe_engine`: `load_all_recipes`, `filter_recipes_by_ingredients_and_profile`, `get_recipe_by_name`, `Recipe.*` | **Defer** | meal planning v2 |

---

## NEW in C# (no Python source — build fresh)
| Item | Where | Note |
|---|---|---|
| `IReceiptOcrClient`, `IFlyerProvider` interfaces | Core/Abstractions | PORTING 0.C; Integrations implements, App wires |
| Numbered migration ledger | `Database` | replaces implicit `_migrate` |
| Optimizer settings: `maxStores`(3), `minItemSavingPct`(10%), `minStoreSaving`($5) | `ConfigStore` | single-profile config |
| Redesigned optimizer algorithm | `BasketOptimizerService` | hybrid gate + greedy 1..N (spec in PORTING Phase 4) |
| Deal-feed preference filter | new service | the filtering pulled OUT of `FlyersRepo` |
| Local notifications | App | `Plugin.LocalNotification`; Android 13+ `POST_NOTIFICATIONS` |
| Bulk/multi-image receipt scan + printed-date extraction (+ manual fallback) | App + ReceiptIngestion | the 6-month paper backfill on-ramp |

---

## Counts (rough)
- **Port:** ~70 functions (all of prices_repo, items, aliases, receipts, shopping_list, stores(8), flyers CRUD, unit-norm, multibuy, price-history, alerts, ingredient-mapping, shopping-list-svc, flyer-ingest/sync, budget(3), config core, domain records).
- **Replace:** ~12 (schema→ledger, flyers list_* filtering, optimizer, preferences single-profile, scheduler, azure split, ensure_* DDL).
- **Defer (v2):** ~40 (items_admin, member_requests, planning_service, deals_service, meal_suggestion, weekly_planner, family_requests, list_audit, db_maintenance, demo_seed, recipe_engine, config multi-member, preferences multi-member/meal, gas-cost, distance/flipp store fns).

**Gate cleared:** the v1 repo/service surface — especially the 22 `prices_repo` functions the scaffold omitted — is now fully enumerated. Phase 2 can proceed without method-discovery churn.
