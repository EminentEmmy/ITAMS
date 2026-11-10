using ITAMS.Api.Dtos;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

// No TenantId appears anywhere in this file: the DbContext filters reads and stamps writes.
[ApiController]
[Route("api/assets")]
public class AssetsController(ApplicationDbContext db) : ControllerBase
{
    private const string Writers = "TenantAdmin,Technician";

    [HttpGet]
    public async Task<ActionResult<PagedResult<AssetDto>>> List(
        [FromQuery] string? search,
        [FromQuery] AssetStatus? status,
        [FromQuery] AssetType? type,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Assets.AsNoTracking().AsQueryable();

        if (status is not null) query = query.Where(a => a.Status == status);
        if (type is not null) query = query.Where(a => a.Type == type);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(a => a.SerialNumber.Contains(s)
                                  || (a.Model != null && a.Model.Contains(s))
                                  || (a.Location != null && a.Location.Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.CreatedUtc).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AssetDto(a.Id, a.SerialNumber, a.Type, a.Model, a.Location, a.Status, a.Notes, a.CreatedUtc))
            .ToListAsync(ct);

        return new PagedResult<AssetDto>(items, total, page, pageSize);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AssetDto>> Get(int id, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return asset is null ? NotFound() : ToDto(asset);   // another tenant's asset is simply "not found"
    }

    [HttpPost]
    [Authorize(Roles = Writers)]
    public async Task<ActionResult<AssetDto>> Create([FromBody] AssetRequest req, CancellationToken ct)
    {
        if (req.Status == AssetStatus.Decommissioned && !User.IsInRole("TenantAdmin"))
            return Forbid();

        var serial = req.SerialNumber.Trim();
        if (await db.Assets.AnyAsync(a => a.SerialNumber == serial, ct))
            return Conflict(new { error = $"An asset with serial number {serial} already exists." });

        var asset = new Asset
        {
            SerialNumber = serial,
            Type = req.Type,
            Model = req.Model?.Trim(),
            Location = req.Location?.Trim(),
            Status = req.Status,
            Notes = req.Notes?.Trim()
        };

        db.Assets.Add(asset);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two people adding the same serial at the same instant: the unique index wins.
            return Conflict(new { error = $"An asset with serial number {serial} already exists." });
        }

        return CreatedAtAction(nameof(Get), new { id = asset.Id }, ToDto(asset));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Writers)]
    public async Task<ActionResult<AssetDto>> Update(int id, [FromBody] AssetRequest req, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();

        if (req.Status == AssetStatus.Decommissioned && asset.Status != AssetStatus.Decommissioned && !User.IsInRole("TenantAdmin"))
            return Forbid();

        var serial = req.SerialNumber.Trim();
        if (serial != asset.SerialNumber && await db.Assets.AnyAsync(a => a.SerialNumber == serial, ct))
            return Conflict(new { error = $"An asset with serial number {serial} already exists." });

        asset.SerialNumber = serial;
        asset.Type = req.Type;
        asset.Model = req.Model?.Trim();
        asset.Location = req.Location?.Trim();
        asset.Status = req.Status;
        asset.Notes = req.Notes?.Trim();

        await db.SaveChangesAsync(ct);
        return ToDto(asset);
    }

    /// <summary>Assets are never physically deleted. DELETE decommissions, and the audit trail records who did it.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<IActionResult> Decommission(int id, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();

        asset.Status = AssetStatus.Decommissioned;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static AssetDto ToDto(Asset a) =>
        new(a.Id, a.SerialNumber, a.Type, a.Model, a.Location, a.Status, a.Notes, a.CreatedUtc);
}
