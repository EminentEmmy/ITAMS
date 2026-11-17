namespace ITAMS.Api.Dtos;

public record SessionDto(Guid Id, DateTime StartedUtc, DateTime LastActiveUtc, DateTime ExpiresUtc,
    string? IpAddress, string? UserAgent, bool Current);
