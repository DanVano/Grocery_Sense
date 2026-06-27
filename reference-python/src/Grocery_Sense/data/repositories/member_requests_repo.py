from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import List, Optional

from Grocery_Sense.data.connection import connection_scope


@dataclass
class MemberRequestRow:
    id: int
    member_id: Optional[int]
    member_name: str
    kind: str  # 'meal' | 'item'
    label: str
    item_row_ids: List[int] = field(default_factory=list)
    created_at: str = ""
    reviewed: bool = False


def _decode_row_ids(raw) -> List[int]:
    # item_row_ids is stored as a JSON array; tolerate NULL/empty/legacy junk
    # by returning [] rather than letting a bad blob crash the review screen.
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except (ValueError, TypeError):
        return []
    if not isinstance(data, list):
        return []
    return [int(x) for x in data if isinstance(x, (int, float))]


def _row_to_obj(row) -> MemberRequestRow:
    return MemberRequestRow(
        id=int(row["id"]),
        member_id=int(row["member_id"]) if row["member_id"] is not None else None,
        member_name=str(row["member_name"] or ""),
        kind=str(row["kind"] or ""),
        label=str(row["label"] or ""),
        item_row_ids=_decode_row_ids(row["item_row_ids"]),
        created_at=str(row["created_at"] or ""),
        reviewed=bool(row["reviewed"] or 0),
    )


def add_request(
    *,
    member_id: Optional[int],
    member_name: str,
    kind: str,
    label: str,
    item_row_ids: List[int],
) -> int:
    with connection_scope() as conn:
        cur = conn.execute(
            """
            INSERT INTO member_requests
                (member_id, member_name, kind, label, item_row_ids)
            VALUES (?, ?, ?, ?, ?)
            """,
            (
                int(member_id) if member_id is not None else None,
                (member_name or "").strip(),
                (kind or "").strip(),
                (label or "").strip(),
                json.dumps([int(x) for x in (item_row_ids or [])]),
            ),
        )
        conn.commit()
        return int(cur.lastrowid)


def get_request(request_id: int) -> Optional[MemberRequestRow]:
    with connection_scope() as conn:
        row = conn.execute(
            "SELECT id, member_id, member_name, kind, label, item_row_ids, "
            "created_at, reviewed FROM member_requests WHERE id = ?",
            (int(request_id),),
        ).fetchone()
    return _row_to_obj(row) if row else None


def list_unreviewed() -> List[MemberRequestRow]:
    with connection_scope() as conn:
        rows = conn.execute(
            "SELECT id, member_id, member_name, kind, label, item_row_ids, "
            "created_at, reviewed FROM member_requests "
            "WHERE reviewed = 0 ORDER BY id DESC"
        ).fetchall()
    return [_row_to_obj(r) for r in rows]


def list_all(*, limit: Optional[int] = None) -> List[MemberRequestRow]:
    sql = (
        "SELECT id, member_id, member_name, kind, label, item_row_ids, "
        "created_at, reviewed FROM member_requests ORDER BY id DESC"
    )
    with connection_scope() as conn:
        if limit is not None:
            rows = conn.execute(sql + " LIMIT ?", (int(limit),)).fetchall()
        else:
            rows = conn.execute(sql).fetchall()
    return [_row_to_obj(r) for r in rows]


def count_unreviewed() -> int:
    with connection_scope() as conn:
        row = conn.execute(
            "SELECT COUNT(*) FROM member_requests WHERE reviewed = 0"
        ).fetchone()
    return int(row[0]) if row else 0


def mark_reviewed(request_id: int) -> None:
    with connection_scope() as conn:
        conn.execute(
            "UPDATE member_requests SET reviewed = 1 WHERE id = ?",
            (int(request_id),),
        )
        conn.commit()


def mark_all_reviewed() -> None:
    with connection_scope() as conn:
        conn.execute("UPDATE member_requests SET reviewed = 1 WHERE reviewed = 0")
        conn.commit()
