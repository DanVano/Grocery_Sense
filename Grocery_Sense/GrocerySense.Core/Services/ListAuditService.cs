namespace GrocerySense.Core;

// Port of reference-python/.../services/list_audit_service.py — classify each active-list item vs its
// 180-day usual price: new_item | good_deal | usual_price | expensive.
public sealed class ListAuditService
{
    public Dictionary<string, object?> AuditActiveList(int windowDays = 180) => throw new NotImplementedException();
}
