namespace GrocerySense.Core;

public static class PathSafety
{
    public static bool IsUnderDirectory(string rootDir, string path)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(path)) return false;

        var root = Path.GetFullPath(rootDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
