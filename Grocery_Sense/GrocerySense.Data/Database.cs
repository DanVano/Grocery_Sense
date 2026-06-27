using Microsoft.Data.Sqlite;

namespace GrocerySense.Data;

/// <summary>
/// Schema + migrations — port of reference-python/src/Grocery_Sense/data/schema.py
/// (create_tables, _migrate, the __new-table structural rebuilds).
/// ARCHITECTURE.md recommends turning the implicit "_migrate" into a numbered migration
/// ledger here before the app ships — mobile upgrades need deterministic migrations.
/// Feature-local DDL (flyers, unit-norm columns) self-created in Python; fold it into the
/// ledger here so the schema is created in one deterministic place.
/// </summary>
public static class Database
{
    public static void CreateTables(SqliteConnection conn)
        => throw new NotImplementedException("Port DDL from reference-python/.../data/schema.py::create_tables");

    public static void Migrate(SqliteConnection conn)
        => throw new NotImplementedException("Port + replace with a numbered migration ledger.");

    public static void Initialize(SqliteConnectionFactory factory)
        => throw new NotImplementedException("Open a connection, CreateTables, then Migrate.");
}
