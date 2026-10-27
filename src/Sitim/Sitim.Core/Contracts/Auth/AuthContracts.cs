namespace Sitim.Core.Contracts.Auth;

/// <summary>Body for POST /api/auth/login.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// Tokens issued by /api/auth/login and /api/auth/refresh. The access token is a
/// short-lived JWT (15 min). The refresh token is opaque (32 bytes Base64URL) and
/// the client should store it in an HttpOnly cookie — NOT in JS-accessible storage.
/// </summary>
public sealed record LoginResult(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    DateTime RefreshExpiresAtUtc);

/// <summary>Body for POST /api/auth/refresh.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Body for POST /api/auth/logout (revokes the supplied refresh token).</summary>
public sealed record LogoutRequest(string? RefreshToken);

/// <summary>Current user identity returned by /api/auth/me.</summary>
public sealed record MeResult(
    Guid UserId,
    string Email,
    IReadOnlyList<string> Roles,
    Guid? InstitutionId,
    string? InstitutionName);

/// <summary>Body for POST /api/users/set-password (completes the invitation flow).</summary>
public sealed record SetPasswordRequest(Guid UserId, string Token, string NewPassword);
