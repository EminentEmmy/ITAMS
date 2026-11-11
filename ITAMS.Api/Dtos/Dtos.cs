using System.ComponentModel.DataAnnotations;
using ITAMS.Domain.Enums;

namespace ITAMS.Api.Dtos;

// ---------- Requests ----------

public class LoginRequest
{
    [Required, StringLength(50)] public string TenantId { get; set; } = "";
    [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [Required, StringLength(200)] public string Password { get; set; } = "";
}

public class AssetRequest
{
    [Required, StringLength(100), RegularExpression(@"^[A-Za-z0-9_\-\.]+$", ErrorMessage = "Serial number may only contain letters, digits, '-', '_' and '.'.")]
    public string SerialNumber { get; set; } = "";

    [EnumDataType(typeof(AssetType))] public AssetType Type { get; set; }
    [StringLength(100)] public string? Model { get; set; }
    [StringLength(200)] public string? Location { get; set; }
    [EnumDataType(typeof(AssetStatus))] public AssetStatus Status { get; set; } = AssetStatus.InStock;
    [StringLength(1000)] public string? Notes { get; set; }
}

public class MaintenanceRequest
{
    [Required, StringLength(2000, MinimumLength = 3)] public string Description { get; set; } = "";
    [StringLength(200)] public string? PartReplaced { get; set; }
}

public class ChangePasswordRequest
{
    [Required, StringLength(200)] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(200, MinimumLength = 12, ErrorMessage = "The new password must be at least 12 characters.")]
    public string NewPassword { get; set; } = "";
}

// ---------- Platform owner requests ----------

public class CreateTenantRequest
{
    [Required, RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9\-]{1,48}[A-Za-z0-9]$",
        ErrorMessage = "Company code must be 3 to 50 letters, digits or hyphens, and cannot start or end with a hyphen.")]
    public string Id { get; set; } = "";
    [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; } = "";
    [Required, StringLength(200, MinimumLength = 2)] public string AdminFullName { get; set; } = "";
    [Required, EmailAddress, StringLength(200)] public string AdminEmail { get; set; } = "";
}

public class CreateUserRequest
{
    [Required, StringLength(200, MinimumLength = 2)] public string FullName { get; set; } = "";
    [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [EnumDataType(typeof(UserRole))] public UserRole Role { get; set; }
}

public class SetStatusRequest
{
    public bool IsActive { get; set; }
}

public class SetRoleRequest
{
    [EnumDataType(typeof(UserRole))] public UserRole Role { get; set; }
}

// ---------- Responses ----------

public record UserInfoDto(string Id, string FullName, string Email, string Role, string TenantId, bool MustChangePassword);

public record AssetDto(int Id, string SerialNumber, AssetType Type, string? Model, string? Location,
    AssetStatus Status, string? Notes, DateTime CreatedUtc);

public record MaintenanceDto(int Id, int AssetId, string TechnicianName, string Description,
    string? PartReplaced, DateTime PerformedUtc);

public record AuditDto(long Id, DateTimeOffset TimestampUtc, string UserId, string Action,
    string EntityName, string EntityId, string? OldValues, string? NewValues, string IpAddress,
    string? UserName = null, string? EntityLabel = null);

// ---------- Platform owner responses ----------

public record PlatformOverviewDto(int Tenants, int ActiveTenants, int Users, int Assets,
    int AssetsUnderRepair, int FailedSignIns24h, int LockedAccounts);

public record TenantSummaryDto(string Id, string Name, bool IsActive, DateTime CreatedUtc,
    int Users, int Assets, int UnderRepair, DateTimeOffset? LastSignIn);

public record PlatformUserDto(Guid Id, string FullName, string Email, string Role, bool IsActive,
    bool IsLocked, bool MustChangePassword, DateTimeOffset? LastSignIn);

public record TenantDetailDto(TenantSummaryDto Summary, IReadOnlyList<PlatformUserDto> Users);

/// <summary>Shown exactly once. The server only stores a hash.</summary>
public record CredentialsDto(string Email, string TemporaryPassword);

public record CreateTenantResponse(TenantSummaryDto Tenant, CredentialsDto Admin);

/// <summary>A person in the signed-in organization, as seen by that organization's administrator.</summary>
public record StaffDto(Guid Id, string FullName, string Email, string Role, bool IsActive,
    bool IsLocked, bool MustChangePassword, DateTimeOffset? LastSignIn);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
