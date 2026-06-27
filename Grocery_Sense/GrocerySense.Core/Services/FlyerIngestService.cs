namespace GrocerySense.Core;

// Port of reference-python/.../services/flyer_ingest_service.py — manual flyer asset/JSON ingest.
public sealed class FlyerIngestService
{
    public FlyerIngestResult IngestAssets(int? storeId, string? validFrom, string? validTo, IReadOnlyList<string> filePaths,
        string rawJsonDir, string sourceType = "manual_upload", string? sourceRef = null, string? note = null,
        bool tryItemMapping = true) => throw new NotImplementedException();
}
