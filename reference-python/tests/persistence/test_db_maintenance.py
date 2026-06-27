"""
Phase 2B — DB backup and export tests.

Verifies:
- backup_database() creates a file, row counts match source.
- export_to_csv() / export_to_json() write files with correct headers/keys.
- Prune logic keeps only _KEEP_BACKUPS most-recent files.
"""
from __future__ import annotations

import csv
import json
import sqlite3
import time
from pathlib import Path

import pytest

from Grocery_Sense.data import connection as conn_mod
from Grocery_Sense.data.repositories.stores_repo import create_store
from Grocery_Sense.services.db_maintenance_service import (
    _KEEP_BACKUPS,
    backup_database,
    export_to_csv,
    export_to_json,
)


# ---------------------------------------------------------------------------
# Backup
# ---------------------------------------------------------------------------

def test_backup_creates_file(isolated_db, tmp_path):
    dest = tmp_path / "backups"
    path = backup_database(dest_dir=dest)
    assert path.exists()
    assert path.suffix == ".db"


def test_backup_row_counts_match(isolated_db, tmp_path):
    create_store("TestMart", address="1 Main St")
    create_store("FreshMart", address="2 Oak Ave")

    dest = tmp_path / "backups"
    backup_path = backup_database(dest_dir=dest)

    src = sqlite3.connect(str(conn_mod.current_db_path()))
    bak = sqlite3.connect(str(backup_path))
    try:
        src_count = src.execute("SELECT COUNT(*) FROM stores").fetchone()[0]
        bak_count = bak.execute("SELECT COUNT(*) FROM stores").fetchone()[0]
    finally:
        src.close()
        bak.close()

    assert src_count == bak_count == 2


def test_backup_prunes_old_files(isolated_db, tmp_path):
    dest = tmp_path / "backups"
    # Create more than _KEEP_BACKUPS backups with slight delays so mtime differs
    for _ in range(_KEEP_BACKUPS + 2):
        backup_database(dest_dir=dest)
        time.sleep(0.01)

    remaining = list(dest.glob("grocery_sense_*.db"))
    assert len(remaining) == _KEEP_BACKUPS


# ---------------------------------------------------------------------------
# CSV export
# ---------------------------------------------------------------------------

def test_export_csv_stores(isolated_db, tmp_path):
    create_store("ExportMart")
    files = export_to_csv(tmp_path / "csv")
    stores_csv = tmp_path / "csv" / "stores.csv"
    assert stores_csv in files
    assert stores_csv.exists()

    with stores_csv.open(encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)
    assert any(r["name"] == "ExportMart" for r in rows)


# ---------------------------------------------------------------------------
# JSON export
# ---------------------------------------------------------------------------

def test_export_json_stores(isolated_db, tmp_path):
    create_store("JsonMart")
    files = export_to_json(tmp_path / "json")
    stores_json = tmp_path / "json" / "stores.json"
    assert stores_json in files
    assert stores_json.exists()

    data = json.loads(stores_json.read_text(encoding="utf-8"))
    assert isinstance(data, list)
    assert any(r["name"] == "JsonMart" for r in data)


def test_export_skips_missing_tables(isolated_db, tmp_path):
    # Should not raise even if some optional tables don't exist yet.
    files = export_to_csv(tmp_path / "csv_skip")
    assert isinstance(files, list)
