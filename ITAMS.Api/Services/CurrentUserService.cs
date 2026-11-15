using System.Security.Claims;
using ITAMS.Domain.Abstractions;

namespace ITAMS.Api.Services;

public sealed class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService, ITenantOverride
{
    // Set only by the public report endpoint, after it has validated an organization's report link.
    private string? _publicTenant;

    private HttpContext? Ctx => accessor.HttpContext;

    // While the public override is active the request has NO user identity, even if the browser also sent a session cookie.
    private string? Claim(string type) => _publicTenant is null ? Ctx?.User.FindFirstValue(type) : null;

    public void UseTenant(string tenantId) => _publicTenant = tenantId;

    public string? TenantId => _publicTenant ?? Ctx?.User.FindFirstValue("tid");
    public string? UserId => Claim("sub");
    public string? FullName => Claim("name");
    public string? IpAddress => Ctx?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Ctx?.Request.Headers.UserAgent.ToString();
    public bool IsAuthenticated => _publicTenant is null && Ctx?.User.Identity?.IsAuthenticated == true;
}
