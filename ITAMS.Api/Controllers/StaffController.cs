using System.Text.Json;
using ITAMS.Api.Auth;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>
/// A customer's administrator managing their own people. Nothing here names a tenant:
/// the DbContext filter confines every query to the signed-in administrator's organization,
/// so another organization's user simply comes back as "not found".
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Roles = "TenantAdmin")]
public class StaffController(ApplicationDbContext db, IPasswordHasher hasher, ICurrentUserService me) : ControllerBase
{
    private Guid MyId => Guid.Parse(me.UserId!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StaffDto>>> List(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync(ct);

        var lastByUser = await db.SecurityAuditLogs
            .Where(l => l.Action == "LOGIN_SUCCESS")
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(l => l.TimestampUtc) })
            .ToDictionaryAsync(x => x.UserId, x => x.Last, ct);

        var now = DateTime.UtcNow;
        return users.Select(u => new StaffDto(
            u.Id, u.FullName, u.Email, u.Role.ToString(), u.IsActive,
            u.LockoutEndUtc > now, u.MustChangePassword,
            lastByUser.TryGetValue(u.Id.ToString(), out var last) ? (DateTimeOffset?)last : null)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<CredentialsDto>> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        if (req.Role == UserRole.SuperAdmin) return BadRequest(new { error = "That role is not available." });

        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { error = $"{email} already has an account." });

        var password = TempPassword.Generate();
        var user = new AppUser
        {
            TenantId = me.TenantId!,     // from the signed token, never from the request
            FullName = req.FullName.Trim(),
            Email = email,
            Role = req.Role,
            PasswordHash = hasher.Hash(password),
            MustChangePassword = true
        };

        db.Users.Add(user);
        Audit("TENANT_USER_CREATED", user.Id, new { email, role = user.Role.ToString() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = $"{email} already has an account." });
        }

        return new CredentialsDto(email, password);
    }

    /// <summary>Issues a new temporary password, unlocks the account and ends the person's sessions.</summary>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<CredentialsDto>> ResetPassword(Guid id, CancellationToken ct)
    {
        if (id == MyId) return BadRequest(new { error = "Use \"Change password\" for your own account." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var password = TempPassword.Generate();
        user.PasswordHash = hasher.Hash(password);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;

        await RevokeSessions(id, ct);
        Audit("TENANT_USER_PASSWORD_RESET", id);
        await db.SaveChangesAsync(ct);

        return new CredentialsDto(user.Email, password);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetStatusRequest req, CancellationToken ct)
    {
        if (id == MyId) return BadRequest(new { error = "You cannot disable your own account." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        user.IsActive = req.IsActive;
        if (!req.IsActive) await RevokeSessions(id, ct);

        Audit(req.IsActive ? "TENANT_USER_ENABLED" : "TENANT_USER_DISABLED", id);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>The person is signed out so the new role applies at once. Because you cannot change your own role,
    /// at least one active administrator (you) always remains.</summary>
    [HttpPatch("{id:guid}/role")]
    public async Task<IActionResult> SetRole(Guid id, [FromBody] SetRoleRequest req, CancellationToken ct)
    {
        if (id == MyId) return BadRequest(new { error = "You cannot change your own role." });
        if (req.Role == UserRole.SuperAdmin) return BadRequest(new { error = "That role is not available." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        if (user.Role == req.Role) return NoContent();

        var before = user.Role;
        user.Role = req.Role;

        await RevokeSessions(id, ct);
        Audit("TENANT_USER_ROLE_CHANGED", id, new { from = before.ToString(), to = req.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------------ helpers
    private async Task RevokeSessions(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedUtc == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedUtc = now;
    }

    private void Audit(string action, Guid targetUserId, object? details = null) =>
        db.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            TenantId = me.TenantId!,
            UserId = me.UserId!,
            Action = action,
            EntityName = nameof(AppUser),
            EntityId = targetUserId.ToString(),
            NewValues = details is null ? null : JsonSerializer.Serialize(details),   // never contains passwords
            IpAddress = Truncate(me.IpAddress ?? "unknown", 45)!,
            UserAgent = Truncate(me.UserAgent, 255)
        });

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
