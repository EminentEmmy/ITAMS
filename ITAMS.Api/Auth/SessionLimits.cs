namespace ITAMS.Api.Auth;

public class SessionLimits
{
    /// <summary>Longest a single sign-in can last, however often it is refreshed.</summary>
    public int MaxHours { get; set; } = 12;

    /// <summary>Shorter ceiling for the platform owner, whose account can see every customer.</summary>
    public int OwnerMaxHours { get; set; } = 8;

    /// <summary>
    /// How long a "this session is still good" answer may be remembered. A revoked session, disabled user,
    /// suspended organization or changed role is noticed within this many seconds (0 = checked on every request).
    /// </summary>
    public int ValidationCacheSeconds { get; set; } = 5;
}
