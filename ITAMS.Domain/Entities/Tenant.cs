namespace ITAMS.Domain.Entities;

/// <summary>A customer company. Id is a short slug such as "nahco".</summary>
public class Tenant
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
