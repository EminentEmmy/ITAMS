using ITAMS.Domain.Common;

namespace ITAMS.Domain.Entities;

public class MaintenanceLog : TenantEntity
{
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = default!;

    public Guid TechnicianId { get; set; }
    /// <summary>Snapshot of the technician's name at the time, so history stays readable.</summary>
    public string TechnicianName { get; set; } = default!;

    public string Description { get; set; } = default!;
    public string? PartReplaced { get; set; }
    public DateTime PerformedUtc { get; set; } = DateTime.UtcNow;
}
