using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ITAMS.Tests;

/// <summary>The tenant a request acts for comes from a signed token. Forging or editing one must never work.</summary>
[Collection("Isolation")]
public class TokenBoundaryTests(TestEnvironment env)
{
    [Fact]
    public async Task Valid_credentials_do_not_work_under_another_tenants_company_code()
    {
        using var client = env.NewClient();

        // admin@alpha.test with the right password, but claiming to belong to beta.
        var res = await TestEnvironment.TrySignInAsync(client, "beta", "admin@alpha.test", TestEnvironment.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task An_unknown_company_code_is_rejected_the_same_way()
    {
        using var client = env.NewClient();

        var res = await TestEnvironment.TrySignInAsync(client, "does-not-exist", "admin@alpha.test", TestEnvironment.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_token_with_an_edited_tenant_claim_is_rejected()
    {
        var token = await LoginTokenAsync("alpha", "admin@alpha.test");

        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        var edited = payload.Replace("\"tid\":\"alpha\"", "\"tid\":\"beta\"");
        Assert.NotEqual(payload, edited);   // make sure the test really tampered with something
        var tampered = $"{parts[0]}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(edited))}.{parts[2]}";

        var res = await GetWithCookieAsync("/api/assets", tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_the_wrong_key_is_rejected()
    {
        var alphaAdmin = await env.UserIdAsync("alpha", "admin@alpha.test");
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", alphaAdmin.ToString()), new Claim("tid", "beta"), new Claim("role", "TenantAdmin"),
                new Claim("name", "Forger"), new Claim("email", "forger@x.test")
            }),
            Issuer = "itams",
            Audience = "itams-clients",
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-signing-key-0123456789")),
                SecurityAlgorithms.HmacSha256)
        });

        var res = await GetWithCookieAsync("/api/assets", forged);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Garbage_in_the_session_cookie_is_rejected()
    {
        var res = await GetWithCookieAsync("/api/assets", "not-a-token");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Requests_that_change_data_need_the_anti_forgery_header()
    {
        // A valid session cookie but no X-ITAMS-Request header: what a cross-site form post would look like.
        var token = await LoginTokenAsync("alpha", "admin@alpha.test");
        using var bare = env.NewClient(handleCookies: false, csrfHeader: false);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets")
        {
            Content = new StringContent("""{"serialNumber":"NO-HEADER-1","type":"Monitor","status":"InStock"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Cookie", $"ITAMS_AUTH={token}");

        var res = await bare.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------------------------------------------------------------- helpers
    /// <summary>Signs in on a client that does not keep cookies, and returns the raw ITAMS_AUTH token from Set-Cookie.</summary>
    private async Task<string> LoginTokenAsync(string tenant, string email)
    {
        using var client = env.NewClient(handleCookies: false);
        var res = await TestEnvironment.TrySignInAsync(client, tenant, email, TestEnvironment.Password);
        Assert.True(res.IsSuccessStatusCode);

        var setCookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ITAMS_AUTH="));
        return setCookie["ITAMS_AUTH=".Length..].Split(';')[0];
    }

    private async Task<HttpResponseMessage> GetWithCookieAsync(string url, string token)
    {
        using var client = env.NewClient(handleCookies: false);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"ITAMS_AUTH={token}");
        return await client.SendAsync(request);
    }
}
