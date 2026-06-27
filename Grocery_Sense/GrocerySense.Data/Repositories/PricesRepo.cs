using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/prices_repo.py
// The richest repo: single-item + batch readers and the median/"usual"/six-month-low math.
// Keep raw SQL + window functions; chunk IN-lists at 900 params (_SQL_PARAM_CHUNK).
public static class PricesRepo
{
    public static int AddPricePoint(SqliteConnection conn, int itemId, int storeId, double unitPrice, string unit,
        double? quantity = null, double? totalPrice = null, string? rawName = null, int? confidence = null,
        string source = "manual", string? date = null, int? receiptId = null, int? flyerSourceId = null)
        => throw new NotImplementedException();

    public static IReadOnlyList<PricePoint> GetPricesForItem(SqliteConnection conn, int itemId, int? storeId = null,
        int sinceDays = 365, int? limit = null) => throw new NotImplementedException();

    public static PricePoint? GetMostRecentPrice(SqliteConnection conn, int itemId, int? storeId = null) => throw new NotImplementedException();

    public static PriceStats GetPriceStatsForItem(SqliteConnection conn, int itemId, int? storeId = null, int sinceDays = 365)
        => throw new NotImplementedException();

    // (median, samples, basis) — basis in receipt_median | estimated_median | unknown
    public static (double? Price, int Samples, string Basis) GetUsualUnitPrice(SqliteConnection conn, int itemId,
        int? storeId = null, bool receiptOnly = true, int minSamples = 4, int sinceDays = 180) => throw new NotImplementedException();

    public static (double? Price, string? WhenIso) GetSixMonthLowUnitPrice(SqliteConnection conn, int itemId,
        int? storeId = null, int sinceDays = 183) => throw new NotImplementedException();

    public static double? GetActiveFlyerUnitPrice(SqliteConnection conn, int itemId, int storeId) => throw new NotImplementedException();

    public static IReadOnlyList<(int ItemId, int LineCount, int DistinctReceipts)> ListStapleItemIds(SqliteConnection conn,
        int sinceDays = 90, int minDistinctReceipts = 3, int minLineItems = 4) => throw new NotImplementedException();

    // PORT (batch): get_most_recent_prices_by_store_batch, get_active_flyer_prices_batch,
    // get_usual_unit_price_batch, get_six_month_low_batch, get_recent_avg_unit_price_*_batch,
    // get_purchase_cadence_batch — see prices_repo.py. Port these before BasketOptimizer/Planning.
}
