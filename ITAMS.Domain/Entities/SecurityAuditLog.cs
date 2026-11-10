namespace ITAMS.Domain.Entities;

/// <summary>Append-only. Nothing in the application ever updates or deletes these rows.</summary>
public class SecurityAuditLog
{
    public long Id { get; set; }
    public string TenantId { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public string Action { get; set; } = default!;      // e.g. ASSET_DECOMMISSIONED
    public string EntityName { get; set; } = default!;
    public string EntityId { get; set; } = default!;    // string so Guid and int keys both fit
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string IpAddress { get; set; } = default!;
    public string? UserAgent { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}
