using ITAMS.Api.Dtos;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

[ApiController]
[Route("api/assets/{assetId:int}/maintenance")]
public class MaintenanceController(ApplicationDbContext db, ICurrentUserService me) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MaintenanceDto>>> List(int assetId, CancellationToken ct)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) return NotFound();

        return await db.MaintenanceLogs.AsNoTracking()
            .Where(m => m.AssetId == assetId)
            .OrderByDescending(m => m.PerformedUtc)
            .Select(m => new MaintenanceDto(m.Id, m.AssetId, m.TechnicianName, m.Description, m.PartReplaced, m.PerformedUtc))
            .ToListAsync(ct);
    }

    [HttpPost]
    [Authorize(Roles = "TenantAdmin,Technician")]
    public async Task<ActionResult<MaintenanceDto>> Create(int assetId, [FromBody] MaintenanceRequest req, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return NotFound();
        if (asset.Status == AssetStatus.Decommissioned)
            return Conflict(new { error = "This asset is decommissioned. Maintenance cannot be logged." });

        var log = new MaintenanceLog
        {
            AssetId = assetId,
            TechnicianId = Guid.Parse(me.UserId!),
            TechnicianName = me.FullName ?? "Unknown",
            Description = req.Description.Trim(),
            PartReplaced = req.PartReplaced?.Trim()
        };

        db.MaintenanceLogs.Add(log);
        await db.SaveChangesAsync(ct);

        return Ok(new MaintenanceDto(log.Id, log.AssetId, log.TechnicianName, log.Description, log.PartReplaced, log.PerformedUtc));
    }
}
