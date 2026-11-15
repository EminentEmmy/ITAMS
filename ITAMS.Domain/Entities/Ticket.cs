using ITAMS.Domain.Common;
using ITAMS.Domain.Enums;

namespace ITAMS.Domain.Entities;

/// <summary>A fault report or service request for the organization's IT team.</summary>
public class Ticket : TenantEntity
{
    /// <summary>Human-friendly number, unique within the organization (shown as TKT-000123).</summary>
    public int Number { get; set; }

    public string Title { get; set; } = default!;
    public string Description { get; set; } = default!;
    public TicketCategory Category { get; set; }
    public TicketImpact Impact { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.New;
    public TicketSource Source { get; set; }

    // ---- who reported it (typed by the reporter, never trusted as an identity)
    public string ReporterName { get; set; } = default!;
    public string? ReporterContact { get; set; }
    public string? Location { get; set; }
    /// <summary>What the reporter typed ("printer at desk 14"). Technicians link the real asset below.</summary>
    public string? AssetReference { get; set; }

    /// <summary>Set only for tickets raised from a signed-in account.</summary>
    public Guid? CreatedByUserId { get; set; }

    // ---- handled by IT
    public int? AssetId { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
