namespace GrocerySense.Core;

// Port of reference-python/.../services/db_maintenance_service.py — backup + CSV/JSON export.
public sealed class DbMaintenanceService
{
    public string BackupDatabase(string? destDir = null) => throw new NotImplementedException();

    public IReadOnlyList<string> ExportToCsv(string destDir) => throw new NotImplementedException();

    public IReadOnlyList<string> ExportToJson(string destDir) => throw new NotImplementedException();
}
