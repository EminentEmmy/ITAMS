using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Services;

public static class TicketSaver
{
    public static string Reference(int number) => $"TKT-{number:D6}";

    /// <summary>The reporter's impact sets the first priority; IT can change it.</summary>
    public static TicketPriority PriorityFor(TicketImpact impact) => impact switch
    {
        TicketImpact.Blocking => TicketPriority.High,
        TicketImpact.Degraded => TicketPriority.Normal,
        _ => TicketPriority.Low
    };

    /// <summary>
    /// Saves a new ticket with the next number for its organization. Two people reporting at the same instant can pick
    /// the same number; the unique index rejects the second, which then simply takes the following number.
    /// </summary>
    public static async Task SaveNewAsync(ApplicationDbContext db, Ticket ticket, CancellationToken ct)
    {
        db.Tickets.Add(ticket);

        for (var attempt = 1; ; attempt++)
        {
            ticket.Number = ((await db.Tickets.AsNoTracking().MaxAsync(t => (int?)t.Number, ct)) ?? 0) + 1;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // number already taken: loop and try the next one
            }
        }
    }
}
