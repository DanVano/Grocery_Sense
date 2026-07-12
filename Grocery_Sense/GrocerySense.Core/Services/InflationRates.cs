namespace GrocerySense.Core;

// Manually-maintained CAD food-inflation rates (no network). Foundation for Stage 4's two consumers:
// the inflation-adjusted deal baseline (ClassifyDeal) and BasketOptimizer usualAvg — see V3_Phase0_plan.md.
// I0 lands storage + the compounding multiplier only (zero behavior change); the weighted baseline is I1.
public static class InflationRates
{
    // Recency half-life for the adjusted baseline (I1+). Hardcoded, not a user setting — tune on device.
    public const double HalfLifeDays = 90;

    // Accepted bounds for a user-edited annual rate (I4). Reject outside this range — fail loud, never clamp
    // (a clamped 900% typo would silently corrupt every adjusted baseline).
    public const double RateMinPct = -20;
    public const double RateMaxPct = 50;
    public static bool IsRateInBounds(double pct) => pct is >= RateMinPct and <= RateMaxPct;

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

    // Recency-weighted average of inflation-adjusted prices. Each point is lifted to `today` via Multiplier,
    // then weighted by 0.5^(ageDays / halfLife) so recent prices dominate. Undated points must be filtered by
    // the caller (never fabricate a date). Returns (null, 0) when there is nothing to average.
    public static (double? Baseline, int SampleCount) WeightedAdjustedAverage(
        IEnumerable<(DateOnly Date, double Price)> points,
        DateOnly today,
        IReadOnlyDictionary<string, double> ratesByYear,
        double halfLifeDays = HalfLifeDays)
    {
        double sumWeight = 0, sumWeightedAdj = 0;
        int n = 0;
        foreach (var (date, price) in points)
        {
            var (mult, _) = Multiplier(date, today, ratesByYear);
            var ageDays = Math.Max(0, today.DayNumber - date.DayNumber); // future-dated => treat as current
            var weight = Math.Pow(0.5, ageDays / halfLifeDays);
            sumWeight += weight;
            sumWeightedAdj += weight * (price * mult);
            n++;
        }
        return sumWeight > 0 ? (sumWeightedAdj / sumWeight, n) : (null, 0);
    }
}
