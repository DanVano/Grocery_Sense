from __future__ import annotations

import sqlite3
from contextlib import contextmanager
from dataclasses import dataclass
from typing import Iterator, Optional, List
from datetime import datetime, timezone

from Grocery_Sense.data.connection import connection_scope, get_connection


@contextmanager
def _conn_ctx(conn: Optional[sqlite3.Connection]) -> Iterator[sqlite3.Connection]:
    """If `conn` is provided, yield it without committing/closing (caller owns
    the transaction). Otherwise open one, commit on exit, close on exit."""
    if conn is not None:
        yield conn
        return
    with connection_scope() as c:
        yield c



@dataclass
class ItemAlias:
    id: int
    alias_text: str
    item_id: int
    confidence: float
    source: str
    created_at: str
    last_seen_at: Optional[str]
    times_seen: int


class ItemAliasesRepo:
    def __init__(self, db_path: Optional[str] = None) -> None:
        self.db_path = db_path

    def get_by_alias(
        self,
        alias_text: str,
        *,
        conn: Optional[sqlite3.Connection] = None,
    ) -> Optional[ItemAlias]:
        alias_text = alias_text.strip().lower()
        with _conn_ctx(conn) as c:
            row = c.execute(
                """
                SELECT id, alias_text, item_id, confidence, source, created_at, last_seen_at, times_seen
                FROM item_aliases
                WHERE alias_text = ?
                """,
                (alias_text,),
            ).fetchone()

        if not row:
            return None
        return ItemAlias(*row)

    def upsert_alias(
        self,
        alias_text: str,
        item_id: int,
        confidence: float = 1.0,
        source: str = "manual",
        *,
        conn: Optional[sqlite3.Connection] = None,
    ) -> None:
        alias_text = alias_text.strip().lower()
        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        with _conn_ctx(conn) as c:
            c.execute(
                """
                INSERT INTO item_aliases (alias_text, item_id, confidence, source, created_at, last_seen_at, times_seen)
                VALUES (?, ?, ?, ?, ?, ?, 1)
                ON CONFLICT(alias_text) DO UPDATE SET
                    item_id = excluded.item_id,
                    confidence = excluded.confidence,
                    source = excluded.source,
                    last_seen_at = excluded.last_seen_at,
                    times_seen = item_aliases.times_seen + 1
                """,
                (alias_text, item_id, confidence, source, now, now),
            )

    def mark_seen(
        self,
        alias_text: str,
        *,
        conn: Optional[sqlite3.Connection] = None,
    ) -> None:
        alias_text = alias_text.strip().lower()
        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        with _conn_ctx(conn) as c:
            c.execute(
                """
                UPDATE item_aliases
                SET last_seen_at = ?, times_seen = times_seen + 1
                WHERE alias_text = ?
                """,
                (now, alias_text),
            )

    def list_all(self) -> List[ItemAlias]:
        with connection_scope() as conn:
            rows = conn.execute(
                """
                SELECT id, alias_text, item_id, confidence, source, created_at, last_seen_at, times_seen
                FROM item_aliases
                ORDER BY times_seen DESC, alias_text ASC
                """
            ).fetchall()
        return [ItemAlias(*r) for r in rows]
