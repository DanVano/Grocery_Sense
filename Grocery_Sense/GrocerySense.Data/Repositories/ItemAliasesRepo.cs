using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/item_aliases_repo.py
// Class-based in Python. alias_text stored lowercase + UNIQUE; lookups case-insensitive.
// Methods accept an optional connection so the ingest loop can batch upserts in one transaction.
public sealed class ItemAliasesRepo
{
    public ItemAlias? GetByAlias(string aliasText, SqliteConnection? conn = null) => throw new NotImplementedException();

    public void UpsertAlias(string aliasText, int itemId, double confidence = 1.0, string source = "manual",
        SqliteConnection? conn = null) => throw new NotImplementedException();

    public void MarkSeen(string aliasText, SqliteConnection? conn = null) => throw new NotImplementedException();

    public IReadOnlyList<ItemAlias> ListAll() => throw new NotImplementedException();
}
