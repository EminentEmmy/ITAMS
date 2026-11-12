using System.Net;
using System.Net.Http.Json;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Tests;

/// <summary>One customer's administrator trying to read or manage another customer's people and history.</summary>
[Collection("Isolation")]
public class StaffAndAuditIsolationTests(TestEnvironment env)
{
    // ---------------------------------------------------------------- staff
    [Fact]
    public async Task The_staff_list_contains_only_the_administrators_own_people()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var staff = await alpha.GetJsonAsync<List<StaffDto>>("/api/users");

        await using var raw = env.Raw();
        var expected = await raw.Users.IgnoreQueryFilters().Where(u => u.TenantId == "alpha").Select(u => u.Id).ToListAsync();
        Assert.Equal(expected.Order(), staff.Select(s => s.Id).Order());
        var betaIds = await raw.Users.IgnoreQueryFilters().Where(u => u.TenantId == "beta").Select(u => u.Id).ToListAsync();
        Assert.NotEmpty(betaIds);   // control: beta really has people who could leak
        Assert.DoesNotContain(staff, s => betaIds.Contains(s.Id));
    }

    [Fact]
    public async Task Resetting_another_tenants_users_password_fails_and_changes_nothing()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaTech = await env.UserIdAsync("beta", "tech@beta.test");
        var before = await env.UserRowAsync(betaTech);

        var res = await alpha.PostAsync($"/api/users/{betaTech}/reset-password", null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var after = await env.UserRowAsync(betaTech);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.False(after.MustChangePassword);
    }

    [Fact]
    public async Task Disabling_another_tenants_user_fails_and_changes_nothing()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaAdmin = await env.UserIdAsync("beta", "admin@beta.test");

        var res = await alpha.PatchJsonAsync($"/api/users/{betaAdmin}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.True((await env.UserRowAsync(betaAdmin)).IsActive);
    }

    [Fact]
    public async Task Changing_the_role_of_another_tenants_user_fails_and_changes_nothing()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaTech = await env.UserIdAsync("beta", "tech@beta.test");

        var res = await alpha.PatchJsonAsync($"/api/users/{betaTech}/role", new { role = "TenantAdmin" });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(UserRole.Technician, (await env.UserRowAsync(betaTech)).Role);
    }

    [Fact]
    public async Task A_new_user_always_lands_in_the_callers_tenant_even_if_the_body_names_another()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var res = await alpha.PostRawJsonAsync("/api/users",
            """{"fullName":"Probe User","email":"probe@alpha.test","role":"Technician","tenantId":"beta"}""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await using var raw = env.Raw();
        var probe = await raw.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == "probe@alpha.test");
        Assert.Equal("alpha", probe.TenantId);
        Assert.True(probe.MustChangePassword);
    }

    [Fact]
    public async Task The_same_email_may_exist_in_two_tenants_and_the_check_does_not_leak()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        // tech@beta.test is a real beta account. Alpha must be allowed to use that address (a 409 would reveal it).
        var res = await alpha.PostJsonAsync("/api/users", new { fullName = "Same Email", email = "tech@beta.test", role = "Auditor" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await using var raw = env.Raw();
        var tenants = await raw.Users.IgnoreQueryFilters().Where(u => u.Email == "tech@beta.test").Select(u => u.TenantId).ToListAsync();
        Assert.Contains("alpha", tenants);
        Assert.Contains("beta", tenants);
    }

    [Fact]
    public async Task Technicians_and_auditors_cannot_manage_staff()
    {
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        var auditor = await env.SignInAsync("alpha", "audit@alpha.test");

        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tech.PostJsonAsync("/api/users", new { fullName = "Nope", email = "nope@alpha.test", role = "TenantAdmin" })).StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_lock_themselves_out()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var me = await env.UserIdAsync("alpha", "admin@alpha.test");

        Assert.Equal(HttpStatusCode.BadRequest, (await alpha.PatchJsonAsync($"/api/users/{me}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alpha.PatchJsonAsync($"/api/users/{me}/role", new { role = "Auditor" })).StatusCode);
        Assert.Equal(UserRole.TenantAdmin, (await env.UserRowAsync(me)).Role);
        Assert.True((await env.UserRowAsync(me)).IsActive);
    }

    [Fact]
    public async Task A_platform_owner_account_cannot_be_created_through_the_customer_api()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var res = await alpha.PostJsonAsync("/api/users", new { fullName = "Sneaky", email = "sneaky@alpha.test", role = "SuperAdmin" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---------------------------------------------------------------- audit trail
    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task The_audit_trail_contains_only_the_callers_tenants_events(string tenant)
    {
        var admin = await env.SignInAsync(tenant, $"admin@{tenant}.test");

        var page = await admin.GetJsonAsync<PagedResult<AuditDto>>("/api/audit?pageSize=100");

        var own = await env.AuditIdsAsync(tenant);
        var other = await env.AuditIdsAsync(tenant == "alpha" ? "beta" : "alpha");
        Assert.NotEmpty(page.Items);
        Assert.NotEmpty(other);   // control: the other tenant really has events that could leak
        Assert.All(page.Items, i => Assert.Contains(i.Id, own));
        Assert.DoesNotContain(page.Items, i => other.Contains(i.Id));
    }

    [Fact]
    public async Task Technicians_cannot_read_the_audit_trail_but_auditors_can()
    {
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        var auditor = await env.SignInAsync("alpha", "audit@alpha.test");

        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/audit")).StatusCode);
    }
}
