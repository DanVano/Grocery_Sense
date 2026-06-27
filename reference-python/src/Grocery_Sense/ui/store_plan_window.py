from __future__ import annotations

import tkinter as tk
from tkinter import ttk, messagebox
from typing import Callable, Optional

from Grocery_Sense.services.planning_service import PlanningService


class StorePlanWindow(tk.Toplevel):
    def __init__(self, parent: tk.Tk, *, log: Optional[Callable[[str], None]] = None) -> None:
        super().__init__(parent)
        self.title("Store Plan")
        self.geometry("900x620")
        self._log = log or (lambda msg: None)

        self.planner = PlanningService()

        self._build_ui()
        self.refresh()

    def _build_ui(self) -> None:
        top = ttk.Frame(self)
        top.pack(fill=tk.X, padx=10, pady=10)

        ttk.Label(top, text="Store Plan (with estimated costs)", font=("Segoe UI", 12, "bold")).pack(side=tk.LEFT)

        ttk.Button(top, text="Refresh", command=self.refresh).pack(side=tk.RIGHT)

        # Costs panel
        costs = ttk.LabelFrame(self, text="Estimates")
        costs.pack(fill=tk.X, padx=10, pady=(0, 10))

        self.var_basket = tk.StringVar(value="Basket (plan split): n/a")
        self.var_baseline = tk.StringVar(value="Baseline (all at one favorite): n/a")
        self.var_savings = tk.StringVar(value="Estimated savings: n/a")
        self.var_coverage = tk.StringVar(value="Coverage: n/a")

        ttk.Label(costs, textvariable=self.var_basket).pack(anchor="w", padx=10, pady=(6, 0))
        ttk.Label(costs, textvariable=self.var_baseline).pack(anchor="w", padx=10, pady=(2, 0))
        ttk.Label(costs, textvariable=self.var_savings).pack(anchor="w", padx=10, pady=(2, 0))
        ttk.Label(costs, textvariable=self.var_coverage).pack(anchor="w", padx=10, pady=(2, 6))

        # Store breakdown table
        mid = ttk.Frame(self)
        mid.pack(fill=tk.BOTH, expand=True, padx=10, pady=(0, 10))

        cols = ("store", "items", "est_subtotal", "estimated_items", "missing_items")
        self.tree = ttk.Treeview(mid, columns=cols, show="headings")
        self.tree.heading("store", text="Store")
        self.tree.heading("items", text="# Items")
        self.tree.heading("est_subtotal", text="Est Subtotal")
        self.tree.heading("estimated_items", text="Estimated")
        self.tree.heading("missing_items", text="Missing")

        self.tree.column("store", width=320, anchor="w")
        self.tree.column("items", width=90, anchor="center")
        self.tree.column("est_subtotal", width=140, anchor="e")
        self.tree.column("estimated_items", width=100, anchor="center")
        self.tree.column("missing_items", width=90, anchor="center")

        yscroll = ttk.Scrollbar(mid, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=yscroll.set)

        self.tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        yscroll.pack(side=tk.RIGHT, fill=tk.Y)

        # Summary box
        bottom = ttk.LabelFrame(self, text="Summary")
        bottom.pack(fill=tk.BOTH, expand=False, padx=10, pady=(0, 10))

        self.summary_text = tk.Text(bottom, height=10, wrap="word")
        self.summary_text.pack(fill=tk.BOTH, expand=True, padx=8, pady=8)

        ttk.Button(self, text="Close", command=self.destroy).pack(pady=(0, 10))

    def refresh(self) -> None:
        try:
            plan = self.planner.build_plan_for_active_list(max_stores=3)
        except Exception as e:
            messagebox.showerror("Store Plan Error", str(e))
            return

        self.tree.delete(*self.tree.get_children())

        costs = plan.get("costs", {}) or {}
        basket = costs.get("basket_total_estimate")
        baseline = costs.get("baseline_total_estimate")
        savings = costs.get("estimated_savings")
        coverage = costs.get("coverage") or {}

        self.var_basket.set(f"Basket (plan split): {'$' + format(basket, '.2f') if basket is not None else 'n/a'}")
        self.var_baseline.set(
            f"Baseline (all at one favorite): {'$' + format(baseline, '.2f') if baseline is not None else 'n/a'}"
        )
        self.var_savings.set(f"Estimated savings: {'$' + format(savings, '.2f') if savings is not None else 'n/a'}")

        if isinstance(coverage, dict):
            self.var_coverage.set(
                f"Coverage: {coverage.get('estimated_items', 0)}/{coverage.get('total_items', 0)} items estimated "
                f"({coverage.get('missing_items', 0)} missing)"
            )
        else:
            self.var_coverage.set("Coverage: n/a")

        stores_struct = plan.get("stores", {}) or {}
        for sid, data in stores_struct.items():
            st = data.get("store")
            items = data.get("items") or []
            est_sub = data.get("estimated_subtotal")
            est_items = data.get("estimated_items", 0)
            miss_items = data.get("missing_items", 0)

            store_name = getattr(st, "name", f"Store {sid}")
            if getattr(st, "is_favorite", False):
                store_name += " ★"

            self.tree.insert(
                "",
                "end",
                values=(
                    store_name,
                    len(items),
                    f"${est_sub:.2f}" if isinstance(est_sub, (int, float)) else "n/a",
                    est_items,
                    miss_items,
                ),
            )

        summary = plan.get("summary", "") or ""
        self.summary_text.delete("1.0", tk.END)
        self.summary_text.insert(tk.END, summary)

        self._log("[StorePlan] Refreshed with cost estimates.")


def open_store_plan_window(parent: tk.Tk, log: Optional[Callable[[str], None]] = None) -> None:
    StorePlanWindow(parent, log=log)
