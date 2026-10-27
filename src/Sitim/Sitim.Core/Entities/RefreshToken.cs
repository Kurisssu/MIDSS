namespace Sitim.Core.Entities;

/// <summary>
/// Long-lived (7 day) opaque token used to obtain new short-lived access tokens
/// without re-prompting the user for credentials. Stored hashed (never in clear)
/// so a DB leak does not yield usable tokens.
///
/// Lifecycle:
/// <list type="number">
///   <item>Issued at login alongside the access token.</item>
///   <item>Refresh flow rotates it: each successful refresh REVOKES the current
///         token and ISSUES a new one, linking them via <see cref="ReplacedByTokenHash"/>.
///         A reuse of the old token after rotation flags a leak — the entire
///         family (chain) is then revoked as a security precaution.</item>
///   <item>Logout revokes the active token (chain ends).</item>
/// </list>
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// SHA-256 hash of the token plaintext. Plaintext is sent over the wire only
    /// to the issuing client and never persisted server-side.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Set when the token is revoked (either by rotation, logout, or breach detection).</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Free-text reason; useful for debugging revocations.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>
    /// When this token has been ROTATED (i.e. used once for refresh), this points to
    /// the hash of the freshly issued successor. If the old token shows up again, that's
    /// a reuse attempt — the breach handler walks <see cref="ReplacedByTokenHash"/>
    /// forward and revokes every member of the chain.
    /// </summary>
    public string? ReplacedByTokenHash { get; set; }

    /// <summary>IP of the requester at issue time (audit).</summary>
    public string? CreatedByIp { get; set; }

    /// <summary>User-Agent string at issue time (audit).</summary>
    public string? UserAgent { get; set; }

    /// <summary>Convenience computed: token is currently valid for refresh.</summary>
    public bool IsActive => RevokedAtUtc is null && ExpiresAtUtc > DateTime.UtcNow;
}
