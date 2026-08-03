using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class InflationRatesTests
{
    private static readonly IReadOnlyDictionary<string, double> Rates = new Dictionary<string, double>
    {
        ["2022"] = 9.8, ["2023"] = 7.8, ["2024"] = 2.2, ["2025"] = 3.5,
    };

    [Fact]
    public void Full_year_applies_the_whole_rate()
    {
        var (m, missing) = InflationRates.Multiplier(new(2022, 1, 1), new(2023, 1, 1), Rates);
        Assert.Equal(1.098, m, precision: 4); // $100 -> $109.80
        Assert.False(missing);
    }

    [Fact]
    public void Multi_year_compounds_each_year()
    {
        var (m, missing) = InflationRates.Multiplier(new(2022, 1, 1), new(2026, 1, 1), Rates);
        Assert.Equal(1.252, m, precision: 3); // ~×1.25 over 2022..2025
        Assert.False(missing);
    }

    [Fact]
    public void Partial_year_is_prorated_by_day_fraction()
    {
        // First half of 2022 (181 of 365 days) -> 1.098 ^ (181/365).
        var (m, _) = InflationRates.Multiplier(new(2022, 1, 1), new(2022, 7, 1), Rates);
        var expected = Math.Pow(1.098, 181.0 / 365.0);
        Assert.Equal(expected, m, precision: 6);
        Assert.True(m is > 1.0 and < 1.098); // strictly between "nothing" and "full year"
    }

    [Fact]
    public void Missing_intervening_year_contributes_zero_and_flags()
    {
        var partial = new Dictionary<string, double> { ["2022"] = 9.8 }; // 2023 absent
        var (m, missing) = InflationRates.Multiplier(new(2022, 1, 1), new(2024, 1, 1), partial);
        Assert.Equal(1.098, m, precision: 4); // 2022 full year only; 2023 segment is 0%
        Assert.True(missing);
    }

    [Fact]
    public void To_before_or_equal_from_is_identity()
    {
        Assert.Equal((1.0, false), InflationRates.Multiplier(new(2024, 1, 1), new(2022, 1, 1), Rates));
        Assert.Equal((1.0, false), InflationRates.Multiplier(new(2023, 5, 1), new(2023, 5, 1), Rates));
    }

    // ---- WeightedAdjustedAverage (the I1 recency-weighted baseline) ----

    [Fact]
    public void Weighted_average_of_no_points_is_null_with_zero_samples()
    {
        var (baseline, n) = InflationRates.WeightedAdjustedAverage(
            Array.Empty<(DateOnly, double)>(), new DateOnly(2026, 8, 1), Rates);
        Assert.Null(baseline);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Recent_point_outweighs_older_one_via_the_90_day_half_life()
    {
        // Empty rates => multiplier 1 for both points, so ONLY the recency weight differs:
        // weights 1 and 0.5^(180/90) = 0.25 -> (10 + 20*0.25) / 1.25 = 12, not the unweighted 15.
        var today = new DateOnly(2026, 8, 1);
        var (baseline, n) = InflationRates.WeightedAdjustedAverage(
            new[] { (today, 10.0), (today.AddDays(-180), 20.0) }, today, new Dictionary<string, double>());
        Assert.Equal(12.0, baseline!.Value, precision: 6);
        Assert.Equal(2, n);
    }

    [Fact]
    public void Future_dated_point_is_treated_as_current_never_deflated()
    {
        // Multiplier(to <= from) = 1 and ageDays clamps to 0 (weight 1), so the price passes through
        // untouched even with real rates in the table — no deflation, no down-weighting.
        var today = new DateOnly(2024, 6, 1);
        var (baseline, n) = InflationRates.WeightedAdjustedAverage(
            new[] { (today.AddDays(30), 10.0) }, today, Rates);
        Assert.Equal(10.0, baseline!.Value, precision: 6);
        Assert.Equal(1, n);
    }

    [Theory]
    [InlineData(-20.0, true)]
    [InlineData(50.0, true)]
    [InlineData(0.0, true)]
    [InlineData(-20.1, false)]
    [InlineData(50.1, false)]
    [InlineData(900.0, false)]
    public void IsRateInBounds_rejects_outside_minus20_to_50(double pct, bool expected)
    {
        Assert.Equal(expected, InflationRates.IsRateInBounds(pct));
    }
}
