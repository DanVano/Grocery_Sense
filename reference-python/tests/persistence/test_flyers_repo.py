"""
FlyersRepo — dedicated CRUD + schema coverage.

This repo was previously only exercised indirectly (flyer ingest/sync, basket,
meal-suggestion tests). It is the largest persistence module in the repo and
owns a load-bearing rebuild migration, so it gets a direct test here.

Covers:
  - ensure_schema creates the four flyer tables and is idempotent
  - create_flyer_batch / create_batch alias persist a row
  - add_asset / add_raw_json persist + are FK-linked to the batch
  - add_deal / add_deals (bulk) / insert_deals (alias) persist
  - list_deals_for_flyer orders by normalized unit price ascending
  - list_active_deals filters by valid-date window and batch status
  - ON DELETE CASCADE removes deals/assets/raw_json when a batch is deleted
  - _migrate_flyer_deals_item_id_to_integer rebuilds a legacy TEXT item_id
    column to INTEGER and PRESERVES rows (CAST '5'->5, ''/NULL -> NULL)
  - upsert_store / list_stores convenience helpers

Preference-aware filtering (apply_preferences=True) is covered by the
preferences/deal-feed suites; these tests pass apply_preferences=False so the
assertions are deterministic and independent of household config.
"""

from __future__ import annotations

import pytest

from Grocery_Sense.data.connection import get_connection
from Grocery_Sense.data.repositories.flyers_repo import FlyersRepo


# ---------------------------------------------------------------------------
# Fixtures / helpers
# ---------------------------------------------------------------------------


@pytest.fixture()
def repo(isolated_db) -> FlyersRepo:
    r = FlyersRepo()
    r.ensure_schema()
    return r


@pytest.fixture()
def store_id(repo: FlyersRepo) -> int:
    return repo.upsert_store("Test Mart")


def _batch(repo: FlyersRepo, store_id: int, *, valid_from=None, valid_to=None, status="active") -> int:
    return repo.create_flyer_batch(
        store_id=store_id,
        valid_from=valid_from,
        valid_to=valid_to,
        source_type="manual",
        note="unit-test",
        status=status,
    )


# ---------------------------------------------------------------------------
# Schema
# ---------------------------------------------------------------------------


class TestEnsureSchema:
    def test_creates_all_flyer_tables(self, repo):
        with get_connection() as c:
            names = {
                r[0]
                for r in c.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'"
                ).fetchall()
            }
        assert {"flyer_batches", "flyer_assets", "flyer_raw_json", "flyer_deals"} <= names

    def test_idempotent(self, repo, store_id):
        # Second build must not wipe data or raise.
        fid = _batch(repo, store_id)
        repo._schema_ready = False  # force a re-build path
        repo.ensure_schema()
        with get_connection() as c:
            n = c.execute("SELECT COUNT(*) FROM flyer_batches WHERE id=?", (fid,)).fetchone()[0]
        assert n == 1

    def test_item_id_column_is_integer(self, repo):
        with get_connection() as c:
            cols = {r[1]: str(r[2]).upper() for r in c.execute("PRAGMA table_info(flyer_deals)").fetchall()}
        assert cols["item_id"] == "INTEGER"


# ---------------------------------------------------------------------------
# Batches / assets / raw json
# ---------------------------------------------------------------------------


class TestBatchesAssetsRaw:
    def test_create_flyer_batch_persists(self, repo, store_id):
        fid = _batch(repo, store_id, valid_from="2026-06-01", valid_to="2026-06-30")
        assert fid > 0
        with get_connection() as c:
            row = c.execute("SELECT store_id, valid_from, status FROM flyer_batches WHERE id=?", (fid,)).fetchone()
        assert row["store_id"] == store_id
        assert row["valid_from"] == "2026-06-01"
        assert row["status"] == "active"

    def test_create_batch_alias_maps_fields(self, repo, store_id):
        fid = repo.create_batch(source="flipp", store_id=store_id, flyer_name="Weekly", valid_from="2026-06-01", valid_to="2026-06-07")
        with get_connection() as c:
            row = c.execute("SELECT source_type, note FROM flyer_batches WHERE id=?", (fid,)).fetchone()
        assert row["source_type"] == "flipp"
        assert row["note"] == "Weekly"

    def test_add_asset_and_raw_json_link_to_batch(self, repo, store_id):
        fid = _batch(repo, store_id)
        aid = repo.add_asset(fid, asset_type="pdf", path="/tmp/x.pdf", sha256="abc")
        rid = repo.add_raw_json(fid, raw_json='{"k":1}', sha256="def")
        with get_connection() as c:
            a = c.execute("SELECT flyer_id, asset_type FROM flyer_assets WHERE id=?", (aid,)).fetchone()
            r = c.execute("SELECT flyer_id, json FROM flyer_raw_json WHERE id=?", (rid,)).fetchone()
        assert a["flyer_id"] == fid and a["asset_type"] == "pdf"
        assert r["flyer_id"] == fid and r["json"] == '{"k":1}'

    def test_set_batch_status(self, repo, store_id):
        fid = _batch(repo, store_id)
        repo.set_batch_status(fid, "archived")
        with get_connection() as c:
            assert c.execute("SELECT status FROM flyer_batches WHERE id=?", (fid,)).fetchone()[0] == "archived"


# ---------------------------------------------------------------------------
# Deals
# ---------------------------------------------------------------------------


class TestDeals:
    def test_add_deal_single(self, repo, store_id):
        fid = _batch(repo, store_id)
        did = repo.add_deal(flyer_id=fid, store_id=store_id, title="Milk", deal_total=3.99, item_id=42)
        with get_connection() as c:
            row = c.execute("SELECT title, deal_total, item_id FROM flyer_deals WHERE id=?", (did,)).fetchone()
        assert row["title"] == "Milk"
        assert row["deal_total"] == 3.99
        assert row["item_id"] == 42  # stored as INTEGER

    def test_add_deals_bulk(self, repo, store_id):
        fid = _batch(repo, store_id)
        n = repo.add_deals(
            [
                {"flyer_id": fid, "store_id": store_id, "title": "A", "norm_unit_price": 2.0},
                {"flyer_id": fid, "store_id": store_id, "title": "B", "norm_unit_price": 1.0},
            ]
        )
        assert n == 2
        with get_connection() as c:
            assert c.execute("SELECT COUNT(*) FROM flyer_deals WHERE flyer_id=?", (fid,)).fetchone()[0] == 2

    def test_add_deals_empty_is_noop(self, repo):
        assert repo.add_deals([]) == 0

    def test_insert_deals_alias_parses_price_string(self, repo, store_id):
        fid = _batch(repo, store_id)
        n = repo.insert_deals(fid, store_id, [{"title": "Eggs", "price": "$4.49"}])
        assert n == 1
        with get_connection() as c:
            row = c.execute("SELECT title, deal_total FROM flyer_deals WHERE flyer_id=?", (fid,)).fetchone()
        assert row["title"] == "Eggs"
        assert row["deal_total"] == 4.49

    def test_list_deals_for_flyer_orders_by_norm_unit_price(self, repo, store_id):
        fid = _batch(repo, store_id)
        repo.add_deals(
            [
                {"flyer_id": fid, "store_id": store_id, "title": "Pricey", "norm_unit_price": 9.0},
                {"flyer_id": fid, "store_id": store_id, "title": "Cheap", "norm_unit_price": 1.0},
                {"flyer_id": fid, "store_id": store_id, "title": "Mid", "norm_unit_price": 5.0},
            ]
        )
        deals = repo.list_deals_for_flyer(fid, apply_preferences=False)
        assert [d["title"] for d in deals] == ["Cheap", "Mid", "Pricey"]


# ---------------------------------------------------------------------------
# Active-deal date filtering
# ---------------------------------------------------------------------------


class TestListActiveDeals:
    def test_filters_by_date_window_and_status(self, repo, store_id):
        day = "2026-06-15"
        active = _batch(repo, store_id, valid_from="2026-06-10", valid_to="2026-06-20", status="active")
        expired = _batch(repo, store_id, valid_from="2026-05-01", valid_to="2026-05-31", status="active")
        future = _batch(repo, store_id, valid_from="2026-07-01", valid_to="2026-07-31", status="active")
        archived = _batch(repo, store_id, valid_from="2026-06-10", valid_to="2026-06-20", status="archived")

        for fid, title in [(active, "IN"), (expired, "PAST"), (future, "FUTURE"), (archived, "ARCHIVED")]:
            repo.add_deal(flyer_id=fid, store_id=store_id, title=title, deal_total=1.0)

        titles = {d["title"] for d in repo.list_active_deals(on_date=day, apply_preferences=False)}
        assert titles == {"IN"}

    def test_store_filter(self, repo):
        a = repo.upsert_store("Store A")
        b = repo.upsert_store("Store B")
        fa = _batch(repo, a, valid_from="2026-06-10", valid_to="2026-06-20")
        fb = _batch(repo, b, valid_from="2026-06-10", valid_to="2026-06-20")
        repo.add_deal(flyer_id=fa, store_id=a, title="from-a", deal_total=1.0)
        repo.add_deal(flyer_id=fb, store_id=b, title="from-b", deal_total=1.0)

        only_a = repo.list_active_deals(on_date="2026-06-15", store_id=a, apply_preferences=False)
        assert {d["title"] for d in only_a} == {"from-a"}


# ---------------------------------------------------------------------------
# FK cascade
# ---------------------------------------------------------------------------


class TestCascade:
    def test_delete_batch_cascades_children(self, repo, store_id):
        fid = _batch(repo, store_id)
        repo.add_asset(fid, asset_type="pdf", path="/tmp/x.pdf")
        repo.add_raw_json(fid, raw_json="{}")
        repo.add_deal(flyer_id=fid, store_id=store_id, title="Doomed", deal_total=1.0)

        with get_connection() as c:
            c.execute("DELETE FROM flyer_batches WHERE id=?", (fid,))
            c.commit()
            assert c.execute("SELECT COUNT(*) FROM flyer_deals WHERE flyer_id=?", (fid,)).fetchone()[0] == 0
            assert c.execute("SELECT COUNT(*) FROM flyer_assets WHERE flyer_id=?", (fid,)).fetchone()[0] == 0
            assert c.execute("SELECT COUNT(*) FROM flyer_raw_json WHERE flyer_id=?", (fid,)).fetchone()[0] == 0


# ---------------------------------------------------------------------------
# Legacy TEXT item_id -> INTEGER rebuild migration
# ---------------------------------------------------------------------------


_LEGACY_DDL = """
CREATE TABLE flyer_deals (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    flyer_id INTEGER NOT NULL,
    asset_id INTEGER,
    store_id INTEGER NOT NULL,
    page_index INTEGER,
    title TEXT,
    description TEXT,
    price_text TEXT,
    deal_qty REAL,
    deal_total REAL,
    unit_price REAL,
    unit TEXT,
    norm_unit_price REAL,
    norm_unit TEXT,
    norm_note TEXT,
    item_id TEXT,
    mapping_confidence REAL,
    confidence REAL,
    created_at TEXT NOT NULL
)
"""


class TestItemIdMigration:
    def test_text_item_id_rebuilt_to_integer_preserving_rows(self, isolated_db):
        # Lay down a legacy-shaped flyer_deals (item_id TEXT) with mixed values.
        with get_connection() as c:
            c.execute(_LEGACY_DDL)
            c.executemany(
                "INSERT INTO flyer_deals (flyer_id, store_id, title, item_id, created_at) VALUES (?,?,?,?,?)",
                [
                    (1, 1, "numeric", "5", "2026-06-01T00:00:00"),
                    (1, 1, "empty", "", "2026-06-01T00:00:00"),
                    (1, 1, "null", None, "2026-06-01T00:00:00"),
                ],
            )
            c.commit()
            assert c.execute("PRAGMA table_info(flyer_deals)").fetchall()  # sanity: exists
            legacy_type = {r[1]: str(r[2]).upper() for r in c.execute("PRAGMA table_info(flyer_deals)").fetchall()}
            assert legacy_type["item_id"] == "TEXT"

        # A fresh repo builds schema -> triggers the rebuild migration.
        FlyersRepo().ensure_schema()

        with get_connection() as c:
            new_type = {r[1]: str(r[2]).upper() for r in c.execute("PRAGMA table_info(flyer_deals)").fetchall()}
            rows = {
                r["title"]: r["item_id"]
                for r in c.execute("SELECT title, item_id FROM flyer_deals").fetchall()
            }

        assert new_type["item_id"] == "INTEGER"
        assert rows == {"numeric": 5, "empty": None, "null": None}  # rows preserved, '' -> NULL


# ---------------------------------------------------------------------------
# Stores convenience
# ---------------------------------------------------------------------------


class TestStores:
    def test_upsert_is_idempotent_by_name(self, repo):
        a = repo.upsert_store("Same Name")
        b = repo.upsert_store("Same Name")
        assert a == b

    def test_upsert_rejects_blank(self, repo):
        with pytest.raises(ValueError):
            repo.upsert_store("   ")

    def test_list_stores_sorted(self, repo):
        repo.upsert_store("Zed")
        repo.upsert_store("Alpha")
        names = [s.name for s in repo.list_stores()]
        assert names == sorted(names)
