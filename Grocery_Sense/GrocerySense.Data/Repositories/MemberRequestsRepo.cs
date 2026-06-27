using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/member_requests_repo.py
// item_row_ids is a JSON array column -> serialize/deserialize on read/write.
public static class MemberRequestsRepo
{
    public static int AddRequest(SqliteConnection conn, int? memberId, string memberName, string kind, string label,
        IReadOnlyList<int> itemRowIds) => throw new NotImplementedException();

    public static MemberRequestRow? GetRequest(SqliteConnection conn, int requestId) => throw new NotImplementedException();

    public static IReadOnlyList<MemberRequestRow> ListUnreviewed(SqliteConnection conn) => throw new NotImplementedException();

    public static IReadOnlyList<MemberRequestRow> ListAll(SqliteConnection conn, int? limit = null) => throw new NotImplementedException();

    public static int CountUnreviewed(SqliteConnection conn) => throw new NotImplementedException();

    public static void MarkReviewed(SqliteConnection conn, int requestId) => throw new NotImplementedException();

    public static void MarkAllReviewed(SqliteConnection conn) => throw new NotImplementedException();
}
