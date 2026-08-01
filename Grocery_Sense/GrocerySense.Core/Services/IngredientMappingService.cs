using System.Text.RegularExpressions;
using FuzzySharp;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.StrategySensitive;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/ingredient_mapping_service.py — noisy text -> canonical item_id.
// Strategy: normalize pipeline -> exact alias cache -> fuzzy match (FuzzySharp TokenSortRatio, the C# stand-in
// for rapidfuzz token_sort_ratio) -> optional auto-learn.
//
// Scoring note (per PORTING.md): FuzzySharp returns 0-100; Python thresholds are 0.78/0.90. We divide the
// score by 100 and compare against the fractional thresholds — the conversion is documented at the call site.
//
// Composes ItemAliasesRepo. Standalone callers use the factory overload (opens one connection); bulk callers
// (receipt/flyer/list ingest) pass their existing connection/transaction so a many-line loop no longer opens
// one connection per line. Singleton with a per-run candidate cache + buffered learns; a coarse lock guards
// that mutable state. ponytail: single-user v1 ingest is sequential, so one lock is fine.
public sealed class IngredientMappingService
{
    private const double AcceptThreshold = 0.78;
    private const double LearnThreshold = 0.90;
    private const bool AutoLearn = true;

    private static readonly Dictionary<string, string> DefaultAbbrev = new()
    {
        ["chk"] = "chicken", ["thg"] = "thigh", ["thgh"] = "thigh", ["brst"] = "breast", ["grnd"] = "ground",
        ["bf"] = "beef", ["pork"] = "pork", ["skls"] = "skinless", ["bnls"] = "boneless", ["bp"] = "boneless",
        ["pkg"] = "pack", ["vp"] = "value pack", ["lg"] = "large", ["sm"] = "small", ["org"] = "organic",
    };

    private static readonly HashSet<string> Stopwords = new()
    {
        "fresh", "large", "small", "pack", "value", "bulk", "club", "family", "tray", "super", "store",
    };

    private static readonly Regex NonAlnum = new(@"[^a-z0-9\s]");
    private static readonly Regex Spaces = new(@"\s+");

    private readonly SqliteConnectionFactory _factory;
    
    private readonly object _sync = new();

    // Candidate-name list, loaded once per run (callers map many strings against the same catalog).
    private List<(int Id, string Name)>? _choices;
    private List<string>? _choiceNames;
    // High-confidence learns + alias-cache touches are buffered and written in ONE transaction by
    // FlushLearnedAliases(), so an ingest loop doesn't open a write txn per matched line.
    private List<(string Alias, int ItemId, double Confidence, string Source)> _pendingLearns = new();
    private List<string> _pendingTouches = new();

    public IngredientMappingService(SqliteConnectionFactory factory) => _factory = factory;

    // Standalone overload: opens one connection through the factory and delegates. Connection-open touches no
    // shared state, so it stays outside the lock; the mutable candidate cache/learns are guarded inside.
    public MappingResult MapToItem(string rawText)
    {
        using var conn = _factory.Open();
        return MapToItem(conn, rawText);
    }

    // Bulk overload: reuse the caller's connection (and transaction, when the caller is mid-write) so an ingest
    // loop maps every line on one connection instead of opening one per line.
    public MappingResult MapToItem(SqliteConnection conn, string rawText, SqliteTransaction? tx = null)
    {
        lock (_sync)
        {
            var normalized = NormalizePipeline(rawText);
            var rawKey = (rawText ?? "").Trim().ToLowerInvariant();
            if (normalized.Length == 0 && rawKey.Length == 0)
                return new MappingResult(null, null, 0.0, "none", normalized);

            // 1) Exact alias cache hit. Try the normalized key first, then the raw lowercased text: manual
            // corrections (CorrectLineMapping) and receipt auto-learns store the alias as raw punctuated text,
            // which the normalize pipeline strips (% / stopwords / abbrevs) — so a normalized-only lookup would
            // never find them. Whichever key hit is the one we mark as seen.
            var matchedKey = normalized;
            var alias = normalized.Length > 0 ? ItemAliasesRepo.GetByAlias(conn, normalized, tx) : null;
            if (alias is null && rawKey.Length > 0 && rawKey != normalized)
            {
                alias = ItemAliasesRepo.GetByAlias(conn, rawKey, tx);
                matchedKey = rawKey;
            }
            if (alias is not null)
            {
                _pendingTouches.Add(matchedKey);
                return new MappingResult(alias.ItemId, null, alias.Confidence, "alias", normalized);
            }

            if (normalized.Length == 0)
                return new MappingResult(null, null, 0.0, "none", normalized);

            // 2) Fuzzy match against canonical item names.
            var (choices, names) = GetChoices(conn, tx);
            if (choices.Count == 0)
                return new MappingResult(null, null, 0.0, "none", normalized);

            var best = Process.ExtractOne(normalized, names, s => s, ScorerCache.Get<TokenSortScorer>());
            if (best is null)
                return new MappingResult(null, null, 0.0, "none", normalized);

            var confidence = best.Score / 100.0; // FuzzySharp 0-100 -> 0..1 to compare against 0.78/0.90.
            var bestItemId = choices[best.Index].Id;

            if (confidence < AcceptThreshold)
                return new MappingResult(null, null, confidence, "none", normalized);

            if (AutoLearn && confidence >= LearnThreshold)
                _pendingLearns.Add((normalized, bestItemId, confidence, "auto_fuzzy"));

            return new MappingResult(bestItemId, choices[best.Index].Name, confidence, "fuzzy", normalized);
        }
    }

    // Persist buffered auto-learned aliases + cache touches in one transaction. Callers MUST flush after
    // their mapping loop (for receipt ingest, flush BEFORE opening the receipt transaction).
    public void FlushLearnedAliases()
    {
        lock (_sync)
        {
            if (_pendingLearns.Count == 0 && _pendingTouches.Count == 0) return;
            var learns = _pendingLearns;
            var touches = _pendingTouches;
            _pendingLearns = new();
            _pendingTouches = new();

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            foreach (var (aliasText, itemId, confidence, source) in learns)
                ItemAliasesRepo.UpsertAlias(conn, aliasText, itemId, confidence, source, tx);
            foreach (var aliasText in touches)
                ItemAliasesRepo.MarkSeen(conn, aliasText, tx);
            tx.Commit();
        }
    }

    // Drop the cached candidate list — call when a NEW item is created mid-run so later matches see it.
    public void InvalidateChoices()
    {
        lock (_sync) { _choices = null; _choiceNames = null; }
    }

    // ---------------- normalization ----------------

    public string NormalizePipeline(string raw)
    {
        var t = Normalize(raw);
        t = ExpandAbbrev(t);
        t = Normalize(t);
        t = RemoveStopwords(t);
        t = Normalize(t);
        return t;
    }

    private static string Normalize(string text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        t = NonAlnum.Replace(t, " ");
        return Spaces.Replace(t, " ").Trim();
    }

    private static string ExpandAbbrev(string text) =>
        string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(tok => DefaultAbbrev.GetValueOrDefault(tok, tok)));

    private static string RemoveStopwords(string text) =>
        string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Stopwords.Contains(t)));

    private (List<(int Id, string Name)> Choices, List<string> Names) GetChoices(SqliteConnection conn,
        SqliteTransaction? tx = null)
    {
        if (_choices is null)
        {
            _choices = ItemsRepo.ListAllItemNames(conn, tx).Select(x => (x.Id, x.CanonicalName)).ToList();
            // Score against lowercased names: the input side is already lowercased by NormalizePipeline, and
            // FuzzySharp's TokenSortScorer is case-sensitive — without this "milk" vs "Milk" scores 0.75 and
            // misses the 0.78 accept threshold. Python's rapidfuzz default_process lowercases both sides.
            _choiceNames = _choices.Select(c => c.Name.ToLowerInvariant()).ToList();
        }
        return (_choices, _choiceNames!);
    }
}
