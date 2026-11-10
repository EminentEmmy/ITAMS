namespace ITAMS.Api.Middleware;

public static class SecurityMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            // Strict CSP: no inline scripts or styles anywhere in wwwroot.
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            if (ctx.Request.Path.StartsWithSegments("/api"))
                h["Cache-Control"] = "no-store";

            await next();
        });

    /// <summary>
    /// CSRF defence on top of SameSite=Strict cookies: every state-changing /api call must carry a custom header.
    /// A cross-site form or image cannot add custom headers, and cross-origin fetch is blocked by CORS (not enabled).
    /// </summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var m = ctx.Request.Method;
            var isSafe = HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m);

            if (!isSafe && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-ITAMS-Request"] != "1")
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing request header." });
                return;
            }

            await next();
        });
}
