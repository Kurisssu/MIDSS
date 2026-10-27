namespace Sitim.Web.Services;

/// <summary>
/// Circuit-scoped in-memory store for tokens and user info.
/// Hydrated from cookies on circuit start, repopulated by login / refresh flows.
/// </summary>
public sealed class AuthTokenStore
{
    /// <summary>Short-lived JWT access token (default 15 minutes).</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Opaque long-lived refresh token (default 7 days). Used by <see cref="AuthTokenHandler"/>
    /// to mint a new access token transparently when the current one expires.
    /// </summary>
    public string? RefreshToken { get; set; }

    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
    public Guid? InstitutionId { get; set; }
    public string? InstitutionName { get; set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);
    public bool IsSuperAdmin => Roles.Contains("SuperAdmin");

    public void Clear()
    {
        Token = null;
        RefreshToken = null;
        UserId = null;
        Email = null;
        Roles = [];
        InstitutionId = null;
        InstitutionName = null;
    }
}
