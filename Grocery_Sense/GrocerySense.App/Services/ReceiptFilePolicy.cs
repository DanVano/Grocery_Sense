using GrocerySense.Core;

namespace GrocerySense.App.Services;

// Single owner of the receipt-import file policy. Every capture path — the Receipts page (take photo,
// import from library, backfill) and the global Scan FAB — copies through CopyPickAsync.
//
// These bounds used to be declared independently in Receipts.razor and QuickScanService. Two copies of
// a security limit drift, and a drifted limit is a vulnerability rather than untidy duplication, so the
// ceiling, the allowlist and the destination live here and nowhere else.
public static class ReceiptFilePolicy
{
    // A hostile or accidentally huge picker file is rejected before the OCR clients, which read the
    // whole copy into memory, ever see it.
    public const long MaxImportBytes = 20L * 1024 * 1024;

    public const string DefaultExtension = ".jpg";

    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff" };

    public static string ReceiptsDir() => Path.Combine(FileSystem.AppDataDirectory, "receipts");

    // Picker/camera streams are copied into app-data because the source path can expire on Android.
    public static async Task<string> CopyPickAsync(FileResult pick, CancellationToken ct = default)
    {
        await using var source = await pick.OpenReadAsync();
        return await BoundedFileCopy.CopyAsync(
            source, pick.FileName, ReceiptsDir(), Extensions,
            defaultExtension: DefaultExtension, maxBytes: MaxImportBytes, ct: ct);
    }
}
