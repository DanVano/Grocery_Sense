using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class DbMaintenanceServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_dbmaint_{Guid.NewGuid():N}");
    public DbMaintenanceServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static void SeedReceiptWithMoney(TempDb db, string total)
    {
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, total_amount, source) VALUES ($s, '2026-06-01', $t, 'receipt')";
        cmd.Parameters.AddWithValue("$s", store);
        cmd.Parameters.AddWithValue("$t", total); // TEXT money
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Backup_produces_a_valid_db_copy_with_the_data()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Metro");
        var backup = Path.Combine(_dir, "backup.db");

        new DbMaintenanceService(db.Factory).BackupDatabase(backup);

        Assert.True(File.Exists(backup));
        using var conn = new SqliteConnection($"Data Source={backup}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM stores WHERE name = 'Metro'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Csv_export_writes_header_and_keeps_money_text_exact()
    {
        using var db = new TempDb();
        SeedReceiptWithMoney(db, "12.34");

        var written = new DbMaintenanceService(db.Factory).ExportToCsv(_dir);

        var receiptsCsv = written.Single(p => p.EndsWith("receipts.csv"));
        var lines = File.ReadAllLines(receiptsCsv);
        Assert.Contains("total_amount", lines[0]);          // header row
        Assert.Contains("12.34", lines[1]);                 // exact TEXT money, no 12.3400000001
    }

    [Fact]
    public void Json_export_parses_and_keeps_money_text_exact()
    {
        using var db = new TempDb();
        SeedReceiptWithMoney(db, "12.34");

        var written = new DbMaintenanceService(db.Factory).ExportToJson(_dir);

        var receiptsJson = written.Single(p => p.EndsWith("receipts.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(receiptsJson));
        var row = doc.RootElement[0];
        Assert.Equal("12.34", row.GetProperty("total_amount").GetString());
    }

    [Fact]
    public void Export_skips_missing_and_empty_tables()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "OnlyStore"); // stores has a row; receipts/prices/items/list empty

        var written = new DbMaintenanceService(db.Factory).ExportToCsv(_dir);

        Assert.Single(written);
        Assert.EndsWith("stores.csv", written[0]);
    }
}
