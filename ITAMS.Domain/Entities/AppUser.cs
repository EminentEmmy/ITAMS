using ITAMS.Domain.Enums;

namespace ITAMS.Domain.Entities;

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = default!;
    public string Email { get; set; } = default!;       // stored lower-case
    public string FullName { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;

    // Brute-force protection
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
