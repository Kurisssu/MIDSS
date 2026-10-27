using Sitim.Core.Entities;

namespace Sitim.Core.Services;

/// <summary>
/// Server-side refresh-token issuing &amp; validation. Implementations persist
/// hashes, never plaintexts, and enforce rotation + reuse-detection semantics.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>Mint a brand-new refresh token (e.g. at login). Returns the plaintext (sent to client).</summary>
    Task<RefreshTokenIssue> IssueAsync(
        Guid userId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate the supplied plaintext refresh token and rotate it: revoke the current
    /// token and issue a new one. Returns the freshly issued plaintext.
    /// Throws <see cref="RefreshTokenSecurityException"/> on any anomaly (expired,
    /// revoked, reuse-detected).
    /// </summary>
    Task<RefreshTokenIssue> RotateAsync(
        string plaintextToken,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke the supplied refresh token (e.g. on logout). Idempotent — already-revoked
    /// tokens silently no-op so a stale client logout doesn't 500.
    /// </summary>
    Task RevokeAsync(
        string plaintextToken,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>The plaintext + lifecycle metadata returned to the caller after issue/rotate.</summary>
public sealed record RefreshTokenIssue(
    string Plaintext,
    DateTime ExpiresAtUtc,
    Guid UserId);

/// <summary>Thrown when the refresh-token guarantees are violated (security event).</summary>
public sealed class RefreshTokenSecurityException : Exception
{
    public RefreshTokenSecurityException(string message) : base(message) { }
}
