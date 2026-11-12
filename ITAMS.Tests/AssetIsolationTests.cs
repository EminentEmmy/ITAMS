using System.Net;
using System.Net.Http.Json;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Tests;

/// <summary>Through the real HTTP API: one customer attacking another customer's assets.</summary>
[Collection("Isolation")]
public class AssetIsolationTests(TestEnvironment env)
{
    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task Listing_returns_exactly_the_callers_tenants_assets(string tenant)
    {
        var client = await env.SignInAsync(tenant, $"admin@{tenant}.test");

        var page = await client.GetJsonAsync<PagedResult<AssetDto>>("/api/assets?pageSize=100");

        var expected = await env.AssetIdsAsync(tenant);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Order(), page.Items.Select(a => a.Id).Order());
    }

    [Fact]
    public async Task Search_cannot_reach_another_tenants_data()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var beta = await alpha.GetJsonAsync<PagedResult<AssetDto>>("/api/assets?search=BETA");
        Assert.Empty(beta.Items);

        // SHARED-001 exists in both tenants: alpha must see exactly its own one.
        var shared = await alpha.GetJsonAsync<PagedResult<AssetDto>>("/api/assets?search=SHARED-001");
        var single = Assert.Single(shared.Items);
        Assert.Equal("alpha", (await env.AssetRowAsync(single.Id)).TenantId);
    }

    [Fact]
    public async Task Another_tenants_asset_is_reported_as_not_found()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaId = await env.AssetIdAsync("beta", "BETA-1");

        var res = await alpha.GetAsync($"/api/assets/{betaId}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);   // not 403: its existence is not even confirmed
    }

    [Fact]
    public async Task Updating_another_tenants_asset_fails_and_changes_nothing()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaId = await env.AssetIdAsync("beta", "BETA-1");
        var before = await env.AssetRowAsync(betaId);

        var res = await alpha.PutJsonAsync($"/api/assets/{betaId}", new
        {
            serialNumber = "HIJACKED", type = "Monitor", status = "Deployed", location = "attacker desk", notes = "owned"
        });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var after = await env.AssetRowAsync(betaId);
        Assert.Equal(before.SerialNumber, after.SerialNumber);
        Assert.Equal(before.Location, after.Location);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Notes, after.Notes);
    }

    [Fact]
    public async Task Decommissioning_another_tenants_asset_fails_and_changes_nothing()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaId = await env.AssetIdAsync("beta", "BETA-2");
        var before = (await env.AssetRowAsync(betaId)).Status;

        var res = await alpha.DeleteAsync($"/api/assets/{betaId}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(before, (await env.AssetRowAsync(betaId)).Status);
        Assert.NotEqual(AssetStatus.Decommissioned, (await env.AssetRowAsync(betaId)).Status);
    }

    [Fact]
    public async Task Maintenance_history_of_another_tenants_asset_is_not_readable_or_writable()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaId = await env.AssetIdAsync("beta", "BETA-1");

        await using var raw = env.Raw();
        var before = await raw.MaintenanceLogs.IgnoreQueryFilters().CountAsync(m => m.AssetId == betaId);

        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/api/assets/{betaId}/maintenance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await alpha.PostJsonAsync($"/api/assets/{betaId}/maintenance", new { description = "injected entry" })).StatusCode);

        var after = await raw.MaintenanceLogs.IgnoreQueryFilters().CountAsync(m => m.AssetId == betaId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task A_tenant_id_sent_in_the_request_body_is_ignored()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var res = await alpha.PostRawJsonAsync("/api/assets",
            """{"serialNumber":"BODY-TENANT-1","type":"Monitor","status":"InStock","tenantId":"beta"}""");

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<AssetDto>(TestHttp.Json);
        Assert.Equal("alpha", (await env.AssetRowAsync(created!.Id)).TenantId);
    }

    [Fact]
    public async Task A_duplicate_serial_check_does_not_reveal_another_tenants_serials()
    {
        var beta = await env.SignInAsync("beta", "admin@beta.test");

        // ALPHA-ONLY-1 exists only in alpha. Beta must be allowed to use it: a 409 would leak that alpha owns it.
        var first = await beta.PostJsonAsync("/api/assets", new { serialNumber = "ALPHA-ONLY-1", type = "Monitor", status = "InStock" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // ...but duplicates inside beta itself are still caught.
        var second = await beta.PostJsonAsync("/api/assets", new { serialNumber = "ALPHA-ONLY-1", type = "Monitor", status = "InStock" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Without_signing_in_the_asset_api_refuses_everything()
    {
        using var anonymous = env.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task An_auditor_can_read_but_not_write_and_a_technician_cannot_decommission()
    {
        var auditor = await env.SignInAsync("alpha", "audit@alpha.test");
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        var alphaId = await env.AssetIdAsync("alpha", "ALPHA-1");

        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await auditor.PostJsonAsync("/api/assets", new { serialNumber = "AUDITOR-WRITE", type = "Monitor", status = "InStock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.DeleteAsync($"/api/assets/{alphaId}")).StatusCode);
    }
}
