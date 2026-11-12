using System.Text.Json;
using ITAMS.Api.Auth;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Common;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>
/// The platform owner's console API. This is the ONLY place that bypasses the tenant filter
/// (IgnoreQueryFilters), so it is locked to SuperAdmins who belong to the reserved "platform" tenant.
/// Tenant data is exposed read-only: the owner can see everything but cannot edit a customer's assets.
/// Every action here is written to the audit log under the affected organization, so customers can see it too.
/// </summary>
[ApiController]
[Route("api/platform")]
[Authorize(Policy = "PlatformOwner")]
public class PlatformController(ApplicationDbContext db, IPasswordHasher hasher, ICurrentUserService me) : ControllerBase
{
    private const string Platform = PlatformConstants.TenantId;

    // ------------------------------------------------------------------ overview
    [HttpGet("overview")]
    public async Task<ActionResult<PlatformOverviewDto>> Overview(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        return new PlatformOverviewDto(
            Tenants: await db.Tenants.CountAsync(t => t.Id != Platform, ct),
            ActiveTenants: await db.Tenants.CountAsync(t => t.Id != Platform && t.IsActive, ct),
            Users: await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId != Platform && u.IsActive, ct),
            Assets: await db.Assets.IgnoreQueryFilters().CountAsync(ct),
            AssetsUnderRepair: await db.Assets.IgnoreQueryFilters().CountAsync(a => a.Status == AssetStatus.UnderRepair, ct),
            FailedSignIns24h: await db.SecurityAuditLogs.IgnoreQueryFilters()
                .CountAsync(l => l.Action == "LOGIN_FAILED" && l.TimestampUtc > since, ct),
            LockedAccounts: await db.Users.IgnoreQueryFilters().CountAsync(u => u.LockoutEndUtc > now, ct));
    }

    // ------------------------------------------------------------------ organizations
    [HttpGet("tenants")]
    public async Task<ActionResult<IReadOnlyList<TenantSummaryDto>>> Tenants(CancellationToken ct) =>
        await Summaries(null, ct);

    [HttpGet("tenants/{id}")]
    public async Task<ActionResult<TenantDetailDto>> Detail(string id, CancellationToken ct)
    {
        id = id.Trim().ToLowerInvariant();
        var summary = (await Summaries(id, ct)).FirstOrDefault();
        if (summary is null) return NotFound();

        var users = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == id).OrderBy(u => u.FullName).ToListAsync(ct);

        var lastByUser = await db.SecurityAuditLogs.IgnoreQueryFilters()
            .Where(l => l.TenantId == id && l.Action == "LOGIN_SUCCESS")
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(l => l.TimestampUtc) })
            .ToDictionaryAsync(x => x.UserId, x => x.Last, ct);

        var now = DateTime.UtcNow;
        var dtos = users.Select(u => new PlatformUserDto(
            u.Id, u.FullName, u.Email, u.Role.ToString(), u.IsActive,
            u.LockoutEndUtc > now, u.MustChangePassword,
            lastByUser.TryGetValue(u.Id.ToString(), out var last) ? (DateTimeOffset?)last : null)).ToList();

        // Opening an organization is always recorded. The customer sees the first view in each
        // 30-minute window; every view stays visible to the platform owner in this console.
        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-30);
        var shownRecently = await db.SecurityAuditLogs.IgnoreQueryFilters().AnyAsync(l =>
            l.TenantId == id &&
            l.Action == "PLATFORM_TENANT_OPENED" &&
            l.TenantVisible &&
            l.TimestampUtc > windowStart, ct);

        Audit(id, "PLATFORM_TENANT_OPENED", nameof(Tenant), id, tenantVisible: !shownRecently);
        await db.SaveChangesAsync(ct);

        return new TenantDetailDto(summary, dtos);
    }

    [HttpPost("tenants")]
    public async Task<ActionResult<CreateTenantResponse>> CreateTenant([FromBody] CreateTenantRequest req, CancellationToken ct)
    {
        var id = req.Id.Trim().ToLowerInvariant();
        if (id == Platform || await db.Tenants.AnyAsync(t => t.Id == id, ct))
            return Conflict(new { error = $"The company code '{id}' is already taken." });

        var password = TempPassword.Generate();
        var admin = NewUser(id, req.AdminFullName, req.AdminEmail, UserRole.TenantAdmin, password);

        db.Tenants.Add(new Tenant { Id = id, Name = req.Name.Trim() });
        db.Users.Add(admin);
        Audit(id, "PLATFORM_TENANT_CREATED", nameof(Tenant), id, new { name = req.Name.Trim() });
        Audit(id, "PLATFORM_USER_CREATED", nameof(AppUser), admin.Id.ToString(), new { email = admin.Email, role = admin.Role.ToString() });
        await db.SaveChangesAsync(ct);

        var summary = (await Summaries(id, ct)).First();
        return new CreateTenantResponse(summary, new CredentialsDto(admin.Email, password));
    }

    /// <summary>Suspending blocks new sign-ins and token refreshes at once; already-issued access tokens expire within 15 minutes.</summary>
    [HttpPatch("tenants/{id}/status")]
    public async Task<ActionResult<TenantSummaryDto>> SetTenantStatus(string id, [FromBody] SetStatusRequest req, CancellationToken ct)
    {
        id = id.Trim().ToLowerInvariant();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id && t.Id != Platform, ct);
        if (tenant is null) return NotFound();

        tenant.IsActive = req.IsActive;

        if (!req.IsActive)
        {
            var now = DateTime.UtcNow;
            var tokens = await db.RefreshTokens.IgnoreQueryFilters()
                .Where(t => t.TenantId == id && t.RevokedUtc == null).ToListAsync(ct);
            foreach (var t in tokens) t.RevokedUtc = now;
        }

        Audit(id, req.IsActive ? "PLATFORM_TENANT_REACTIVATED" : "PLATFORM_TENANT_SUSPENDED", nameof(Tenant), id);
        await db.SaveChangesAsync(ct);

        return (await Summaries(id, ct)).First();
    }

    // ------------------------------------------------------------------ read-only organization data
    [HttpGet("tenants/{id}/assets")]
    public async Task<ActionResult<PagedResult<AssetDto>>> TenantAssets(
        string id,
        [FromQuery] string? search,
        [FromQuery] AssetStatus? status,
        [FromQuery] AssetType? type,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        id = id.Trim().ToLowerInvariant();
        if (!await db.Tenants.AnyAsync(t => t.Id == id && t.Id != Platform, ct)) return NotFound();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Assets.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == id);
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

    [HttpGet("tenants/{id}/audit")]
    public async Task<ActionResult<PagedResult<AuditDto>>> TenantAudit(
        string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        id = id.Trim().ToLowerInvariant();
        if (!await db.Tenants.AnyAsync(t => t.Id == id && t.Id != Platform, ct)) return NotFound();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.SecurityAuditLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == id);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.TimestampUtc).ThenByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new AuditDto(l.Id, l.TimestampUtc, l.UserId, l.Action, l.EntityName, l.EntityId, l.OldValues, l.NewValues, l.IpAddress))
            .ToListAsync(ct);

        return new PagedResult<AuditDto>(items, total, page, pageSize);
    }

    // ------------------------------------------------------------------ giving and managing access
    [HttpPost("tenants/{id}/users")]
    public async Task<ActionResult<CredentialsDto>> CreateUser(string id, [FromBody] CreateUserRequest req, CancellationToken ct)
    {
        id = id.Trim().ToLowerInvariant();
        if (req.Role == UserRole.SuperAdmin)
            return BadRequest(new { error = "Platform owner accounts cannot be created inside an organization." });
        if (!await db.Tenants.AnyAsync(t => t.Id == id && t.Id != Platform, ct)) return NotFound();

        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == id && u.Email == email, ct))
            return Conflict(new { error = $"{email} already has an account in this organization." });

        var password = TempPassword.Generate();
        var user = NewUser(id, req.FullName, email, req.Role, password);

        db.Users.Add(user);
        Audit(id, "PLATFORM_USER_CREATED", nameof(AppUser), user.Id.ToString(), new { email, role = user.Role.ToString() });
        await db.SaveChangesAsync(ct);

        return new CredentialsDto(user.Email, password);
    }

    /// <summary>Issues a new temporary password, unlocks the account and ends all of the user's sessions.</summary>
    [HttpPost("users/{userId:guid}/reset-password")]
    public async Task<ActionResult<CredentialsDto>> ResetPassword(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId && u.TenantId != Platform, ct);
        if (user is null) return NotFound();

        var password = TempPassword.Generate();
        user.PasswordHash = hasher.Hash(password);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;

        await RevokeSessions(userId, ct);
        Audit(user.TenantId, "PLATFORM_PASSWORD_RESET", nameof(AppUser), userId.ToString());
        await db.SaveChangesAsync(ct);

        return new CredentialsDto(user.Email, password);
    }

    [HttpPatch("users/{userId:guid}/status")]
    public async Task<IActionResult> SetUserStatus(Guid userId, [FromBody] SetStatusRequest req, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId && u.TenantId != Platform, ct);
        if (user is null) return NotFound();

        user.IsActive = req.IsActive;
        if (!req.IsActive) await RevokeSessions(userId, ct);

        Audit(user.TenantId, req.IsActive ? "PLATFORM_USER_ENABLED" : "PLATFORM_USER_DISABLED", nameof(AppUser), userId.ToString());
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------------ helpers
    private AppUser NewUser(string tenantId, string fullName, string email, UserRole role, string temporaryPassword) => new()
    {
        TenantId = tenantId,
        FullName = fullName.Trim(),
        Email = email.Trim().ToLowerInvariant(),
        Role = role,
        PasswordHash = hasher.Hash(temporaryPassword),
        MustChangePassword = true
    };

    private async Task RevokeSessions(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tokens = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.RevokedUtc == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedUtc = now;
    }

    private async Task<List<TenantSummaryDto>> Summaries(string? onlyId, CancellationToken ct)
    {
        var tenantQuery = db.Tenants.AsNoTracking().Where(t => t.Id != Platform);
        if (onlyId is not null) tenantQuery = tenantQuery.Where(t => t.Id == onlyId);
        var tenants = await tenantQuery.OrderBy(t => t.Name).ToListAsync(ct);

        var userCounts = await db.Users.IgnoreQueryFilters()
            .GroupBy(u => u.TenantId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var assetCounts = await db.Assets.IgnoreQueryFilters()
            .GroupBy(a => a.TenantId)
            .Select(g => new { Id = g.Key, Total = g.Count(), Repair = g.Count(a => a.Status == AssetStatus.UnderRepair) })
            .ToDictionaryAsync(x => x.Id, ct);

        var lastSignIn = await db.SecurityAuditLogs.IgnoreQueryFilters()
            .Where(l => l.Action == "LOGIN_SUCCESS")
            .GroupBy(l => l.TenantId)
            .Select(g => new { Id = g.Key, Last = g.Max(l => l.TimestampUtc) })
            .ToDictionaryAsync(x => x.Id, x => x.Last, ct);

        return tenants.Select(t =>
        {
            assetCounts.TryGetValue(t.Id, out var assetInfo);
            return new TenantSummaryDto(
                t.Id, t.Name, t.IsActive, t.CreatedUtc,
                userCounts.GetValueOrDefault(t.Id),
                assetInfo?.Total ?? 0,
                assetInfo?.Repair ?? 0,
                lastSignIn.TryGetValue(t.Id, out var last) ? (DateTimeOffset?)last : null);
        }).ToList();
    }

    private void Audit(string tenantId, string action, string entityName, string entityId,
                   object? details = null, bool tenantVisible = true) =>
        db.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            TenantId = tenantId,
            UserId = me.UserId ?? "unknown",           // the platform owner's id: always attributable
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            NewValues = details is null ? null : JsonSerializer.Serialize(details),   // never contains passwords
            IpAddress = Truncate(me.IpAddress ?? "unknown", 45)!,
            UserAgent = Truncate(me.UserAgent, 255),
            TenantVisible = tenantVisible
        });

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
