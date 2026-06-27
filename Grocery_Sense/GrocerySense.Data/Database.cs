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
