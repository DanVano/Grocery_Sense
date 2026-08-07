using Microsoft.Data.Sqlite;

namespace GrocerySense.Data;

/// <summary>
/// Schema + migrations — port of reference-python/src/Grocery_Sense/data/schema.py, restructured
/// into a numbered migration ledger (the playbook's required shape for deterministic mobile upgrades).
///
/// Each entry in <see cref="_migrations"/> is one ordered, transactional step; its 1-based index is the
/// schema_version it produces. Migration 1 creates the full v1 schema in one place — the historical
/// Python create_tables / _migrate / __new-rebuild dance collapses to a single create because v1 is a
/// clean start (no Python DB is ever migrated into C#). Append v2+ steps below; never edit a shipped one.
///
/// Divergences from schema.py (deliberate, see PORTING.md):
///   - Money columns (amounts, unit_price, total_price, line_total, discount) are TEXT, not REAL —
///     Microsoft.Data.Sqlite round-trips `decimal` losslessly as TEXT; REAL would drop cents.
///   - stores.distance_km is dropped (optimizer redesign cut distance/gas from v1).
///   - member_requests + user_profile tables omitted (family picks -> v2; preferences stay in config JSON).
/// </summary>
public static class Database
{
    // Ordered, append-only. _migrations[v-1] is the script that upgrades the DB to version v.
    private static readonly string[] _migrations =
    {
        // ----- Migration 1: initial v1 schema -----
        // Parents before children so forward FK references always resolve while foreign_keys is ON.
        """
        CREATE TABLE stores (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT NOT NULL,
            address         TEXT,
            city            TEXT,
            postal_code     TEXT,
            flipp_store_id  TEXT,
            is_favorite     INTEGER NOT NULL DEFAULT 0,
            priority        INTEGER NOT NULL DEFAULT 0,
            shop_here       INTEGER NOT NULL DEFAULT 1,
            is_active       INTEGER NOT NULL DEFAULT 1,
            notes           TEXT,
            created_at      TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX idx_stores_name ON stores(name);

        CREATE TABLE items (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            canonical_name       TEXT NOT NULL UNIQUE,
            category             TEXT,
            default_unit         TEXT,
            typical_package_size REAL,
            typical_package_unit TEXT,
            is_tracked           INTEGER NOT NULL DEFAULT 1,
            notes                TEXT,
            created_at           TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX idx_items_name ON items(canonical_name);

        CREATE TABLE receipts (
            id                       INTEGER PRIMARY KEY AUTOINCREMENT,
            store_id                 INTEGER NOT NULL,
            purchase_date            TEXT NOT NULL,
            subtotal_amount          TEXT,
            tax_amount               TEXT,
            total_amount             TEXT,
            source                   TEXT NOT NULL,
            file_path                TEXT,
            image_overall_confidence INTEGER,
            keep_image_until         TEXT,
            azure_request_id         TEXT,
            created_at               TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (store_id) REFERENCES stores(id) ON DELETE CASCADE
        );
        CREATE INDEX idx_receipts_store_date ON receipts(store_id, purchase_date);
        CREATE INDEX idx_receipts_purchase_date ON receipts(purchase_date);

        CREATE TABLE flyer_sources (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            provider    TEXT NOT NULL,
            external_id TEXT,
            store_id    INTEGER NOT NULL,
            valid_from  TEXT NOT NULL,
            valid_to    TEXT NOT NULL,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (store_id) REFERENCES stores(id) ON DELETE CASCADE
        );
        CREATE INDEX idx_flyer_validity ON flyer_sources(store_id, valid_from, valid_to);

        CREATE TABLE prices (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id          INTEGER NOT NULL,
            store_id         INTEGER NOT NULL,
            receipt_id       INTEGER,
            flyer_source_id  INTEGER,
            source           TEXT NOT NULL,
            date             TEXT NOT NULL,
            unit_price       TEXT NOT NULL,
            unit             TEXT NOT NULL,
            quantity         REAL,
            total_price      TEXT,
            raw_name         TEXT,
            confidence       INTEGER,
            norm_unit_price  REAL,              -- normalized comparison price (per kg/each); set by UnitNormalization (Phase 3)
            norm_unit        TEXT,
            norm_note        TEXT,
            created_at       TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (item_id)         REFERENCES items(id)         ON DELETE CASCADE,
            FOREIGN KEY (store_id)        REFERENCES stores(id)        ON DELETE CASCADE,
            FOREIGN KEY (receipt_id)      REFERENCES receipts(id)      ON DELETE CASCADE,
            FOREIGN KEY (flyer_source_id) REFERENCES flyer_sources(id) ON DELETE CASCADE
        );
        CREATE INDEX idx_prices_item_date ON prices(item_id, date);
        CREATE INDEX idx_prices_item_store_date ON prices(item_id, store_id, date);
        CREATE INDEX idx_prices_flyer_source_id ON prices(flyer_source_id);
        CREATE INDEX idx_prices_source_date ON prices(source, date);
        CREATE INDEX idx_prices_date ON prices(date);
        CREATE INDEX idx_prices_item_coalesced ON prices(item_id, date(COALESCE(date, created_at)));
        CREATE INDEX idx_prices_receipt_id ON prices(receipt_id);

        CREATE TABLE receipt_line_items (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            receipt_id   INTEGER NOT NULL,
            line_index   INTEGER NOT NULL,
            item_id      INTEGER,
            description  TEXT,
            quantity     REAL,
            unit_price   TEXT,
            line_total   TEXT,
            discount     TEXT,
            confidence   INTEGER,
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE,
            FOREIGN KEY (item_id)    REFERENCES items(id)    ON DELETE SET NULL
        );
        CREATE INDEX idx_receipt_line_items_receipt_id ON receipt_line_items(receipt_id);
        CREATE INDEX idx_receipt_line_items_item_id ON receipt_line_items(item_id);

        CREATE TABLE item_aliases (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            alias_text   TEXT NOT NULL UNIQUE,
            item_id      INTEGER NOT NULL,
            confidence   REAL NOT NULL DEFAULT 1.0,
            source       TEXT NOT NULL DEFAULT 'manual',
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            last_seen_at TEXT,
            times_seen   INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (item_id) REFERENCES items(id)
        );
        CREATE INDEX idx_item_aliases_item_id ON item_aliases(item_id);

        CREATE TABLE shopping_list (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id             INTEGER,
            display_name        TEXT NOT NULL,
            quantity            REAL,
            unit                TEXT,
            category            TEXT,
            planned_store_id    INTEGER,
            added_by            TEXT,
            added_by_member_id  INTEGER,
            added_at            TEXT NOT NULL DEFAULT (datetime('now')),
            is_checked_off      INTEGER NOT NULL DEFAULT 0,
            is_active           INTEGER NOT NULL DEFAULT 1,
            is_deleted          INTEGER NOT NULL DEFAULT 0,
            notes               TEXT,
            FOREIGN KEY (item_id)          REFERENCES items(id)  ON DELETE SET NULL,
            FOREIGN KEY (planned_store_id) REFERENCES stores(id) ON DELETE SET NULL
        );
        CREATE INDEX idx_shopping_list_active
            ON shopping_list(is_active, is_deleted, is_checked_off, planned_store_id);

        CREATE TABLE deleted_receipt_backups (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            original_receipt_id INTEGER,
            deleted_at          TEXT NOT NULL,
            backup_json         TEXT NOT NULL
        );

        CREATE TABLE receipt_raw_json (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            receipt_id   INTEGER NOT NULL UNIQUE,
            operation_id TEXT,
            json_path    TEXT,
            raw_json     TEXT,
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
        );

        CREATE TABLE receipt_file_hashes (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            file_hash   TEXT NOT NULL UNIQUE,
            receipt_id  INTEGER NOT NULL,
            file_path   TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
        );

        CREATE TABLE receipt_signatures (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            signature   TEXT NOT NULL UNIQUE,
            receipt_id  INTEGER NOT NULL,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
        );

        -- Flyer subsystem: feature-local tables flyers_repo.py self-creates (folded in here per the
        -- playbook). store_id stays a loose int (no FK, matching Python). Money columns are TEXT;
        -- item_id is INTEGER from the start (the legacy TEXT->INT rebuild is N/A for a clean start).
        CREATE TABLE flyer_batches (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            store_id    INTEGER NOT NULL,
            valid_from  TEXT,
            valid_to    TEXT,
            source_type TEXT,
            source_ref  TEXT,
            note        TEXT,
            status      TEXT DEFAULT 'active',
            imported_at TEXT NOT NULL
        );
        CREATE INDEX idx_flyer_batches_store_id ON flyer_batches(store_id);
        CREATE INDEX idx_flyer_batches_status ON flyer_batches(status);
        CREATE INDEX idx_flyer_batches_valid ON flyer_batches(valid_from, valid_to);

        CREATE TABLE flyer_assets (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            flyer_id   INTEGER NOT NULL,
            asset_type TEXT NOT NULL,
            path       TEXT NOT NULL,
            sha256     TEXT,
            created_at TEXT NOT NULL,
            FOREIGN KEY (flyer_id) REFERENCES flyer_batches(id) ON DELETE CASCADE
        );

        CREATE TABLE flyer_raw_json (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            flyer_id   INTEGER NOT NULL,
            sha256     TEXT,
            json       TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (flyer_id) REFERENCES flyer_batches(id) ON DELETE CASCADE
        );

        CREATE TABLE flyer_deals (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            flyer_id           INTEGER NOT NULL,
            asset_id           INTEGER,
            store_id           INTEGER NOT NULL,
            page_index         INTEGER,
            title              TEXT,
            description        TEXT,
            price_text         TEXT,
            deal_qty           REAL,
            deal_total         TEXT,
            unit_price         TEXT,
            unit               TEXT,
            norm_unit_price    TEXT,
            norm_unit          TEXT,
            norm_note          TEXT,
            item_id            INTEGER,
            mapping_confidence REAL,
            confidence         REAL,
            created_at         TEXT NOT NULL,
            FOREIGN KEY (flyer_id) REFERENCES flyer_batches(id) ON DELETE CASCADE,
            FOREIGN KEY (asset_id) REFERENCES flyer_assets(id) ON DELETE SET NULL
        );
        CREATE INDEX idx_flyer_deals_flyer_id ON flyer_deals(flyer_id);
        CREATE INDEX idx_flyer_deals_store_id ON flyer_deals(store_id);
        CREATE INDEX idx_flyer_deals_item_id ON flyer_deals(item_id);
        """,

        // ----- Migration 2: price-drop alerts (PriceDropAlertService, Phase 4) -----
        // Port of the table price_drop_alert_service.py self-creates. Prices are unit-price doubles (REAL),
        // matching the engine math + the PriceDropAlert record. The Python "add missing column" ALTER cruft
        // is N/A on a clean start. suggested_qty is computed-but-not-persisted (Python omits it too).
        """
        CREATE TABLE price_drop_alerts (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id               INTEGER,
            store_id              INTEGER,
            store_name            TEXT,
            item_name             TEXT,
            current_price         REAL,
            usual_price           REAL,
            pct_below_usual       REAL,
            six_month_low         REAL,
            pct_above_low         REAL,
            alert_kind            TEXT,
            is_staple             INTEGER NOT NULL DEFAULT 0,
            receipt_samples       INTEGER NOT NULL DEFAULT 0,
            basis                 TEXT,
            source                TEXT,
            last_seen_at_or_below TEXT,
            notes                 TEXT,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            status                TEXT NOT NULL DEFAULT 'open',
            dismissed_at          TEXT
        );
        CREATE INDEX idx_price_drop_alerts_status ON price_drop_alerts(status);
        CREATE INDEX idx_price_drop_alerts_created_at ON price_drop_alerts(created_at);
        CREATE INDEX idx_price_drop_alerts_dismissed
            ON price_drop_alerts(dismissed_at) WHERE dismissed_at IS NOT NULL;
        """,

        // ----- Migration 3: shopping_list priority (must_have | normal | wait_for_sale) -----
        // Household "buy this now / only on sale" label. NOT NULL DEFAULT backfills existing rows to 'normal'.
        """
        ALTER TABLE shopping_list ADD COLUMN priority TEXT NOT NULL DEFAULT 'normal';
        """,

        // ----- Migration 4: savings watchlist -----
        // User-curated items to watch for a deal. target_price NULL => alert on any good deal (falls back to
        // the optimizer's MinItemSavingPct below usual). is_active soft-toggles a removed/paused watch.
        """
        CREATE TABLE watchlist (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id      INTEGER NOT NULL,
            target_price REAL,
            is_active    INTEGER NOT NULL DEFAULT 1,
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE CASCADE
        );
        CREATE INDEX idx_watchlist_active ON watchlist(is_active);
        """,

        // ----- Migration 5: family meal-picks (member_requests, v2 Phase 5) -----
        // Port of the table member_requests_repo.py self-creates. member_id is a config-JSON member id (no DB
        // members table -> no FK, matching Python). item_row_ids is a JSON array of shopping_list ids the pick
        // created, so a parent review can soft-delete exactly those rows.
        """
        CREATE TABLE member_requests (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            member_id    INTEGER,
            member_name  TEXT NOT NULL DEFAULT '',
            kind         TEXT NOT NULL,
            label        TEXT NOT NULL DEFAULT '',
            item_row_ids TEXT NOT NULL DEFAULT '[]',
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            reviewed     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX idx_member_requests_reviewed ON member_requests(reviewed);
        """,

        // ----- Migration 6: persist stock-up suggested quantity on alerts -----
        // Migration 2 kept suggested_qty compute-only (matching Python); the Savings page lost the hint on
        // reload. Persisted now so the "buy N" guidance survives the round-trip.
        """
        ALTER TABLE price_drop_alerts ADD COLUMN suggested_qty REAL;
        ALTER TABLE price_drop_alerts ADD COLUMN suggested_qty_note TEXT;
        """,

        // ----- Migration 7: index case-insensitive canonical item lookup (perf) -----
        // Exact item lookups compare case-insensitively (GetItemByName / GetItemsByNames). The BINARY
        // idx_items_name can't serve a NOCASE predicate, so those seeks fell back to a scan as the catalog
        // grew. A NOCASE index lets `canonical_name = ? COLLATE NOCASE` seek instead. Additive index only.
        "CREATE INDEX idx_items_name_nocase ON items(canonical_name COLLATE NOCASE);",

        // ----- Migration 8: user-entered recipes (custom family recipes, food-savings follow-up) -----
        // Merged into the RecipeEngine catalog at load; user recipes shadow same-name catalog recipes.
        // List columns are JSON arrays of strings (decoded defensively, junk -> []). No item_id column,
        // so ItemsAdminRepo.ItemIdTables / the FK-sweep test are deliberately untouched.
        // Renumbered from 7->8 during the origin/main merge (2026-07-21): origin/main's perf-index
        // migration claimed 7 first (already on origin/main); this table keeps its rows, just a new number.
        """
        CREATE TABLE user_recipes (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
            servings    INTEGER,
            ingredients TEXT NOT NULL DEFAULT '[]',
            steps       TEXT NOT NULL DEFAULT '[]',
            tags        TEXT NOT NULL DEFAULT '[]',
            created_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """,

        // ----- Migration 9: index the coalesced-date expression for the staple scan (perf) -----
        // ListStapleItemIds is the one prices query with no item_id bound — it filters on
        // date(COALESCE(date, created_at)) >= cutoff and GROUPs by item_id. The existing
        // idx_prices_item_coalesced leads with item_id, so it can't serve a date-only range; that scan
        // read the whole prices table to keep a ~90-day slice. This bare functional index (same expression,
        // no item_id prefix) lets the range seek instead, so the cost tracks the window, not total history.
        // Additive index only; no data change.
        "CREATE INDEX idx_prices_coalesced_date ON prices(date(COALESCE(date, created_at)));",

        // ----- Migration 10: the confirmed Smart Week plan (V3 Phase 3, grill Q11) -----
        // SINGLETON row (CHECK id = 1, INSERT OR REPLACE): the one currently-confirmed plan. Lives in
        // SQLite, not config JSON, so it commits atomically WITH the shopping-list upsert and rides DB
        // backup/export. snapshot_json holds recipe ids/names, confirmed goals, and reviewed
        // ingredient->item mappings — item ids inside JSON escape MergeItems remapping (the FK sweep only
        // covers item_id COLUMNS), so every read path must validate ids and fall back by normalized name.
        """
        CREATE TABLE selected_smart_week_plan (
            id            INTEGER PRIMARY KEY CHECK (id = 1),
            week_start    TEXT NOT NULL,
            confirmed_at  TEXT NOT NULL,
            snapshot_json TEXT NOT NULL
        );
        """,
    };

    /// <summary>Highest schema version this build knows how to produce.</summary>
    public static int LatestVersion => _migrations.Length;

    /// <summary>Opens a connection via the factory and applies any pending migrations.</summary>
    public static void Initialize(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        Initialize(conn);
    }

    /// <summary>
    /// Applies every migration newer than the DB's current schema_version, each in its own
    /// transaction (rollback on failure). Idempotent: a DB already at <see cref="LatestVersion"/>
    /// is left untouched, so re-running never drops or rewrites existing rows.
    /// </summary>
    public static void Initialize(SqliteConnection conn)
    {
        EnsureVersionTable(conn);
        var current = GetVersion(conn);

        // P1-5 newer-schema guard: a DB written by a NEWER app (e.g. restored onto an older install) must
        // fail loud with both numbers — never migrate down, never open blind and let mismatched code
        // corrupt data the newer schema owns.
        if (current > _migrations.Length)
            throw new InvalidOperationException(
                $"This database is schema version {current}, but this app only supports up to " +
                $"{_migrations.Length}. It was created by a newer version of Grocery Sense — " +
                "update the app, or restore a backup made by this version.");

        for (var version = current + 1; version <= _migrations.Length; version++)
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = _migrations[version - 1];
                cmd.ExecuteNonQuery();
            }
            SetVersion(conn, tx, version);
            tx.Commit();
        }
    }

    private static void EnsureVersionTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    private static int GetVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static void SetVersion(SqliteConnection conn, SqliteTransaction tx, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($v);";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.ExecuteNonQuery();
    }
}
