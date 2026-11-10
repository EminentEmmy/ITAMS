namespace ITAMS.Api.Auth;

/// <summary>
/// Both tokens live in HttpOnly + Secure + SameSite=Strict cookies, so JavaScript (and therefore XSS) can never read them.
/// The refresh cookie is scoped to /api/auth so it is not sent with ordinary API calls.
/// </summary>
public static class AuthCookies
{
    public const string Access = "ITAMS_AUTH";
    public const string Refresh = "ITAMS_REFRESH";
    private const string RefreshPath = "/api/auth";

    public static void Set(HttpResponse res, string access, DateTime accessExpiresUtc, string refresh, DateTime refreshExpiresUtc)
    {
        res.Cookies.Append(Access, access, Options("/", accessExpiresUtc));
        res.Cookies.Append(Refresh, refresh, Options(RefreshPath, refreshExpiresUtc));
    }

    public static void Clear(HttpResponse res)
    {
        res.Cookies.Delete(Access, Options("/", null));
        res.Cookies.Delete(Refresh, Options(RefreshPath, null));
    }

    private static CookieOptions Options(string path, DateTime? expiresUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = path,
        IsEssential = true,
        Expires = expiresUtc is null ? null : new DateTimeOffset(expiresUtc.Value, TimeSpan.Zero)
    };
}
