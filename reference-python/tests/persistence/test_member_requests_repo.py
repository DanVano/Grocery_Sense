"""member_requests_repo — CRUD + JSON round-trip for the family-picks queue.

Relies on the autouse isolated_db fixture (per-test temp SQLite) which runs
create_tables, so the member_requests table self-verifies as present here.
"""

from __future__ import annotations

from Grocery_Sense.data.repositories import member_requests_repo as repo


def test_add_and_get_round_trips_item_row_ids():
    req_id = repo.add_request(
        member_id=2,
        member_name="Emma",
        kind="meal",
        label="Chicken Rice",
        item_row_ids=[10, 11, 12],
    )
    row = repo.get_request(req_id)
    assert row is not None
    assert row.member_id == 2
    assert row.member_name == "Emma"
    assert row.kind == "meal"
    assert row.label == "Chicken Rice"
    assert row.item_row_ids == [10, 11, 12]
    assert row.reviewed is False


def test_list_unreviewed_and_count():
    repo.add_request(member_id=2, member_name="Emma", kind="item", label="cookies", item_row_ids=[1])
    repo.add_request(member_id=3, member_name="Liam", kind="item", label="apples", item_row_ids=[2])

    assert repo.count_unreviewed() == 2
    unreviewed = repo.list_unreviewed()
    assert {r.label for r in unreviewed} == {"cookies", "apples"}


def test_mark_reviewed_drops_from_unreviewed():
    rid = repo.add_request(member_id=2, member_name="Emma", kind="item", label="cookies", item_row_ids=[1])
    repo.add_request(member_id=3, member_name="Liam", kind="item", label="apples", item_row_ids=[2])

    repo.mark_reviewed(rid)

    assert repo.count_unreviewed() == 1
    assert [r.label for r in repo.list_unreviewed()] == ["apples"]
    # still retrievable, just flagged reviewed
    assert repo.get_request(rid).reviewed is True


def test_mark_all_reviewed():
    repo.add_request(member_id=2, member_name="Emma", kind="item", label="cookies", item_row_ids=[1])
    repo.add_request(member_id=3, member_name="Liam", kind="item", label="apples", item_row_ids=[2])

    repo.mark_all_reviewed()

    assert repo.count_unreviewed() == 0
    assert repo.list_unreviewed() == []


def test_empty_item_row_ids_round_trips_as_list():
    rid = repo.add_request(member_id=2, member_name="Emma", kind="item", label="milk", item_row_ids=[])
    assert repo.get_request(rid).item_row_ids == []
