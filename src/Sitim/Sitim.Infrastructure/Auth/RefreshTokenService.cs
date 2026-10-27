using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sitim.Core.Entities;
using Sitim.Core.Options;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using System.Security.Cryptography;

namespace Sitim.Infrastructure.Auth;

/// <summary>
/// EF Core-backed refresh-token store with rotation + reuse detection.
/// </summary>
public sealed class RefreshTokenService : IRefreshTokenService
{
    private const int TokenByteLength = 32; // 256 bits — strong against brute force

    private readonly AppDbContext _db;
    private readonly AuthOptions _options;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        AppDbContext db,
        IOptions<AuthOptions> options,
        ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RefreshTokenIssue> IssueAsync(
        Guid userId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        return await PersistFreshAsync(userId, ip, userAgent, cancellationToken);
    }

    public async Task<RefreshTokenIssue> RotateAsync(
        string plaintextToken,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
            throw new RefreshTokenSecurityException("Empty refresh token.");

        var hash = Hash(plaintextToken);
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null)
            throw new RefreshTokenSecurityException("Unknown refresh token.");

        if (token.RevokedAtUtc is not null)
        {
            // Token reuse — someone is replaying an already-rotated token. Could be
            // a leaked credential. Burn the entire chain (every successor) to evict
            // the attacker AND the legitimate device, forcing both to re-login.
            await RevokeFamilyAsync(token, "reuse-detected", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Refresh-token reuse detected for user {UserId}. Entire token family revoked.",
                token.UserId);

            throw new RefreshTokenSecurityException("Refresh token reuse detected — session invalidated.");
        }

        if (token.ExpiresAtUtc <= DateTime.UtcNow)
            throw new RefreshTokenSecurityException("Refresh token expired.");

        // Happy path: rotate. Issue successor first so we can link the rotated one to it.
        var fresh = await PersistFreshAsync(token.UserId, ip, userAgent, cancellationToken);

        token.RevokedAtUtc = DateTime.UtcNow;
        token.RevokedReason = "rotated";
        token.ReplacedByTokenHash = Hash(fresh.Plaintext);

        await _db.SaveChangesAsync(cancellationToken);
        return fresh;
    }

    public async Task RevokeAsync(
        string plaintextToken,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken)) return;

        var hash = Hash(plaintextToken);
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || token.RevokedAtUtc is not null) return; // idempotent

        token.RevokedAtUtc = DateTime.UtcNow;
        token.RevokedReason = reason;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<RefreshTokenIssue> PersistFreshAsync(
        Guid userId,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        var plaintext = GenerateUrlSafeToken();
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(plaintext),
            UserId = userId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(Math.Clamp(_options.RefreshTokenDays, 1, 90)),
            CreatedByIp = Truncate(ip, 64),
            UserAgent = Truncate(userAgent, 256),
        };
        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new RefreshTokenIssue(plaintext, entity.ExpiresAtUtc, userId);
    }

    /// <summary>
    /// Walks the rotation chain forward from <paramref name="start"/> and revokes
    /// every token still active. Used when reuse is detected.
    /// </summary>
    private async Task RevokeFamilyAsync(RefreshToken start, string reason, CancellationToken ct)
    {
        var cursor = start;
        var stamp = DateTime.UtcNow;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (cursor is not null && visited.Add(cursor.TokenHash))
        {
            if (cursor.RevokedAtUtc is null)
            {
                cursor.RevokedAtUtc = stamp;
                cursor.RevokedReason = reason;
            }

            if (string.IsNullOrEmpty(cursor.ReplacedByTokenHash)) break;
            cursor = await _db.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == cursor.ReplacedByTokenHash, ct);
        }
    }

    private static string GenerateUrlSafeToken()
    {
        Span<byte> buffer = stackalloc byte[TokenByteLength];
        RandomNumberGenerator.Fill(buffer);
        // Url-safe base64 (so it survives any URL / cookie / JSON encoding round trip).
        return Convert.ToBase64String(buffer)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Hash(string plaintext)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
