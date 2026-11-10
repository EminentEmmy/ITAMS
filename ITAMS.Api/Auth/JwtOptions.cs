namespace ITAMS.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "itams";
    public string Audience { get; set; } = "itams-clients";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Never commit this. Dev: user-secrets. Prod: Azure Key Vault / env var. Minimum 32 bytes.</summary>
    public string Key { get; set; } = "";
}
