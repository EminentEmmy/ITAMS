using System.Linq.Expressions;
using System.Security.Claims;
using ITAMS.Api.Auth;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Entities;
using ITAMS.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Api.Controllers;

/// <summary>
/// "Where am I signed in?" Everyone sees and controls only their own sessions. Signing a session out ends its access
/// token as well (the per-request session check), so it stops working within seconds, not when the token expires.
/// </summary>
[ApiController]
[Route("api/sessions")]
public class SessionsController(ApplicationDbContext db, ICurrentUserService me) : ControllerBase
{
    private Guid MyId => Guid.Parse(me.UserId!);
    private Guid CurrentSession => Guid.TryParse(User.FindFirstValue("sid"), out var id) ? id : Guid.Empty;

    [HttpGet]
    public async Task<IReadOnlyList<SessionDto>> List(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var myId = MyId;
        var current = CurrentSession;

        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == myId && t.RevokedUtc == null && t.ExpiresUtc > now
                     && t.SessionExpiresUtc > now && t.SessionId != Guid.Empty)
            .ToListAsync(ct);

        return tokens.GroupBy(t => t.SessionId).Select(g =>
        {
            var latest = g.OrderByDescending(t => t.CreatedUtc).First();   // the newest token shows when it was last refreshed
            return new SessionDto(g.Key, latest.SessionStartedUtc, latest.CreatedUtc, latest.SessionExpiresUtc,
                latest.CreatedByIp, latest.UserAgent, g.Key == current);
        }).OrderByDescending(s => s.Current).ThenByDescending(s => s.LastActiveUtc).ToList();
    }

    /// <summary>Sign one session out (including, if you choose, the one you are using).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var tokens = await ActiveTokens(t => t.SessionId == id, ct);
        if (tokens.Count == 0) return NotFound();     // not yours, or already ended: indistinguishable on purpose

        End(tokens);
        var isCurrent = id == CurrentSession;
        Audit(isCurrent ? "SESSION_SIGNED_OUT" : "SESSION_REVOKED", id);
        await db.SaveChangesAsync(ct);

        if (isCurrent) AuthCookies.Clear(Response);
        return NoContent();
    }

    /// <summary>Keep this device signed in, sign every other one out.</summary>
    [HttpPost("revoke-others")]
    public async Task<IActionResult> RevokeOthers(CancellationToken ct)
    {
        var current = CurrentSession;
        var tokens = await ActiveTokens(t => t.SessionId != current, ct);

        End(tokens);
        Audit("SESSIONS_REVOKED_OTHERS", current);
        await db.SaveChangesAsync(ct);
        return Ok(new { signedOut = tokens.Select(t => t.SessionId).Distinct().Count() });
    }

    /// <summary>Sign out everywhere, including this device.</summary>
    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAll(CancellationToken ct)
    {
        var tokens = await ActiveTokens(_ => true, ct);

        End(tokens);
        Audit("SESSIONS_REVOKED_ALL", CurrentSession);
        await db.SaveChangesAsync(ct);

        AuthCookies.Clear(Response);
        return NoContent();
    }

    // ------------------------------------------------------------------ helpers
    private async Task<List<RefreshToken>> ActiveTokens(Expression<Func<RefreshToken, bool>> extra, CancellationToken ct)
    {
        var myId = MyId;
        return await db.RefreshTokens
            .Where(t => t.UserId == myId && t.RevokedUtc == null)
            .Where(extra)
            .ToListAsync(ct);
    }

    private static void End(IEnumerable<RefreshToken> tokens)
    {
        var now = DateTime.UtcNow;
        foreach (var t in tokens) t.RevokedUtc = now;
    }

    private void Audit(string action, Guid sessionId) =>
        db.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            TenantId = me.TenantId!,
            UserId = me.UserId!,
            Action = action,
            EntityName = "UserSession",
            EntityId = sessionId.ToString(),
            IpAddress = Truncate(me.IpAddress ?? "unknown", 45)!,
            UserAgent = Truncate(me.UserAgent, 255)
        });

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
