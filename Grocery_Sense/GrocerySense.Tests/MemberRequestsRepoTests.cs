using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class MemberRequestsRepoTests
{
    [Fact]
    public void Add_and_get_round_trips_including_row_ids()
    {
        using var db = new TempDb();
        var id = MemberRequestsRepo.AddRequest(db.Conn, memberId: 2, memberName: "Kid", kind: "meal",
            label: "Tacos", itemRowIds: new[] { 10, 11, 12 });

        var row = MemberRequestsRepo.GetRequest(db.Conn, id);
        Assert.NotNull(row);
        Assert.Equal(2, row!.MemberId);
        Assert.Equal("Kid", row.MemberName);
        Assert.Equal("meal", row.Kind);
        Assert.Equal("Tacos", row.Label);
        Assert.Equal(new[] { 10, 11, 12 }, row.ItemRowIds);
        Assert.False(row.Reviewed);
    }

    [Fact]
    public void Unreviewed_list_and_count_track_review_state()
    {
        using var db = new TempDb();
        var a = MemberRequestsRepo.AddRequest(db.Conn, 2, "Kid", "item", "milk", Array.Empty<int>());
        MemberRequestsRepo.AddRequest(db.Conn, 3, "Kid2", "item", "eggs", Array.Empty<int>());
        Assert.Equal(2, MemberRequestsRepo.CountUnreviewed(db.Conn));

        MemberRequestsRepo.MarkReviewed(db.Conn, a);
        Assert.Equal(1, MemberRequestsRepo.CountUnreviewed(db.Conn));
        Assert.Single(MemberRequestsRepo.ListUnreviewed(db.Conn));
    }

    [Fact]
    public void Malformed_row_ids_decode_to_empty_not_a_crash()
    {
        using var db = new TempDb();
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO member_requests (member_id, member_name, kind, label, item_row_ids) " +
                "VALUES (2, 'Kid', 'meal', 'X', 'not-json')";
            cmd.ExecuteNonQuery();
        }
        var row = Assert.Single(MemberRequestsRepo.ListUnreviewed(db.Conn));
        Assert.Empty(row.ItemRowIds);
    }
}
