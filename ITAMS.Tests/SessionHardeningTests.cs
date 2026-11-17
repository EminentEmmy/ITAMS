using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ITAMS.Tests;

/// <summary>
/// A signed token is not enough: ending a session, disabling a user, changing a role or suspending an organization
/// must stop access at once, and a login must have a hard ceiling that refreshing cannot extend.
/// Each test uses its own freshly created user so it cannot disturb the others.
/// </summary>
[Collection("Isolation")]
public class SessionHardeningTests(TestEnvironment env)
{
    // Must match the key the fixture gives the app. Used to prove that even a correctly SIGNED token is refused without a live session.
    private const string TestSigningKey = "test-only-signing-key-that-is-longer-than-32-bytes!!";

    // ---------------------------------------------------------------- helpers
    private async Task<(Guid Id, string Email)> NewUserAsync(string tenant, UserRole role)
    {
        var email = $"s{Guid.NewGuid():N}@{tenant}.test";
        await using var db = env.Raw();
        var user = new AppUser
        {
            TenantId = tenant, Email = email, FullName = "Session Tester", Role = role,
            PasswordHash = new Pbkdf2PasswordHasher().Hash(TestEnvironment.Password), MustChangePassword = false
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, email);
    }

    private async Task<HttpClient> SignInFreshAsync(string tenant, string email)
    {
        var client = env.NewClient();   // its own cookie jar, so its own session
        var res = await TestEnvironment.TrySignInAsync(client, tenant, email, TestEnvironment.Password);
        Assert.True(res.IsSuccessStatusCode, $"Could not sign in {email}: {(int)res.StatusCode}");
        return client;
    }

    private static async Task<List<SessionDto>> SessionsOf(HttpClient client) =>
        await client.GetJsonAsync<List<SessionDto>>("/api/sessions");

    // ================================================================ immediate effect
    [Fact]
    public async Task Disabling_a_user_ends_their_access_at_once()
    {
        var (id, email) = await NewUserAsync("alpha", UserRole.Technician);
        var client = await SignInFreshAsync("alpha", email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/assets")).StatusCode);

        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchJsonAsync($"/api/users/{id}/status", new { isActive = false })).StatusCode);

        // the 15-minute access token is still unexpired, but the session behind it is gone
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_a_role_ends_the_old_access_at_once()
    {
        var (id, email) = await NewUserAsync("alpha", UserRole.Technician);
        var client = await SignInFreshAsync("alpha", email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/assets")).StatusCode);

        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchJsonAsync($"/api/users/{id}/role", new { role = "Requester" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/assets")).StatusCode);
    }

    [Fact]
    public async Task Resetting_a_password_ends_the_sessions_at_once()
    {
        var (id, email) = await NewUserAsync("alpha", UserRole.Technician);
        var client = await SignInFreshAsync("alpha", email);

        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{id}/reset-password", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Suspending_an_organization_ends_every_session_in_it_at_once()
    {
        await env.CreateTenantWithAdminAsync("sessionco", "admin@sessionco.test");
        var client = await SignInFreshAsync("sessionco", "admin@sessionco.test");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var owner = await env.SignInAsync("platform", "owner@platform.test");
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchJsonAsync("/api/platform/tenants/sessionco/status", new { isActive = false })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // ================================================================ hard ceiling
    [Fact]
    public async Task A_session_ends_at_its_ceiling_and_refreshing_cannot_extend_it()
    {
        var (id, email) = await NewUserAsync("alpha", UserRole.Technician);
        var client = await SignInFreshAsync("alpha", email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        // pretend the ceiling has just been reached
        await using (var raw = env.Raw())
            await raw.RefreshTokens.IgnoreQueryFilters().Where(t => t.UserId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.SessionExpiresUtc, DateTime.UtcNow.AddMinutes(-1)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);          // access token: refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode); // refresh: refused too

        // signing in again starts a fresh session
        var again = await SignInFreshAsync("alpha", email);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Refreshing_keeps_the_same_session_and_the_same_ceiling()
    {
        var (_, email) = await NewUserAsync("alpha", UserRole.Technician);
        var client = await SignInFreshAsync("alpha", email);
        var before = Assert.Single(await SessionsOf(client));

        await Task.Delay(50);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);

        var after = Assert.Single(await SessionsOf(client));
        Assert.Equal(before.Id, after.Id);                     // the same sign-in
        Assert.Equal(before.ExpiresUtc, after.ExpiresUtc);     // the ceiling did not move
        Assert.Equal(before.StartedUtc, after.StartedUtc);
        Assert.True(after.LastActiveUtc >= before.LastActiveUtc);
    }

    [Fact]
    public async Task The_platform_owner_gets_a_shorter_ceiling_than_staff()
    {
        var owner = await env.SignInAsync("platform", "owner@platform.test");
        var staff = await env.SignInAsync("alpha", "admin@alpha.test");

        Assert.All(await SessionsOf(owner), s => Assert.InRange((s.ExpiresUtc - s.StartedUtc).TotalHours, 7.9, 8.1));
        Assert.All(await SessionsOf(staff), s => Assert.InRange((s.ExpiresUtc - s.StartedUtc).TotalHours, 11.9, 12.1));
    }

    // ================================================================ tokens without a live session
    [Fact]
    public async Task A_correctly_signed_token_with_no_session_is_refused()
    {
        var adminId = await env.UserIdAsync("alpha", "admin@alpha.test");

        string Forge(params Claim[] extra)
        {
            var claims = new List<Claim>
            {
                new("sub", adminId.ToString()), new("tid", "alpha"), new("role", "TenantAdmin"),
                new("name", "Forged"), new("email", "admin@alpha.test")
            };
            claims.AddRange(extra);
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims), Issuer = "itams", Audience = "itams-clients",
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey)), SecurityAlgorithms.HmacSha256)
            });
        }

        // valid signature, valid expiry, but no session id (an old-style token) ...
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWithTokenAsync(Forge()));
        // ... or a session id that does not exist
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWithTokenAsync(Forge(new Claim("sid", Guid.NewGuid().ToString()))));
    }

    private async Task<HttpStatusCode> GetWithTokenAsync(string token)
    {
        using var client = env.NewClient(handleCookies: false);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"ITAMS_AUTH={token}");
        return (await client.SendAsync(request)).StatusCode;
    }

    // ================================================================ the sessions list and controls
    [Fact]
    public async Task The_sessions_list_shows_only_my_sessions_and_marks_the_current_one()
    {
        var (id, email) = await NewUserAsync("alpha", UserRole.Technician);
        var laptop = await SignInFreshAsync("alpha", email);
        var phone = await SignInFreshAsync("alpha", email);

        var list = await SessionsOf(laptop);

        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s.Current);
        await using var raw = env.Raw();
        var mine = await raw.RefreshTokens.IgnoreQueryFilters().Where(t => t.UserId == id).Select(t => t.SessionId).Distinct().ToListAsync();
        Assert.Equal(mine.Order(), list.Select(s => s.Id).Order());   // nobody else's sessions
        _ = phone;
    }

    [Fact]
    public async Task Signing_out_another_session_ends_it_but_not_mine()
    {
        var (_, email) = await NewUserAsync("alpha", UserRole.Technician);
        var laptop = await SignInFreshAsync("alpha", email);
        var phone = await SignInFreshAsync("alpha", email);
        var phoneSession = (await SessionsOf(phone)).Single(s => s.Current);

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.DeleteAsync($"/api/sessions/{phoneSession.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);   // the phone is out
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);            // the laptop is not
    }

    [Fact]
    public async Task Sign_out_all_other_sessions_keeps_only_this_one()
    {
        var (_, email) = await NewUserAsync("alpha", UserRole.Technician);
        var laptop = await SignInFreshAsync("alpha", email);
        var phone = await SignInFreshAsync("alpha", email);
        var tablet = await SignInFreshAsync("alpha", email);

        var res = await laptop.PostAsync("/api/sessions/revoke-others", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Single(await SessionsOf(laptop));
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_every_session_including_this_one()
    {
        var (_, email) = await NewUserAsync("alpha", UserRole.Technician);
        var laptop = await SignInFreshAsync("alpha", email);
        var phone = await SignInFreshAsync("alpha", email);

        var res = await laptop.PostAsync("/api/sessions/revoke-all", null);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ITAMS_AUTH=") && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));   // browser is told to drop the cookie
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_sign_out_someone_elses_session()
    {
        var (_, email) = await NewUserAsync("alpha", UserRole.Technician);
        var victim = await SignInFreshAsync("alpha", email);
        var victimSession = (await SessionsOf(victim)).Single();

        var sameTenantAdmin = await env.SignInAsync("alpha", "admin@alpha.test");
        var otherTenantAdmin = await env.SignInAsync("beta", "admin@beta.test");

        Assert.Equal(HttpStatusCode.NotFound, (await sameTenantAdmin.DeleteAsync($"/api/sessions/{victimSession.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenantAdmin.DeleteAsync($"/api/sessions/{victimSession.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/auth/me")).StatusCode);   // still signed in
    }

    [Fact]
    public async Task The_sessions_api_requires_signing_in()
    {
        using var anonymous = env.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/sessions/revoke-all", null)).StatusCode);
    }
}
