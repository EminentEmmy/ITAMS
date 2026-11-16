using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ITAMS.Api.Dtos;

namespace ITAMS.Tests;

/// <summary>
/// Turns the security checklist into tests that run on every build, so a future change cannot quietly undo it:
/// no dangerous DOM code, strict CSP, safe cookies, no token in any response body, and hostile ticket text stays inert.
/// </summary>
[Collection("Isolation")]
public class SecurityGuardTests(TestEnvironment env)
{
    // ================================================================ front end: static guards
    private static string WwwRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ITAMS.Api", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find ITAMS.Api/wwwroot above " + AppContext.BaseDirectory);
    }

    private static string StripComments(string js)
    {
        js = Regex.Replace(js, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(js, @"(?<![:'""\\])//.*$", "", RegexOptions.Multiline);   // keeps "https://..." intact
    }

    private static readonly (string Name, Regex Pattern)[] DangerousScriptPatterns =
    [
        ("innerHTML", new Regex(@"\binnerHTML\b")),
        ("outerHTML", new Regex(@"\bouterHTML\b")),
        ("insertAdjacentHTML", new Regex(@"\binsertAdjacentHTML\b")),
        ("document.write", new Regex(@"\bdocument\s*\.\s*write(ln)?\b")),
        ("eval()", new Regex(@"\beval\s*\(")),
        ("new Function()", new Regex(@"\bnew\s+Function\s*\(")),
        ("srcdoc", new Regex(@"\.srcdoc\b")),
        ("setAttribute('on...')", new Regex(@"setAttribute\(\s*['""]on", RegexOptions.IgnoreCase)),
        ("javascript: URL", new Regex(@"javascript\s*:", RegexOptions.IgnoreCase)),
        ("session token in browser storage", new Regex(@"(local|session)Storage\s*\.\s*setItem\(\s*['""][^'""]*(token|jwt|auth|session)", RegexOptions.IgnoreCase))
    ];

    [Fact]
    public void Front_end_scripts_never_use_dangerous_dom_sinks_or_store_tokens()
    {
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(WwwRoot(), "js"), "*.js", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var (name, pattern) in DangerousScriptPatterns)
                if (pattern.IsMatch(code)) problems.Add($"{Path.GetFileName(file)}: {name}");
        }

        Assert.True(problems.Count == 0, "Dangerous front-end code found:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Front_end_pages_have_no_inline_script_style_or_event_handlers_and_load_nothing_from_other_sites()
    {
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(WwwRoot(), "*.html"))
        {
            var html = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match m in Regex.Matches(html, @"<script\b[^>]*>", RegexOptions.IgnoreCase))
                if (!Regex.IsMatch(m.Value, @"\bsrc\s*=", RegexOptions.IgnoreCase)) problems.Add($"{name}: inline <script>");
            if (Regex.IsMatch(html, @"<style\b", RegexOptions.IgnoreCase)) problems.Add($"{name}: inline <style> block");
            if (Regex.IsMatch(html, @"<[^>]+\sstyle\s*=", RegexOptions.IgnoreCase)) problems.Add($"{name}: inline style attribute");
            if (Regex.IsMatch(html, @"<[^>]+\son[a-z]+\s*=", RegexOptions.IgnoreCase)) problems.Add($"{name}: inline event handler");
            if (Regex.IsMatch(html, @"<(script|link|img|iframe)\b[^>]+(src|href)\s*=\s*[""']https?://", RegexOptions.IgnoreCase))
                problems.Add($"{name}: loads a resource from another site");
        }

        Assert.True(problems.Count == 0,
            "These would be blocked by the strict CSP (or weaken it):\n" + string.Join("\n", problems));
    }

    // ================================================================ headers
    [Theory]
    [InlineData("/login.html")]
    [InlineData("/report.html")]
    [InlineData("/index.html")]
    public async Task Pages_carry_a_strict_content_security_policy_and_the_other_security_headers(string path)
    {
        using var client = env.NewClient(handleCookies: false);

        var res = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var csp = string.Join(" ", res.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(res.Headers.Contains("Referrer-Policy"));
    }

    [Fact]
    public async Task Api_responses_are_never_cached()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");

        var res = await alpha.GetAsync("/api/auth/me");

        Assert.Contains("no-store", string.Join(",", res.Headers.GetValues("Cache-Control")));
    }

    // ================================================================ cookies and tokens
    [Fact]
    public async Task Session_cookies_are_httponly_secure_samesite_strict_and_no_token_appears_in_the_response_body()
    {
        using var client = env.NewClient(handleCookies: false);

        var res = await TestEnvironment.TrySignInAsync(client, "alpha", "admin@alpha.test", TestEnvironment.Password);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cookies = res.Headers.GetValues("Set-Cookie").ToList();
        foreach (var name in new[] { "ITAMS_AUTH=", "ITAMS_REFRESH=" })
        {
            var cookie = Assert.Single(cookies, c => c.StartsWith(name));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("path=/api/auth", cookies.Single(c => c.StartsWith("ITAMS_REFRESH=")), StringComparison.OrdinalIgnoreCase);   // refresh cookie is not sent to ordinary API calls

        // JavaScript must never be handed a token: the body holds only the user's profile
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotMatch(@"eyJ[A-Za-z0-9_-]{8,}\.", body);          // looks like a JWT
        foreach (var cookie in cookies)
        {
            var value = cookie.Split('=', 2)[1].Split(';')[0];
            Assert.DoesNotContain(value, body);
        }
    }

    // ================================================================ hostile ticket text
    private const string HostileTitle = "XSS-PROBE-<img src=x onerror=alert(1)>";
    private const string HostileDescription = "<script>alert('stolen')</script> \"><svg onload=alert(2)> javascript:alert(3)";
    private const string HostileName = "\"><b onmouseover=alert(4)>Eve</b>";

    [Fact]
    public async Task Hostile_ticket_text_is_kept_exactly_as_data_and_never_sent_as_raw_html()
    {
        var res = await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, new
        {
            name = HostileName, category = "Other", impact = "Minor", title = HostileTitle, description = HostileDescription, website = ""
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        var list = await admin.GetAsync("/api/tickets?search=XSS-PROBE&pageSize=100");

        // 1. nothing is altered or half-encoded: the front end shows it as plain text (textContent)
        var page = (await list.Content.ReadFromJsonAsync<PagedResult<TicketDto>>(TestHttp.Json))!;
        var ticket = Assert.Single(page.Items, t => t.Title == HostileTitle);
        Assert.Equal(HostileDescription, ticket.Description);
        Assert.Equal(HostileName, ticket.ReporterName);

        // 2. the response is JSON that browsers must not sniff as a page
        Assert.StartsWith("application/json", list.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", list.Headers.GetValues("X-Content-Type-Options").Single());

        // 3. defence in depth: even the raw bytes contain no live tag, because JSON escapes < > & '
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<img", raw);
        Assert.DoesNotContain("<script", raw);
        Assert.DoesNotContain("<svg", raw);
    }

    [Fact]
    public async Task Hostile_text_in_the_audit_trail_is_also_returned_without_raw_tags()
    {
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, new
        {
            name = HostileName, category = "Other", impact = "Minor", title = HostileTitle + " (audit)", description = HostileDescription, website = ""
        });

        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        var res = await admin.GetAsync("/api/audit?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.Contains("XSS-PROBE", raw);          // it really is in the trail
        Assert.DoesNotContain("<img", raw);
        Assert.DoesNotContain("<script", raw);
    }
}
