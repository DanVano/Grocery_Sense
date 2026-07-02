using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void IsUnderDirectory_accepts_child_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs_root");
        var file = Path.Combine(root, "receipts", "a.jpg");

        Assert.True(PathSafety.IsUnderDirectory(root, file));
    }

    [Fact]
    public void IsUnderDirectory_rejects_sibling_prefix_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs_root");
        var file = Path.Combine(Path.GetTempPath(), "gs_root_evil", "a.jpg");

        Assert.False(PathSafety.IsUnderDirectory(root, file));
    }

    [Fact]
    public void IsUnderDirectory_rejects_parent_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs_root");
        var file = Path.Combine(root, "..", "outside.jpg");

        Assert.False(PathSafety.IsUnderDirectory(root, file));
    }
}
