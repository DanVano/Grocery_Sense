using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/family_requests_service.py — secondary members pick meals/items;
// parent reviews. pickable_recipes excludes household allergens.
public sealed class FamilyRequestsService
{
    public MemberRequestRow? PickMeal(int memberId, string recipeName) => throw new NotImplementedException();

    public MemberRequestRow? PickItem(int memberId, string text, double quantity = 1.0, string unit = "each")
        => throw new NotImplementedException();

    public IReadOnlyList<string> PickableRecipes() => throw new NotImplementedException();

    public int UnreviewedCount() => throw new NotImplementedException();

    public IReadOnlyList<MemberRequestRow> ListUnreviewed() => throw new NotImplementedException();

    public void MarkReviewed(int requestId) => throw new NotImplementedException();

    public void RemoveRequest(int requestId) => throw new NotImplementedException();
}
