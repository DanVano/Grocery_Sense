using GrocerySense.Integrations;

namespace GrocerySense.Core;

// The DB-writing half split out of reference-python/.../integrations/azure_docint_client.py
// (ARCHITECTURE.md recommendation). Owns: file-hash dedupe (before any API call), Azure call via the
// OCR client, signature dedupe (merchant+date+total), unit normalization + multibuy parsing, and the
// stores/items/receipts/line_items/prices writes. replace_existing is the only delete+re-ingest path.
public sealed class ReceiptIngestionService
{
    private readonly AzureReceiptOcrClient _ocr;

    public ReceiptIngestionService(AzureReceiptOcrClient ocr) => _ocr = ocr;

    public Task<IngestOutcome> IngestReceiptFileAsync(string filePath, bool replaceExisting = false,
        CancellationToken ct = default) => throw new NotImplementedException();
}
