using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class UserRecipesRepoTests
{
    [Fact]
    public void Add_then_list_round_trips_all_fields()
    {
        using var db = new TempDb();
        var id = UserRecipesRepo.Add(db.Conn, "Dad's Chili", 6,
            new[] { "ground beef", "kidney beans", "tomatoes" }, new[] { "brown beef", "simmer 1h" },
            new[] { "beef", "comfort" });

        var rows = UserRecipesRepo.ListAll(db.Conn);
        var row = Assert.Single(rows);
        Assert.Equal(id, row.Id);
        Assert.Equal("Dad's Chili", row.Name);
        Assert.Equal(6, row.Servings);
        Assert.Equal(new[] { "ground beef", "kidney beans", "tomatoes" }, row.Ingredients);
        Assert.Equal(new[] { "brown beef", "simmer 1h" }, row.Steps);
        Assert.Equal(new[] { "beef", "comfort" }, row.Tags);
    }

    [Fact]
    public void Duplicate_name_case_insensitive_throws_sqlite_constraint()
    {
        using var db = new TempDb();
        UserRecipesRepo.Add(db.Conn, "Dad's Chili", null, new[] { "beef" }, Array.Empty<string>(), Array.Empty<string>());
        var ex = Assert.Throws<SqliteException>(() =>
            UserRecipesRepo.Add(db.Conn, "DAD'S CHILI", null, new[] { "beef" }, Array.Empty<string>(), Array.Empty<string>()));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void Update_replaces_fields_and_delete_removes_the_row()
    {
        using var db = new TempDb();
        var id = UserRecipesRepo.Add(db.Conn, "Chili", 4, new[] { "beef" }, Array.Empty<string>(), Array.Empty<string>());

        UserRecipesRepo.Update(db.Conn, id, "Chili v2", 8, new[] { "beef", "beans" },
            new[] { "simmer" }, new[] { "comfort" });
        var row = Assert.Single(UserRecipesRepo.ListAll(db.Conn));
        Assert.Equal("Chili v2", row.Name);
        Assert.Equal(8, row.Servings);
        Assert.Equal(2, row.Ingredients.Count);

        UserRecipesRepo.Delete(db.Conn, id);
        Assert.Empty(UserRecipesRepo.ListAll(db.Conn));
    }

    [Fact]
    public void Junk_json_in_a_list_column_decodes_to_empty()
    {
        using var db = new TempDb();
        var id = UserRecipesRepo.Add(db.Conn, "Chili", null, new[] { "beef" }, Array.Empty<string>(), Array.Empty<string>());
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE user_recipes SET ingredients = 'not json' WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(UserRecipesRepo.ListAll(db.Conn)[0].Ingredients);
    }
}
