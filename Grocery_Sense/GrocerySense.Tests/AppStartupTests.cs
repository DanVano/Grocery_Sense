using GrocerySense.Data;
using Xunit;

namespace GrocerySense.Tests;

// The startup state machine moved from the MAUI App head into Data precisely so this invariant is
// testable off-device: migrations run, and a broken DB must be VISIBLE (Error + message), never a
// silent retry that leaves the app pretending to load.
public sealed class AppStartupTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"gs_startup_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Ready_after_migrations_on_a_good_db()
    {
        var path = TempDbPath();
        try
        {
            var startup = new AppStartup(new SqliteConnectionFactory(path));
            var changed = false;
            startup.Changed += () => changed = true;

            await startup.EnsureStartedAsync();

            Assert.Equal(StartupStatus.Ready, startup.Status);
            Assert.Null(startup.Error);
            Assert.True(changed); // the UI is notified so it can leave the loading frame
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Broken_db_surfaces_error_verbatim_and_never_stays_loading()
    {
        // A file that isn't a valid SQLite database → Initialize throws when it's opened/read.
        var path = TempDbPath();
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xDE, 0xAD, 0xBE, 0xEF });
        try
        {
            var startup = new AppStartup(new SqliteConnectionFactory(path));

            await startup.EnsureStartedAsync();

            Assert.Equal(StartupStatus.Error, startup.Status);
            Assert.False(string.IsNullOrWhiteSpace(startup.Error)); // the failure is shown, not swallowed
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task EnsureStartedAsync_is_single_flight()
    {
        var path = TempDbPath();
        try
        {
            var startup = new AppStartup(new SqliteConnectionFactory(path));

            var first = startup.EnsureStartedAsync();
            var second = startup.EnsureStartedAsync();

            Assert.Same(first, second); // Lazy<Task>: every caller awaits the one migration run
            await first;
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort cleanup */ }
        }
    }
}
