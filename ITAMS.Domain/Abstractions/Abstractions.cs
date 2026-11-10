namespace ITAMS.Domain.Abstractions;

/// <summary>Who is making the current request (read from the validated JWT).</summary>
public interface ICurrentUserService
{
    string? TenantId { get; }
    string? UserId { get; }
    string? FullName { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    bool IsAuthenticated { get; }
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);
}
