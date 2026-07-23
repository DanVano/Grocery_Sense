using System.Globalization;
using System.Text.Json;
using GrocerySense.Core.Abstractions;

namespace GrocerySense.Integrations;

// Flipp provider via the UNOFFICIAL backflipp endpoints (the same ones flipp.com's web app calls).
// No API key and no contract: Flipp can change or block this without notice. Every failure is thrown
// loud — FlyerSyncService catches per store and discloses it in FlyerSyncResult.Errors — and the manual
// flyer-photo path (FlyerIngestService) remains the fallback when this breaks. Never fabricates deals:
// an HTTP error or unexpected response shape throws; a genuinely empty flyer returns an empty list.
//
// Flow: GET /flipp/flyers?postal_code={pc}&locale=en-ca  -> current flyers near the postal code;
// filter to merchant ≈ storeName; GET /flipp/flyers/{id}/flyer_items per matching flyer (max 2).
public sealed class FlippClient : IFlyerProvider
{
    private const string BaseUrl = "https://backflipp.wishabi.com/flipp";
    private const int MaxFlyersPerStore = 2;

    // Resource bounds on the unofficial (untrusted, no-contract) endpoint: cap body size, JSON depth,
    // items per flyer, and per-field text so a hostile/broken response can't exhaust memory or CPU.
    private const long MaxResponseBytes = 4L * 1024 * 1024;
    private const int MaxItemsPerFlyer = 2000;
    private const int MaxTextChars = 1000;
    private static readonly TimeSpan MaxResponseReadTime = TimeSpan.FromSeconds(20);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http;

    public FlippClient() : this(SharedHttp) { }
    public FlippClient(HttpClient http) => _http = http; // test seam: fake handler with canned JSON

    public async Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
        string storeName, string postalCode, CancellationToken ct = default)
    {
        var store = (storeName ?? "").Trim();
        var postal = (postalCode ?? "").Replace(" ", "").ToUpperInvariant();
        if (store.Length == 0) throw new ArgumentException("storeName is required.", nameof(storeName));
        if (postal.Length == 0)
            throw new InvalidOperationException("Postal code is not set — add it in Preferences before syncing flyers.");

        var flyersUrl = $"{BaseUrl}/flyers?postal_code={Uri.EscapeDataString(postal)}&locale=en-ca";
        using var flyersDoc = await GetJsonAsync(flyersUrl, ct);
        var flyers = RootArray(flyersDoc.RootElement, "flyers")
            ?? throw new InvalidOperationException(
                "Flipp flyers response had an unexpected shape — the unofficial API may have changed.");

        var matching = new List<(long Id, string? ValidFrom, string? ValidTo)>();
        foreach (var f in flyers)
        {
            var merchant = Str(f, "merchant") ?? Str(f, "merchant_name") ?? "";
            if (!MerchantMatches(merchant, store)) continue;
            if (Num(f, "id") is not { } id) continue;
            matching.Add(((long)id, IsoDateOnly(Str(f, "valid_from")), IsoDateOnly(Str(f, "valid_to"))));
            if (matching.Count >= MaxFlyersPerStore) break;
        }

        var deals = new List<Dictionary<string, object?>>();
        foreach (var (flyerId, validFrom, validTo) in matching)
        {
            ct.ThrowIfCancellationRequested();
            var itemsUrl = $"{BaseUrl}/flyers/{flyerId}/flyer_items";
            using var itemsDoc = await GetJsonAsync(itemsUrl, ct);
            var items = RootArray(itemsDoc.RootElement, "items")
                ?? throw new InvalidOperationException(
                    $"Flipp flyer_items response for flyer {flyerId} had an unexpected shape.");
            if (items.Count > MaxItemsPerFlyer)
                throw new InvalidOperationException($"Flipp flyer {flyerId} returned too many items.");

            foreach (var it in items)
            {
                var name = Str(it, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var price = Num(it, "current_price");
                // The promo phrase ("2/$5", "Buy 1 Get 1") rides in price_text so multi-buy parsing sees it.
                var promo = FirstNonEmpty(Str(it, "sale_story"), Str(it, "pre_price_text"), Str(it, "post_price_text"));
                var priceText = promo ?? (price is { } p ? "$" + p.ToString("0.00", CultureInfo.InvariantCulture) : null);

                deals.Add(new Dictionary<string, object?>
                {
                    ["title"] = name,
                    ["description"] = FirstNonEmpty(Str(it, "description"), promo, name),
                    ["price_text"] = priceText,
                    ["price"] = price,
                    ["unit_price"] = price,
                    ["unit"] = null,
                    ["valid_from"] = IsoDateOnly(Str(it, "valid_from")) ?? validFrom,
                    ["valid_to"] = IsoDateOnly(Str(it, "valid_to")) ?? validTo,
                    ["page_index"] = Num(it, "page"),
                });
            }
        }
        return deals;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

        // 429/403 = the unofficial endpoint is telling us to back off (or has blocked us). Typed throw so
        // FlyerSyncService aborts the remaining stores and persists retry_not_before (P1-4) instead of
        // hammering per-store.
        if (resp.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
        {
            TimeSpan? retryAfter = resp.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - DateTimeOffset.UtcNow,
                _ => null,
            };
            throw new FlyerProviderThrottledException(
                $"Flipp returned {(int)resp.StatusCode} ({resp.StatusCode}) — backing off.", retryAfter);
        }

        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("Flipp response exceeded 4 MiB.");

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(MaxResponseReadTime);
        // Enforce the cap even when the server omits Content-Length (chunked): LoadIntoBufferAsync throws if exceeded.
        await resp.Content.LoadIntoBufferAsync(MaxResponseBytes, readCts.Token);
        var stream = await resp.Content.ReadAsStreamAsync(readCts.Token);
        await using (stream.ConfigureAwait(false))
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, readCts.Token);
    }

    // Accepts either a bare JSON array or an object wrapping the array under `key`.
    private static List<JsonElement>? RootArray(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
            return arr.EnumerateArray().ToList();
        return null;
    }

    // "No Frills" matches "No Frills Ontario" / "NoFrills"; comparison is letters+digits, case-insensitive.
    internal static bool MerchantMatches(string merchant, string storeName)
    {
        var m = Canon(merchant);
        var s = Canon(storeName);
        return m.Length > 0 && s.Length > 0 && (m.Contains(s) || s.Contains(m));
    }

    private static string Canon(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // First 10 chars of an ISO-ish timestamp ("2026-07-10T04:00:00-04:00" -> "2026-07-10"); null if not a date.
    private static string? IsoDateOnly(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim();
        return t.Length >= 10 && DateOnly.TryParseExact(t[..10], "yyyy-MM-dd", out _) ? t[..10] : null;
    }

    private static string? Str(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)
            || v.ValueKind != JsonValueKind.String)
            return null;

        var text = v.GetString();
        return text is { Length: > MaxTextChars }
            ? throw new InvalidOperationException($"Flipp field '{key}' exceeded {MaxTextChars} characters.")
            : text;
    }

    private static double? Num(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            // Flipp sometimes serializes prices as strings ("1.99").
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }
}
