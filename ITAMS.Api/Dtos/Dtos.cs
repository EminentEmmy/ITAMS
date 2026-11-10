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

// ---------- Responses ----------

public record UserInfoDto(string Id, string FullName, string Email, string Role, string TenantId);

public record AssetDto(int Id, string SerialNumber, AssetType Type, string? Model, string? Location,
    AssetStatus Status, string? Notes, DateTime CreatedUtc);

public record MaintenanceDto(int Id, int AssetId, string TechnicianName, string Description,
    string? PartReplaced, DateTime PerformedUtc);

public record AuditDto(long Id, DateTimeOffset TimestampUtc, string UserId, string Action,
    string EntityName, string EntityId, string? OldValues, string? NewValues, string IpAddress);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
