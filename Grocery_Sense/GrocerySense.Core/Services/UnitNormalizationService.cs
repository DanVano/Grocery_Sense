using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/unit_normalization_service.py — the ONLY place for unit math.
// Normalizes to a base per dimension (weight->kg, volume->L, count->each, dozen<->each) and records
// norm_unit_price/norm_unit/norm_note.
//
// Divergence from Python: no ensure_schema()/ALTER dance — items.default_unit and prices.norm_* columns
// are created up front by the migration ledger (Database.cs), so the schema is always ready. DB-backed
// methods take the caller's connection (+ optional tx) so a Phase-5 ingest can backfill default_unit inside
// its own transaction. The pure methods (Convert/NormalizeUnit/GuessUnitFromText) touch no DB.
public sealed class UnitNormalizationService
{
    private const double LbToKg = 0.45359237;
    private const double GPerOz = 28.3495231;

    // litres-relative volume constants
    private const double MlPerL = 1000.0;
    private const double FlOzPerL = 33.8140226;
    private const double CupPerL = 4.22675284;
    private const double TbspPerL = 67.6280454;
    private const double TspPerL = 202.884136;
    private const double GalPerL = 0.264172052;
    private const double PintPerL = 2.11337642;

    // base-per-unit factor maps: value = base units in one of `key` (kg per unit / L per unit).
    private static readonly Dictionary<string, double> WeightKgPerUnit = new()
    {
        ["kg"] = 1.0, ["g"] = 0.001, ["lb"] = LbToKg, ["oz"] = GPerOz / 1000.0,
    };

    private static readonly Dictionary<string, double> VolumeLPerUnit = new()
    {
        ["L"] = 1.0, ["ml"] = 1.0 / MlPerL, ["fl_oz"] = 1.0 / FlOzPerL, ["cup"] = 1.0 / CupPerL,
        ["tbsp"] = 1.0 / TbspPerL, ["tsp"] = 1.0 / TspPerL, ["gal"] = 1.0 / GalPerL, ["pint"] = 1.0 / PintPerL,
    };

    // ---------------- public normalization (DB-backed) ----------------

    // Returns the price normalized into the item's default unit, backfilling default_unit from the first
    // meaningful observed unit. Mirrors Python normalize(): unknown observed -> guess from description -> each.
    public NormalizedPrice Normalize(SqliteConnection conn, int itemId, double unitPrice, string observedUnit,
        string? description = null, SqliteTransaction? tx = null)
    {
        var obs = NormalizeUnit(observedUnit);
        if (obs == "unknown") obs = GuessUnitFromText(description ?? "");
        if (obs == "unknown") obs = "each";

        SetItemDefaultUnitIfMissing(conn, itemId, obs, tx);
        var defaultUnit = GetItemDefaultUnit(conn, itemId, tx) ?? obs;

        if (defaultUnit == obs)
            return new NormalizedPrice(unitPrice, defaultUnit, "no_conversion");

        var converted = Convert(unitPrice, obs, defaultUnit);
        if (converted is null)
            return new NormalizedPrice(unitPrice, obs, $"no_conversion_possible({obs}->{defaultUnit})");

        return new NormalizedPrice(converted.Value, defaultUnit, $"converted({obs}->{defaultUnit})");
    }

    public string? GetItemDefaultUnit(SqliteConnection conn, int itemId, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT default_unit FROM items WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", itemId);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return null;
        var s = v.ToString()?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    // Sets items.default_unit to observedUnit only when currently NULL/empty and the unit is recognized.
    public void SetItemDefaultUnitIfMissing(SqliteConnection conn, int itemId, string observedUnit,
        SqliteTransaction? tx = null)
    {
        var unit = NormalizeUnit(observedUnit);
        if (unit == "unknown") return;
        if (GetItemDefaultUnit(conn, itemId, tx) is not null) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE items SET default_unit = $u WHERE id = $id;";
        cmd.Parameters.AddWithValue("$u", unit);
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    // ---------------- pure unit math ----------------

    // Convert a per-unit price between compatible units (weight<->weight, volume<->volume, dozen<->each).
    // Returns null for cross-dimension pairs (the caller keeps the observed unit).
    public double? Convert(double unitPrice, string from, string to)
    {
        var f = NormalizeUnit(from);
        var t = NormalizeUnit(to);
        if (f == t) return unitPrice;

        if (WeightKgPerUnit.TryGetValue(f, out var wf) && WeightKgPerUnit.TryGetValue(t, out var wt))
            return unitPrice * (wt / wf);
        if (VolumeLPerUnit.TryGetValue(f, out var vf) && VolumeLPerUnit.TryGetValue(t, out var vt))
            return unitPrice * (vt / vf);
        if (f == "dozen" && t == "each") return unitPrice / 12.0;
        if (f == "each" && t == "dozen") return unitPrice * 12.0;
        return null;
    }

    // Fold a raw unit string to its canonical token, or "unknown".
    public string NormalizeUnit(string? u)
    {
        if (string.IsNullOrWhiteSpace(u)) return "unknown";
        var s = u.Trim().ToLowerInvariant();
        return s switch
        {
            "ea" or "each" or "unit" or "units" or "ct" or "count" => "each",
            "lb" or "lbs" or "#" or "pound" or "pounds" => "lb",
            "kg" or "kgs" or "kilogram" or "kilograms" => "kg",
            "g" or "gram" or "grams" => "g",
            "oz" or "ounce" or "ounces" => "oz",
            "l" or "litre" or "litres" or "liter" or "liters" => "L",
            "ml" or "millilitre" or "millilitres" or "milliliter" or "milliliters" => "ml",
            "fl oz" or "fl_oz" or "floz" or "fluid oz" or "fluid ounce" or "fluid ounces" => "fl_oz",
            "cup" or "cups" => "cup",
            "tbsp" or "tablespoon" or "tablespoons" => "tbsp",
            "tsp" or "teaspoon" or "teaspoons" => "tsp",
            "gal" or "gallon" or "gallons" => "gal",
            "pint" or "pints" or "pt" => "pint",
            "dozen" or "doz" => "dozen",
            "bunch" or "bunches" => "bunch",
            "case" or "cases" => "case",
            "pack" or "packs" or "package" or "packages" or "pkg" => "pack",
            _ => "unknown",
        };
    }

    // Infer a unit from free receipt/flyer text. Order is load-bearing: fl_oz before oz, L before ml, etc.
    public string GuessUnitFromText(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();

        // weight
        if (M(t, @"\bkg\b") || M(t, @"\bkilogram(s)?\b")) return "kg";
        if (M(t, @"(\d+(\.\d+)?)\s*g\b") || M(t, @"\bgrams?\b")) return "g";
        if (M(t, @"\blb(s)?\b") || M(t, @"\bpound(s)?\b") || M(t, @"\b#\b")) return "lb";
        if (M(t, @"\bfl\.?\s*oz\b") || M(t, @"\bfluid\s+ounce(s)?\b")) return "fl_oz";
        if (M(t, @"\boz\b") || M(t, @"\bounce(s)?\b")) return "oz";

        // volume
        if (M(t, @"\blitres?\b") || M(t, @"\bliters?\b") || M(t, @"\b(\d+(\.\d+)?)\s*l\b")) return "L";
        if (M(t, @"\bml\b") || M(t, @"\bmillilitres?\b") || M(t, @"\bmilliliters?\b")) return "ml";
        if (M(t, @"\bcups?\b")) return "cup";
        if (M(t, @"\btbsp\b") || M(t, @"\btablespoons?\b")) return "tbsp";
        if (M(t, @"\btsp\b") || M(t, @"\bteaspoons?\b")) return "tsp";
        if (M(t, @"\bgallons?\b") || M(t, @"\bgal\b")) return "gal";
        if (M(t, @"\bpints?\b")) return "pint";

        // count / pack
        if (M(t, @"\bdozen\b")) return "dozen";
        if (M(t, @"\bbunch(es)?\b")) return "bunch";
        if (M(t, @"\bcase(s)?\b")) return "case";
        if (M(t, @"\bpack(s|age|ages)?\b")) return "pack";
        if (M(t, @"\b(ea|each|unit(s)?|ct|count)\b")) return "each";

        return "unknown";
    }

    private static bool M(string text, string pattern) => Regex.IsMatch(text, pattern);
}
