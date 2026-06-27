"""
Grocery_Sense.services.db_maintenance_service

DB backup (WAL-safe online copy) and CSV/JSON export.
"""
from __future__ import annotations

import csv
import json
import sqlite3
import tempfile
from datetime import datetime
from pathlib import Path
from typing import List, Optional

from Grocery_Sense.data.connection import current_db_path, get_connection

_KEEP_BACKUPS = 7
_EXPORT_TABLES = ["receipts", "prices", "items", "shopping_list", "stores"]


def backup_database(dest_dir: Optional[Path] = None) -> Path:
    """
    Copy the live DB to dest_dir using sqlite3's online backup API.
    WAL-safe: no risk of catching the DB mid-write.

    Returns the path of the new backup file.
    Prunes the oldest backups, keeping only the most recent _KEEP_BACKUPS.
    """
    src_path = current_db_path()
    if dest_dir is None:
        dest_dir = src_path.parent / "backups"
    dest_dir.mkdir(parents=True, exist_ok=True)

    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    dest_path = dest_dir / f"grocery_sense_{ts}.db"

    # Write to a temp file first, then rename — atomic on the same filesystem.
    tmp = dest_path.with_suffix(".tmp")
    src_conn = get_connection()
    try:
        dst_conn = sqlite3.connect(str(tmp))
        try:
            src_conn.backup(dst_conn)
        finally:
            dst_conn.close()
    finally:
        src_conn.close()

    tmp.replace(dest_path)

    _prune_backups(dest_dir)
    return dest_path


def _prune_backups(dest_dir: Path) -> None:
    backups = sorted(dest_dir.glob("grocery_sense_*.db"), key=lambda p: p.stat().st_mtime)
    for old in backups[:-_KEEP_BACKUPS]:
        try:
            old.unlink()
        except OSError:
            pass


def export_to_csv(dest_dir: Path) -> List[Path]:
    """
    Dump _EXPORT_TABLES to CSV files in dest_dir.
    Returns list of written paths.
    """
    dest_dir.mkdir(parents=True, exist_ok=True)
    written: List[Path] = []

    conn = get_connection()
    try:
        for table in _EXPORT_TABLES:
            try:
                rows = conn.execute(f"SELECT * FROM {table}").fetchall()  # noqa: S608 — internal, not user input
            except sqlite3.OperationalError:
                continue  # table doesn't exist yet
            if not rows:
                continue
            out = dest_dir / f"{table}.csv"
            with out.open("w", newline="", encoding="utf-8") as f:
                writer = csv.writer(f)
                writer.writerow(rows[0].keys())
                writer.writerows(rows)
            written.append(out)
    finally:
        conn.close()

    return written


def export_to_json(dest_dir: Path) -> List[Path]:
    """
    Dump _EXPORT_TABLES to JSON files in dest_dir.
    Returns list of written paths.
    """
    dest_dir.mkdir(parents=True, exist_ok=True)
    written: List[Path] = []

    conn = get_connection()
    try:
        for table in _EXPORT_TABLES:
            try:
                rows = conn.execute(f"SELECT * FROM {table}").fetchall()  # noqa: S608
            except sqlite3.OperationalError:
                continue
            if not rows:
                continue
            data = [dict(r) for r in rows]
            out = dest_dir / f"{table}.json"
            tmp = out.with_suffix(".tmp")
            tmp.write_text(json.dumps(data, indent=2, default=str), encoding="utf-8")
            tmp.replace(out)
            written.append(out)
    finally:
        conn.close()

    return written
