using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FuzzySharp;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.StrategySensitive;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Receipt ingest orchestrator. Receipt/raw/line/price writes are transactional; item/alias/unit prep is not.
// ponytail: uses the injected IngredientMappingService (accept 0.78); Python ingest used 0.75 — 3-point
// divergence, not worth a second mapper instance + DI change.
public sealed class ReceiptIngestionService
{
    // P0-3 service-boundary bounds — authoritative here (the tests project has no App reference, so
    // UI-level checks are unprovable). Batch size aligns with the documented chunk-of-10 backfill protocol.
    public const int MaxBatchFiles = 10;
    public const long MaxBatchAggregateBytes = 100L * 1024 * 1024;
    // Application parse/persistence guard on the OCR response (post-SDK-buffering — NOT a transport limit).
    public const int MaxRawJsonChars = 16 * 1024 * 1024;
    // Pre-BuildIngest validation, enforced before any DB open or catalog write.
    public const int MaxReceiptLines = 300;
    public const int MaxMerchantChars = 200;
    public const int MaxFieldChars = 500;

    private readonly IReceiptOcrClient _ocr;
    private readonly OcrGate _gate;
    private readonly SqliteConnectionFactory _factory;
    private readonly IngredientMappingService _mapper;
    private readonly UnitNormalizationService _unitNorm;
    private readonly MultiBuyDealService _multibuy;
    private readonly ItemAliasesRepo _aliases = new();

    private const int StoreMatchThreshold = 85;

    public ReceiptIngestionService(IReceiptOcrClient ocr, OcrGate gate, SqliteConnectionFactory factory,
        IngredientMappingService mapper, UnitNormalizationService unitNorm, MultiBuyDealService multibuy)
    {
        _ocr = ocr;
        _gate = gate;
        _factory = factory;
        _mapper = mapper;
        _unitNorm = unitNorm;
        _multibuy = multibuy;
    }

    // One-shot ingest (single-scan path): prepare then auto-commit with the OCR/mtime date. Behavior is
    // identical to the pre-split method — Commit with no override resolves OcrDate ?? FallbackDate.
    public async Task<IngestOutcome> IngestReceiptFileAsync(string filePath, bool replaceExisting = false,
        CancellationToken ct = default)
    {
        var prepared = await PrepareReceiptFileAsync(filePath, replaceExisting, ct);
        return prepared.Duplicate ?? CommitPreparedReceipt(prepared, null, ct);
    }

    // Phase 1 (backfill on-ramp): the OCR + parse half, split from the DB write so the caller can confirm the
    // purchase date before committing. Runs file-hash dedupe (pre-OCR), OCR, signature dedupe, then parse. If a
    // dedupe decides the outcome, returns it in Duplicate with Ingest == null (no date prompt needed).
    //
    // Replace mode (P0-1) only OBSERVES the duplicate owners here — nothing is deleted until the commit
    // transaction, so an OCR error, cancel, or backfill skip after prepare leaves the original untouched.
    public async Task<ReceiptPrepared> PrepareReceiptFileAsync(string filePath, bool replaceExisting = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Receipt file not found", filePath);

        var fileHash = ComputeSha256(filePath);
        var fallbackDate = InferDate(filePath);

        // 1) file-hash dedupe (BEFORE any OCR call).
        int? fileHashOwner = null;
        using (var conn = _factory.Open())
        {
            var existing = ReceiptsRepo.FindReceiptIdByFileHash(conn, fileHash);
            if (existing is not null)
            {
                if (!replaceExisting)
                    return Decided(new IngestOutcome(existing, true, null, "file_hash"), fallbackDate);
                fileHashOwner = existing;
            }
        }

        // 2) OCR — the one paid call, serialized + deadlined by the injected singleton gate.
        var (operationId, rawJson) = await _gate.RunAsync(
            tok => _ocr.AnalyzeReceiptFileAsync(filePath, tok), ct);

        // Parse/persistence guard: refuse to navigate or store a pathologically large OCR response.
        var rawJsonStr = RawJson.ToJsonString(rawJson);
        if (rawJsonStr.Length > MaxRawJsonChars)
            throw new InvalidDataException(
                $"OCR response is {rawJsonStr.Length / (1024 * 1024)} MiB of JSON — over the 16 MiB guard; not parsed or persisted.");

        // 3) signature dedupe (catches rescans of the same receipt). One typed parse of the raw JSON —
        // the header feeds the signature, the confirm dialog AND the ingest build below.
        var doc = ReceiptDocument.Parse(rawJson, MaxMerchantChars);
        int? signatureOwner = null;
        var signature = MakeSignature(doc.Header.Merchant, doc.Header.IsoDate, doc.Header.Total);
        if (signature is not null)
        {
            using var conn = _factory.Open();
            var existingSig = ReceiptsRepo.FindReceiptIdBySignature(conn, signature);
            if (existingSig is not null)
            {
                if (!replaceExisting)
                    return Decided(new IngestOutcome(existingSig, true, operationId, "signature"),
                        fallbackDate, operationId);
                signatureOwner = existingSig;
            }
        }

        // Fail closed: the file-hash owner and the signature owner are DIFFERENT receipts — a replace would
        // have to delete two receipts, which is never inferred. Disclosed conflict; no delete, no import.
        if (fileHashOwner is not null && signatureOwner is not null && fileHashOwner != signatureOwner)
            return Decided(ConflictOutcome(operationId,
                $"file-hash matches receipt #{fileHashOwner} but merchant/date/total match receipt #{signatureOwner}"),
                fallbackDate, operationId);

        // 4) resolve item ids/unit-norm/multibuy from the parsed document (pre-transaction; mapper
        // writes alias/items here).
        var ingest = BuildIngest(doc, rawJsonStr, filePath, operationId, fileHash, signature, ct);
        _mapper.FlushLearnedAliases();

        var ocrDate = doc.Header.IsoDate.Length > 0 ? doc.Header.IsoDate : null;
        return new ReceiptPrepared(ingest, operationId, ocrDate, fallbackDate,
            string.IsNullOrEmpty(doc.Header.Merchant) ? "Unknown Store" : doc.Header.Merchant,
            doc.Header.Total, ingest.Lines.Count,
            replaceExisting, null, fileHashOwner, signatureOwner);
    }

    // The DB-write half. confirmedDate overrides the purchase date on the receipt AND every price row (both
    // read r.PurchaseDate in ReceiptsRepo.IngestReceipt). With no override, resolves OcrDate ?? FallbackDate —
    // the single-scan default. Backfill callers pass an explicit confirmedDate so an undated old receipt is
    // never stamped "today".
    //
    // Replace mode: the owner re-read, backup-delete and insert commit in ONE transaction — either the
    // replacement fully lands or the original receipt graph and backup ledger stay exactly as they were.
    public IngestOutcome CommitPreparedReceipt(ReceiptPrepared prepared, string? confirmedDate = null,
        CancellationToken ct = default)
    {
        if (prepared.Duplicate is not null) return prepared.Duplicate;
        if (prepared.Ingest is null) throw new InvalidOperationException("Nothing prepared to commit.");

        var date = confirmedDate ?? prepared.OcrDate ?? prepared.FallbackDate;
        var ingest = prepared.Ingest with { PurchaseDate = date };
        var operationId = prepared.OperationId;

        // Closes the await→commit cancellation window: once cancelled, no transaction is ever begun.
        ct.ThrowIfCancellationRequested();

        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        int? deletedReceiptId = null;
        try
        {
            if (prepared.ReplaceRequested)
            {
                // Re-read both owners inside the tx — never delete a row prepare didn't observe.
                var hashOwner = ingest.FileHash is { } fh ? ReceiptsRepo.FindReceiptIdByFileHash(conn, fh, tx) : null;
                var sigOwner = ingest.Signature is { } sg ? ReceiptsRepo.FindReceiptIdBySignature(conn, sg, tx) : null;

                if (OwnerAppearedOrChanged(prepared.FileHashOwnerId, hashOwner)
                    || OwnerAppearedOrChanged(prepared.SignatureOwnerId, sigOwner)
                    || (hashOwner is not null && sigOwner is not null && hashOwner != sigOwner))
                {
                    tx.Rollback();
                    return ConflictOutcome(operationId,
                        "the duplicate receipt changed between prepare and commit");
                }

                // An owner that disappeared since prepare needs no delete; a confirmed one is backed up
                // and deleted inside this same transaction.
                var target = hashOwner ?? sigOwner;
                if (target is not null)
                {
                    ReceiptsRepo.DeleteReceiptWithBackup(conn, target.Value, tx);
                    deletedReceiptId = target;
                }
            }

            var receiptId = ReceiptsRepo.IngestReceipt(conn, ingest, tx);
            tx.Commit();
            return new IngestOutcome(receiptId, false, operationId, null, deletedReceiptId is not null);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && deletedReceiptId is null)
        {
            // Legitimate duplicate race on a non-deleting commit (concurrent import of the same receipt).
            // When this commit DELETED an owner the filter is false and the exception propagates instead:
            // the rolled-back original would be rediscovered here and a real insert failure would be
            // misreported as "duplicate".
            tx.Rollback();
            if (ingest.FileHash is { } fh && ReceiptsRepo.FindReceiptIdByFileHash(conn, fh) is { } byHash)
                return new IngestOutcome(byHash, true, operationId, "file_hash");
            if (ingest.Signature is { } sig && ReceiptsRepo.FindReceiptIdBySignature(conn, sig) is { } bySig)
                return new IngestOutcome(bySig, true, operationId, "signature");
            throw;
        }
    }

    // "Appeared" (prepare saw none, one exists now) or "changed" (a different receipt owns the key now).
    // A disappeared owner is NOT a conflict — there is simply nothing left to delete.
    private static bool OwnerAppearedOrChanged(int? observed, int? current) =>
        current is not null && current != observed;

    private static IngestOutcome ConflictOutcome(string? operationId, string detail) =>
        new(null, false, operationId, ReplaceConflict: true, ConflictDetail: detail);

    // Backfill batch import: prepare each file, ask dateResolver for the confirmed purchase date, then commit.
    // dateResolver returns the ISO date to use, or null to SKIP the receipt (no write) — there is no "default
    // to today" path, so an undated receipt the user declines to date is simply skipped, never mis-stamped.
    // Alerts are deliberately NOT scanned here (backfill suppression); run ScanRecentReceipts once afterwards.
    // ponytail: sequential — 50-150 receipts, each gated on a human date confirm; concurrency buys nothing.
    public async Task<BatchImportSummary> ImportBatchAsync(IReadOnlyList<string> filePaths,
        Func<ReceiptPrepared, CancellationToken, Task<string?>> dateResolver, bool replaceExisting = false,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // P0-3 authoritative caps, before any OCR: a UI can repeat these early for UX, but only this
        // boundary is provable (the tests project has no App reference). Over-limit = disclosed reject,
        // zero paid calls.
        if (filePaths.Count > MaxBatchFiles)
            throw new InvalidOperationException(
                $"Backfill imports are capped at {MaxBatchFiles} files per batch (got {filePaths.Count}) — " +
                "import in chunks of 10.");
        long aggregateBytes = 0;
        foreach (var p in filePaths)
            if (new FileInfo(p) is { Exists: true } fi)
                aggregateBytes += fi.Length;
        if (aggregateBytes > MaxBatchAggregateBytes)
            throw new InvalidOperationException(
                $"Backfill batch totals {aggregateBytes / (1024 * 1024)} MiB — over the " +
                $"{MaxBatchAggregateBytes / (1024 * 1024)} MiB cap; import fewer or smaller photos.");

        var items = new List<BatchImportItem>(filePaths.Count);
        for (var i = 0; i < filePaths.Count; i++)
        {
            var path = filePaths[i];
            if (ct.IsCancellationRequested)
            {
                items.Add(new BatchImportItem(path, BatchImportStatus.Cancelled, null, null));
                continue;
            }
            try
            {
                var prepared = await PrepareReceiptFileAsync(path, replaceExisting, ct);
                if (prepared.Duplicate is { } dup)
                {
                    items.Add(dup.ReplaceConflict
                        ? new BatchImportItem(path, BatchImportStatus.Conflict, null, dup.ConflictDetail)
                        : new BatchImportItem(path,
                            dup.DuplicateReason == "signature"
                                ? BatchImportStatus.DuplicateSignature : BatchImportStatus.DuplicateFile,
                            dup.ReceiptId, dup.DuplicateReason));
                    continue;
                }

                var confirmedDate = await dateResolver(prepared, ct);
                if (confirmedDate is null)
                {
                    items.Add(new BatchImportItem(path, BatchImportStatus.Skipped, null,
                        prepared.OcrFoundDate ? "skipped" : "no date confirmed"));
                    continue;
                }

                var outcome = CommitPreparedReceipt(prepared, confirmedDate, ct);
                var committed = outcome.ReplaceConflict
                    ? new BatchImportItem(path, BatchImportStatus.Conflict, null, outcome.ConflictDetail)
                    : outcome.WasDuplicate
                        ? new BatchImportItem(path,
                            outcome.DuplicateReason == "signature"
                                ? BatchImportStatus.DuplicateSignature : BatchImportStatus.DuplicateFile,
                            outcome.ReceiptId, outcome.DuplicateReason)
                        : new BatchImportItem(path, BatchImportStatus.Imported, outcome.ReceiptId, null);
                items.Add(committed);
            }
            catch (OperationCanceledException)
            {
                items.Add(new BatchImportItem(path, BatchImportStatus.Cancelled, null, null));
            }
            catch (Exception e)
            {
                items.Add(new BatchImportItem(path, BatchImportStatus.Failed, null, e.Message));
            }
            progress?.Report(i + 1);
        }
        return new BatchImportSummary(items);
    }

    private static ReceiptPrepared Decided(IngestOutcome outcome, string fallbackDate,
        string? operationId = null) =>
        new(null, operationId, null, fallbackDate, "", null, 0, false, outcome);

    // Business half only: the document module owns parsing/caps/derivation; this owns catalog writes,
    // multibuy adjustment, mapping and normalization.
    private ReceiptIngest BuildIngest(ReceiptDocument doc, string rawJsonStr, string filePath,
        string operationId, string fileHash, string? signature, CancellationToken ct)
    {
        var purchaseDate = doc.Header.IsoDate.Length > 0 ? doc.Header.IsoDate : InferDate(filePath);

        // Line materialization (and its P0-3 count guard) runs BEFORE any DB open or catalog write.
        var parsed = doc.ParseLines(MaxReceiptLines, MaxFieldChars, ct);

        using var conn = _factory.Open();
        var storeId = GetOrCreateStoreId(conn, doc.Header.Merchant);

        var lines = new List<ReceiptIngestLine>();
        foreach (var pl in parsed)
        {
            ct.ThrowIfCancellationRequested();

            var adj = _multibuy.Adjust(pl.Description, pl.Quantity, pl.UnitPrice, pl.LineTotal, pl.Discount);
            var dealNote = adj.DealNote;
            if (pl.QuantityReportedButInvalid)
                dealNote = string.IsNullOrEmpty(dealNote) ? "qty_invalid_defaulted" : $"{dealNote};qty_invalid_defaulted";

            var mapping = _mapper.MapToItem(conn, pl.Description);
            var (itemId, mapConf15) = UpsertItemFromMapping(conn, pl.Description, mapping);
            if (mapping.ItemId is null) _mapper.InvalidateChoices(); // a new item exists; later lines can match it

            var observedUnit = _unitNorm.GuessUnitFromText(pl.Description);
            if (observedUnit == "unknown") observedUnit = "each";

            NormalizedPrice? norm = adj.UnitPrice is not null
                ? _unitNorm.Normalize(conn, itemId, adj.UnitPrice.Value, observedUnit, pl.Description)
                : null;

            var combinedNote = norm is not null
                ? (string.IsNullOrEmpty(dealNote) ? norm.Note : $"{norm.Note};{dealNote}")
                : dealNote;

            lines.Add(new ReceiptIngestLine(pl.Index, itemId, pl.Description, adj.Quantity, adj.UnitPrice,
                adj.LineTotal, pl.Discount, ConfidenceTo15(pl.Confidence) ?? mapConf15, observedUnit,
                norm?.NormUnitPrice, norm?.NormUnit, combinedNote));
        }

        return new ReceiptIngest(storeId, purchaseDate, doc.Header.Subtotal, doc.Header.Tax, doc.Header.Total,
            filePath, ConfidenceTo15(doc.Header.OverallConfidence),
            operationId, null, rawJsonStr, fileHash, signature, lines);
    }

    private (int ItemId, int? Confidence15) UpsertItemFromMapping(SqliteConnection conn, string desc, MappingResult mapping)
    {
        if (mapping.ItemId is not null)
            return (mapping.ItemId.Value, ConfidenceTo15(mapping.Confidence));

        var cleaned = desc.Trim();
        if (cleaned.Length == 0) cleaned = "Unknown Item";
        var item = ItemsRepo.CreateItem(conn, cleaned);
        try { _aliases.UpsertAlias(conn, desc, item.Id, 0.60, "receipt_auto"); } catch { /* best-effort */ }
        return (item.Id, 2);
    }

    // Fuzzy-match the merchant to an existing store (token-set >= 85) else create one.
    private static int GetOrCreateStoreId(SqliteConnection conn, string merchant)
    {
        merchant = string.IsNullOrWhiteSpace(merchant) ? "Unknown Store" : merchant.Trim();
        var stores = StoresRepo.ListStores(conn);
        if (stores.Count == 0) return StoresRepo.CreateStore(conn, merchant).Id;

        var best = Process.ExtractOne(merchant, stores.Select(s => s.Name).ToList(), s => s,
            ScorerCache.Get<TokenSetScorer>());
        if (best is not null && best.Score >= StoreMatchThreshold) return stores[best.Index].Id;
        return StoresRepo.CreateStore(conn, merchant).Id;
    }

    // ---------- signature ----------

    private static string? MakeSignature(string merchant, string date, double? total)
    {
        if (string.IsNullOrEmpty(merchant) || string.IsNullOrEmpty(date) || total is null) return null;
        return $"{NormalizeMerchant(merchant)}|{date}|{total.Value.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeMerchant(string s)
    {
        s = (s ?? "").ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return Regex.Replace(s, @"[^a-z0-9 \-]", "");
    }

    private static string InferDate(string filePath)
    {
        try { return File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd"); } // LOCAL: receipt date is a calendar day
        catch { return DateTime.Now.ToString("yyyy-MM-dd"); }
    }

    private static int? ConfidenceTo15(double? conf) => conf switch
    {
        null => null,
        >= 0.90 => 5,
        >= 0.75 => 4,
        >= 0.60 => 3,
        >= 0.40 => 2,
        _ => 1,
    };

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
