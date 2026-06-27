"""
Grocery_Sense.ui.family_requests_window

Parent review queue for "family picks". Lists unreviewed member picks (meals +
items), grouped by member, with Mark reviewed / Remove from list actions.

ponytail: desktop in-app review only. Real OS/mobile push is deferred to the
Android/iOS port; family_requests_service.unreviewed_count() is the hook a push
layer would call.
"""

from __future__ import annotations

import tkinter as tk
from tkinter import ttk, messagebox

from Grocery_Sense.services import family_requests_service


def open_family_requests_window(parent, *, log=None, on_change=None) -> None:
    def _log(msg: str) -> None:
        if log:
            log(msg)

    win = tk.Toplevel(parent)
    win.title("Family Picks — Review")
    win.geometry("560x460")

    root = ttk.Frame(win)
    root.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

    ttk.Label(
        root, text="New family picks", font=("Segoe UI", 12, "bold")
    ).pack(anchor="w")
    ttk.Label(
        root,
        text="Picks your family added to the shared list. Remove ones you don't want.",
        foreground="#555",
    ).pack(anchor="w", pady=(0, 8))

    list_frame = ttk.Frame(root)
    list_frame.pack(fill=tk.BOTH, expand=True)
    listbox = tk.Listbox(list_frame, height=14)
    scrollbar = ttk.Scrollbar(list_frame, orient=tk.VERTICAL, command=listbox.yview)
    listbox.configure(yscrollcommand=scrollbar.set)
    listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
    scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

    current: list = []

    def refresh() -> None:
        nonlocal current
        listbox.delete(0, tk.END)
        # group by member: "Member" header then its picks
        requests = family_requests_service.list_unreviewed()
        current = requests
        if not requests:
            listbox.insert(tk.END, "(no new picks)")
            return
        by_member: dict = {}
        for r in requests:
            by_member.setdefault(r.member_name or "Unknown", []).append(r)
        # rebuild flat current list in display order so indices line up
        current = []
        for member_name in sorted(by_member, key=str.lower):
            listbox.insert(tk.END, f"— {member_name} —")
            current.append(None)  # header row, not selectable as a request
            for r in by_member[member_name]:
                if r.kind == "meal":
                    detail = f"{r.label} (meal, {len(r.item_row_ids)} items)"
                else:
                    detail = f"{r.label} (item)"
                listbox.insert(tk.END, f"    {detail}")
                current.append(r)
        if on_change:
            on_change()

    def _selected_request():
        sel = listbox.curselection()
        if not sel:
            _log("Family Picks: select a pick first.")
            return None
        idx = int(sel[0])
        if idx < 0 or idx >= len(current):
            return None
        req = current[idx]
        if req is None:
            _log("Family Picks: that's a member heading, select a pick under it.")
            return None
        return req

    def on_mark_reviewed() -> None:
        req = _selected_request()
        if not req:
            return
        family_requests_service.mark_reviewed(req.id)
        _log(f"Marked reviewed: {req.member_name} — {req.label}")
        refresh()

    def on_remove() -> None:
        req = _selected_request()
        if not req:
            return
        if not messagebox.askyesno(
            "Remove pick",
            f"Remove '{req.label}' ({req.member_name}) from the shopping list?",
            parent=win,
        ):
            return
        family_requests_service.remove_request(req.id)
        _log(f"Removed from list: {req.member_name} — {req.label}")
        refresh()

    def on_mark_all() -> None:
        from Grocery_Sense.data.repositories import member_requests_repo
        member_requests_repo.mark_all_reviewed()
        _log("Marked all family picks reviewed.")
        refresh()

    btns = ttk.Frame(root)
    btns.pack(fill=tk.X, pady=(10, 0))
    ttk.Button(btns, text="Refresh", command=refresh).pack(side=tk.LEFT, padx=(0, 8))
    ttk.Button(btns, text="Mark reviewed", command=on_mark_reviewed).pack(side=tk.LEFT, padx=(0, 8))
    ttk.Button(btns, text="Remove from list", command=on_remove).pack(side=tk.LEFT, padx=(0, 8))
    ttk.Button(btns, text="Mark all reviewed", command=on_mark_all).pack(side=tk.RIGHT)

    refresh()
