using GrocerySense.Data;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Tests;

// Real temp-file SQLite DB with the schema applied, plus one open connection. Repo tests use this
// instead of mocking — the migration ledger + pragmas + decimal/TEXT round-trip are part of what we test.
internal sealed class TempDb : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gs_test_{Guid.NewGuid():N}.db");

    public SqliteConnectionFactory Factory { get; }
    public SqliteConnection Conn { get; }

    public TempDb()
    {
        Factory = new SqliteConnectionFactory(_path);
        Database.Initialize(Factory);
        Conn = Factory.Open();
    }

    public void Dispose()
    {
        Conn.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch { /* temp file, ignore */ }
        }
    }
}
