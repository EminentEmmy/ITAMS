namespace ITAMS.Domain.Abstractions;

/// <summary>
/// Lets the public report endpoint act inside one organization for the rest of the request, after it has
/// validated that organization's report link. While active, the request has no user identity.
/// Nothing else may call this.
/// </summary>
public interface ITenantOverride
{
    void UseTenant(string tenantId);
}
