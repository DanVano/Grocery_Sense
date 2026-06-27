"""
pricebrain.data.schema

SQLite schema definition and initialization for the Price app backend.
"""

from typing import Optional
import sqlite3


_SCHEMA_VERSION = 1  # bump when adding a new numbered migration below


def _get_schema_version(cur: sqlite3.Cursor) -> int:
    cur.execute(
        "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)"
    )
    row = cur.execute("SELECT version FROM schema_version LIMIT 1").fetchone()
    return int(row[0]) if row else 0


def _set_schema_version(cur: sqlite3.Cursor, version: int) -> None:
    cur.execute("DELETE FROM schema_version")
    cur.execute("INSERT INTO schema_version (version) VALUES (?)", (version,))


def create_tables(conn: sqlite3.Connection) -> None:
    """
    Create all tables if they do not exist.

    Run this once at startup (safe to call multiple times).
    """
    cur = conn.cursor()

    # --- stores ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS stores (
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
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_stores_name ON stores(name);"
    )

    # --- items ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS items (
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
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_items_name ON items(canonical_name);"
    )

    # --- receipts ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS receipts (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            store_id                INTEGER NOT NULL,
            purchase_date           TEXT NOT NULL,           -- 'YYYY-MM-DD'
            subtotal_amount         REAL,                    -- pre-tax
            tax_amount              REAL,
            total_amount            REAL,                    -- subtotal + tax
            source                  TEXT NOT NULL,           -- 'receipt' | 'manual'
            file_path               TEXT,                    -- temp path to image/pdf
            image_overall_confidence INTEGER,                -- 1-5
            keep_image_until        TEXT,                    -- date to keep image until
            azure_request_id        TEXT,
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (store_id) REFERENCES stores(id) ON DELETE CASCADE
        );
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_receipts_store_date
        ON receipts(store_id, purchase_date);
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_receipts_purchase_date ON receipts(purchase_date);"
    )

    # --- flyer_sources ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS flyer_sources (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            provider    TEXT NOT NULL,           -- e.g. 'flipp'
            external_id TEXT,                    -- flipp flyer id
            store_id    INTEGER NOT NULL,        -- link to stores
            valid_from  TEXT NOT NULL,           -- 'YYYY-MM-DD'
            valid_to    TEXT NOT NULL,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (store_id) REFERENCES stores(id) ON DELETE CASCADE
        );
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_flyer_validity
        ON flyer_sources(store_id, valid_from, valid_to);
        """
    )

    # --- prices ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS prices (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id          INTEGER NOT NULL,
            store_id         INTEGER NOT NULL,
            receipt_id       INTEGER,           -- nullable if source != 'receipt'
            flyer_source_id  INTEGER,           -- nullable if source != 'flyer'
            source           TEXT NOT NULL,     -- 'receipt' | 'flyer' | 'manual'
            date             TEXT NOT NULL,     -- 'YYYY-MM-DD'
            unit_price       REAL NOT NULL,     -- pre-tax, normalized (e.g. per kg)
            unit             TEXT NOT NULL,     -- 'kg', 'lb', 'each', etc.
            quantity         REAL,              -- actual quantity (e.g. 1.25 kg)
            total_price      REAL,              -- line total pre-tax
            raw_name         TEXT,              -- original text from receipt/flyer
            confidence       INTEGER,           -- 1-5 mapping confidence
            created_at       TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (item_id)         REFERENCES items(id)         ON DELETE CASCADE,
            FOREIGN KEY (store_id)        REFERENCES stores(id)        ON DELETE CASCADE,
            FOREIGN KEY (receipt_id)      REFERENCES receipts(id)      ON DELETE CASCADE,
            FOREIGN KEY (flyer_source_id) REFERENCES flyer_sources(id) ON DELETE CASCADE
        );
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_prices_item_date
        ON prices(item_id, date);
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_prices_item_store_date
        ON prices(item_id, store_id, date);
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_prices_flyer_source_id
        ON prices(flyer_source_id);
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_prices_source_date
        ON prices(source, date);
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_prices_date ON prices(date);"
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_prices_item_coalesced "
        "ON prices(item_id, date(COALESCE(date, created_at)));"
    )
    # Supports the ON DELETE CASCADE from receipts -> prices; without it a
    # receipt delete / undo / re-ingest full-scans prices to find rows to cascade.
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_prices_receipt_id ON prices(receipt_id);"
    )

    # --- receipt_line_items ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS receipt_line_items (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            receipt_id   INTEGER NOT NULL,
            line_index   INTEGER NOT NULL,
            item_id      INTEGER,
            description  TEXT,
            quantity     REAL,
            unit_price   REAL,
            line_total   REAL,
            discount     REAL,
            confidence   INTEGER,
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE,
            FOREIGN KEY (item_id)    REFERENCES items(id)    ON DELETE SET NULL
        );
        """
    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_receipt_line_items_receipt_id
        ON receipt_line_items(receipt_id);
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_receipt_line_items_item_id "
        "ON receipt_line_items(item_id);"
    )

    # --- Fuzzy matching ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS item_aliases (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            alias_text TEXT NOT NULL UNIQUE,
            item_id INTEGER NOT NULL,
            confidence REAL NOT NULL DEFAULT 1.0,
            source TEXT NOT NULL DEFAULT 'manual',
            created_at TEXT NOT NULL DEFAULT (datetime('now')),
            last_seen_at TEXT,
            times_seen INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY(item_id) REFERENCES items(id)
        );
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_item_aliases_item_id ON item_aliases(item_id);"
    )

    # --- shopping_list ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS shopping_list (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id             INTEGER,           -- nullable, we may not know canonical item yet
            display_name        TEXT NOT NULL,     -- what user typed/spoke
            quantity            REAL,
            unit                TEXT,
            category            TEXT,
            planned_store_id    INTEGER,           -- nullable
            added_by            TEXT,
            added_by_member_id  INTEGER,           -- nullable FK to config member id
            added_at            TEXT NOT NULL DEFAULT (datetime('now')),
            is_checked_off      INTEGER NOT NULL DEFAULT 0,  -- 0/1
            is_active           INTEGER NOT NULL DEFAULT 1,  -- 0/1
            is_deleted          INTEGER NOT NULL DEFAULT 0,  -- soft-delete flag
            notes               TEXT,
            FOREIGN KEY (item_id)          REFERENCES items(id)   ON DELETE SET NULL,
            FOREIGN KEY (planned_store_id) REFERENCES stores(id)  ON DELETE SET NULL
        );
        """

    )
    cur.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_shopping_list_active
        ON shopping_list(is_active, is_deleted, is_checked_off, planned_store_id);
        """
    )

    # --- member_requests ("family picks": a member picks a meal/item, parent reviews) ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS member_requests (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            member_id    INTEGER,                 -- household member id (lives in config JSON, not a DB FK)
            member_name  TEXT,                    -- snapshot for display
            kind         TEXT NOT NULL,           -- 'meal' | 'item'
            label        TEXT NOT NULL,           -- recipe name or item text
            item_row_ids TEXT,                    -- JSON list of shopping_list.id this pick created
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            reviewed     INTEGER NOT NULL DEFAULT 0
        );
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS idx_member_requests_unreviewed "
        "ON member_requests(reviewed, id);"
    )

    # --- user_profile ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS user_profile (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            household_name      TEXT,
            postal_code         TEXT,
            currency            TEXT DEFAULT 'CAD',
            preferred_store_ids TEXT,           -- e.g. '1,2,5' or JSON string
            eats_chicken        INTEGER NOT NULL DEFAULT 1,
            eats_beef           INTEGER NOT NULL DEFAULT 1,
            eats_pork           INTEGER NOT NULL DEFAULT 1,
            eats_fish           INTEGER NOT NULL DEFAULT 1,
            is_vegetarian       INTEGER NOT NULL DEFAULT 0,
            is_gluten_free      INTEGER NOT NULL DEFAULT 0,
            has_nut_allergy     INTEGER NOT NULL DEFAULT 0,
            created_at          TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at          TEXT
        );
        """
    )

    # --- deleted_receipt_backups (Undo) ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS deleted_receipt_backups (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            original_receipt_id INTEGER,
            deleted_at TEXT NOT NULL,
            backup_json TEXT NOT NULL
        );
        """
    )

    # --- receipt_raw_json (canonical: id PK + UNIQUE(receipt_id)) ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS receipt_raw_json (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            receipt_id   INTEGER NOT NULL UNIQUE,
            operation_id TEXT,
            json_path    TEXT,
            raw_json     TEXT,
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
        );
        """
    )

    # --- receipt_file_hashes (canonical: id PK + UNIQUE(file_hash)) ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS receipt_file_hashes (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            file_hash   TEXT NOT NULL UNIQUE,
            receipt_id  INTEGER NOT NULL,
            file_path   TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
        );
        """
    )

    # --- receipt_signatures (canonical: id PK + UNIQUE(signature)) ---
    cur.execute(
        """
        CREATE TABLE IF NOT EXISTS receipt_signatures (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            signature   TEXT NOT NULL UNIQUE,
            receipt_id  INTEGER NOT NULL,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
        );
        """
    )

    # schema_version table (ledger for numbered migrations)
    cur.execute(
        "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)"
    )

    conn.commit()


def _table_first_col(conn: sqlite3.Connection, table: str) -> Optional[str]:
    rows = conn.execute(f"PRAGMA table_info({table})").fetchall()
    if not rows:
        return None
    return rows[0][1]


def _has_unique_on(conn: sqlite3.Connection, table: str, col: str) -> bool:
    """True if there is a UNIQUE index covering exactly `col` on `table`."""
    indexes = conn.execute(f"PRAGMA index_list({table})").fetchall()
    for idx in indexes:
        # idx columns: (seq, name, unique, origin, partial)
        if not idx[2]:
            continue
        info = conn.execute(f"PRAGMA index_info({idx[1]})").fetchall()
        # info columns: (seqno, cid, name)
        if len(info) == 1 and info[0][2] == col:
            return True
    return False


def _migrate_receipt_support_tables(conn: sqlite3.Connection) -> None:
    """Rebuild legacy variants of receipt_raw_json / receipt_file_hashes /
    receipt_signatures into the canonical (id PK + UNIQUE natural-key) shape.

    Handles two legacy shapes:
      (a) azure_docint pre-canonical: natural-key column was the PRIMARY KEY.
      (b) receipts_repo pre-canonical: id PK already, but no UNIQUE on
          receipt_raw_json.receipt_id (signatures/file_hashes already UNIQUE).
    """
    cur = conn.cursor()

    # FK enforcement must be off during structural rebuild per SQLite docs.
    cur.execute("PRAGMA foreign_keys = OFF;")
    try:
        # --- receipt_raw_json ---
        first = _table_first_col(conn, "receipt_raw_json")
        if first is not None:
            needs_rebuild = first != "id" or not _has_unique_on(conn, "receipt_raw_json", "receipt_id")
            if needs_rebuild:
                cur.executescript(
                    """
                    CREATE TABLE receipt_raw_json__new (
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        receipt_id   INTEGER NOT NULL UNIQUE,
                        operation_id TEXT,
                        json_path    TEXT,
                        raw_json     TEXT,
                        created_at   TEXT NOT NULL DEFAULT (datetime('now')),
                        FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
                    );
                    INSERT INTO receipt_raw_json__new (receipt_id, operation_id, json_path, raw_json, created_at)
                        SELECT receipt_id, operation_id, json_path, raw_json, created_at
                        FROM receipt_raw_json
                        WHERE rowid IN (
                            SELECT MAX(rowid) FROM receipt_raw_json GROUP BY receipt_id
                        );
                    DROP TABLE receipt_raw_json;
                    ALTER TABLE receipt_raw_json__new RENAME TO receipt_raw_json;
                    """
                )

        # --- receipt_file_hashes ---
        first = _table_first_col(conn, "receipt_file_hashes")
        if first is not None and first != "id":
            cur.executescript(
                """
                CREATE TABLE receipt_file_hashes__new (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_hash   TEXT NOT NULL UNIQUE,
                    receipt_id  INTEGER NOT NULL,
                    file_path   TEXT,
                    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
                );
                INSERT INTO receipt_file_hashes__new (file_hash, receipt_id, file_path, created_at)
                    SELECT file_hash, receipt_id, file_path, created_at FROM receipt_file_hashes;
                DROP TABLE receipt_file_hashes;
                ALTER TABLE receipt_file_hashes__new RENAME TO receipt_file_hashes;
                """
            )

        # --- receipt_signatures ---
        first = _table_first_col(conn, "receipt_signatures")
        if first is not None and first != "id":
            cur.executescript(
                """
                CREATE TABLE receipt_signatures__new (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    signature   TEXT NOT NULL UNIQUE,
                    receipt_id  INTEGER NOT NULL,
                    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (receipt_id) REFERENCES receipts(id) ON DELETE CASCADE
                );
                INSERT INTO receipt_signatures__new (signature, receipt_id, created_at)
                    SELECT signature, receipt_id, created_at FROM receipt_signatures;
                DROP TABLE receipt_signatures;
                ALTER TABLE receipt_signatures__new RENAME TO receipt_signatures;
                """
            )

        conn.commit()
    finally:
        cur.execute("PRAGMA foreign_keys = ON;")


def _migrate(conn: sqlite3.Connection) -> None:
    """Apply incremental migrations for columns added after the initial schema."""
    cur = conn.cursor()

    current_version = _get_schema_version(cur)

    # -------------------------------------------------------------------------
    # Pre-ledger migrations (PRAGMA probes) — applied when version == 0.
    # These ran before the schema_version table existed; stamp as version 1
    # once they are done so future migrations can key off the integer.
    # -------------------------------------------------------------------------
    if current_version < 1:
        existing = {row[1] for row in cur.execute("PRAGMA table_info(shopping_list)").fetchall()}
        for col, sql in [
            ("category",           "ALTER TABLE shopping_list ADD COLUMN category TEXT"),
            ("added_by_member_id", "ALTER TABLE shopping_list ADD COLUMN added_by_member_id INTEGER"),
            ("is_deleted",         "ALTER TABLE shopping_list ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0"),
        ]:
            if col not in existing:
                cur.execute(sql)

        stores_cols = {row[1] for row in cur.execute("PRAGMA table_info(stores)").fetchall()}
        if "shop_here" not in stores_cols:
            cur.execute("ALTER TABLE stores ADD COLUMN shop_here INTEGER NOT NULL DEFAULT 1")
        if "is_active" not in stores_cols:
            cur.execute("ALTER TABLE stores ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1")
        if "distance_km" not in stores_cols:
            cur.execute("ALTER TABLE stores ADD COLUMN distance_km REAL")

        cur.execute("DROP INDEX IF EXISTS idx_shopping_list_active")
        cur.execute(
            "CREATE INDEX IF NOT EXISTS idx_shopping_list_active "
            "ON shopping_list(is_active, is_deleted, is_checked_off, planned_store_id)"
        )
        _set_schema_version(cur, 1)

    # -------------------------------------------------------------------------
    # Add new numbered migrations here (version 2, 3, …):
    #
    # if current_version < 2:
    #     cur.execute("ALTER TABLE foo ADD COLUMN bar TEXT")
    #     _set_schema_version(cur, 2)
    # -------------------------------------------------------------------------

    conn.commit()
    _migrate_receipt_support_tables(conn)


def initialize_database() -> None:
    """
    Convenience helper: open a connection, create tables, close it.

    Call this once at app startup in your Tkinter / CLI entrypoint.
    """
    from .connection import get_connection  # local import to avoid cycles

    conn = get_connection()
    try:
        create_tables(conn)
        _migrate(conn)
    finally:
        conn.close()
