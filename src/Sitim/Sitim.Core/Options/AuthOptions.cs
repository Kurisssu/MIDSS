namespace Sitim.Core.Options;

/// <summary>
/// Long-lived authentication settings (refresh tokens, lockouts, etc.).
/// Short-lived JWT settings stay in <c>Jwt:*</c>.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Lifetime of a refresh token in days. Default 7 — OWASP-balanced for medical apps.</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>How often the institution-active check hits the DB (seconds). Cache fills the gaps.</summary>
    public int InstitutionActiveCacheSeconds { get; set; } = 30;
}
