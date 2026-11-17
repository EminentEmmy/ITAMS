using ITAMS.Api.Auth;
using ITAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ITAMS.Api.Services;

public interface ISessionValidator
{
    Task<bool> IsValidAsync(Guid sessionId, string roleInToken, CancellationToken ct);
}

/// <summary>
/// A signed token alone is not enough: on every request the session behind it is checked against the database.
/// Valid means: some refresh token of that session is still active and inside the session's hard ceiling,
/// the user is still enabled, the user's organization is not suspended, and the role has not changed.
/// So signing someone out, disabling them, suspending their organization or changing their role takes effect at once
/// instead of when their 15-minute token happens to expire.
/// </summary>
public sealed class SessionValidator(ApplicationDbContext db, IMemoryCache cache, IOptions<SessionLimits> options) : ISessionValidator
{
    private sealed record Snapshot(bool Valid, string Role);

    public async Task<bool> IsValidAsync(Guid sessionId, string roleInToken, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(options.Value.ValidationCacheSeconds, 0));
        var key = $"session:{sessionId}";

        Snapshot? snapshot = null;
        if (ttl > TimeSpan.Zero) cache.TryGetValue(key, out snapshot);

        if (snapshot is null)
        {
            snapshot = await LoadAsync(sessionId, ct);
            if (ttl > TimeSpan.Zero) cache.Set(key, snapshot, ttl);
        }

        return snapshot.Valid && snapshot.Role == roleInToken;
    }

    private async Task<Snapshot> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // One query. The tenant filter is bypassed on purpose: this runs while the request is still being authenticated.
        var row = await (
            from t in db.RefreshTokens.IgnoreQueryFilters()
            where t.SessionId == sessionId && t.RevokedUtc == null && t.ExpiresUtc > now && t.SessionExpiresUtc > now
            join u in db.Users.IgnoreQueryFilters() on t.UserId equals u.Id
            join tenant in db.Tenants on u.TenantId equals tenant.Id
            where u.IsActive && tenant.IsActive
            select new { u.Role }).FirstOrDefaultAsync(ct);

        return row is null ? new Snapshot(false, "") : new Snapshot(true, row.Role.ToString());
    }
}
