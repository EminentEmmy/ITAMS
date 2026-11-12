using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Tests;

/// <summary>
/// The data-layer guarantees: whatever code runs on top, the DbContext itself keeps tenants apart.
/// </summary>
[Collection("Isolation")]
public class TenantFilterTests(TestEnvironment env)
{
    [Fact]
    public async Task Every_query_returns_only_rows_of_the_current_tenant()
    {
        await using var db = env.Db("alpha");

        var assets = await db.Assets.ToListAsync();
        var users = await db.Users.ToListAsync();
        var logs = await db.MaintenanceLogs.ToListAsync();
        var audit = await db.SecurityAuditLogs.ToListAsync();

        Assert.NotEmpty(assets);   // not vacuous: there really is alpha data to see
        Assert.NotEmpty(users);
        Assert.NotEmpty(logs);
        Assert.NotEmpty(audit);
        Assert.All(assets, a => Assert.Equal("alpha", a.TenantId));
        Assert.All(users, u => Assert.Equal("alpha", u.TenantId));
        Assert.All(logs, l => Assert.Equal("alpha", l.TenantId));
        Assert.All(audit, l => Assert.Equal("alpha", l.TenantId));
    }

    [Fact]
    public async Task Control_check_the_database_really_holds_several_tenants()
    {
        // If this failed, the test above would pass for the wrong reason.
        await using var db = env.Raw();
        var tenants = await db.Assets.IgnoreQueryFilters().Select(a => a.TenantId).Distinct().ToListAsync();
        Assert.Contains("alpha", tenants);
        Assert.Contains("beta", tenants);
    }

    [Fact]
    public async Task With_no_signed_in_tenant_nothing_is_visible()
    {
        await using var db = env.Db(null);

        Assert.Empty(await db.Assets.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.MaintenanceLogs.ToListAsync());
        Assert.Empty(await db.RefreshTokens.ToListAsync());
        Assert.Empty(await db.SecurityAuditLogs.ToListAsync());

        Assert.NotEmpty(await db.Assets.IgnoreQueryFilters().ToListAsync());   // control: the rows exist
    }

    [Fact]
    public async Task A_row_of_another_tenant_cannot_be_loaded_by_its_id()
    {
        var betaAssetId = await env.AssetIdAsync("beta", "BETA-1");

        await using var alpha = env.Db("alpha");
        Assert.Null(await alpha.Assets.FirstOrDefaultAsync(a => a.Id == betaAssetId));
        Assert.Empty(await alpha.MaintenanceLogs.Where(m => m.AssetId == betaAssetId).ToListAsync());
    }

    [Fact]
    public async Task New_rows_are_stamped_with_the_current_tenant_whatever_the_caller_says()
    {
        int id;
        await using (var db = env.Db("alpha", Guid.NewGuid().ToString()))
        {
            var asset = new Asset { SerialNumber = "STAMP-PROBE-1", Type = AssetType.Monitor, TenantId = "beta" };   // the lie
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            id = asset.Id;
        }

        Assert.Equal("alpha", (await env.AssetRowAsync(id)).TenantId);
    }

    [Fact]
    public async Task Creating_tenant_data_without_a_tenant_is_refused()
    {
        await using var db = env.Db(null);
        db.Assets.Add(new Asset { SerialNumber = "NO-TENANT-1", Type = AssetType.Monitor });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_rows_tenant_cannot_be_changed_by_an_update_and_its_audit_entry_stays_with_the_real_tenant()
    {
        var assetId = await env.AssetIdAsync("alpha", "ALPHA-2");

        await using (var db = env.Db("alpha", Guid.NewGuid().ToString()))
        {
            var asset = await db.Assets.FirstAsync(a => a.Id == assetId);
            asset.TenantId = "beta";        // attempt to hand the row to another customer
            asset.Notes = "moved?";
            await db.SaveChangesAsync();
        }

        Assert.Equal("alpha", (await env.AssetRowAsync(assetId)).TenantId);

        await using var alpha = env.Db("alpha");
        await using var beta = env.Db("beta");
        var key = assetId.ToString();
        Assert.Contains(await alpha.SecurityAuditLogs.ToListAsync(),
            l => l.EntityName == nameof(Asset) && l.EntityId == key && l.Action == "ASSET_UPDATED");
        Assert.DoesNotContain(await beta.SecurityAuditLogs.ToListAsync(),
            l => l.EntityName == nameof(Asset) && l.EntityId == key);
    }

    [Fact]
    public async Task Changes_are_audited_under_the_tenant_that_made_them()
    {
        int id;
        await using (var db = env.Db("alpha", Guid.NewGuid().ToString()))
        {
            var asset = new Asset { SerialNumber = "AUDIT-PROBE-1", Type = AssetType.Monitor };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            id = asset.Id;
        }

        await using var alpha = env.Db("alpha");
        await using var beta = env.Db("beta");
        var key = id.ToString();

        Assert.Contains(await alpha.SecurityAuditLogs.ToListAsync(),
            l => l.EntityName == nameof(Asset) && l.EntityId == key && l.Action == "ASSET_CREATED");
        Assert.DoesNotContain(await beta.SecurityAuditLogs.ToListAsync(),
            l => l.EntityName == nameof(Asset) && l.EntityId == key);
    }

    [Fact]
    public async Task The_audit_log_cannot_be_edited()
    {
        await using var db = env.Db("alpha");
        var row = await db.SecurityAuditLogs.FirstAsync();
        row.Action = "TAMPERED";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_audit_log_cannot_be_deleted()
    {
        await using var db = env.Db("alpha");
        db.SecurityAuditLogs.Remove(await db.SecurityAuditLogs.FirstAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Synchronous_saving_is_blocked_so_stamping_and_auditing_cannot_be_skipped()
    {
        await using var db = env.Db("alpha");
        Assert.Throws<NotSupportedException>(() => db.SaveChanges());
    }
}
