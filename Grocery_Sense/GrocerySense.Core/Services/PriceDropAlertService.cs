namespace GrocerySense.Core;

// Port of reference-python/.../services/price_drop_alert_service.py — staple/current-quote scan and
// alert persistence (JSON-file backed in Python; consider a SQLite table in C#).
public sealed class PriceDropAlertService
{
    public int RefreshEngineAlerts(bool staplesOnly = true) => throw new NotImplementedException();

    public IReadOnlyList<Dictionary<string, object?>> ComputeEngineAlerts(bool staplesOnly = true) => throw new NotImplementedException();

    public IReadOnlyList<PriceDropAlert> GetAlerts(int limit = 250) => throw new NotImplementedException();

    public IReadOnlyList<Dictionary<string, object?>> GetOpenAlerts() => throw new NotImplementedException();

    public void DismissAlert(int alertId) => throw new NotImplementedException();
}
