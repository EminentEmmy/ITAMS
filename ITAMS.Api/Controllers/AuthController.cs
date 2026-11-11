using System.Security.Claims;
using ITAMS.Api.Auth;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Infrastructure.Data;
using ITAMS.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ITAMS.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    ApplicationDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IOptions<JwtOptions> jwtOptions,
    ICurrentUserService me) : ControllerBase
{
    private const string GenericLoginError = "Invalid credentials, or the account is locked.";
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Verified when the user does not exist, so "unknown user" and "wrong password" take the same time.
    private static readonly string DummyHash = new Pbkdf2PasswordHasher().Hash("timing-equaliser");

    // ------------------------------------------------------------------ login
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var tenantId = req.TenantId.Trim().ToLowerInvariant();
        var email = req.Email.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        var tenantActive = await db.Tenants.AnyAsync(t => t.Id == tenantId && t.IsActive, ct);

        // Login happens before we know the tenant from a token, so the tenant filter is bypassed here
        // and the tenant is matched explicitly instead.
        var user = tenantActive
            ? await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Email == email, ct)
            : null;

        if (user is null || !user.IsActive)
        {
            hasher.Verify(req.Password, DummyHash);
            Audit(tenantId, Trunc(email, 100)!, "LOGIN_FAILED", "unknown");
            await db.SaveChangesAsync(ct);
            return Unauthorized(new { error = GenericLoginError });
        }

        if (user.LockoutEndUtc is { } lockoutEnd && lockoutEnd > now)
        {
            Audit(tenantId, user.Id.ToString(), "LOGIN_BLOCKED_LOCKED", user.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Unauthorized(new { error = GenericLoginError });
        }

        if (!hasher.Verify(req.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            var action = "LOGIN_FAILED";
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockoutEndUtc = now.Add(LockoutDuration);
                user.FailedLoginCount = 0;
                action = "ACCOUNT_LOCKED";
            }
            Audit(tenantId, user.Id.ToString(), action, user.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Unauthorized(new { error = GenericLoginError });
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;

        var session = CreateSession(user);
        Audit(tenantId, user.Id.ToString(), "LOGIN_SUCCESS", user.Id.ToString());
        await db.SaveChangesAsync(ct);

        AuthCookies.Set(Response, session.Access, session.AccessExpires, session.RefreshRaw, session.RefreshExpires);
        return Ok(ToDto(user));
    }

    // ------------------------------------------------------------------ refresh (rotating)
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (!Request.Cookies.TryGetValue(AuthCookies.Refresh, out var raw) || string.IsNullOrWhiteSpace(raw))
            return Unauthorized();

        var hash = tokens.Hash(raw);
        var stored = await db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || stored.ExpiresUtc <= now)
        {
            AuthCookies.Clear(Response);
            return Unauthorized();
        }

        if (stored.RevokedUtc is { } revokedAt)
        {
            // A rotated-out token was presented again. Within 10 s that is just two tabs racing;
            // later than that it looks like a stolen token, so every session for the user is killed.
            if (now - revokedAt > TimeSpan.FromSeconds(10))
            {
                var active = await db.RefreshTokens.IgnoreQueryFilters()
                    .Where(t => t.UserId == stored.UserId && t.RevokedUtc == null).ToListAsync(ct);
                foreach (var t in active) t.RevokedUtc = now;

                Audit(stored.TenantId, stored.UserId.ToString(), "REFRESH_TOKEN_REUSE_DETECTED", stored.UserId.ToString());
                await db.SaveChangesAsync(ct);
                AuthCookies.Clear(Response);
            }
            return Unauthorized();
        }

        var tenantActive = await db.Tenants.AnyAsync(t => t.Id == stored.TenantId && t.IsActive, ct);
        var user = tenantActive
            ? await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == stored.UserId && u.IsActive, ct)
            : null;

        if (user is null)
        {
            stored.RevokedUtc = now;
            await db.SaveChangesAsync(ct);
            AuthCookies.Clear(Response);
            return Unauthorized();
        }

        var session = CreateSession(user);
        stored.RevokedUtc = now;
        stored.ReplacedByTokenHash = session.RefreshHash;
        await db.SaveChangesAsync(ct);

        AuthCookies.Set(Response, session.Access, session.AccessExpires, session.RefreshRaw, session.RefreshExpires);
        return Ok(ToDto(user));
    }

    // ------------------------------------------------------------------ logout
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(AuthCookies.Refresh, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var hash = tokens.Hash(raw);
            var stored = await db.RefreshTokens.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedUtc == null, ct);

            if (stored is not null)
            {
                stored.RevokedUtc = DateTime.UtcNow;
                Audit(stored.TenantId, stored.UserId.ToString(), "LOGOUT", stored.UserId.ToString());
                await db.SaveChangesAsync(ct);
            }
        }

        AuthCookies.Clear(Response);
        return NoContent();
    }

    // ------------------------------------------------------------------ change password
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        var userId = Guid.Parse(me.UserId!);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        if (!hasher.Verify(req.CurrentPassword, user.PasswordHash))
        {
            Audit(user.TenantId, user.Id.ToString(), "PASSWORD_CHANGE_FAILED", user.Id.ToString());
            await db.SaveChangesAsync(ct);
            return BadRequest(new { error = "The current password is incorrect." });   // 400, not 401: the session itself is fine
        }

        if (req.NewPassword == req.CurrentPassword)
            return BadRequest(new { error = "Choose a password that is different from the current one." });

        var now = DateTime.UtcNow;
        user.PasswordHash = hasher.Hash(req.NewPassword);
        user.MustChangePassword = false;

        // Every session ends, including this one: the user must sign in again with the new password.
        var active = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedUtc == null).ToListAsync(ct);
        foreach (var t in active) t.RevokedUtc = now;

        Audit(user.TenantId, user.Id.ToString(), "PASSWORD_CHANGED", user.Id.ToString());
        await db.SaveChangesAsync(ct);

        AuthCookies.Clear(Response);
        return NoContent();
    }

    // ------------------------------------------------------------------ me
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new UserInfoDto(
        User.FindFirstValue("sub")!,
        User.FindFirstValue("name")!,
        User.FindFirstValue("email")!,
        User.FindFirstValue("role")!,
        User.FindFirstValue("tid")!,
        User.FindFirstValue("mcp") == "1"));

    // ------------------------------------------------------------------ helpers
    private sealed record Session(string Access, DateTime AccessExpires, string RefreshRaw, string RefreshHash, DateTime RefreshExpires);

    private Session CreateSession(AppUser user)
    {
        var jwt = jwtOptions.Value;
        var now = DateTime.UtcNow;
        var (rawRefresh, refreshHash) = tokens.CreateRefreshToken();
        var refreshExpires = now.AddDays(jwt.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TenantId = user.TenantId,
            TokenHash = refreshHash,
            ExpiresUtc = refreshExpires,
            CreatedByIp = Trunc(me.IpAddress, 45),
            UserAgent = Trunc(me.UserAgent, 255)
        });

        return new Session(tokens.CreateAccessToken(user), now.AddMinutes(jwt.AccessTokenMinutes), rawRefresh, refreshHash, refreshExpires);
    }

    private void Audit(string tenantId, string userId, string action, string entityId) =>
        db.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            EntityName = nameof(AppUser),
            EntityId = entityId,
            IpAddress = Trunc(me.IpAddress ?? "unknown", 45)!,
            UserAgent = Trunc(me.UserAgent, 255)
        });

    private static UserInfoDto ToDto(AppUser u) => new(u.Id.ToString(), u.FullName, u.Email, u.Role.ToString(), u.TenantId, u.MustChangePassword);

    private static string? Trunc(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
