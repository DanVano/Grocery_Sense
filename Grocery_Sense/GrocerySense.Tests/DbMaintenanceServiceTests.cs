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
    public void Export_includes_user_recipes()
    {
        using var db = new TempDb();
        UserRecipesRepo.Add(db.Conn, "Dad's Chili", 4,
            new[] { "beef" }, Array.Empty<string>(), Array.Empty<string>());

        var files = new DbMaintenanceService(db.Factory).ExportToCsv(_dir);

        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("user_recipes"));
    }

    // F05 made shopping-list notes free user text and shopping_list is exported — lock the
    // formula-injection neutralization on THIS path so an export refactor can't drop it silently.
    [Fact]
    public void Csv_export_neutralizes_formulas_in_user_edited_list_notes()
    {
        using var db = new TempDb();
        var id = ShoppingListRepo.AddItem(db.Conn, "Milk");
        ShoppingListRepo.UpdateItemDetails(db.Conn, id, 1.0, "", "=HYPERLINK(\"http://evil\",\"click\")");

        var files = new DbMaintenanceService(db.Factory).ExportToCsv(_dir);

        var csv = File.ReadAllText(files.Single(f => Path.GetFileName(f).StartsWith("shopping_list")));
        Assert.Contains("'=HYPERLINK", csv);                    // neutralized with a leading quote
        Assert.DoesNotContain("\n=HYPERLINK", csv);             // never raw at cell start
    }

    [Fact]
    public void CleanupShareArtifacts_deletes_only_old_known_paths()
    {
        var oldBackup = Path.Combine(_dir, "grocery_sense_20260101_000000.db");
        var freshBackup = Path.Combine(_dir, "grocery_sense_20260718_000000.db");
        var unrelated = Path.Combine(_dir, "keep.db");
        var oldExport = Path.Combine(_dir, "export_csv_20260101_000000");
        File.WriteAllText(oldBackup, "old");
        File.WriteAllText(freshBackup, "fresh");
        File.WriteAllText(unrelated, "keep");
        Directory.CreateDirectory(oldExport);
        File.WriteAllText(Path.Combine(oldExport, "receipts.csv"), "old");
        File.SetLastWriteTimeUtc(oldBackup, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(freshBackup, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(oldExport, DateTime.UtcNow.AddDays(-2));

        var removed = DbMaintenanceService.CleanupShareArtifacts(
            _dir, DateTime.UtcNow.AddHours(-24));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(oldBackup));
        Assert.False(Directory.Exists(oldExport));
        Assert.True(File.Exists(freshBackup));
        Assert.True(File.Exists(unrelated));
    }

    // ---- P0-2 orphan sweep: unreferenced intake files older than the cutoff, both dirs, nothing else ----

    private static string TouchFile(string dir, string name, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Sweep_removes_only_unreferenced_old_files_in_both_intake_dirs()
    {
        using var db = new TempDb();
        var receiptsDir = Path.Combine(_dir, "receipts");
        var flyersDir = Path.Combine(_dir, "flyers");
        var old = DateTime.UtcNow.AddDays(-2);

        var orphanReceipt = TouchFile(receiptsDir, "orphan.jpg", old);
        var youngOrphan = TouchFile(receiptsDir, "young.jpg", DateTime.UtcNow); // inside the 24 h age gate
        var referencedReceipt = TouchFile(receiptsDir, "referenced.jpg", old);
        var orphanFlyer = TouchFile(flyersDir, "orphan-page.jpg", old);
        var referencedFlyer = TouchFile(flyersDir, "referenced-page.jpg", old);

        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO receipts (store_id, purchase_date, source, file_path) VALUES ($s, '2026-06-01', 'receipt', $p)";
            cmd.Parameters.AddWithValue("$s", store);
            cmd.Parameters.AddWithValue("$p", referencedReceipt);
            cmd.ExecuteNonQuery();
        }
        var flyerId = FlyersRepo.CreateFlyerBatch(db.Conn, store, "2026-06-01", "2026-06-08");
        FlyersRepo.AddAsset(db.Conn, flyerId, "image", referencedFlyer);

        var removed = new DbMaintenanceService(db.Factory)
            .SweepUnreferencedIntakeFiles(receiptsDir, flyersDir, DateTime.UtcNow.AddHours(-24));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(orphanReceipt));
        Assert.False(File.Exists(orphanFlyer));
        Assert.True(File.Exists(youngOrphan));       // age gate: an in-flight batch is never reaped
        Assert.True(File.Exists(referencedReceipt)); // referenced rows are never touched
        Assert.True(File.Exists(referencedFlyer));
    }

    [Fact]
    public void Sweep_of_missing_dirs_is_a_noop()
    {
        using var db = new TempDb();
        var removed = new DbMaintenanceService(db.Factory).SweepUnreferencedIntakeFiles(
            Path.Combine(_dir, "no-receipts"), Path.Combine(_dir, "no-flyers"), DateTime.UtcNow);
        Assert.Equal(0, removed);
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
        var header = lines[0].Split(',');
        var cells = lines[1].Split(',');
        var idx = Array.IndexOf(header, "total_amount");
        Assert.True(idx >= 0, "total_amount column present");
        Assert.Equal("12.34", cells[idx]);                  // exact TEXT money — a substring match would pass 12.3400000001
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
    public void Csv_export_neutralizes_formula_injection_in_text_cells()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "=1+1"); // a store name that OCR/receipt text could smuggle in

        var written = new DbMaintenanceService(db.Factory).ExportToCsv(_dir);

        var storesCsv = written.Single(p => p.EndsWith("stores.csv"));
        var line = File.ReadAllLines(storesCsv)[1];
        Assert.Contains("'=1+1", line);        // prefixed with ' so a spreadsheet treats it as text
        Assert.DoesNotContain(",=1+1", line);  // never a bare formula at a cell boundary
    }

    [Fact]
    public void Csv_export_leaves_negative_numbers_uncorrupted()
    {
        using var db = new TempDb();
        using (var cmd = db.Conn.CreateCommand()) // typical_package_size is REAL -> a double, leads with '-'
        {
            cmd.CommandText = "INSERT INTO items (canonical_name, typical_package_size) VALUES ('Milk', -2.5)";
            cmd.ExecuteNonQuery();
        }

        var written = new DbMaintenanceService(db.Factory).ExportToCsv(_dir);

        var itemsCsv = written.Single(p => p.EndsWith("items.csv"));
        var line = File.ReadAllLines(itemsCsv)[1];
        Assert.Contains("-2.5", line);      // numeric cells are exempt from neutralization
        Assert.DoesNotContain("'-2.5", line);
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
