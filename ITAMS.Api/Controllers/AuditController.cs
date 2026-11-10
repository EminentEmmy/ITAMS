using ITAMS.Api.Dtos;
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
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditDto>>> List(
        [FromQuery] string? action, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.SecurityAuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(l => l.Action == action.Trim());

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.TimestampUtc).ThenByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new AuditDto(l.Id, l.TimestampUtc, l.UserId, l.Action, l.EntityName, l.EntityId, l.OldValues, l.NewValues, l.IpAddress))
            .ToListAsync(ct);

        return new PagedResult<AuditDto>(items, total, page, pageSize);
    }
}
