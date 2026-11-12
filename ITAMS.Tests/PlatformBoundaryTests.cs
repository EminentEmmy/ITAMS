using System.Net;
using ITAMS.Api.Dtos;

namespace ITAMS.Tests;

/// <summary>
/// The platform console is the one place allowed to look across tenants, so its door must be tight:
/// only the real owner gets in, and even the owner can look but not change customer data.
/// </summary>
[Collection("Isolation")]
public class PlatformBoundaryTests(TestEnvironment env)
{
    [Theory]
    [InlineData("/api/platform/overview")]
    [InlineData("/api/platform/tenants")]
    [InlineData("/api/platform/tenants/beta")]
    [InlineData("/api/platform/tenants/beta/assets")]
    [InlineData("/api/platform/tenants/beta/audit")]
    public async Task A_customer_administrator_cannot_use_platform_endpoints(string url)
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        Assert.Equal(HttpStatusCode.Forbidden, (await alpha.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_customer_administrator_cannot_create_or_suspend_organizations()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var create = await alpha.PostJsonAsync("/api/platform/tenants",
            new { id = "evilcorp", name = "Evil", adminFullName = "Eve", adminEmail = "eve@evil.test" });
        var suspend = await alpha.PatchJsonAsync("/api/platform/tenants/beta/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, suspend.StatusCode);
    }

    [Fact]
    public async Task A_SuperAdmin_role_inside_a_customer_tenant_is_not_a_platform_owner()
    {
        // super@alpha.test has role SuperAdmin but belongs to "alpha". The policy also requires the platform tenant.
        var fake = await env.SignInAsync("alpha", "super@alpha.test");

        Assert.Equal(HttpStatusCode.Forbidden, (await fake.GetAsync("/api/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fake.GetAsync("/api/platform/tenants/beta/assets")).StatusCode);
    }

    [Fact]
    public async Task The_owner_sees_every_customer_but_not_the_platform_tenant_itself()
    {
        var owner = await env.SignInAsync("platform", "owner@platform.test");

        var tenants = await owner.GetJsonAsync<List<TenantSummaryDto>>("/api/platform/tenants");

        var ids = tenants.Select(t => t.Id).ToList();
        Assert.Contains("alpha", ids);
        Assert.Contains("beta", ids);
        Assert.DoesNotContain("platform", ids);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task The_owners_view_of_a_tenants_assets_is_exactly_that_tenants_assets(string tenant)
    {
        var owner = await env.SignInAsync("platform", "owner@platform.test");

        var page = await owner.GetJsonAsync<PagedResult<AssetDto>>($"/api/platform/tenants/{tenant}/assets?pageSize=100");

        Assert.Equal((await env.AssetIdsAsync(tenant)).Order(), page.Items.Select(a => a.Id).Order());
    }

    [Fact]
    public async Task The_owner_can_look_but_not_change_customer_data()
    {
        var owner = await env.SignInAsync("platform", "owner@platform.test");
        var alphaId = await env.AssetIdAsync("alpha", "ALPHA-1");
        var before = await env.AssetRowAsync(alphaId);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await owner.PostJsonAsync("/api/assets", new { serialNumber = "OWNER-WRITE", type = "Monitor", status = "InStock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await owner.PutJsonAsync($"/api/assets/{alphaId}", new { serialNumber = "CHANGED", type = "Monitor", status = "Deployed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/assets/{alphaId}")).StatusCode);

        Assert.Equal(before.SerialNumber, (await env.AssetRowAsync(alphaId)).SerialNumber);
    }

    [Fact]
    public async Task Through_the_customer_api_the_owner_sees_no_customer_data()
    {
        var owner = await env.SignInAsync("platform", "owner@platform.test");

        var page = await owner.GetJsonAsync<PagedResult<AssetDto>>("/api/assets");

        Assert.Empty(page.Items);   // the owner's own tenant is empty; the platform API is the only window into customers
    }

    [Fact]
    public async Task The_reserved_platform_company_code_cannot_be_registered()
    {
        var owner = await env.SignInAsync("platform", "owner@platform.test");

        var res = await owner.PostJsonAsync("/api/platform/tenants",
            new { id = "platform", name = "Imposter", adminFullName = "Mallory", adminEmail = "mallory@x.test" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_suspended_organization_cannot_sign_in()
    {
        await env.CreateTenantWithAdminAsync("gamma", "admin@gamma.test");   // its own tenant: other tests are unaffected
        var owner = await env.SignInAsync("platform", "owner@platform.test");

        using (var before = env.NewClient())
            Assert.Equal(HttpStatusCode.OK, (await TestEnvironment.TrySignInAsync(before, "gamma", "admin@gamma.test", TestEnvironment.Password)).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await owner.PatchJsonAsync("/api/platform/tenants/gamma/status", new { isActive = false })).StatusCode);

        using var after = env.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TestEnvironment.TrySignInAsync(after, "gamma", "admin@gamma.test", TestEnvironment.Password)).StatusCode);
    }
}
