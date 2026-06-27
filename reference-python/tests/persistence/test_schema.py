"""
M1 — schema.py

Covers:
  - initialize_database creates every expected core table and index
  - create_tables is idempotent (safe to call twice)
  - _migrate is idempotent; survives a partial column state
  - Column types for the 'is_deleted' migration are honored
"""

from __future__ import annotations

from Grocery_Sense.data.connection import get_connection
from Grocery_Sense.data.schema import _migrate, create_tables, initialize_database


EXPECTED_TABLES = {
    "stores",
    "items",
    "receipts",
    "flyer_sources",
    "prices",
    "receipt_line_items",
    "item_aliases",
    "shopping_list",
    "user_profile",
}

EXPECTED_INDEXES = {
    "idx_stores_name",
    "idx_items_name",
    "idx_receipts_store_date",
    "idx_flyer_validity",
    "idx_prices_item_date",
    "idx_prices_item_store_date",
    "idx_prices_flyer_source_id",
    "idx_prices_source_date",
    "idx_prices_receipt_id",
    "idx_receipt_line_items_receipt_id",
    "idx_item_aliases_item_id",
    "idx_shopping_list_active",
}


def _table_names(conn) -> set:
    rows = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
    ).fetchall()
    return {r[0] for r in rows}


def _index_names(conn) -> set:
    rows = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'"
    ).fetchall()
    return {r[0] for r in rows}


# ---------------------------------------------------------------------------
# Table + index presence
# ---------------------------------------------------------------------------


class TestSchemaCreation:
    def test_all_core_tables_present(self, isolated_db):
        with get_connection() as conn:
            tables = _table_names(conn)
        assert EXPECTED_TABLES <= tables, f"missing: {EXPECTED_TABLES - tables}"

    def test_all_expected_indexes_present(self, isolated_db):
        with get_connection() as conn:
            indexes = _index_names(conn)
        assert EXPECTED_INDEXES <= indexes, f"missing: {EXPECTED_INDEXES - indexes}"

    def test_receipt_cascade_delete_uses_receipt_id_index(self, isolated_db):
        """The receipts->prices ON DELETE CASCADE must hit idx_prices_receipt_id,
        not scan the whole prices table."""
        with get_connection() as conn:
            plan = conn.execute(
                "EXPLAIN QUERY PLAN DELETE FROM prices WHERE receipt_id = 1"
            ).fetchall()
        detail = " ".join(str(r[-1]) for r in plan)
        assert "idx_prices_receipt_id" in detail, detail
        assert "SCAN prices" not in detail, detail

    def test_prices_table_has_expected_columns(self, isolated_db):
        with get_connection() as conn:
            cols = {r[1] for r in conn.execute("PRAGMA table_info(prices)").fetchall()}
        required = {
            "id",
            "item_id",
            "store_id",
            "receipt_id",
            "flyer_source_id",
            "source",
            "date",
            "unit_price",
            "unit",
            "quantity",
            "total_price",
            "raw_name",
            "confidence",
            "created_at",
        }
        assert required <= cols

    def test_shopping_list_has_all_columns_after_migration(self, isolated_db):
        with get_connection() as conn:
            cols = {r[1] for r in conn.execute("PRAGMA table_info(shopping_list)").fetchall()}
        # initial columns + _migrate additions
        required = {
            "id",
            "display_name",
            "planned_store_id",
            "added_by",
            "is_checked_off",
            "is_active",
            "category",            # added by _migrate
            "added_by_member_id",  # added by _migrate
            "is_deleted",          # added by _migrate
        }
        assert required <= cols


# ---------------------------------------------------------------------------
# Idempotency
# ---------------------------------------------------------------------------


class TestIdempotency:
    def test_create_tables_is_idempotent(self, isolated_db):
        with get_connection() as conn:
            # Second call should not raise.
            create_tables(conn)
            create_tables(conn)

    def test_initialize_database_is_idempotent(self, isolated_db):
        # Conftest already ran this once. Run again to confirm safety.
        initialize_database()
        initialize_database()
        with get_connection() as conn:
            assert EXPECTED_TABLES <= _table_names(conn)

    def test_migrate_is_idempotent(self, isolated_db):
        with get_connection() as conn:
            _migrate(conn)
            _migrate(conn)
            cols = {r[1] for r in conn.execute("PRAGMA table_info(shopping_list)").fetchall()}
        # Columns should appear exactly once; re-running migrate should not duplicate.
        assert "is_deleted" in cols


# ---------------------------------------------------------------------------
# Column defaults
# ---------------------------------------------------------------------------


class TestColumnDefaults:
    def test_stores_created_at_has_default(self, isolated_db):
        with get_connection() as conn:
            conn.execute("INSERT INTO stores (name) VALUES ('autofilled')")
            conn.commit()
            row = conn.execute(
                "SELECT created_at FROM stores WHERE name = 'autofilled'"
            ).fetchone()
        assert row[0]  # not NULL/empty

    def test_stores_is_active_column_present_after_migrate(self, isolated_db):
        with get_connection() as conn:
            cols = {r[1] for r in conn.execute("PRAGMA table_info(stores)").fetchall()}
        assert "is_active" in cols


# ---------------------------------------------------------------------------
# is_active migration preserves existing rows
# ---------------------------------------------------------------------------


class TestIsActiveMigration:
    def test_existing_row_defaults_active_after_adding_column(self, tmp_path):
        """Simulate an old DB without is_active; _migrate must add it as 1."""
        import sqlite3

        db_path = tmp_path / "old.db"
        conn = sqlite3.connect(str(db_path))
        # Minimal old-shape stores (no is_active)
        conn.execute(
            """
            CREATE TABLE stores (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """
        )
        # shopping_list with all columns the _migrate index references
        conn.execute(
            """
            CREATE TABLE shopping_list (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                display_name TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                is_checked_off INTEGER NOT NULL DEFAULT 0,
                planned_store_id INTEGER
            )
            """
        )
        conn.execute("INSERT INTO stores (name) VALUES ('Legacy Store')")
        conn.commit()
        conn.close()

        # Run _migrate against the old DB (no is_active column yet).
        conn2 = sqlite3.connect(str(db_path))
        conn2.row_factory = sqlite3.Row
        _migrate(conn2)
        conn2.close()

        conn3 = sqlite3.connect(str(db_path))
        conn3.row_factory = sqlite3.Row
        cols = {r[1] for r in conn3.execute("PRAGMA table_info(stores)").fetchall()}
        assert "is_active" in cols

        row = conn3.execute("SELECT is_active FROM stores WHERE name = 'Legacy Store'").fetchone()
        assert row["is_active"] == 1, "existing rows must default to active=1"
        conn3.close()

    def test_items_is_tracked_defaults_to_1(self, isolated_db):
        """
        schema.py declares items.is_tracked DEFAULT 1, but items_admin_repo
        re-adds it with DEFAULT 0 (schema drift between initial and admin
        paths). The initial schema declares 1 — locked in here.
        """
        with get_connection() as conn:
            conn.execute("INSERT INTO items (canonical_name) VALUES ('defaulted')")
            conn.commit()
            row = conn.execute(
                "SELECT is_tracked FROM items WHERE canonical_name = 'defaulted'"
            ).fetchone()
        assert row[0] == 1

    def test_shopping_list_is_deleted_defaults_to_0(self, isolated_db):
        """Migration adds is_deleted INTEGER NOT NULL DEFAULT 0."""
        with get_connection() as conn:
            conn.execute(
                "INSERT INTO shopping_list (display_name) VALUES ('default row')"
            )
            conn.commit()
            row = conn.execute(
                "SELECT is_deleted FROM shopping_list WHERE display_name = 'default row'"
            ).fetchone()
        assert row[0] == 0


