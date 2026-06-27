from __future__ import annotations

import threading
import tkinter as tk
from tkinter import ttk
from typing import Any, Callable, Dict, Optional

from Grocery_Sense.services import list_audit_service


_CLASS_LABEL = {
    "great": "🔥 Great",
    "good": "✅ Good",
    "typical": "➖ Typical",
    "expensive": "⚠️ Overpay",
    "no_data": "— no data",
    "unknown": "— no usual",
}


class ListAuditWindow(tk.Toplevel):
    """Pre-shop audit: best current price per list item vs your usual, with
    overpay flags so you know what to leave for a future trip."""

    def __init__(self, master: Optional[tk.Misc] = None, *, log: Optional[Callable[[str], None]] = None) -> None:
        super().__init__(master)
        self.title("Shopping List — Price Audit")
        self.geometry("1000x620")
        self.minsize(880, 520)
        self._log = log

        self._build_ui()
        self._refresh()
        self.protocol("WM_DELETE_WINDOW", self.destroy)

    def _build_ui(self) -> None:
        root = ttk.Frame(self, padding=10)
        root.pack(fill="both", expand=True)

        top = ttk.Frame(root)
        top.pack(fill="x")
        ttk.Label(top, text="What to buy now vs. wait on", font=("Segoe UI", 11, "bold")).pack(side="left")
        self._refresh_btn = ttk.Button(top, text="Refresh", command=self._refresh)
        self._refresh_btn.pack(side="right")

        self._status_var = tk.StringVar(value="")
        ttk.Label(root, textvariable=self._status_var, foreground="#666").pack(anchor="w", pady=(4, 0))

        self._caveat_var = tk.StringVar(value="")
        ttk.Label(root, textvariable=self._caveat_var, foreground="#a60", wraplength=960, justify="left").pack(
            anchor="w", pady=(2, 8)
        )

        body = ttk.Frame(root)
        body.pack(fill="both", expand=True)
        body.rowconfigure(0, weight=1)
        body.columnconfigure(0, weight=1)

        cols = ("verdict", "item", "qty", "best", "store", "usual", "pct", "line")
        self.tree = ttk.Treeview(body, columns=cols, show="headings", height=16)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vsb = ttk.Scrollbar(body, orient="vertical", command=self.tree.yview)
        vsb.grid(row=0, column=1, sticky="ns")
        self.tree.configure(yscrollcommand=vsb.set)

        headings = {
            "verdict": ("Verdict", 110, "w"),
            "item": ("Item", 260, "w"),
            "qty": ("Qty", 60, "e"),
            "best": ("Best now", 100, "e"),
            "store": ("Store", 150, "w"),
            "usual": ("Usual", 100, "e"),
            "pct": ("vs usual", 90, "e"),
            "line": ("Est. line", 100, "e"),
        }
        for key, (text, width, anchor) in headings.items():
            self.tree.heading(key, text=text)
            self.tree.column(key, width=width, anchor=anchor)

        # Highlight overpays.
        self.tree.tag_configure("overpay", background="#fff0f0")

    def _refresh(self) -> None:
        self._refresh_btn.config(state="disabled")
        self._status_var.set("Auditing list…")
        self.tree.delete(*self.tree.get_children())

        def worker():
            try:
                audit = list_audit_service.audit_active_list()
                self.after(0, lambda: self._populate(audit, None))
            except Exception as exc:
                self.after(0, lambda: self._populate(None, exc))

        threading.Thread(target=worker, daemon=True).start()

    def _populate(self, audit: Optional[Dict[str, Any]], error: Optional[Exception]) -> None:
        self._refresh_btn.config(state="normal")
        if error is not None:
            self._status_var.set(f"Error: {error}")
            return

        self.tree.delete(*self.tree.get_children())
        for li in audit["line_items"]:
            cls = li["classification"]
            verdict = _CLASS_LABEL.get(cls, cls)
            best = f"${li['best_unit']:.2f}" if li["best_unit"] is not None else "—"
            usual = f"${li['usual_unit']:.2f}" if li["usual_unit"] is not None else "—"
            pct = f"{li['pct_vs_usual']:+.0f}%" if li["pct_vs_usual"] is not None else "—"
            line = f"${li['est_line_cost']:.2f}" if li["est_line_cost"] is not None else "—"
            tags = ("overpay",) if cls == "expensive" else ()
            self.tree.insert(
                "", "end",
                values=(verdict, li["name"], f"{li['qty']:g}", best, li["best_store"], usual, pct, line),
                tags=tags,
            )

        overpays = len(audit["overpay_items"])
        net = audit["savings_vs_usual"]
        sign = "saving" if net >= 0 else "OVER"
        parts = [
            f"Est. basket ${audit['estimated_total']:.2f}",
            f"{sign} ${abs(net):.2f} vs usual",
            f"{overpays} overpay item(s)",
            f"{audit['priced_count']} priced / {audit['unknown_price_count']} no-history / {len(audit['unmatched'])} unmatched",
        ]
        self._status_var.set("  •  ".join(parts))

        caveat = ""
        if audit["estimate_caveat"]:
            caveat = ("Note: some list quantities use a different unit than the price (e.g. 'each' vs '/kg'); "
                      "line totals are rough. The verdict per item is the reliable signal.")
        if audit["unmatched"]:
            caveat += ("  Unmatched items have no canonical match yet — map them in Item Manager to price them: "
                       + ", ".join(audit["unmatched"][:8]) + ("…" if len(audit["unmatched"]) > 8 else ""))
        self._caveat_var.set(caveat)

        if self._log:
            try:
                self._log(f"List audit: {overpays} overpay item(s), est ${audit['estimated_total']:.2f}")
            except Exception:
                pass


def open_list_audit_window(
    master: Optional[tk.Misc] = None, *, log: Optional[Callable[[str], None]] = None
) -> ListAuditWindow:
    return ListAuditWindow(master, log=log)
