namespace GrocerySense.Core;

// Manually-maintained CAD food-inflation rates (no network). Foundation for Stage 4's two consumers:
// the inflation-adjusted deal baseline (ClassifyDeal) and BasketOptimizer usualAvg — see V3_Phase0_plan.md.
// I0 lands storage + the compounding multiplier only (zero behavior change); the weighted baseline is I1.
public static class InflationRates
{
    // Recency half-life for the adjusted baseline (I1+). Hardcoded, not a user setting — tune on device.
    public const double HalfLifeDays = 90;

    // StatCan annual food-inflation % (2026 provisional). Seeded into user_config.json only when absent,
    // then user-editable (Preferences, I4). Year-string keys match the snake_case JSON dict shape.
    public static IReadOnlyDictionary<string, double> Seed { get; } = new Dictionary<string, double>
    {
        ["2019"] = 3.7,
        ["2020"] = 2.4,
        ["2021"] = 2.2,
        ["2022"] = 9.8,
        ["2023"] = 7.8,
        ["2024"] = 2.2,
        ["2025"] = 3.5,
        ["2026"] = 4.3, // provisional
    };

    // Factor that brings a price paid on `from` up to its equivalent on `to`, compounding each calendar
    // year's rate over the fraction of that year the interval covers (partial years pro-rated by day count).
    // `to <= from` => 1.0 — never deflate (a future/equal-dated point is already current).
    // A calendar year with no rate entry contributes 0% for its segment AND flips MissingYear so the caller
    // can disclose the gap — never a silent guess (CLAUDE: fail loud / never fake).
    public static (double Multiplier, bool MissingYear) Multiplier(
        DateOnly from, DateOnly to, IReadOnlyDictionary<string, double> ratesByYear)
    {
        if (to <= from) return (1.0, false);

        double multiplier = 1.0;
        bool missingYear = false;
        var cursor = from;
        while (cursor < to)
        {
            var year = cursor.Year;
            var nextYearStart = new DateOnly(year + 1, 1, 1);
            var segmentEnd = to < nextYearStart ? to : nextYearStart;      // half-open [cursor, segmentEnd)
            var segmentDays = segmentEnd.DayNumber - cursor.DayNumber;
            var daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;

            if (ratesByYear.TryGetValue(year.ToString(), out var ratePct))
                multiplier *= Math.Pow(1 + ratePct / 100.0, (double)segmentDays / daysInYear);
            else
                missingYear = true; // 0% for this segment (× 1), flagged.

            cursor = segmentEnd;
        }
        return (multiplier, missingYear);
    }
}
