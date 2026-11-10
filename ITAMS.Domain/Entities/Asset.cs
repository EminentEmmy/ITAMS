using ITAMS.Domain.Common;
using ITAMS.Domain.Enums;

namespace ITAMS.Domain.Entities;

public class Asset : TenantEntity
{
    public string SerialNumber { get; set; } = default!;
    public AssetType Type { get; set; }
    public string? Model { get; set; }
    public string? Location { get; set; }   // e.g. "MMIA T2 Check-in Desk 14"
    public AssetStatus Status { get; set; } = AssetStatus.InStock;
    public string? Notes { get; set; }

    public List<MaintenanceLog> MaintenanceLogs { get; set; } = new();
}
