using ITAMS.Api.Dtos;
using ITAMS.Api.Services;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>
/// Report a fault with no sign-in, using an organization's private link. This door only lets people SUBMIT:
/// there is no way to list, read or look up any ticket from here.
/// The link's token travels in the X-Report-Token header (not the URL) so it stays out of server logs.
/// An unknown, switched-off or suspended link always answers the same plain 404.
/// </summary>
[ApiController]
[Route("api/public/report")]
[AllowAnonymous]
[EnableRateLimiting("public-report")]
public class PublicReportController(ApplicationDbContext db, ITenantOverride tenantScope) : ControllerBase
{
    private async Task<Tenant?> ResolveAsync(CancellationToken ct)
    {
        var token = Request.Headers["X-Report-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;

        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(
            t => t.PublicReportToken == token && t.PublicReportEnabled && t.IsActive, ct);
    }

    /// <summary>Lets the page show "Report a fault to NAHCO IT support". Reveals nothing but the organization's name.</summary>
    [HttpGet]
    public async Task<IActionResult> Info(CancellationToken ct)
    {
        var tenant = await ResolveAsync(ct);
        return tenant is null ? NotFound() : Ok(new { organization = tenant.Name });
    }

    [HttpPost]
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> Submit([FromBody] PublicTicketRequest req, CancellationToken ct)
    {
        var tenant = await ResolveAsync(ct);
        if (tenant is null) return NotFound();

        // Honeypot: a filled hidden field means a bot. Answer "success" so it learns nothing, and save nothing.
        if (!string.IsNullOrWhiteSpace(req.Website))
            return StatusCode(StatusCodes.Status201Created, new TicketReferenceDto(TicketSaver.Reference(0)));

        tenantScope.UseTenant(tenant.Id);   // from here the DbContext acts inside this one organization

        var ticket = new Ticket
        {
            Title = req.Title.Trim(),
            Description = req.Description.Trim(),
            Category = req.Category,
            Impact = req.Impact,
            Priority = TicketSaver.PriorityFor(req.Impact),
            Status = TicketStatus.New,
            Source = TicketSource.PublicLink,
            ReporterName = req.Name.Trim(),
            ReporterContact = req.Contact?.Trim(),
            Location = req.Location?.Trim(),
            AssetReference = req.AssetReference?.Trim(),
            UpdatedUtc = DateTime.UtcNow
        };

        await TicketSaver.SaveNewAsync(db, ticket, ct);
        return StatusCode(StatusCodes.Status201Created, new TicketReferenceDto(TicketSaver.Reference(ticket.Number)));
    }
}
