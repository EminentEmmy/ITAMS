namespace ITAMS.Domain.Common;

/// <summary>
/// Base class for every row that belongs to a tenant (airline / ground handler).
/// TenantId is stamped by the DbContext from the signed JWT, never from client input,
/// and every query is automatically filtered by it.
/// </summary>
public abstract class TenantEntity
{
    public int Id { get; set; }
    public string TenantId { get; set; } = default!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
