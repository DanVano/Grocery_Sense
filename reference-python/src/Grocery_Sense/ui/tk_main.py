"""
Grocery_Sense.ui.tk_main

Tkinter prototype UI for Grocery Sense.

Main menu:
- Initialize DB
- Shopping List (Add / Check / Delete)
- Meal Suggestions
- Weekly Plan
- Receipt Import (Azure)
- Receipt Browser (Delete/Undo)
- Stores Management
- Store Plan (with savings)
- Price History Viewer
- Item Manager
- Flyer Import (Manual)
- Seed Demo Data
"""

from __future__ import annotations

import logging
import logging.handlers
import threading
import traceback
import tkinter as tk
from pathlib import Path
from tkinter import ttk, messagebox
from tkinter.scrolledtext import ScrolledText

from Grocery_Sense.data.schema import initialize_database
from Grocery_Sense.config import config_store

from Grocery_Sense.services.shopping_list_service import ShoppingListService
from Grocery_Sense.services.meal_suggestion_service import MealSuggestionService, explain_suggested_meal
from Grocery_Sense.services.price_history_service import PriceHistoryService
from Grocery_Sense.services.flyer_sync_scheduler import FlyerSyncScheduler

from Grocery_Sense.services.weekly_planner_service import (
    WeeklyPlannerService,
    summarize_weekly_plan,
)
from Grocery_Sense.services.demo_seed_service import seed_demo_data

from Grocery_Sense.services import family_requests_service

from Grocery_Sense.ui.basket_optimizer_window import open_basket_optimizer_window
from Grocery_Sense.ui.deal_feed_window import open_deal_feed_window
from Grocery_Sense.ui.family_requests_window import open_family_requests_window
from Grocery_Sense.ui.flyer_import_window import open_flyer_import_window
from Grocery_Sense.ui.item_manager_window import open_item_manager_window
from Grocery_Sense.ui.list_audit_window import open_list_audit_window
from Grocery_Sense.ui.preference_window import open_preferences_window
from Grocery_Sense.ui.price_history_window import open_price_history_window
from Grocery_Sense.ui.receipt_import_window import open_receipt_import_window
from Grocery_Sense.ui.receipt_browser_window import open_receipt_browser_window
from Grocery_Sense.ui.store_plan_window import open_store_plan_window
from Grocery_Sense.ui.budget_window import open_budget_window
from Grocery_Sense.ui.store_settings_window import open_store_settings_window
from Grocery_Sense.ui.price_drop_alerts_window import open_price_drop_alerts_window
from Grocery_Sense.ui.stores_management_window import open_stores_management_window



def _setup_file_logger() -> logging.Logger:
    from Grocery_Sense.data.connection import get_db_path
    log_path = get_db_path().parent / "grocery_sense.log"
    logger = logging.getLogger("grocery_sense")
    if not logger.handlers:
        handler = logging.handlers.RotatingFileHandler(
            log_path, maxBytes=2 * 1024 * 1024, backupCount=3, encoding="utf-8"
        )
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
        logger.addHandler(handler)
        logger.setLevel(logging.INFO)
    return logger


_file_logger = _setup_file_logger()


class GrocerySenseApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("Grocery Sense - Prototype")
        self.geometry("980x700")

        self._db_ready = threading.Event()

        self.shopping_list_service = ShoppingListService()
        self.meal_suggestion_service = MealSuggestionService(
            price_history_service=PriceHistoryService()
        )
        self.weekly_planner_service = WeeklyPlannerService(
            meal_suggestion_service=self.meal_suggestion_service,
            shopping_list_service=self.shopping_list_service,
        )

        self._build_main_menu()
        self._build_log_panel()
        self._log("App started.")

        # Defer scheduler + alert check until widgets are realized and the
        # mainloop is pumping, so any `after(0, ...)` callbacks dispatched from
        # workers don't fire against an unrealized window.
        self._flyer_scheduler = FlyerSyncScheduler(on_sync_complete=self._on_flyer_sync_done)
        self.after(200, self._init_db_async)

        # Cancel background timers cleanly on window close.
        self.protocol("WM_DELETE_WINDOW", self._on_close)

        # Refresh the family-picks badge whenever the main window regains focus
        # (e.g. after closing the shopping-list window where picks are made).
        self.bind("<FocusIn>", lambda _e: self._refresh_request_badge())

    def _on_close(self) -> None:
        try:
            scheduler = getattr(self, "_flyer_scheduler", None)
            if scheduler is not None:
                scheduler.stop()
        except Exception:
            pass
        self.destroy()

    def _init_db_async(self) -> None:
        """Initialize/migrate the DB off the main thread; then start the alert check."""
        self._log("Initializing database schema…")

        def worker() -> None:
            try:
                initialize_database()
                self._db_ready.set()
                self.after(0, lambda: self._log("Database ready."))
                self.after(0, self._refresh_request_badge)
                self.after(0, self._flyer_scheduler.start)  # safe: DB is ready
                threading.Thread(
                    target=self._check_price_drop_alerts, daemon=True
                ).start()
            except Exception as exc:
                self.after(
                    0, lambda e=exc: messagebox.showerror("Error", str(e))
                )

        threading.Thread(target=worker, daemon=True).start()

    # ------------------------------------------------------------------
    # Base UI helpers
    # ------------------------------------------------------------------

    def _build_main_menu(self) -> None:
        frame = ttk.Frame(self)
        frame.pack(side=tk.TOP, fill=tk.X, padx=10, pady=10)

        ttk.Label(frame, text="Grocery Sense - Main Menu", font=("Segoe UI", 14, "bold")).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 8)
        )

        # Family-picks notification badge (parent review queue). Text is bound to
        # _requests_badge_var and refreshed by _refresh_request_badge().
        self._requests_badge_var = tk.StringVar(value="Family Picks")
        self._requests_badge_btn = ttk.Button(
            frame,
            textvariable=self._requests_badge_var,
            command=self._safe_call(self._open_family_requests_window),
            width=22,
        )
        self._requests_badge_btn.grid(row=0, column=2, sticky="e", pady=(0, 8))

        # Buttons grouped by task so the user can scan by intent instead of
        # hunting through a flat 1-21 list. Each tuple is (label, command).
        sections = [
            ("Shop", [
                ("Shopping List", self._open_shopping_list_window),
                ("List Price Audit", lambda: open_list_audit_window(self, log=self._log)),
                ("Deal Feed", lambda: open_deal_feed_window(self, log=self._log)),
                ("Basket Optimizer", lambda: open_basket_optimizer_window(self, log=self._log)),
                ("Store Plan (savings)", lambda: open_store_plan_window(self, log=self._log)),
            ]),
            ("Plan", [
                ("Meal Suggestions", self._open_meal_suggestions_window),
                ("Weekly Plan", self._open_weekly_plan_window),
                ("Plan My Week (guided)", self._open_plan_my_week_window),
                ("Price Drop Alerts", lambda: open_price_drop_alerts_window(self, log=self._log)),
            ]),
            ("Receipts & Prices", [
                ("Receipt Import (Azure)", lambda: open_receipt_import_window(self, log=self._log)),
                ("Receipt Browser", lambda: open_receipt_browser_window(self, log=self._log)),
                ("Flyer Import (Manual)", lambda: open_flyer_import_window(self, log=self._log)),
                ("Price History", lambda: open_price_history_window(self)),
                ("Budget Tracker", lambda: open_budget_window(self, log=self._log)),
            ]),
            ("Catalog & Stores", [
                ("Item Manager", lambda: open_item_manager_window(self, log=self._log)),
                ("Stores Management", self._open_stores_management_window),
                ("Store Shopping Selection", lambda: open_store_settings_window(self, log=self._log)),
            ]),
            ("Setup & Data", [
                ("Initialize / Verify DB", self._handle_init_db),
                ("Preferences", lambda: open_preferences_window(self, log=self._log)),
                ("Seed Demo Data", self._seed_demo_data),
                ("Sync Flyers", self._manual_sync_flyers),
                ("Backup Database", self._backup_database),
                ("Export Data (CSV/JSON)", self._export_data),
            ]),
        ]

        grid_row = 1
        for title, buttons in sections:
            section = ttk.LabelFrame(frame, text=title)
            section.grid(row=grid_row, column=0, columnspan=3, sticky="ew", pady=(0, 6))
            for i, (label, command) in enumerate(buttons):
                ttk.Button(
                    section,
                    text=label,
                    command=self._safe_call(command),
                    width=28,
                ).grid(row=i // 3, column=i % 3, sticky="w", padx=4, pady=3)
            grid_row += 1

    def _backup_database(self) -> None:
        from Grocery_Sense.services.db_maintenance_service import backup_database
        path = backup_database()
        self._log(f"Backup saved: {path}")
        messagebox.showinfo("Backup Complete", f"Database backed up to:\n{path}", parent=self)

    def _export_data(self) -> None:
        from tkinter import filedialog
        from Grocery_Sense.services.db_maintenance_service import export_to_csv, export_to_json
        dest = filedialog.askdirectory(title="Choose export folder", parent=self)
        if not dest:
            return
        dest_path = Path(dest)
        csv_files = export_to_csv(dest_path / "csv")
        json_files = export_to_json(dest_path / "json")
        total = len(csv_files) + len(json_files)
        self._log(f"Exported {total} files to {dest_path}")
        messagebox.showinfo(
            "Export Complete",
            f"Exported {len(csv_files)} CSV and {len(json_files)} JSON files to:\n{dest_path}",
            parent=self,
        )

    def _build_log_panel(self) -> None:
        self.log_box = ScrolledText(self, state=tk.NORMAL, height=12)
        self.log_box.pack(side=tk.BOTTOM, fill=tk.BOTH, expand=False, padx=10, pady=10)
        self._log("Log initialized.")

    def _log(self, message: str) -> None:
        _file_logger.info(message)
        try:
            self.log_box.insert(tk.END, message + "\n")
            self.log_box.see(tk.END)
        except Exception:
            pass

    def _log_exception(self, prefix: str) -> None:
        _file_logger.exception(prefix)
        self._log(prefix)
        self._log(traceback.format_exc())

    def _safe_call(self, func):
        def wrapper():
            try:
                func()
            except Exception as exc:
                self._log_exception("ERROR:")
                messagebox.showerror("Error", str(exc) or exc.__class__.__name__)
        return wrapper

    # ------------------------------------------------------------------
    # Handlers / windows
    # ------------------------------------------------------------------

    def _handle_init_db(self) -> None:
        self._log("Initializing database schema…")

        def worker():
            try:
                initialize_database()
                self.after(0, lambda: self._log("Database schema initialized / verified."))
            except Exception as exc:
                self.after(0, lambda e=exc: messagebox.showerror("Error", str(e)))

        threading.Thread(target=worker, daemon=True).start()

    def _open_stores_management_window(self) -> None:
        open_stores_management_window(self, log=self._log)

    def _seed_demo_data(self) -> None:
        self._log("Seeding demo data…")

        def worker():
            try:
                result = seed_demo_data(reset_first=True, n_price_points=200, days_back=90, seed=42)
                self.after(0, lambda: self._log(
                    f"Demo seed complete: stores={result['stores']}, "
                    f"items={result['items']}, prices={result['price_points']}"
                ))
            except Exception as exc:
                self.after(0, lambda e=exc: messagebox.showerror("Error", str(e)))

        threading.Thread(target=worker, daemon=True).start()

    # ------------------------------------------------------------------
    # Family picks (member picks → parent review)
    # ------------------------------------------------------------------

    def _refresh_request_badge(self) -> None:
        """Update the main-menu badge with the unreviewed family-pick count."""
        if not self._db_ready.is_set():
            return
        try:
            n = family_requests_service.unreviewed_count()
        except Exception:
            return
        self._requests_badge_var.set(f"🔔 Family Picks ({n})" if n else "Family Picks")

    def _open_family_requests_window(self) -> None:
        open_family_requests_window(
            self, log=self._log, on_change=self._refresh_request_badge
        )
        self._refresh_request_badge()

    def _open_meal_picker_dialog(self, parent, member_id, member_name, *, on_done=None) -> None:
        """Let a member pick a meal; its ingredients are added to the list.

        Recipes containing a household allergen are already excluded by
        family_requests_service.pickable_recipes() (household hard excludes).
        """
        names = family_requests_service.pickable_recipes()

        dlg = tk.Toplevel(parent)
        dlg.title(f"Pick a meal — {member_name}")
        dlg.geometry("420x460")

        root = ttk.Frame(dlg)
        root.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)
        ttk.Label(root, text="What would you like to eat?", font=("Segoe UI", 11, "bold")).pack(anchor="w")
        ttk.Label(root, text="Adds the meal's ingredients to the shared list.", foreground="#555").pack(
            anchor="w", pady=(0, 8)
        )

        filter_var = tk.StringVar()
        ttk.Entry(root, textvariable=filter_var).pack(fill=tk.X, pady=(0, 6))

        list_frame = ttk.Frame(root)
        list_frame.pack(fill=tk.BOTH, expand=True)
        listbox = tk.Listbox(list_frame)
        scrollbar = ttk.Scrollbar(list_frame, orient=tk.VERTICAL, command=listbox.yview)
        listbox.configure(yscrollcommand=scrollbar.set)
        listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        shown: list = []

        def repopulate(*_a) -> None:
            nonlocal shown
            q = (filter_var.get() or "").strip().lower()
            shown = [n for n in names if q in n.lower()] if q else list(names)
            listbox.delete(0, tk.END)
            if not shown:
                listbox.insert(tk.END, "(no matching recipes)")
                return
            for n in shown:
                listbox.insert(tk.END, n)

        filter_var.trace_add("write", repopulate)

        def do_pick() -> None:
            sel = listbox.curselection()
            if not sel or not shown:
                self._log("Pick a meal: select a recipe first.")
                return
            idx = int(sel[0])
            if idx < 0 or idx >= len(shown):
                return
            recipe_name = shown[idx]
            family_requests_service.pick_meal(member_id, recipe_name)
            self._log(f"Meal picked: {recipe_name} — {member_name}")
            dlg.destroy()
            if on_done:
                on_done()

        listbox.bind("<Double-Button-1>", lambda _e: do_pick())

        btns = ttk.Frame(root)
        btns.pack(fill=tk.X, pady=(8, 0))
        ttk.Button(btns, text="Add this meal", command=self._safe_call(do_pick)).pack(side=tk.LEFT)
        ttk.Button(btns, text="Cancel", command=dlg.destroy).pack(side=tk.RIGHT)

        repopulate()

    # ------------------------------------------------------------------
    # Shopping List window
    # ------------------------------------------------------------------

    def _open_shopping_list_window(self) -> None:
        win = tk.Toplevel(self)
        win.title("Shopping List")
        win.geometry("820x560")

        root = ttk.Frame(win)
        root.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        ttk.Label(root, text="Shopping List", font=("Segoe UI", 12, "bold")).grid(
            row=0, column=0, sticky="w"
        )

        # --- Member selector
        members = config_store.list_members()
        member_id_by_name = {m.name: m.id for m in members}
        try:
            active_name = config_store.get_active_member().name
        except Exception:
            active_name = members[0].name if members else ""

        member_frame = ttk.Frame(root)
        member_frame.grid(row=0, column=0, sticky="e")
        ttk.Label(member_frame, text="Who's shopping:").pack(side=tk.LEFT, padx=(0, 6))
        member_var = tk.StringVar(value=active_name)
        member_cb = ttk.Combobox(
            member_frame,
            textvariable=member_var,
            values=[m.name for m in members],
            width=14,
            state="readonly",
        )
        member_cb.pack(side=tk.LEFT)

        def on_member_change(_evt=None) -> None:
            name = member_var.get()
            mid = member_id_by_name.get(name)
            if mid is not None:
                config_store.set_active_member_id(mid)

        member_cb.bind("<<ComboboxSelected>>", on_member_change)

        # --- Add item panel
        add_frame = ttk.LabelFrame(root, text="Add Item")
        add_frame.grid(row=1, column=0, sticky="ew", pady=(10, 10))
        add_frame.columnconfigure(1, weight=1)

        ttk.Label(add_frame, text="Name").grid(row=0, column=0, sticky="w", padx=8, pady=6)
        name_var = tk.StringVar()
        name_entry = ttk.Entry(add_frame, textvariable=name_var)
        name_entry.grid(row=0, column=1, sticky="ew", padx=8, pady=6)

        ttk.Label(add_frame, text="Qty").grid(row=0, column=2, sticky="w", padx=8, pady=6)
        qty_var = tk.StringVar()
        qty_entry = ttk.Entry(add_frame, textvariable=qty_var, width=10)
        qty_entry.grid(row=0, column=3, sticky="w", padx=8, pady=6)

        ttk.Label(add_frame, text="Unit").grid(row=0, column=4, sticky="w", padx=8, pady=6)
        unit_var = tk.StringVar(value="each")
        unit_entry = ttk.Entry(add_frame, textvariable=unit_var, width=10)
        unit_entry.grid(row=0, column=5, sticky="w", padx=8, pady=6)

        # --- List panel
        list_frame = ttk.Frame(root)
        list_frame.grid(row=2, column=0, sticky="nsew")
        root.rowconfigure(2, weight=1)
        root.columnconfigure(0, weight=1)

        listbox = tk.Listbox(list_frame, height=14)
        scrollbar = ttk.Scrollbar(list_frame, orient=tk.VERTICAL, command=listbox.yview)
        listbox.configure(yscrollcommand=scrollbar.set)

        listbox.grid(row=0, column=0, sticky="nsew")
        scrollbar.grid(row=0, column=1, sticky="ns")
        list_frame.rowconfigure(0, weight=1)
        list_frame.columnconfigure(0, weight=1)

        current_items = []
        hide_checked_var = tk.BooleanVar(value=True)

        def refresh() -> None:
            nonlocal current_items
            listbox.delete(0, tk.END)
            items = self.shopping_list_service.get_active_items(include_checked_off=True)
            if hide_checked_var.get():
                items = [it for it in items if not it.is_checked_off]
            current_items = items

            if not current_items:
                listbox.insert(tk.END, "(no items)")
                return

            # build id → name lookup fresh on each refresh (members can change)
            id_to_name = {m.id: m.name for m in config_store.list_members()}

            for it in current_items:
                status = "✓" if it.is_checked_off else " "
                qty = "" if it.quantity is None else str(it.quantity)
                unit = "" if it.unit is None else str(it.unit)
                by = id_to_name.get(it.added_by_member_id, it.added_by or "")
                by_label = f" — {by}" if by else ""
                line = f"[{status}] {it.display_name}  {qty} {unit}{by_label}"
                listbox.insert(tk.END, line)

        def get_selected_item():
            if not current_items:
                return None
            sel = listbox.curselection()
            if not sel:
                self._log("Shopping List: select an item first.")
                return None
            idx = int(sel[0])
            if idx < 0 or idx >= len(current_items):
                self._log("Shopping List: invalid selection.")
                return None
            return current_items[idx]

        def on_add_item() -> None:
            name = (name_var.get() or "").strip()
            if not name:
                self._log("Add Item: name is required.")
                return

            qty_raw = (qty_var.get() or "").strip()
            unit = (unit_var.get() or "").strip() or "each"

            quantity = None
            if qty_raw:
                try:
                    quantity = float(qty_raw)
                except ValueError:
                    self._log("Add Item: qty must be a number (or blank).")
                    return

            selected_member_name = member_var.get()
            selected_member_id = member_id_by_name.get(selected_member_name)

            member = config_store.get_member(selected_member_id) if selected_member_id is not None else None
            is_secondary = bool(member and member.role != config_store.ROLE_MASTER)

            if is_secondary:
                # Secondary member → record a "family pick" so the parent is notified.
                family_requests_service.pick_item(
                    selected_member_id,
                    name,
                    quantity=quantity if quantity is not None else 1.0,
                    unit=unit,
                )
            else:
                self.shopping_list_service.add_single_item(
                    name=name,
                    quantity=quantity,
                    unit=unit,
                    planned_store_id=None,
                    notes=None,
                    added_by=selected_member_name or "tk_ui",
                    added_by_member_id=selected_member_id,
                    item_id=None,
                    auto_map=True,
                )
            self._log(f"Added: {name} ({quantity or ''} {unit}) — {selected_member_name or 'unknown'}")
            name_var.set("")
            qty_var.set("")
            name_entry.focus_set()
            refresh()
            self._refresh_request_badge()

        def on_pick_meal() -> None:
            selected_member_name = member_var.get()
            selected_member_id = member_id_by_name.get(selected_member_name)
            if selected_member_id is None:
                self._log("Pick a meal: choose who's shopping first.")
                return
            self._open_meal_picker_dialog(win, selected_member_id, selected_member_name, on_done=lambda: (refresh(), self._refresh_request_badge()))

        def on_toggle_checked() -> None:
            it = get_selected_item()
            if not it:
                return
            new_state = not bool(it.is_checked_off)
            self.shopping_list_service.check_off_item(it.id, checked=new_state)
            self._log(f"{'Checked off' if new_state else 'Unchecked'}: {it.display_name} (id={it.id})")
            refresh()

        def on_delete_item() -> None:
            it = get_selected_item()
            if not it:
                return
            self.shopping_list_service.soft_delete_item(it.id)
            self._log(f"Deleted: {it.display_name} (id={it.id})")
            refresh()

        btn_frame = ttk.Frame(root)
        btn_frame.grid(row=3, column=0, sticky="ew", pady=(10, 0))

        ttk.Button(add_frame, text="Add", command=self._safe_call(on_add_item), width=10).grid(
            row=0, column=6, padx=8, pady=6
        )
        ttk.Button(btn_frame, text="🍽 Pick a meal", command=self._safe_call(on_pick_meal)).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(btn_frame, text="Refresh", command=self._safe_call(refresh)).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(btn_frame, text="Check off / Uncheck", command=self._safe_call(on_toggle_checked)).pack(
            side=tk.LEFT, padx=(0, 8)
        )
        ttk.Button(btn_frame, text="Delete", command=self._safe_call(on_delete_item)).pack(side=tk.LEFT)
        ttk.Checkbutton(
            btn_frame, text="Hide checked-off", variable=hide_checked_var,
            command=self._safe_call(refresh),
        ).pack(side=tk.RIGHT)

        win.bind("<Return>", lambda _e: on_add_item())

        refresh()
        name_entry.focus_set()

    # ------------------------------------------------------------------
    # Meal Suggestions
    # ------------------------------------------------------------------

    def _open_meal_suggestions_window(self) -> None:
        win = tk.Toplevel(self)
        win.title("Meal Suggestions")
        win.geometry("860x540")

        top_frame = ttk.Frame(win)
        top_frame.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=10, pady=10)

        ttk.Label(top_frame, text="Meal Suggestions", font=("Segoe UI", 11, "bold")).grid(
            row=0, column=0, sticky="w"
        )

        listbox = tk.Listbox(top_frame, width=35)
        listbox.grid(row=1, column=0, sticky="nsw", pady=10)

        details = ScrolledText(top_frame, state=tk.NORMAL)
        details.grid(row=1, column=1, sticky="nsew", padx=(10, 0), pady=10)

        top_frame.grid_columnconfigure(1, weight=1)
        top_frame.grid_rowconfigure(1, weight=1)

        suggestions = []
        status_var = tk.StringVar(value="Loading meal suggestions…")
        ttk.Label(top_frame, textvariable=status_var, foreground="#888").grid(
            row=2, column=0, columnspan=2, sticky="w", pady=(4, 0)
        )

        def on_select(_evt):
            idxs = listbox.curselection()
            if not idxs:
                return
            s = suggestions[int(idxs[0])]
            details.delete("1.0", tk.END)
            details.insert(tk.END, explain_suggested_meal(s))

        listbox.bind("<<ListboxSelect>>", on_select)

        def on_add_to_list():
            idxs = listbox.curselection()
            if not idxs:
                self._log("Meal Suggestions: select a recipe first.")
                return
            s = suggestions[int(idxs[0])]
            ings = [str(x).strip() for x in (s.recipe.get("ingredients") or []) if str(x).strip()]
            if not ings:
                messagebox.showinfo("No ingredients", "This recipe lists no ingredients.", parent=win)
                return
            for name in ings:
                self.shopping_list_service.add_single_item(
                    name=name, quantity=1, unit="each", added_by="meal_suggestions_ui", auto_map=True
                )
            recipe_name = s.recipe.get("name") or s.recipe.get("title") or "recipe"
            self._log(f"Added {len(ings)} ingredient(s) from {recipe_name} to shopping list.")
            messagebox.showinfo("Added", f"Added {len(ings)} ingredient(s) to your shopping list.", parent=win)

        ttk.Button(
            top_frame, text="Add ingredients to list", command=self._safe_call(on_add_to_list)
        ).grid(row=3, column=0, columnspan=2, sticky="w", pady=(6, 0))

        def worker():
            try:
                results = self.meal_suggestion_service.suggest_meals_for_week(max_recipes=10)
                win.after(0, lambda: _populate(results, None))
            except Exception as exc:
                win.after(0, lambda: _populate(None, exc))

        def _populate(results, error):
            if not win.winfo_exists():
                return
            if error is not None:
                status_var.set(f"Error: {error}")
                messagebox.showerror("Meal Suggestions Failed", str(error), parent=win)
                return
            if not results:
                status_var.set("No suggestions — recipe catalog is empty or missing.")
                messagebox.showerror(
                    "Recipe Catalog Missing",
                    "No recipes found. Add recipes.json to Grocery_Sense/recipes/ to enable meal suggestions.",
                    parent=win,
                )
                return
            suggestions.extend(results)
            for s in suggestions:
                name = s.recipe.get("name") or s.recipe.get("title") or "Recipe"
                listbox.insert(tk.END, name)
            status_var.set(f"{len(suggestions)} suggestion(s) loaded.")

        threading.Thread(target=worker, daemon=True).start()

    # ------------------------------------------------------------------
    # Weekly Plan
    # ------------------------------------------------------------------

    def _open_weekly_plan_window(self) -> None:
        win = tk.Toplevel(self)
        win.title("Weekly Plan")
        win.geometry("860x580")

        ttk.Label(win, text="Weekly Plan", font=("Segoe UI", 11, "bold")).pack(
            side=tk.TOP, anchor="w", padx=10, pady=10
        )

        summary_box = ScrolledText(win, state=tk.NORMAL)
        summary_box.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=10, pady=(0, 10))

        build_btn = ttk.Button(win, text="Build Weekly Plan", width=22)
        build_btn.pack(side=tk.BOTTOM, pady=8)

        def build_plan():
            summary_box.delete("1.0", tk.END)
            summary_box.insert(tk.END, "Building weekly plan…\n")
            build_btn.config(state="disabled")
            self._log("Building weekly plan (6 recipes, added to shopping list)...")

            def worker():
                try:
                    plan = self.weekly_planner_service.build_weekly_plan(
                        num_recipes=6,
                        persist_to_shopping_list=True,
                        planned_store_id=None,
                        added_by="weekly_planner_ui",
                    )
                    win.after(0, lambda: _populate(plan, None))
                except Exception as exc:
                    win.after(0, lambda: _populate(None, exc))

            def _populate(plan, error):
                if not win.winfo_exists():
                    return
                build_btn.config(state="normal")
                summary_box.delete("1.0", tk.END)
                if error is not None:
                    summary_box.insert(tk.END, f"Error: {error}\n")
                    self._log(f"Weekly plan error: {error}")
                    messagebox.showerror("Weekly Plan Failed", str(error), parent=win)
                    return

                if not plan.suggestions:
                    summary_box.insert(tk.END, "No meals could be planned.\n")
                    messagebox.showerror(
                        "Recipe Catalog Missing",
                        "No recipes found. Add recipes.json to Grocery_Sense/recipes/ to enable weekly planning.",
                        parent=win,
                    )
                    return

                for line in summarize_weekly_plan(plan):
                    summary_box.insert(tk.END, line + "\n")

                summary_box.insert(tk.END, "\nIngredients:\n")
                for ing in plan.planned_ingredients:
                    mapped = "" if ing.item_id is None else f" item_id={ing.item_id} ({ing.match_confidence or 0:.2f})"
                    summary_box.insert(
                        tk.END,
                        f" - {ing.name} (in {ing.approximate_count} recipes){mapped}\n",
                    )

            threading.Thread(target=worker, daemon=True).start()

        build_btn.config(command=self._safe_call(build_plan))
        build_plan()

    # ------------------------------------------------------------------
    # Plan My Week (guided: plan -> review -> add -> optimize)
    # ------------------------------------------------------------------

    def _open_plan_my_week_window(self) -> None:
        win = tk.Toplevel(self)
        win.title("Plan My Week")
        win.geometry("900x640")

        ttk.Label(win, text="Plan My Week", font=("Segoe UI", 12, "bold")).pack(
            side=tk.TOP, anchor="w", padx=10, pady=(10, 4)
        )

        controls = ttk.Frame(win)
        controls.pack(side=tk.TOP, fill=tk.X, padx=10)
        ttk.Label(controls, text="Recipes:").pack(side=tk.LEFT)
        count_var = tk.IntVar(value=6)
        ttk.Spinbox(controls, from_=1, to=14, textvariable=count_var, width=5).pack(side=tk.LEFT, padx=(6, 12))
        build_btn = ttk.Button(controls, text="1) Build plan")
        build_btn.pack(side=tk.LEFT, padx=(0, 8))
        commit_btn = ttk.Button(controls, text="2) Add to list & optimize stores", state="disabled")
        commit_btn.pack(side=tk.LEFT)

        box = ScrolledText(win, state=tk.NORMAL)
        box.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=10, pady=10)

        # Holds the reviewed plan between step 1 and step 2 so we commit the exact
        # recipes the user saw, not a freshly re-rolled (different) set.
        state = {"plan": None}

        def build_plan():
            build_btn.config(state="disabled")
            commit_btn.config(state="disabled")
            box.delete("1.0", tk.END)
            box.insert(tk.END, "Building plan…\n")

            def worker():
                try:
                    plan = self.weekly_planner_service.build_weekly_plan(
                        num_recipes=int(count_var.get()),
                        persist_to_shopping_list=False,
                    )
                    win.after(0, lambda: _show_plan(plan, None))
                except Exception as exc:
                    win.after(0, lambda: _show_plan(None, exc))

            threading.Thread(target=worker, daemon=True).start()

        def _show_plan(plan, error):
            if not win.winfo_exists():
                return
            build_btn.config(state="normal")
            box.delete("1.0", tk.END)
            if error is not None:
                box.insert(tk.END, f"Error: {error}\n")
                messagebox.showerror("Plan My Week Failed", str(error), parent=win)
                return
            if not plan.suggestions:
                box.insert(tk.END, "No meals could be planned (recipe catalog empty?).\n")
                return
            state["plan"] = plan
            box.insert(tk.END, "Review this plan, then click step 2 to commit it.\n\n")
            for line in summarize_weekly_plan(plan):
                box.insert(tk.END, line + "\n")
            box.insert(tk.END, "\nIngredients to be added:\n")
            for ing in plan.planned_ingredients:
                box.insert(tk.END, f"  - {ing.name} (in {ing.approximate_count} recipe(s))\n")
            commit_btn.config(state="normal")

        def commit_plan():
            plan = state.get("plan")
            if plan is None:
                return
            commit_btn.config(state="disabled")
            build_btn.config(state="disabled")
            box.insert(tk.END, "\nAdding ingredients and optimizing stores…\n")

            def worker():
                try:
                    for ing in plan.planned_ingredients:
                        self.shopping_list_service.add_single_item(
                            name=ing.name,
                            quantity=max(1.0, float(ing.approximate_count)),
                            unit="each",
                            added_by="plan_my_week_ui",
                            item_id=ing.item_id,
                            auto_map=True,
                        )
                    from Grocery_Sense.services.basket_optimizer_service import BasketOptimizerService
                    result = BasketOptimizerService().optimize(mode="two_store")
                    win.after(0, lambda: _show_optimized(len(plan.planned_ingredients), result, None))
                except Exception as exc:
                    win.after(0, lambda: _show_optimized(0, None, exc))

            threading.Thread(target=worker, daemon=True).start()

        def _show_optimized(added, result, error):
            if not win.winfo_exists():
                return
            build_btn.config(state="normal")
            if error is not None:
                box.insert(tk.END, f"Error: {error}\n")
                messagebox.showerror("Optimize Failed", str(error), parent=win)
                return
            self._log(f"Plan My Week: added {added} ingredient(s), optimized {len(result.stores)} store(s).")
            box.insert(tk.END, f"\nAdded {added} ingredient(s) to your active shopping list.\n")
            box.insert(tk.END, f"\nOptimized trip ({result.mode}) — estimated total ${result.basket_total_estimated:.2f}\n")
            if result.save_vs_usual_avg is not None:
                box.insert(tk.END, f"Save vs usual: ${result.save_vs_usual_avg:.2f}\n")
            for sp in result.stores:
                box.insert(
                    tk.END,
                    f"  • {sp.store_name}: {len(sp.items)} item(s), ~${sp.total_estimated:.2f}"
                    f" ({sp.unknown_count} unpriced)\n",
                )
            for w in result.warnings:
                box.insert(tk.END, f"  ! {w}\n")
            box.see(tk.END)

        build_btn.config(command=self._safe_call(build_plan))
        commit_btn.config(command=self._safe_call(commit_plan))

    # ------------------------------------------------------------------
    # Flyer sync + price-drop alerts
    # ------------------------------------------------------------------

    def _manual_sync_flyers(self) -> None:
        self._log("Flyer sync: manual sync requested…")
        self._flyer_scheduler.request_sync()

    def _on_flyer_sync_done(self, result) -> None:
        """Called on the sync worker thread after every sync that ran."""
        stores = getattr(result, "stores_synced", 0)
        deals = getattr(result, "deals_inserted", 0)
        errors = getattr(result, "errors", [])
        self.after(0, lambda: self._log(
            f"Flyer sync complete: {stores} store(s), {deals} deal(s) inserted."
            + (f" Errors: {'; '.join(errors)}" if errors else "")
        ))
        # Run price-drop alert check after sync
        threading.Thread(target=self._check_price_drop_alerts, daemon=True).start()

    def _check_price_drop_alerts(self) -> None:
        """Run on a worker thread; posts any alerts back to the main thread."""
        try:
            from Grocery_Sense.services.price_drop_alert_service import PriceDropAlertService
            svc = PriceDropAlertService()
            alerts = svc.compute_engine_alerts(staples_only=False)
        except Exception as exc:
            self.after(0, lambda: self._log(f"Price-drop alert check failed: {exc}"))
            return

        if not alerts:
            return

        def _show_alerts():
            lines = []
            for a in alerts[:10]:  # cap at 10 to avoid a huge popup
                name = a.get("item_name") or a.get("canonical_name") or "Unknown item"
                store = a.get("store_name") or ""
                price = a.get("current_price")
                price_str = f"${price:.2f}" if price is not None else "?"
                lines.append(f"• {name}  {price_str}  @ {store}")

            more = len(alerts) - 10
            body = "\n".join(lines)
            if more > 0:
                body += f"\n… and {more} more."

            messagebox.showinfo(
                f"Price Drop Alerts ({len(alerts)})",
                f"The following tracked items have dropped in price:\n\n{body}",
            )

        self.after(0, _show_alerts)


def main() -> None:
    app = GrocerySenseApp()
    app.mainloop()


if __name__ == "__main__":
    main()
