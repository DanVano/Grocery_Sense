from __future__ import annotations

import threading
import tkinter as tk
from tkinter import ttk, messagebox
from typing import Callable, Optional

from Grocery_Sense.services import budget_service


class BudgetWindow(tk.Toplevel):
    def __init__(self, master: Optional[tk.Misc] = None, *, log: Optional[Callable[[str], None]] = None) -> None:
        super().__init__(master)
        self.title("Budget Tracker")
        self.geometry("680x520")
        self.minsize(560, 400)
        self._log = log or (lambda _: None)
        self._build_ui()
        self._refresh()

    def _build_ui(self) -> None:
        root = ttk.Frame(self, padding=10)
        root.pack(fill="both", expand=True)

        # --- Budget status panel ---
        status_frame = ttk.LabelFrame(root, text="This Month", padding=10)
        status_frame.pack(fill="x", pady=(0, 10))

        self._status_var = tk.StringVar(value="Loading…")
        ttk.Label(status_frame, textvariable=self._status_var, font=("Segoe UI", 12, "bold")).pack(anchor="w")

        self._sub_var = tk.StringVar(value="")
        ttk.Label(status_frame, textvariable=self._sub_var, foreground="#444").pack(anchor="w", pady=(4, 0))

        # Budget setter
        set_frame = ttk.Frame(status_frame)
        set_frame.pack(anchor="w", pady=(8, 0))
        ttk.Label(set_frame, text="Monthly budget ($):").pack(side="left")
        self._budget_entry = ttk.Entry(set_frame, width=12)
        self._budget_entry.pack(side="left", padx=(6, 6))
        ttk.Button(set_frame, text="Save", command=self._save_budget).pack(side="left")
        ttk.Button(set_frame, text="Clear", command=self._clear_budget).pack(side="left", padx=(6, 0))

        # --- Trend table ---
        trend_frame = ttk.LabelFrame(root, text="Monthly Spend (last 12 months)", padding=6)
        trend_frame.pack(fill="both", expand=True)

        cols = ("month", "total", "receipts")
        self.tree = ttk.Treeview(trend_frame, columns=cols, show="headings", height=12)
        self.tree.heading("month", text="Month")
        self.tree.heading("total", text="Spent")
        self.tree.heading("receipts", text="Receipts")
        self.tree.column("month", width=120, anchor="w")
        self.tree.column("total", width=120, anchor="e")
        self.tree.column("receipts", width=90, anchor="center")

        vsb = ttk.Scrollbar(trend_frame, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")

        # Refresh button
        btn_row = ttk.Frame(root)
        btn_row.pack(fill="x", pady=(8, 0))
        self._refresh_btn = ttk.Button(btn_row, text="Refresh", command=self._refresh)
        self._refresh_btn.pack(side="left")
        ttk.Button(btn_row, text="Close", command=self.destroy).pack(side="right")

    def _refresh(self) -> None:
        self._refresh_btn.config(state="disabled")
        self._status_var.set("Loading…")

        def worker():
            try:
                status = budget_service.get_budget_status()
                trend = budget_service.get_trend(12)
                self.after(0, lambda: self._populate(status, trend, None))
            except Exception as exc:
                self.after(0, lambda: self._populate(None, None, exc))

        threading.Thread(target=worker, daemon=True).start()

    def _populate(self, status, trend, error) -> None:
        self._refresh_btn.config(state="normal")
        if error is not None:
            self._status_var.set(f"Error: {error}")
            self._sub_var.set("")
            return

        spent = status["spent"]
        budget = status["budget"]
        month = status["month"]

        if budget is None:
            self._status_var.set(f"Spent ${spent:.2f} in {month}  (no budget set)")
            self._sub_var.set("Enter a monthly budget above to track against it.")
        else:
            remaining = status["remaining"]
            pct = (status["pct_used"] or 0.0) * 100
            over = status["over_budget"]
            color_hint = " ⚠" if status["status"] == "warning" else (" ✗ OVER BUDGET" if over else "")
            self._status_var.set(
                f"Spent ${spent:.2f} of ${budget:.2f} ({pct:.0f}%){color_hint}"
            )
            if over:
                self._sub_var.set(f"${abs(remaining):.2f} over budget for {month}.")
            else:
                self._sub_var.set(f"${remaining:.2f} remaining for {month}.")

        # Pre-fill entry with current budget
        self._budget_entry.delete(0, "end")
        if budget is not None:
            self._budget_entry.insert(0, f"{budget:.2f}")

        # Trend table
        self.tree.delete(*self.tree.get_children())
        for row in (trend or []):
            self.tree.insert("", "end", values=(
                row["month"],
                f"${row['total']:.2f}",
                row["receipt_count"],
            ))

        self._log(f"[Budget] Loaded status for {month}.")

    def _save_budget(self) -> None:
        raw = self._budget_entry.get().strip().lstrip("$")
        try:
            amount = float(raw)
            if amount <= 0:
                raise ValueError
        except (ValueError, TypeError):
            messagebox.showerror("Invalid", "Enter a positive dollar amount.", parent=self)
            return
        try:
            budget_service.save_monthly_budget(amount)
        except Exception as exc:
            messagebox.showerror("Error", str(exc), parent=self)
            return
        self._refresh()

    def _clear_budget(self) -> None:
        try:
            budget_service.save_monthly_budget(None)
        except Exception as exc:
            messagebox.showerror("Error", str(exc), parent=self)
            return
        self._refresh()


def open_budget_window(master: Optional[tk.Misc] = None, *, log: Optional[Callable[[str], None]] = None) -> BudgetWindow:
    return BudgetWindow(master, log=log)
