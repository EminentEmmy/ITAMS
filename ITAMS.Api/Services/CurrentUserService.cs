using System.Security.Claims;
using ITAMS.Domain.Abstractions;

namespace ITAMS.Api.Services;

public sealed class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService
{
    private HttpContext? Ctx => accessor.HttpContext;

    public string? TenantId => Ctx?.User.FindFirstValue("tid");
    public string? UserId => Ctx?.User.FindFirstValue("sub");
    public string? FullName => Ctx?.User.FindFirstValue("name");
    public string? IpAddress => Ctx?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Ctx?.Request.Headers.UserAgent.ToString();
    public bool IsAuthenticated => Ctx?.User.Identity?.IsAuthenticated == true;
}
