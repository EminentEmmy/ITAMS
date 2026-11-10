namespace ITAMS.Domain.Entities;

public class RefreshToken
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string TenantId { get; set; } = default!;

    /// <summary>SHA-256 of the token. The raw token only ever lives in the user's cookie.</summary>
    public string TokenHash { get; set; } = default!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }

    public DateTime? RevokedUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
}
