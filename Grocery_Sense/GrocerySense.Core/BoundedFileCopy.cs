namespace GrocerySense.Core;

// Copies a picker/camera stream into app storage with a hard byte ceiling and an extension allowlist,
// so a hostile or accidentally huge OS-picker file can't be streamed straight into memory by the OCR
// clients (which File.ReadAllBytes the copy). Stdlib only. A partial/oversize copy is deleted on failure.
public static class BoundedFileCopy
{
    public static async Task<string> CopyAsync(
        Stream source,
        string sourceFileName,
        string destinationDirectory,
        IReadOnlySet<string> allowedExtensions,
        string defaultExtension,
        long maxBytes,
        CancellationToken ct = default)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var extension = Path.GetExtension(Path.GetFileName(sourceFileName));
        if (string.IsNullOrWhiteSpace(extension)) extension = defaultExtension;
        extension = extension.ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
            throw new InvalidDataException($"Unsupported file type: {extension}");

        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, $"{Guid.NewGuid():N}{extension}");

        try
        {
            await using var destination = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0) return path;
                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException($"Selected file exceeds {maxBytes / (1024 * 1024)} MiB.");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            try { File.Delete(path); } catch { /* best-effort cleanup of the partial copy */ }
            throw;
        }
    }
}
