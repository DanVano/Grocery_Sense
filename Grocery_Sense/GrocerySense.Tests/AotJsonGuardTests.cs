using System.Text.RegularExpressions;

namespace GrocerySense.Tests;

// The Android head is AOT/trimmed: a reflection-based JsonSerializer.Serialize/Deserialize call passes
// every Windows/test run and then crashes on device, where the reflection metadata was trimmed away.
// The repo rule (CLAUDE.md) is that every serialized JSON type goes through a source-gen
// JsonSerializerContext — but nothing enforced it, and device-only crashes are exactly the failures a
// desktop test suite never sees. This source-scan makes the rule fail at `dotnet test` time instead.
//
// Deliberately regex-simple and false-negative-averse: the prefix match also catches
// SerializeToUtf8Bytes / SerializeAsync / DeserializeAsync, and .razor @code blocks are scanned too
// (the App head is Blazor). A call site passes if the JsonTypeInfo argument — the `SomethingContext
// .Default.Something` source-gen pattern, or an explicit JsonTypeInfo — appears within a 3-line window,
// so multi-line calls don't false-positive.
public sealed class AotJsonGuardTests
{
    // Relative file paths whose matches are accepted despite lacking a source-gen context reference.
    // Empty on purpose: RawJson.cs (the expected candidate — Utf8JsonWriter-only) needs no entry because
    // its sole "JsonSerializer.Serialize" mention is a comment, and comment lines are skipped below.
    // Verify any future entry actually is AOT-safe before adding it.
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex CallSite = new(@"JsonSerializer\.(Serialize|Deserialize)");
    private static readonly Regex SourceGen = new(@"Context\.Default\.|JsonTypeInfo");

    [Fact]
    public void JsonSerializer_call_sites_use_a_source_gen_context()
    {
        var root = FindRepoRoot();
        string[] projects =
        {
            "GrocerySense.App", "GrocerySense.Core", "GrocerySense.Data", "GrocerySense.Integrations",
        };

        var offenders = new List<string>();
        foreach (var project in projects)
        foreach (var pattern in new[] { "*.cs", "*.razor" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), pattern, SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            // obj/ holds the source-generated context implementations themselves; bin/ is build output.
            if (rel.Split(Path.DirectorySeparatorChar).Any(seg => seg is "obj" or "bin"))
                continue;
            if (Allowlist.Contains(rel.Replace('\\', '/')))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue; // prose about the rule, not a call site (e.g. RawJson.cs's header comment)
                if (!CallSite.IsMatch(lines[i]))
                    continue;
                var window = string.Join('\n', lines.Skip(i).Take(3));
                if (SourceGen.IsMatch(window))
                    continue;
                offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Reflection-based System.Text.Json call sites — these pass on Windows and crash on the "
            + "AOT/trimmed Android head:\n" + string.Join("\n", offenders)
            + "\nUse a source-gen JsonSerializerContext (see ReceiptSnapshotContext / RecipeJsonContext).");
    }

    // The test host runs from GrocerySense.Tests/bin/<cfg>/<tfm>/; the source tree isn't copied there,
    // so walk up until we hit the directory that contains the GrocerySense.Data project folder.
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "GrocerySense.Data")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not locate the repo root (a directory containing GrocerySense.Data) above " +
            $"'{AppContext.BaseDirectory}' — did the project layout change?");
    }
}
