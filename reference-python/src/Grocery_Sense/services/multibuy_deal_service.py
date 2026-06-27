from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Optional, Tuple


@dataclass(frozen=True)
class DealAdjusted:
    quantity: float
    unit_price: Optional[float]
    line_total: Optional[float]
    deal_note: str


class MultiBuyDealService:
    """
    Normalize common multi-buy deal formats into an "effective" unit price.

    Handles (v1):
      - "2/$5", "2 / $5.00", "2/5"
      - "3 for 10", "3 for $10.00"
      - "2 @ 4.00" (interpreted as 2 units at $4 each)
      - "Buy 1 get 1", "BOGO", "buy one get one"

    Strategy:
      - Prefer actual receipt amounts when available (line_total and/or discount),
        because some receipts already apply the promo price.
      - If promo pattern indicates bundle qty but receipt qty is missing/wrong,
        adjust quantity (conservatively) only when it matches totals.
    """

    # Regex patterns
    _re_slash = re.compile(r"\b(\d+)\s*/\s*\$?\s*(\d+(?:\.\d+)?)\b")          # 2/$5
    _re_for = re.compile(r"\b(\d+)\s*for\s*\$?\s*(\d+(?:\.\d+)?)\b")          # 3 for 10
    _re_at = re.compile(r"\b(\d+)\s*@\s*\$?\s*(\d+(?:\.\d+)?)\b")             # 2 @ 4.00
    _re_bogo = re.compile(r"\b(bogo|buy\s*1\s*get\s*1|buy\s*one\s*get\s*one)\b", re.IGNORECASE)

    def adjust(
        self,
        *,
        description: str,
        quantity: Optional[float],
        unit_price: Optional[float],
        line_total: Optional[float],
        discount: Optional[float],
    ) -> DealAdjusted:
        # If a quantity WAS reported but is unusable (<=0 / non-numeric) we
        # default it to 1.0 below, which can distort the effective unit price.
        # Disclose that substitution in the deal_note rather than coercing it
        # silently (DealAdjusted is frozen, so we rebuild it with the marker).
        qty_reported_but_invalid = False
        if quantity is not None:
            try:
                qty_reported_but_invalid = float(quantity) <= 0
            except (TypeError, ValueError):
                qty_reported_but_invalid = True

        da = self._adjust_core(
            description=description,
            quantity=quantity,
            unit_price=unit_price,
            line_total=line_total,
            discount=discount,
        )
        if qty_reported_but_invalid:
            note = f"{da.deal_note};qty_defaulted" if da.deal_note else "qty_defaulted"
            da = DealAdjusted(
                quantity=da.quantity,
                unit_price=da.unit_price,
                line_total=da.line_total,
                deal_note=note,
            )
        return da

    def _adjust_core(
        self,
        *,
        description: str,
        quantity: Optional[float],
        unit_price: Optional[float],
        line_total: Optional[float],
        discount: Optional[float],
    ) -> DealAdjusted:
        desc = (description or "").strip()
        try:
            q = float(quantity) if quantity is not None else 1.0
            if q <= 0:
                q = 1.0
        except (TypeError, ValueError):
            q = 1.0
        up = float(unit_price) if unit_price is not None else None
        lt = float(line_total) if line_total is not None else None
        disc = float(discount) if discount is not None else 0.0

        # 1) BOGO-like promotions: compute effective unit price using net total if possible
        if self._re_bogo.search(desc):
            # Use receipt net total if possible:
            # - some receipts show line_total as gross and discount as promo
            # - some show net already (discount 0)
            base_total = lt if lt is not None else (up * q if up is not None else None)
            if base_total is not None and q >= 2:
                net_total = base_total - (disc or 0.0)
                if net_total > 0:
                    eff = net_total / q
                    return DealAdjusted(quantity=q, unit_price=eff, line_total=net_total, deal_note="bogo_effective_price")
            return DealAdjusted(quantity=q, unit_price=up, line_total=lt, deal_note="bogo_detected_no_adjust")

        # 2) Bundle price patterns: 2/$5, 3 for 10
        bundle = self._parse_bundle_price(desc)
        if bundle is not None:
            bundle_qty, bundle_total = bundle

            # If receipt provides a line total, trust it (but reconcile qty if it's obviously a bundle line)
            if lt is not None:
                # If quantity looks wrong and line_total matches bundle_total, fix q
                if (q < bundle_qty) and self._close(lt, bundle_total):
                    q2 = float(bundle_qty)
                    net_total = max(0.0, lt - (disc or 0.0))
                    eff = net_total / q2 if q2 > 0 else None
                    return DealAdjusted(quantity=q2, unit_price=eff, line_total=net_total, deal_note=f"bundle({bundle_qty}/${bundle_total})_qty_fix")

                # If qty is multiple of bundle qty and totals align, still compute effective from totals
                net_total = max(0.0, lt - (disc or 0.0))
                if q > 0:
                    eff = net_total / q
                    return DealAdjusted(quantity=q, unit_price=eff, line_total=net_total, deal_note=f"bundle({bundle_qty}/${bundle_total})_from_total")

            # No line total: fall back to stated promo math
            eff = bundle_total / float(bundle_qty)
            # If quantity is a multiple of bundle qty, keep q and compute implied line total
            implied_total = eff * q
            implied_total = implied_total - (disc or 0.0)
            deal_note = f"bundle({bundle_qty}/${bundle_total})_from_text"
            if q < bundle_qty:
                deal_note += ";qty_below_bundle_unverified"
            return DealAdjusted(quantity=q, unit_price=eff, line_total=implied_total, deal_note=deal_note)

        # 3) "2 @ 4.00" means 2 units at $4 each
        at = self._parse_at_price(desc)
        if at is not None:
            at_qty, each_price = at

            # If unit_price missing, set it
            if up is None or up <= 0:
                up2 = each_price
            else:
                up2 = up

            # If receipt qty is missing/wrong and at_qty makes sense, bump it
            q2 = q
            if q < at_qty:
                # Only bump if totals match or totals missing
                if lt is None:
                    q2 = float(at_qty)
                else:
                    # If line total looks like at_qty * each_price, bump
                    if self._close(lt, float(at_qty) * float(each_price)):
                        q2 = float(at_qty)

            # If line_total missing, compute it (synthesised lt already nets disc)
            lt2 = lt
            if lt2 is None and up2 is not None:
                lt2 = (up2 * q2) - (disc or 0.0)

            # If line_total present, compute effective using net total / qty.
            # When the receipt supplied `lt`, treat it as gross and net the
            # discount exactly once; the synthesised lt2 already did.
            if lt2 is not None and q2 > 0:
                if lt is not None:
                    net_total = max(0.0, lt - (disc or 0.0))
                else:
                    net_total = lt2
                eff = net_total / q2
                return DealAdjusted(quantity=q2, unit_price=eff, line_total=net_total, deal_note=f"at({at_qty}@{each_price})")

            return DealAdjusted(quantity=q2, unit_price=up2, line_total=lt2, deal_note=f"at({at_qty}@{each_price})_no_total")

        # 4) No deal detected: optionally compute unit_price if missing from totals
        if (up is None or up <= 0) and (lt is not None) and q > 0:
            net_total = lt - (disc or 0.0)
            eff = net_total / q
            return DealAdjusted(quantity=q, unit_price=eff, line_total=net_total, deal_note="unit_from_total")

        return DealAdjusted(quantity=q, unit_price=up, line_total=lt, deal_note="no_deal")

    # -----------------------------
    # Parsers
    # -----------------------------

    def _parse_bundle_price(self, text: str) -> Optional[Tuple[int, float]]:
        t = (text or "").lower()

        m = self._re_slash.search(t)
        if m:
            bundle = self._validate_bundle(m.group(1), m.group(2))
            if bundle is not None:
                return bundle

        m = self._re_for.search(t)
        if m:
            bundle = self._validate_bundle(m.group(1), m.group(2))
            if bundle is not None:
                return bundle

        return None

    @staticmethod
    def _validate_bundle(qty_s: str, total_s: str) -> Optional[Tuple[int, float]]:
        """Accept only plausible multi-buy bundles.

        The slash/`for` patterns are otherwise greedy enough to misread ordinary
        text as a bundle price, e.g. "1/2 cup" (qty 1) or a date like "12/2024"
        (total 2024). A real multi-buy has qty >= 2 and a grocery-scale total, so:
          - qty in [2, 24]
          - 0 < total <= 999
        This keeps legitimate dollarless bundles like "2/5" ("2 for $5") working.
        """
        try:
            qty = int(qty_s)
            total = float(total_s)
        except (TypeError, ValueError):
            return None
        if qty < 2 or qty > 24:
            return None
        if total <= 0 or total > 999:
            return None
        return qty, total

    def _parse_at_price(self, text: str) -> Optional[Tuple[int, float]]:
        t = (text or "").lower()
        m = self._re_at.search(t)
        if not m:
            return None
        qty = int(m.group(1))
        each = float(m.group(2))
        if qty > 0 and each > 0:
            return qty, each
        return None

    # -----------------------------
    # Utils
    # -----------------------------

    def _close(self, a: float, b: float, tol: float = 0.02, rel: float = 0.005) -> bool:
        # Absolute floor + relative tolerance so the 2¢ window doesn't vanish on
        # items priced in hundreds.
        diff = abs(float(a) - float(b))
        return diff <= max(tol, rel * max(abs(float(a)), abs(float(b))))
