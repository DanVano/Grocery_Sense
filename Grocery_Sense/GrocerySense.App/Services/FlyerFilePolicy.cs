using GrocerySense.Core;

namespace GrocerySense.App.Services;

// Single owner of the flyer-import file policy, beside ReceiptFilePolicy for the same reason that file
// documents: these bounds used to live inline in Deals.razor calling BoundedFileCopy directly, and two
// copies of a security limit drift — a drifted limit is a vulnerability, not untidy duplication. The
// ceiling, the allowlist (images + PDF) and the destination live here and nowhere else; the startup
// orphan sweep reads the same directory.
public static class FlyerFilePolicy
{
    // A hostile or accidentally huge picker file is rejected before the layout OCR client, which reads
    // the whole copy into memory, ever sees it.
    public const long MaxImportBytes = 20L * 1024 * 1024;

    public const string DefaultExtension = ".jpg";

    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".pdf" };

    public static string FlyersDir() => Path.Combine(FileSystem.AppDataDirectory, "flyers");

    // Picker streams are copied into app-data because the source path can expire on Android.
    public static async Task<string> CopyPickAsync(FileResult pick, CancellationToken ct = default)
    {
        await using var source = await pick.OpenReadAsync();
        return await BoundedFileCopy.CopyAsync(
            source, pick.FileName, FlyersDir(), Extensions,
            defaultExtension: DefaultExtension, maxBytes: MaxImportBytes, ct: ct);
    }
}
