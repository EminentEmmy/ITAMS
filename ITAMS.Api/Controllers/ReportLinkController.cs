using System.Buffers.Text;
using System.Security.Cryptography;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>The organization administrator controls the public report link: create, replace or switch off.</summary>
[ApiController]
[Route("api/settings/report-link")]
[Authorize(Roles = "TenantAdmin")]
public class ReportLinkController(ApplicationDbContext db, ICurrentUserService me) : ControllerBase
{
    private Task<Tenant> MyTenant(CancellationToken ct) => db.Tenants.FirstAsync(t => t.Id == me.TenantId, ct);

    [HttpGet]
    public async Task<ReportLinkDto> Get(CancellationToken ct)
    {
        var t = await MyTenant(ct);
        return new ReportLinkDto(t.PublicReportEnabled && t.PublicReportToken is not null, t.PublicReportToken);
    }

    /// <summary>Creates the link, or replaces it: the old link stops working immediately.</summary>
    [HttpPost]
    public async Task<ReportLinkDto> CreateOrReplace(CancellationToken ct)
    {
        var t = await MyTenant(ct);
        var replacing = t.PublicReportToken is not null;

        t.PublicReportToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));   // 192 bits, 32 characters
        t.PublicReportEnabled = true;

        Audit(replacing ? "TENANT_REPORT_LINK_REPLACED" : "TENANT_REPORT_LINK_CREATED");
        await db.SaveChangesAsync(ct);
        return new ReportLinkDto(true, t.PublicReportToken);
    }

    [HttpDelete]
    public async Task<IActionResult> TurnOff(CancellationToken ct)
    {
        var t = await MyTenant(ct);
        t.PublicReportEnabled = false;
        t.PublicReportToken = null;

        Audit("TENANT_REPORT_LINK_TURNED_OFF");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private void Audit(string action) =>
        db.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            TenantId = me.TenantId!,
            UserId = me.UserId!,
            Action = action,
            EntityName = nameof(Tenant),
            EntityId = me.TenantId!,
            IpAddress = (me.IpAddress ?? "unknown")[..Math.Min((me.IpAddress ?? "unknown").Length, 45)],
            UserAgent = me.UserAgent is { Length: > 255 } ua ? ua[..255] : me.UserAgent
        });
}
