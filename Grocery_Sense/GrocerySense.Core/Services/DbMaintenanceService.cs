using System.Globalization;
using System.Text;
using System.Text.Json;
using GrocerySense.Data;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/db_maintenance_service.py — DB backup + CSV/JSON export.
// Backup uses VACUUM INTO (a clean, WAL-safe single-file snapshot) instead of Python's online-backup API —
// the mobile flow writes one file to the app cache and hands it to the share sheet, so there is no local
// backups directory to prune. Scope is the DB only; receipt images are NOT included.
public sealed class DbMaintenanceService
{
    // Human-meaningful tables (skip dedupe/raw-json/internal). Names are a fixed whitelist, never user input.
    private static readonly string[] ExportTables = { "receipts", "prices", "items", "shopping_list", "stores" };

    private readonly SqliteConnectionFactory _factory;

    public DbMaintenanceService(SqliteConnectionFactory factory) => _factory = factory;

    // Writes a clean copy of the live DB to destPath and returns it. destPath is overwritten if present.
    public string BackupDatabase(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        // VACUUM INTO takes a SQL string literal, not a parameter; the path is app-controlled (cache dir).
        cmd.CommandText = $"VACUUM INTO '{destPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
        return destPath;
    }

    // Sweeps stale share artifacts this service writes into the OS cache dir (plaintext DB backups and
    // CSV/JSON export folders) once they're older than cutoffUtc, so a shared copy doesn't linger in the
    // clear after the share sheet is done. Narrow by name so it never touches unrelated cache files. Returns
    // the count removed. Best-effort: a cache dir the OS already evicted just returns 0.
    public static int CleanupShareArtifacts(string cacheDir, DateTime cutoffUtc)
    {
        if (!Directory.Exists(cacheDir)) return 0;
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(
                     cacheDir, "grocery_sense_*.db", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoffUtc) continue;
            File.Delete(file);
            removed++;
        }

        foreach (var dir in Directory.EnumerateDirectories(
                     cacheDir, "export_*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith("export_csv_", StringComparison.Ordinal)
                && !name.StartsWith("export_json_", StringComparison.Ordinal))
                continue;
            if (Directory.GetLastWriteTimeUtc(dir) >= cutoffUtc) continue;
            Directory.Delete(dir, recursive: true);
            removed++;
        }

        return removed;
    }

    public IReadOnlyList<string> ExportToCsv(string destDir)
    {
        Directory.CreateDirectory(destDir);
        var written = new List<string>();
        using var conn = _factory.Open();
        foreach (var table in ExportTables)
        {
            var data = ReadTable(conn, table);
            if (data is not { Rows.Count: > 0 }) continue;

            var path = Path.Combine(destDir, $"{table}.csv");
            using var w = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            w.WriteLine(string.Join(",", data.Columns.Select(CsvEscape))); // headers: fixed schema names, no neutralization
            foreach (var row in data.Rows)
                w.WriteLine(string.Join(",", row.Select(v => CsvEscape(CsvSanitizeCell(v)))));
            written.Add(path);
        }
        return written;
    }

    public IReadOnlyList<string> ExportToJson(string destDir)
    {
        Directory.CreateDirectory(destDir);
        var written = new List<string>();
        using var conn = _factory.Open();
        foreach (var table in ExportTables)
        {
            var data = ReadTable(conn, table);
            if (data is not { Rows.Count: > 0 }) continue;

            var path = Path.Combine(destDir, $"{table}.json");
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                jw.WriteStartArray();
                foreach (var row in data.Rows)
                {
                    jw.WriteStartObject();
                    for (var i = 0; i < data.Columns.Count; i++)
                    {
                        jw.WritePropertyName(data.Columns[i]);
                        WriteJsonCell(jw, row[i]);
                    }
                    jw.WriteEndObject();
                }
                jw.WriteEndArray();
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            written.Add(path);
        }
        return written;
    }

    private sealed record TableData(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

    // Reads a whole table, or null if it doesn't exist. Values stay as SQLite returns them (TEXT money stays
    // its exact string — no decimal/double round-trip).
    private static TableData? ReadTable(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table}";
            using var r = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
            var rows = new List<object?[]>();
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return new TableData(columns, rows);
        }
        catch (SqliteException e) when (IsNoSuchTable(e))
        {
            return null; // table genuinely doesn't exist -> caller skips it
        }
        // Any other SqliteException (SQLITE_BUSY, I/O, corruption, …) propagates: silently dropping a whole
        // table from an export while reporting success would violate the fail-loud rule.
    }

    private static bool IsNoSuchTable(SqliteException e) =>
        e.SqliteErrorCode == 1 /* SQLITE_ERROR */ &&
        e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    private static string CellString(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        byte[] b => Convert.ToBase64String(b),
        _ => v.ToString() ?? "",
    };

    private static void WriteJsonCell(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case byte[] b: w.WriteStringValue(Convert.ToBase64String(b)); break;
            default: w.WriteStringValue(v.ToString()); break; // TEXT (incl. money) stays an exact string
        }
    }

    // CSV formula-injection neutralization (CWE-1236 / OWASP): a TEXT cell that leads with =,+,-,@,tab,CR is
    // evaluated as a formula when the export is opened in Excel/Sheets, so prefix it with a single quote.
    // Numeric long/double cells are exempt — otherwise every negative number (leading '-') would be corrupted.
    private static string CsvSanitizeCell(object? v)
    {
        var s = CellString(v);
        if (v is long or double) return s;
        return s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;
    }

    // RFC 4180: quote a field containing a comma, quote, CR or LF; escape quotes by doubling.
    private static string CsvEscape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
