using System.ComponentModel.DataAnnotations;
using ITAMS.Domain.Enums;

namespace ITAMS.Api.Dtos;

// ---------- Requests ----------

/// <summary>What anyone with the organization's public link may send. Nothing here is trusted as identity.</summary>
public class PublicTicketRequest
{
    [Required, StringLength(120, MinimumLength = 2)] public string Name { get; set; } = "";
    [StringLength(120)] public string? Contact { get; set; }
    [StringLength(200)] public string? Location { get; set; }
    [StringLength(100)] public string? AssetReference { get; set; }
    [EnumDataType(typeof(TicketCategory))] public TicketCategory Category { get; set; }
    [EnumDataType(typeof(TicketImpact))] public TicketImpact Impact { get; set; }
    [Required, StringLength(150, MinimumLength = 5)] public string Title { get; set; } = "";
    [Required, StringLength(2000, MinimumLength = 10)] public string Description { get; set; } = "";

    /// <summary>Hidden "honeypot" field. People never fill it in, bots usually do.</summary>
    [StringLength(200)] public string? Website { get; set; }
}

/// <summary>A ticket raised from a signed-in account (name and contact come from the account).</summary>
public class StaffTicketRequest
{
    [Required, StringLength(150, MinimumLength = 5)] public string Title { get; set; } = "";
    [Required, StringLength(2000, MinimumLength = 10)] public string Description { get; set; } = "";
    [EnumDataType(typeof(TicketCategory))] public TicketCategory Category { get; set; }
    [EnumDataType(typeof(TicketImpact))] public TicketImpact Impact { get; set; }
    [StringLength(200)] public string? Location { get; set; }
    [StringLength(100)] public string? AssetReference { get; set; }
}

/// <summary>A full update by IT: send every field (null means "none").</summary>
public class TicketUpdateRequest
{
    [EnumDataType(typeof(TicketStatus))] public TicketStatus Status { get; set; }
    [EnumDataType(typeof(TicketPriority))] public TicketPriority Priority { get; set; }
    public Guid? AssignedToUserId { get; set; }
    [StringLength(100)] public string? AssetSerial { get; set; }
    [StringLength(1800)] public string? ResolutionNotes { get; set; }
}

// ---------- Responses ----------

public record TicketDto(
    int Id, int Number, string Reference, string Title, string Description,
    TicketCategory Category, TicketImpact Impact, TicketPriority Priority, TicketStatus Status, TicketSource Source,
    string ReporterName, string? ReporterContact, string? Location, string? AssetReference, string? AssetSerial,
    Guid? AssignedToUserId, string? AssignedToName, string? ResolutionNotes,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? ResolvedUtc);

public record AssigneeDto(Guid Id, string FullName);

public record ReportLinkDto(bool Enabled, string? Token);

public record TicketReferenceDto(string Reference);
