from __future__ import annotations

import tkinter as tk
from tkinter import ttk, messagebox
from typing import Optional, Callable

from Grocery_Sense.data.repositories import stores_repo


class StoresManagementWindow(tk.Toplevel):
    """
    Stores Management: Add, Edit, Archive / Reactivate, Toggle Favourite.
    Archived stores are hidden by default; tick "Show archived" to reveal them.
    """

    def __init__(self, parent: tk.Tk, *, log: Optional[Callable[[str], None]] = None) -> None:
        super().__init__(parent)
        self.title("Stores Management")
        self.geometry("900x560")

        self._log = log or (lambda msg: None)
        self._show_archived = tk.BooleanVar(value=False)

        self._build_ui()
        self.refresh()

    # ---------------- UI ----------------

    def _build_ui(self) -> None:
        top = ttk.Frame(self, padding=10)
        top.pack(fill=tk.X)

        ttk.Label(top, text="Stores Management", font=("Segoe UI", 12, "bold")).pack(side=tk.LEFT)

        ttk.Checkbutton(
            top,
            text="Show archived",
            variable=self._show_archived,
            command=self.refresh,
        ).pack(side=tk.RIGHT)

        mid = ttk.Frame(self, padding=(10, 0, 10, 10))
        mid.pack(fill=tk.BOTH, expand=True)

        cols = ("id", "name", "city", "favorite", "priority", "status")
        self.tree = ttk.Treeview(mid, columns=cols, show="headings", height=18)

        self.tree.heading("id", text="ID")
        self.tree.heading("name", text="Name")
        self.tree.heading("city", text="City")
        self.tree.heading("favorite", text="Favourite")
        self.tree.heading("priority", text="Priority")
        self.tree.heading("status", text="Status")

        self.tree.column("id", width=50, anchor="center")
        self.tree.column("name", width=280, anchor="w")
        self.tree.column("city", width=180, anchor="w")
        self.tree.column("favorite", width=90, anchor="center")
        self.tree.column("priority", width=80, anchor="center")
        self.tree.column("status", width=100, anchor="center")

        yscroll = ttk.Scrollbar(mid, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=yscroll.set)
        self.tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        yscroll.pack(side=tk.RIGHT, fill=tk.Y)

        bottom = ttk.Frame(self, padding=10)
        bottom.pack(fill=tk.X)

        ttk.Button(bottom, text="Add", command=self._add_store, width=10).pack(side=tk.LEFT)
        ttk.Button(bottom, text="Edit", command=self._edit_store, width=10).pack(side=tk.LEFT, padx=(8, 0))

        ttk.Separator(bottom, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=12)

        ttk.Button(bottom, text="Toggle Favourite", command=self._toggle_favorite, width=16).pack(side=tk.LEFT)

        ttk.Separator(bottom, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=12)

        self._archive_btn = ttk.Button(bottom, text="Archive", command=self._toggle_active, width=12)
        self._archive_btn.pack(side=tk.LEFT)

        ttk.Separator(bottom, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=12)

        ttk.Button(bottom, text="Refresh", command=self.refresh, width=10).pack(side=tk.LEFT)

        self.status_var = tk.StringVar(value="Ready.")
        ttk.Label(self, textvariable=self.status_var, padding=(10, 0, 10, 10)).pack(fill=tk.X)

        self.tree.bind("<<TreeviewSelect>>", self._on_select)

    # ---------------- Helpers ----------------

    def _selected_id(self) -> Optional[int]:
        sel = self.tree.selection()
        if not sel:
            return None
        try:
            return int(sel[0])
        except Exception:
            return None

    def _selected_store(self):
        sid = self._selected_id()
        if sid is None:
            return None
        return stores_repo.get_store_by_id(sid)

    def _on_select(self, _event=None) -> None:
        store = self._selected_store()
        if store is None:
            self._archive_btn.configure(text="Archive")
            return
        self._archive_btn.configure(text="Reactivate" if not store.is_active else "Archive")

    # ---------------- Actions ----------------

    def refresh(self) -> None:
        self.tree.delete(*self.tree.get_children())
        try:
            stores = stores_repo.list_stores(
                order_by_priority=True,
                include_archived=self._show_archived.get(),
            )
        except Exception as e:
            messagebox.showerror("Load failed", str(e), parent=self)
            return

        for s in stores:
            self.tree.insert(
                "",
                "end",
                iid=str(s.id),
                values=(
                    s.id,
                    s.name,
                    s.city or "",
                    "Yes" if s.is_favorite else "No",
                    s.priority,
                    "Active" if s.is_active else "Archived",
                ),
            )

        self.status_var.set(f"Loaded {len(stores)} store(s).")
        self._log(f"[StoresManager] Loaded {len(stores)} stores. show_archived={self._show_archived.get()}")

    def _add_store(self) -> None:
        dlg = _StoreFormDialog(self, title="Add Store")
        self.wait_window(dlg)
        if not dlg.result:
            return
        try:
            s = stores_repo.create_store(**dlg.result)
        except Exception as e:
            messagebox.showerror("Add failed", str(e), parent=self)
            return
        self._log(f"[StoresManager] Added store id={s.id} name='{s.name}'")
        self.refresh()
        self.status_var.set(f"Added store '{s.name}'.")

    def _edit_store(self) -> None:
        store = self._selected_store()
        if store is None:
            messagebox.showinfo("No selection", "Select a store first.", parent=self)
            return
        dlg = _StoreFormDialog(self, title="Edit Store", store=store)
        self.wait_window(dlg)
        if not dlg.result:
            return
        try:
            stores_repo.update_store(store.id, **dlg.result)
        except Exception as e:
            messagebox.showerror("Edit failed", str(e), parent=self)
            return
        self._log(f"[StoresManager] Edited store id={store.id}")
        self.refresh()
        self.status_var.set(f"Updated store '{dlg.result['name']}'.")

    def _toggle_favorite(self) -> None:
        store = self._selected_store()
        if store is None:
            messagebox.showinfo("No selection", "Select a store first.", parent=self)
            return
        try:
            stores_repo.set_store_favorite(store.id, not store.is_favorite)
        except Exception as e:
            messagebox.showerror("Toggle failed", str(e), parent=self)
            return
        label = "favourite" if not store.is_favorite else "not favourite"
        self._log(f"[StoresManager] Store id={store.id} marked {label}")
        self.refresh()

    def _toggle_active(self) -> None:
        store = self._selected_store()
        if store is None:
            messagebox.showinfo("No selection", "Select a store first.", parent=self)
            return

        if store.is_active:
            confirmed = messagebox.askyesno(
                "Archive Store",
                f"Archive '{store.name}'?\n\n"
                "Archived stores are hidden from planning, the optimizer, and deal alerts. "
                "All receipts and price history are kept — nothing is deleted.\n\n"
                "Continue?",
                parent=self,
            )
            if not confirmed:
                return

        try:
            stores_repo.set_store_active(store.id, not store.is_active)
        except Exception as e:
            messagebox.showerror("Failed", str(e), parent=self)
            return

        action = "Reactivated" if not store.is_active else "Archived"
        self._log(f"[StoresManager] {action} store id={store.id} name='{store.name}'")
        self.refresh()
        self.status_var.set(f"{action} store '{store.name}'.")


class _StoreFormDialog(tk.Toplevel):
    """Add / Edit modal form."""

    def __init__(self, parent, *, title: str, store=None) -> None:
        super().__init__(parent)
        self.title(title)
        self.resizable(False, False)
        self.grab_set()

        self.result: Optional[dict] = None

        self._name_var = tk.StringVar(value=store.name if store else "")
        self._address_var = tk.StringVar(value=store.address or "" if store else "")
        self._city_var = tk.StringVar(value=store.city or "" if store else "")
        self._postal_var = tk.StringVar(value=store.postal_code or "" if store else "")
        self._flipp_var = tk.StringVar(value=store.flipp_store_id or "" if store else "")
        self._fav_var = tk.BooleanVar(value=store.is_favorite if store else False)
        self._priority_var = tk.IntVar(value=store.priority if store else 0)
        self._notes_var = tk.StringVar(value=store.notes or "" if store else "")

        self._build()
        self.update_idletasks()
        self.geometry(f"+{parent.winfo_rootx()+60}+{parent.winfo_rooty()+60}")

    def _build(self) -> None:
        frame = ttk.Frame(self, padding=16)
        frame.pack(fill=tk.BOTH, expand=True)

        rows = [
            ("Name *", self._name_var, False),
            ("Address", self._address_var, False),
            ("City", self._city_var, False),
            ("Postal Code", self._postal_var, False),
            ("Flipp Store ID", self._flipp_var, False),
            ("Notes", self._notes_var, False),
        ]

        for i, (label, var, _) in enumerate(rows):
            ttk.Label(frame, text=label).grid(row=i, column=0, sticky="e", padx=(0, 8), pady=4)
            ttk.Entry(frame, textvariable=var, width=36).grid(row=i, column=1, sticky="w", pady=4)

        r = len(rows)
        ttk.Label(frame, text="Favourite").grid(row=r, column=0, sticky="e", padx=(0, 8), pady=4)
        ttk.Checkbutton(frame, variable=self._fav_var).grid(row=r, column=1, sticky="w", pady=4)

        r += 1
        ttk.Label(frame, text="Priority (0–10)").grid(row=r, column=0, sticky="e", padx=(0, 8), pady=4)
        ttk.Spinbox(frame, from_=0, to=10, textvariable=self._priority_var, width=8).grid(
            row=r, column=1, sticky="w", pady=4
        )

        r += 1
        btn_frame = ttk.Frame(frame)
        btn_frame.grid(row=r, column=0, columnspan=2, pady=(12, 0))

        ttk.Button(btn_frame, text="Save", command=self._save, width=10).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(btn_frame, text="Cancel", command=self.destroy, width=10).pack(side=tk.LEFT)

    def _save(self) -> None:
        name = self._name_var.get().strip()
        if not name:
            messagebox.showerror("Validation", "Name is required.", parent=self)
            return

        self.result = dict(
            name=name,
            address=self._address_var.get().strip() or None,
            city=self._city_var.get().strip() or None,
            postal_code=self._postal_var.get().strip() or None,
            flipp_store_id=self._flipp_var.get().strip() or None,
            is_favorite=self._fav_var.get(),
            priority=int(self._priority_var.get()),
            notes=self._notes_var.get().strip() or None,
        )
        self.destroy()


def open_stores_management_window(
    parent: tk.Tk, log: Optional[Callable[[str], None]] = None
) -> None:
    StoresManagementWindow(parent, log=log)
