from __future__ import annotations

import tkinter as tk
from tkinter import ttk, messagebox, simpledialog
from typing import Optional, Callable, Any

from Grocery_Sense.data.repositories.items_admin_repo import ItemsAdminRepo, VALID_UNITS


def _fmt_bool(v: Any) -> str:
    try:
        return "Yes" if int(v) == 1 else "No"
    except Exception:
        return "No"


class ItemManagerWindow(tk.Toplevel):
    """
    Item Manager Screen:
      - Search items
      - Toggle tracked
      - Set default unit (each/lb/kg/g)
      - Rename canonical item
      - Merge items
    """

    def __init__(self, parent: tk.Tk, *, log: Optional[Callable[[str], None]] = None) -> None:
        super().__init__(parent)
        self.title("Item Manager")
        self.geometry("1120x680")

        self._log = log or (lambda msg: None)
        self.repo = ItemsAdminRepo()

        self.search_var = tk.StringVar(value="")
        self.unit_var = tk.StringVar(value="each")

        self.merge_target_id: Optional[int] = None
        self.merge_target_name: str = ""

        self._build_ui()
        self.refresh()

    # ---------------- UI ----------------

    def _build_ui(self) -> None:
        top = ttk.Frame(self, padding=10)
        top.pack(fill=tk.X)

        ttk.Label(top, text="Item Manager", font=("Segoe UI", 12, "bold")).pack(side=tk.LEFT)

        search_row = ttk.Frame(self, padding=(10, 0, 10, 10))
        search_row.pack(fill=tk.X)

        ttk.Label(search_row, text="Search:").pack(side=tk.LEFT)
        ent = ttk.Entry(search_row, textvariable=self.search_var, width=45)
        ent.pack(side=tk.LEFT, padx=(8, 8))
        ent.bind("<Return>", lambda e: self.refresh())

        ttk.Button(
            search_row,
            text="Search",
            command=self.refresh,
            width=12,
        ).pack(side=tk.LEFT, padx=(0, 8))

        ttk.Button(
            search_row,
            text="Clear",
            command=self._clear_search,
            width=12,
        ).pack(side=tk.LEFT)

        # Merge target status
        self.merge_target_var = tk.StringVar(value="Merge target: (none)")
        ttk.Label(search_row, textvariable=self.merge_target_var).pack(side=tk.RIGHT)

        mid = ttk.Frame(self, padding=(10, 0, 10, 10))
        mid.pack(fill=tk.BOTH, expand=True)

        cols = ("id", "name", "tracked", "unit", "prices", "last_date")
        self.tree = ttk.Treeview(mid, columns=cols, show="headings", height=18)

        self.tree.heading("id", text="ID")
        self.tree.heading("name", text="Canonical Name")
        self.tree.heading("tracked", text="Tracked")
        self.tree.heading("unit", text="Default Unit")
        self.tree.heading("prices", text="# Prices")
        self.tree.heading("last_date", text="Last Price Date")

        self.tree.column("id", width=70, anchor="center")
        self.tree.column("name", width=420, anchor="w")
        self.tree.column("tracked", width=90, anchor="center")
        self.tree.column("unit", width=110, anchor="center")
        self.tree.column("prices", width=90, anchor="center")
        self.tree.column("last_date", width=140, anchor="center")

        yscroll = ttk.Scrollbar(mid, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=yscroll.set)

        self.tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        yscroll.pack(side=tk.RIGHT, fill=tk.Y)

        bottom = ttk.Frame(self, padding=10)
        bottom.pack(fill=tk.X)

        ttk.Button(
            bottom,
            text="Toggle Tracked",
            command=self._toggle_tracked,
            width=18,
        ).pack(side=tk.LEFT)

        ttk.Button(
            bottom,
            text="Rename",
            command=self._rename_item,
            width=12,
        ).pack(side=tk.LEFT, padx=(8, 0))

        ttk.Separator(bottom, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=12)

        ttk.Label(bottom, text="Default Unit:").pack(side=tk.LEFT)
        unit_box = ttk.Combobox(bottom, textvariable=self.unit_var, values=list(VALID_UNITS), width=8, state="readonly")
        unit_box.pack(side=tk.LEFT, padx=(8, 8))

        ttk.Button(
            bottom,
            text="Apply Unit",
            command=self._apply_unit,
            width=12,
        ).pack(side=tk.LEFT)

        ttk.Separator(bottom, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=12)

        ttk.Button(
            bottom,
            text="Set Merge Target",
            command=self._set_merge_target,
            width=16,
        ).pack(side=tk.LEFT)

        ttk.Button(
            bottom,
            text="Merge Selected → Target",
            command=self._merge_into_target,
            width=22,
        ).pack(side=tk.LEFT, padx=(8, 0))

        ttk.Button(
            bottom,
            text="Clear Target",
            command=self._clear_merge_target,
            width=12,
        ).pack(side=tk.LEFT, padx=(8, 0))

        self.status_var = tk.StringVar(value="Ready.")
        ttk.Label(self, textvariable=self.status_var, padding=(10, 0, 10, 10)).pack(fill=tk.X)

    # ---------------- Helpers ----------------

    def _selected_item_id(self) -> Optional[int]:
        sel = self.tree.selection()
        if not sel:
            return None
        try:
            return int(sel[0])
        except Exception:
            return None

    def _selected_item_row(self) -> Optional[tuple]:
        iid = self._selected_item_id()
        if iid is None:
            return None
        values = self.tree.item(str(iid), "values")
        return values

    # ---------------- Actions ----------------

    def _clear_search(self) -> None:
        self.search_var.set("")
        self.refresh()

    def refresh(self) -> None:
        q = self.search_var.get().strip()
        self.tree.delete(*self.tree.get_children())

        try:
            rows = self.repo.search_items(q, limit=400)
        except Exception as e:
            messagebox.showerror("Load failed", str(e))
            return

        for r in rows:
            self.tree.insert(
                "",
                "end",
                iid=str(r.id),
                values=(
                    r.id,
                    r.canonical_name,
                    _fmt_bool(r.is_tracked),
                    r.default_unit or "",
                    r.price_points,
                    r.last_price_date or "",
                ),
            )

        self.status_var.set(f"Loaded {len(rows)} item(s).")
        self._log(f"[ItemManager] Loaded {len(rows)} items. query='{q}'")

    def _toggle_tracked(self) -> None:
        item_id = self._selected_item_id()
        if item_id is None:
            messagebox.showinfo("No selection", "Select an item first.")
            return
        try:
            new_val = self.repo.toggle_tracked(item_id)
        except Exception as e:
            messagebox.showerror("Toggle failed", str(e))
            return

        self._log(f"[ItemManager] Toggled tracked for item_id={item_id} -> {new_val}")
        self.refresh()

    def _apply_unit(self) -> None:
        item_id = self._selected_item_id()
        if item_id is None:
            messagebox.showinfo("No selection", "Select an item first.")
            return

        unit = self.unit_var.get().strip().lower()
        try:
            self.repo.set_default_unit(item_id, unit)
        except Exception as e:
            messagebox.showerror("Set unit failed", str(e))
            return

        self._log(f"[ItemManager] Set default_unit='{unit}' for item_id={item_id}")
        self.refresh()

    def _rename_item(self) -> None:
        item_id = self._selected_item_id()
        if item_id is None:
            messagebox.showinfo("No selection", "Select an item first.")
            return

        row = self._selected_item_row()
        current_name = row[1] if row and len(row) > 1 else ""

        new_name = simpledialog.askstring("Rename Item", "New canonical name:", initialvalue=current_name, parent=self)
        if new_name is None:
            return

        try:
            self.repo.rename_item(item_id, new_name)
        except Exception as e:
            messagebox.showerror("Rename failed", str(e))
            return

        self._log(f"[ItemManager] Renamed item_id={item_id} -> '{new_name}'")
        self.refresh()

        # if this was the merge target, update label
        if self.merge_target_id == item_id:
            self.merge_target_name = new_name
            self.merge_target_var.set(f"Merge target: {self.merge_target_id} — {self.merge_target_name}")

    def _set_merge_target(self) -> None:
        item_id = self._selected_item_id()
        if item_id is None:
            messagebox.showinfo("No selection", "Select an item first.")
            return

        row = self._selected_item_row()
        name = row[1] if row and len(row) > 1 else ""

        self.merge_target_id = item_id
        self.merge_target_name = str(name)
        self.merge_target_var.set(f"Merge target: {self.merge_target_id} — {self.merge_target_name}")
        self.status_var.set("Merge target set. Now select another item and click 'Merge Selected → Target'.")

    def _clear_merge_target(self) -> None:
        self.merge_target_id = None
        self.merge_target_name = ""
        self.merge_target_var.set("Merge target: (none)")
        self.status_var.set("Merge target cleared.")

    def _merge_into_target(self) -> None:
        if self.merge_target_id is None:
            messagebox.showinfo("No target", "Set a merge target first.")
            return

        source_id = self._selected_item_id()
        if source_id is None:
            messagebox.showinfo("No selection", "Select the SOURCE item to merge into the target.")
            return

        if source_id == self.merge_target_id:
            messagebox.showinfo("Same item", "Select a different item to merge into the target.")
            return

        # get source name
        row = self._selected_item_row()
        source_name = row[1] if row and len(row) > 1 else str(source_id)

        if not messagebox.askyesno(
            "Confirm Merge",
            "This will merge the SOURCE item into the TARGET item:\n\n"
            f"TARGET: {self.merge_target_id} — {self.merge_target_name}\n"
            f"SOURCE: {source_id} — {source_name}\n\n"
            "All item references will be moved to the target, then the source item will be deleted.\n\n"
            "Continue?",
        ):
            return

        try:
            self.repo.merge_items(
                target_item_id=int(self.merge_target_id),
                source_item_id=int(source_id),
                keep_source_as_alias=True,
            )
        except Exception as e:
            messagebox.showerror("Merge failed", str(e))
            return

        self._log(f"[ItemManager] Merged source item_id={source_id} into target item_id={self.merge_target_id}")
        self.status_var.set(f"Merged {source_id} into {self.merge_target_id}.")
        self.refresh()


def open_item_manager_window(parent: tk.Tk, log: Optional[Callable[[str], None]] = None) -> None:
    ItemManagerWindow(parent, log=log)
