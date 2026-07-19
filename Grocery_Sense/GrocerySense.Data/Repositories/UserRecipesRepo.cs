using System.Text.Json;
using System.Text.Json.Serialization;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// User-entered recipes (migration 7). List columns are JSON string arrays via a source-gen context
// (Android AOT rule); junk decodes defensively to [] — a corrupt row must not take the page down.
public static class UserRecipesRepo
{
    private const string Cols = "id, name, servings, ingredients, steps, tags, created_at";

    public static int Add(SqliteConnection conn, string name, int? servings,
        IReadOnlyList<string> ingredients, IReadOnlyList<string> steps, IReadOnlyList<string> tags,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, """
            INSERT INTO user_recipes (name, servings, ingredients, steps, tags)
            VALUES ($name, $servings, $ingredients, $steps, $tags)
            """);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$servings", Db.OrNull(servings));
        cmd.Parameters.AddWithValue("$ingredients", Encode(ingredients));
        cmd.Parameters.AddWithValue("$steps", Encode(steps));
        cmd.Parameters.AddWithValue("$tags", Encode(tags));
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public static void Update(SqliteConnection conn, int id, string name, int? servings,
        IReadOnlyList<string> ingredients, IReadOnlyList<string> steps, IReadOnlyList<string> tags,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, """
            UPDATE user_recipes SET name = $name, servings = $servings,
                ingredients = $ingredients, steps = $steps, tags = $tags
            WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$servings", Db.OrNull(servings));
        cmd.Parameters.AddWithValue("$ingredients", Encode(ingredients));
        cmd.Parameters.AddWithValue("$steps", Encode(steps));
        cmd.Parameters.AddWithValue("$tags", Encode(tags));
        cmd.ExecuteNonQuery();
    }

    public static void Delete(SqliteConnection conn, int id, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "DELETE FROM user_recipes WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static IReadOnlyList<UserRecipeRow> ListAll(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, $"SELECT {Cols} FROM user_recipes ORDER BY name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        var rows = new List<UserRecipeRow>();
        while (r.Read())
            rows.Add(new UserRecipeRow(
                r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                Decode(r.IsDBNull(3) ? null : r.GetString(3)),
                Decode(r.IsDBNull(4) ? null : r.GetString(4)),
                Decode(r.IsDBNull(5) ? null : r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return rows;
    }

    private static string Encode(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values.ToList(), StringListJsonContext.Default.ListString);

    private static IReadOnlyList<string> Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            var list = JsonSerializer.Deserialize(raw, StringListJsonContext.Default.ListString);
            return list?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

[JsonSerializable(typeof(List<string>))]
internal sealed partial class StringListJsonContext : JsonSerializerContext;
