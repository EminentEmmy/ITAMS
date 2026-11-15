using System.Security.Claims;
using ITAMS.Api.Dtos;
using ITAMS.Api.Services;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>
/// Tickets for signed-in people. Everyone may raise one. Requesters (non-IT staff) only ever see their own;
/// IT staff (admins, technicians) and auditors see the whole organization's queue; only admins and technicians can handle them.
/// Nothing here names a tenant: the DbContext confines every query to the signed-in organization.
/// </summary>
[ApiController]
[Route("api/tickets")]
public class TicketsController(ApplicationDbContext db, ICurrentUserService me) : ControllerBase
{
    private const string Handlers = "TenantAdmin,Technician";
    private bool IsRequester => User.IsInRole("Requester");
    private Guid MyId => Guid.Parse(me.UserId!);

    // ------------------------------------------------------------------ reading
    [HttpGet]
    public async Task<ActionResult<PagedResult<TicketDto>>> List(
        [FromQuery] TicketStatus? status, [FromQuery] bool openOnly = false, [FromQuery] bool assignedToMe = false,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var myId = MyId;

        var query = db.Tickets.AsNoTracking().AsQueryable();
        if (IsRequester) query = query.Where(t => t.CreatedByUserId == myId);   // enforced here, not in the browser
        if (status is not null) query = query.Where(t => t.Status == status);
        if (openOnly) query = query.Where(t => t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress);
        if (assignedToMe) query = query.Where(t => t.AssignedToUserId == myId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            var digits = s.StartsWith("TKT-", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
            var number = int.TryParse(digits, out var n) ? n : -1;

            query = query.Where(t => t.Number == number
                                  || t.Title.Contains(s) || t.ReporterName.Contains(s)
                                  || (t.Location != null && t.Location.Contains(s))
                                  || (t.AssetReference != null && t.AssetReference.Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return new PagedResult<TicketDto>(await ToDtos(rows, ct), total, page, pageSize);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TicketDto>> Get(int id, CancellationToken ct)
    {
        var ticket = await FindVisible(id, tracked: false, ct);
        return ticket is null ? NotFound() : (await ToDtos(new[] { ticket }, ct))[0];
    }

    /// <summary>People a ticket can be assigned to (active admins and technicians of this organization).</summary>
    [HttpGet("assignees")]
    [Authorize(Roles = Handlers)]
    public async Task<ActionResult<IReadOnlyList<AssigneeDto>>> Assignees(CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.Role == UserRole.Technician || u.Role == UserRole.TenantAdmin))
            .OrderBy(u => u.FullName).Select(u => new AssigneeDto(u.Id, u.FullName)).ToListAsync(ct);

    // ------------------------------------------------------------------ raising
    [HttpPost]
    public async Task<ActionResult<TicketDto>> Create([FromBody] StaffTicketRequest req, CancellationToken ct)
    {
        var ticket = new Ticket
        {
            Title = req.Title.Trim(),
            Description = req.Description.Trim(),
            Category = req.Category,
            Impact = req.Impact,
            Priority = TicketSaver.PriorityFor(req.Impact),
            Status = TicketStatus.New,
            Source = TicketSource.StaffAccount,
            ReporterName = me.FullName ?? "Staff member",
            ReporterContact = User.FindFirstValue("email"),
            Location = req.Location?.Trim(),
            AssetReference = req.AssetReference?.Trim(),
            CreatedByUserId = MyId,          // from the signed token, never from the request
            UpdatedUtc = DateTime.UtcNow
        };

        await TicketSaver.SaveNewAsync(db, ticket, ct);
        return CreatedAtAction(nameof(Get), new { id = ticket.Id }, (await ToDtos(new[] { ticket }, ct))[0]);
    }

    // ------------------------------------------------------------------ handling
    /// <summary>A full update by IT. Resolving needs a note, and resolving a ticket that is linked to an asset logs maintenance on that asset.</summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = Handlers)]
    public async Task<ActionResult<TicketDto>> Update(int id, [FromBody] TicketUpdateRequest req, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null) return NotFound();

        // assignee must be an active admin or technician of THIS organization
        if (req.AssignedToUserId is Guid assigneeId)
        {
            var assignee = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == assigneeId && u.IsActive
                && (u.Role == UserRole.Technician || u.Role == UserRole.TenantAdmin), ct);
            if (assignee is null) return BadRequest(new { error = "That person cannot be assigned tickets." });
            ticket.AssignedToUserId = assignee.Id;
            ticket.AssignedToName = assignee.FullName;
        }
        else
        {
            ticket.AssignedToUserId = null;
            ticket.AssignedToName = null;
        }

        // linked asset, by serial number, must exist in THIS organization
        var serial = req.AssetSerial?.Trim();
        if (string.IsNullOrEmpty(serial))
        {
            ticket.AssetId = null;
        }
        else
        {
            var assetId = await db.Assets.AsNoTracking().Where(a => a.SerialNumber == serial).Select(a => (int?)a.Id).FirstOrDefaultAsync(ct);
            if (assetId is null) return BadRequest(new { error = "No asset with that serial number exists in your organization." });
            ticket.AssetId = assetId;
        }

        var notes = req.ResolutionNotes?.Trim();
        var finishing = req.Status is TicketStatus.Resolved or TicketStatus.Closed;
        if (finishing && string.IsNullOrEmpty(notes))
            return BadRequest(new { error = "Add a resolution note before resolving or closing a ticket." });

        var wasOpen = ticket.Status is TicketStatus.New or TicketStatus.InProgress;

        ticket.Status = req.Status;
        ticket.Priority = req.Priority;
        ticket.ResolutionNotes = string.IsNullOrEmpty(notes) ? null : notes;
        ticket.ResolvedUtc = finishing ? (ticket.ResolvedUtc ?? DateTime.UtcNow) : (DateTime?)null;
        ticket.UpdatedUtc = DateTime.UtcNow;

        // First time it leaves the open states with an asset attached: record the work in the asset's history.
        if (wasOpen && finishing && ticket.AssetId is int linkedAsset)
        {
            var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == linkedAsset, ct);
            if (asset is not null && asset.Status != AssetStatus.Decommissioned)
            {
                var text = $"Ticket {TicketSaver.Reference(ticket.Number)}: {ticket.ResolutionNotes}";
                db.MaintenanceLogs.Add(new MaintenanceLog
                {
                    AssetId = linkedAsset,
                    TechnicianId = MyId,
                    TechnicianName = me.FullName ?? "Unknown",
                    Description = text.Length <= 2000 ? text : text[..2000]
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return (await ToDtos(new[] { ticket }, ct))[0];
    }

    // ------------------------------------------------------------------ helpers
    private async Task<Ticket?> FindVisible(int id, bool tracked, CancellationToken ct)
    {
        var myId = MyId;
        var query = tracked ? db.Tickets.AsQueryable() : db.Tickets.AsNoTracking();
        if (IsRequester) query = query.Where(t => t.CreatedByUserId == myId);
        return await query.FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    private async Task<List<TicketDto>> ToDtos(IReadOnlyCollection<Ticket> tickets, CancellationToken ct)
    {
        var assetIds = tickets.Where(t => t.AssetId != null).Select(t => t.AssetId!.Value).Distinct().ToList();
        var serials = assetIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.SerialNumber, ct);

        return tickets.Select(t => new TicketDto(
            t.Id, t.Number, TicketSaver.Reference(t.Number), t.Title, t.Description,
            t.Category, t.Impact, t.Priority, t.Status, t.Source,
            t.ReporterName, t.ReporterContact, t.Location, t.AssetReference,
            t.AssetId is int a && serials.TryGetValue(a, out var serial) ? serial : null,
            t.AssignedToUserId, t.AssignedToName, t.ResolutionNotes,
            t.CreatedUtc, t.UpdatedUtc, t.ResolvedUtc)).ToList();
    }
}
