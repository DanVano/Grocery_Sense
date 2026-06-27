"""
pricebrain.data.connection

SQLite connection utilities for the Price app backend.
Stores the database inside src/pricebrain/data/db/
"""

# src/grocery_sense/data/connection.py

from __future__ import annotations

import sqlite3
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator, Optional

# Name of the SQLite file
DB_FILENAME = "grocery_sense.db"

# Tracks whether integrity_check has already passed this process run.
# Keyed by resolved db path so in-memory / test DBs each get their own check.
_integrity_checked: set = set()

# Set by pytest conftest to redirect all connections to a temp test DB.
_TEST_DB_PATH: Optional[str] = None


def current_db_path() -> Path:
    """Return the resolved path of the active database (honoring _TEST_DB_PATH)."""
    if _TEST_DB_PATH is not None:
        return Path(_TEST_DB_PATH)
    return get_db_path()


def get_db_path() -> Path:
    """
    Return the full path to the DB file, inside the 'db' directory next to this
    file: src/grocery_sense/data/db/grocery_sense.db
    """
    base_dir = Path(__file__).resolve().parent / "db"
    base_dir.mkdir(parents=True, exist_ok=True)
    return base_dir / DB_FILENAME


def _check_integrity(conn: sqlite3.Connection, db_path: Path) -> None:
    """
    Run PRAGMA integrity_check on first open of this DB path.
    Raises RuntimeError with a clear message if corruption is detected.
    Skips the check for in-memory databases (':memory:').
    """
    path_key = str(db_path)
    if path_key in _integrity_checked:
        return

    rows = conn.execute("PRAGMA integrity_check;").fetchall()
    # Result is one or more rows; a healthy DB returns exactly [("ok",)]
    results = [row[0] for row in rows]
    if results != ["ok"]:
        detail = "; ".join(str(r) for r in results)
        raise RuntimeError(
            f"SQLite database appears to be corrupted ({db_path}).\n"
            f"integrity_check reported: {detail}\n"
            "Try restoring from a backup or deleting the file to start fresh."
        )

    _integrity_checked.add(path_key)


def get_connection() -> sqlite3.Connection:
    """
    Open a SQLite connection to our DB.

    On the first connection per process, runs PRAGMA integrity_check and raises
    RuntimeError immediately if corruption is detected.
    """
    if _TEST_DB_PATH is not None:
        if _TEST_DB_PATH == ":memory:":
            raise ValueError(
                "Grocery Sense opens one SQLite connection per call, so a "
                "':memory:' database would be a fresh, empty DB on every call "
                "(schema and data invisible across repos). Use a temp-file DB "
                "for tests instead of ':memory:'."
            )
        db_path = Path(_TEST_DB_PATH)
    else:
        db_path = get_db_path()
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row  # nicer dict-like access
    conn.execute("PRAGMA foreign_keys = ON")
    # Wait briefly for a competing writer instead of failing instantly with
    # "database is locked" when background threads (flyer sync, alert check)
    # overlap on the same file.
    conn.execute("PRAGMA busy_timeout = 5000")
    # Per-connection performance pragmas (not persistent like WAL, so set every
    # open). None affect durability under WAL + synchronous=NORMAL.
    # ponytail: fixed sizes; bump only if a profiler on a real large DB says so.
    conn.execute("PRAGMA cache_size = -16000")   # ~16 MB page cache (negative = KB)
    conn.execute("PRAGMA temp_store = MEMORY")    # temp B-trees / sorts in RAM
    conn.execute("PRAGMA mmap_size = 268435456")  # memory-map up to 256 MB of the DB
    if str(db_path) not in _integrity_checked and str(db_path) != ":memory:":
        conn.execute("PRAGMA journal_mode = WAL")
        conn.execute("PRAGMA synchronous = NORMAL")
    _check_integrity(conn, db_path)
    return conn


@contextmanager
def connection_scope() -> Iterator[sqlite3.Connection]:
    """
    Open a connection, commit on clean exit, roll back on error, and ALWAYS close.

    Drop-in replacement for `with get_connection() as conn:`. The raw sqlite3
    connection context manager commits/rolls-back but does NOT close the
    connection, so bare `with get_connection() as conn:` leaks the handle until
    GC. This wrapper preserves the commit-on-success / rollback-on-error
    semantics and adds deterministic close.
    """
    conn = get_connection()
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def reset_integrity_cache() -> None:
    """Clear the integrity-checked cache (test helper)."""
    _integrity_checked.clear()

