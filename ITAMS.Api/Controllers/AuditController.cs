using ITAMS.Api.Dtos;
using ITAMS.Domain.Entities;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize(Roles = "TenantAdmin,Auditor")]
public class AuditController(ApplicationDbContext db) : ControllerBase
{
    /// <param name="category">security | assets | staff | platform (omit for everything)</param>
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditDto>>> List(
        [FromQuery] string? category, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.SecurityAuditLogs.AsNoTracking().AsQueryable();   // tenant filter applies
        query = category switch
        {
            "security" => query.Where(l => l.Action.StartsWith("LOGIN") || l.Action.StartsWith("LOGOUT")
                                        || l.Action.StartsWith("ACCOUNT") || l.Action.StartsWith("PASSWORD")
                                        || l.Action.StartsWith("REFRESH")),
            "assets" => query.Where(l => l.Action.StartsWith("ASSET") || l.Action.StartsWith("MAINTENANCE")),
            "staff" => query.Where(l => l.Action.StartsWith("TENANT_USER") || l.Action.StartsWith("PLATFORM_USER")
                                      || l.Action.StartsWith("PLATFORM_PASSWORD")),
            "platform" => query.Where(l => l.Action.StartsWith("PLATFORM")),
            _ => query
        };

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(l => l.TimestampUtc).ThenByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new AuditDto(l.Id, l.TimestampUtc, l.UserId, l.Action, l.EntityName, l.EntityId, l.OldValues, l.NewValues, l.IpAddress, null, null))
            .ToListAsync(ct);

        // Turn ids into names so the trail is readable. Both lookups are tenant-filtered too.
        var userIds = rows.Select(r => Guid.TryParse(r.UserId, out var g) ? g : (Guid?)null)
            .Concat(rows.Where(r => r.EntityName == nameof(AppUser)).Select(r => Guid.TryParse(r.EntityId, out var g) ? g : (Guid?)null))
            .Where(g => g != null).Select(g => g!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var assetIds = rows.Where(r => r.EntityName == nameof(Asset) && int.TryParse(r.EntityId, out _))
            .Select(r => int.Parse(r.EntityId)).Distinct().ToList();
        var serials = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.SerialNumber, ct);

        var items = rows.Select(r => r with
        {
            // Platform actions are shown as the platform team, not as an individual.
            UserName = r.Action.StartsWith("PLATFORM")
                ? "ITAMS platform team"
                : Guid.TryParse(r.UserId, out var uid) && names.TryGetValue(uid, out var who) ? who : null,
            EntityLabel = r.EntityName switch
            {
                nameof(Asset) when int.TryParse(r.EntityId, out var aid) && serials.TryGetValue(aid, out var serial) => serial,
                nameof(AppUser) when Guid.TryParse(r.EntityId, out var tid) && names.TryGetValue(tid, out var target) => target,
                _ => null
            }
        }).ToList();

        return new PagedResult<AuditDto>(items, total, page, pageSize);
    }
}
