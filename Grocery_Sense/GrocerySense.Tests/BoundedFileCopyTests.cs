using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class BoundedFileCopyTests : TempDirTestBase
{


    [Fact]
    public async Task Oversized_source_throws_and_removes_partial_file()
    {
        await using var source = new MemoryStream(new byte[11]);

        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedFileCopy.CopyAsync(
            source, "receipt.jpg", _dir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg" },
            defaultExtension: ".jpg", maxBytes: 10));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Disallowed_extension_is_rejected()
    {
        await using var source = new MemoryStream(new byte[4]);

        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedFileCopy.CopyAsync(
            source, "malware.exe", _dir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg" },
            defaultExtension: ".jpg", maxBytes: 10));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task In_bounds_source_copies_and_returns_path()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await using var source = new MemoryStream(payload);

        var path = await BoundedFileCopy.CopyAsync(
            source, "receipt.JPG", _dir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg" },
            defaultExtension: ".jpg", maxBytes: 10);

        Assert.True(File.Exists(path));
        Assert.EndsWith(".jpg", path); // extension normalized to lowercase
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }
}
