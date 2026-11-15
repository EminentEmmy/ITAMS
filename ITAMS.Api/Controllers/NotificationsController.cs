using ITAMS.Api.Dtos;
using ITAMS.Api.Services;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>
/// The bell. Notifications are worked out from the signed-in person's own view of the ticket queue (so the tenant
/// filter keeps other organizations out), and "unread" means newer than the time they last pressed "Mark all read".
///  - IT staff (admins, technicians): tickets raised by someone else, and tickets assigned to them
///  - Requesters: movement on their own tickets (in progress, resolved, closed)
///  - Auditors: none
/// </summary>
[ApiController]
[Route("api/notifications")]
public class NotificationsController(ApplicationDbContext db, ICurrentUserService me) : ControllerBase
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);
    private const int MaxItems = 20;

    [HttpGet]
    public async Task<NotificationsDto> Get(CancellationToken ct)
    {
        var myId = Guid.Parse(me.UserId!);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == myId, ct);
        var seen = user.NotificationsSeenUtc ?? user.CreatedUtc;
        var since = DateTime.UtcNow - Window;

        var items = new List<NotificationItemDto>();

        if (user.Role is UserRole.TenantAdmin or UserRole.Technician)
        {
            var fresh = await db.Tickets.AsNoTracking()
                .Where(t => t.CreatedUtc >= since && (t.CreatedByUserId == null || t.CreatedByUserId != myId))
                .ToListAsync(ct);
            items.AddRange(fresh.Select(t => Item(t, "NewTicket", $"New ticket {TicketSaver.Reference(t.Number)}", t.CreatedUtc, seen)));

            var mine = await db.Tickets.AsNoTracking()
                .Where(t => t.AssignedToUserId == myId && t.UpdatedUtc >= since
                         && (t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress))
                .ToListAsync(ct);
            items.AddRange(mine.Select(t => Item(t, "Assigned", $"Assigned to you: {TicketSaver.Reference(t.Number)}", t.UpdatedUtc, seen)));
        }
        else if (user.Role == UserRole.Requester)
        {
            var own = await db.Tickets.AsNoTracking()
                .Where(t => t.CreatedByUserId == myId && t.UpdatedUtc >= since && t.Status != TicketStatus.New)
                .ToListAsync(ct);
            items.AddRange(own.Select(t => Item(t, "Update", $"{TicketSaver.Reference(t.Number)} {StatusText(t.Status)}", t.UpdatedUtc, seen)));
        }

        // the same ticket can appear twice (new AND assigned to me): keep the most recent event per ticket
        var ordered = items.GroupBy(i => i.TicketId).Select(g => g.OrderByDescending(i => i.WhenUtc).First())
            .OrderByDescending(i => i.WhenUtc).ToList();

        return new NotificationsDto(ordered.Count(i => i.Unread), ordered.Take(MaxItems).ToList());
    }

    /// <summary>"Mark all read": everything up to now counts as seen, on every device.</summary>
    [HttpPost("read")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var myId = Guid.Parse(me.UserId!);
        var user = await db.Users.FirstAsync(u => u.Id == myId, ct);
        user.NotificationsSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static NotificationItemDto Item(Ticket t, string kind, string title, DateTime whenUtc, DateTime seen) =>
        new(t.Id, kind, title, t.Location is { Length: > 0 } ? $"{t.Title} ({t.Location})" : t.Title, whenUtc, whenUtc > seen);

    private static string StatusText(TicketStatus s) => s switch
    {
        TicketStatus.InProgress => "is being worked on",
        TicketStatus.Resolved => "was resolved",
        TicketStatus.Closed => "was closed",
        _ => "was updated"
    };
}
