using GrocerySense.Data;
using Microsoft.Data.Sqlite;
using Xunit;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public class SqliteConnectionFactoryTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"gs_test_{Guid.NewGuid():N}.db");

    [Fact]
    public void Open_applies_pragmas_and_round_trips()
    {
        var path = TempDbPath();
        try
        {
            var factory = new SqliteConnectionFactory(path);
            using (var conn = factory.Open())
            {
                Assert.Equal(1L, Scalar(conn, "PRAGMA foreign_keys;"));
                Assert.Equal("wal", (Scalar(conn, "PRAGMA journal_mode;") as string)?.ToLowerInvariant());

                Exec(conn, "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
                Exec(conn, "INSERT INTO t (name) VALUES ('milk');");
                Assert.Equal("milk", Scalar(conn, "SELECT name FROM t WHERE id = 1;") as string);
            }

            // Reopen a fresh connection on the same file → data persisted (real file, not :memory:).
            var factory2 = new SqliteConnectionFactory(path);
            using var conn2 = factory2.Open();
            Assert.Equal("milk", Scalar(conn2, "SELECT name FROM t WHERE id = 1;") as string);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }


    private static void TryDelete(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort cleanup */ }
        }
    }
}
