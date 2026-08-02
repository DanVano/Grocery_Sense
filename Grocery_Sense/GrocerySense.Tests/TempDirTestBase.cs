namespace GrocerySense.Tests;

// Per-test temp directory: created in the constructor, deleted on dispose. Same fixture shape
// FlyerSyncTestBase already uses, lifted out of the sixteen classes that hand-rolled it.
// RestoreStagingTests deliberately stays custom — its Dispose also calls ClearAllPools, which is
// process-wide and why that class is pinned to a non-parallel collection (V2_FOLLOWUPS §4.19).
public abstract class TempDirTestBase : IDisposable
{
    protected readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_test_{Guid.NewGuid():N}");

    protected TempDirTestBase() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    // A uniquely-named file in the temp dir holding `content`. The extension matters only where the
    // code under test sniffs it (flyer ingest treats .pdf differently from an image).
    protected string WriteFile(string content, string ext = ".jpg")
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, content);
        return path;
    }
}
