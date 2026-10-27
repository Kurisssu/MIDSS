using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sitim.Api.Security;
using Sitim.Core.Contracts.Auth;
using Sitim.Core.Contracts.Users;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using Sitim.Infrastructure.Identity;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Sitim.Api.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public sealed class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _users;
        private readonly ITokenService _tokens;
        private readonly IRefreshTokenService _refresh;
        private readonly AppDbContext _db;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<ApplicationUser> users,
            ITokenService tokens,
            IRefreshTokenService refresh,
            AppDbContext db,
            ILogger<AuthController> logger)
        {
            _users = users;
            _tokens = tokens;
            _refresh = refresh;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Login with email+password and receive a JWT access token.
        /// </summary>
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<ActionResult<LoginResult>> Login([FromBody] LoginRequest req)
        {
            var user = await _users.FindByEmailAsync(req.Email);
            if (user is null)
                return Unauthorized();

            // Inactive users cannot log in
            if (!user.IsActive)
                return Unauthorized();

            var ok = await _users.CheckPasswordAsync(user, req.Password);
            if (!ok)
                return Unauthorized();

            var roles = await _users.GetRolesAsync(user);

            string? institutionSlug = null;
            if (user.InstitutionId.HasValue)
            {
                var inst = await _db.Institutions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == user.InstitutionId.Value);
                institutionSlug = inst?.Slug;
            }

            var (token, expiresIn) = _tokens.CreateAccessToken(
                user,
                (IReadOnlyList<string>)roles,
                user.InstitutionId,
                institutionSlug);

            var refresh = await _refresh.IssueAsync(user.Id, ClientIp(), UserAgent());

            return Ok(new LoginResult(token, expiresIn, refresh.Plaintext, refresh.ExpiresAtUtc));
        }

        /// <summary>
        /// Exchange a valid refresh token for a fresh access token + a rotated refresh token.
        /// Refresh-token reuse triggers full family revocation (security event).
        /// </summary>
        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<ActionResult<LoginResult>> Refresh([FromBody] RefreshRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.RefreshToken))
                return Unauthorized();

            RefreshTokenIssue rotated;
            try
            {
                rotated = await _refresh.RotateAsync(req.RefreshToken, ClientIp(), UserAgent());
            }
            catch (RefreshTokenSecurityException ex)
            {
                _logger.LogWarning("Refresh-token rotation rejected: {Reason}", ex.Message);
                return Unauthorized();
            }

            var user = await _users.FindByIdAsync(rotated.UserId.ToString());
            if (user is null || !user.IsActive)
            {
                // The account is gone or deactivated — burn the freshly issued token too.
                await _refresh.RevokeAsync(rotated.Plaintext, "user-inactive");
                return Unauthorized();
            }

            var roles = await _users.GetRolesAsync(user);
            string? institutionSlug = null;
            if (user.InstitutionId.HasValue)
            {
                var inst = await _db.Institutions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == user.InstitutionId.Value);
                if (inst is null || !inst.IsActive)
                {
                    // Institution has been deactivated since the previous token was issued.
                    await _refresh.RevokeAsync(rotated.Plaintext, "institution-inactive");
                    return Unauthorized();
                }
                institutionSlug = inst.Slug;
            }

            var (accessToken, expiresIn) = _tokens.CreateAccessToken(
                user,
                (IReadOnlyList<string>)roles,
                user.InstitutionId,
                institutionSlug);

            return Ok(new LoginResult(accessToken, expiresIn, rotated.Plaintext, rotated.ExpiresAtUtc));
        }

        /// <summary>
        /// Revoke the supplied refresh token. Idempotent — safe to call when the token
        /// has already been rotated/revoked (client just gets 200 either way).
        /// </summary>
        [AllowAnonymous]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] LogoutRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.RefreshToken))
                await _refresh.RevokeAsync(req.RefreshToken, "logout");
            return Ok();
        }

        // ── small helpers ───────────────────────────────────────────────
        private string? ClientIp() =>
            HttpContext.Connection.RemoteIpAddress?.ToString();

        private string? UserAgent() =>
            HttpContext.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

        /// <summary>
        /// Returns the current user's identity, roles, and institution.
        /// </summary>
        [Authorize]
        [HttpGet("me")]
        public async Task<ActionResult<MeResult>> Me()
        {
            var idStr =
                User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                User.FindFirstValue(JwtRegisteredClaimNames.Sub);

            if (!Guid.TryParse(idStr, out var userId))
                return Unauthorized();

            var user = await _users.FindByIdAsync(userId.ToString());
            if (user is null)
                return Unauthorized();

            var roles = await _users.GetRolesAsync(user);

            string? institutionName = null;
            if (user.InstitutionId.HasValue)
            {
                var inst = await _db.Institutions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == user.InstitutionId.Value);
                institutionName = inst?.Name;
            }

            return Ok(new MeResult(
                userId,
                user.Email ?? "",
                (IReadOnlyList<string>)roles,
                user.InstitutionId,
                institutionName));
        }

        /// <summary>
        /// Sets a new password using the Base64URL-encoded password-reset token
        /// delivered via the invitation email. Activates the account on success.
        /// </summary>
        /// <remarks>
        /// Rate-limited (10 attempts / minute / IP) to deter brute-forcing the
        /// userId+token pair. A response header asks browsers not to leak the
        /// token through the <c>Referer</c> when the page navigates away.
        /// </remarks>
        [AllowAnonymous]
        [EnableRateLimiting("set-password")]
        [HttpPost("set-password")]
        public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest req)
        {
            // Stop the browser from leaking the token to any third-party origin
            // when the user navigates away from the set-password page.
            Response.Headers["Referrer-Policy"] = "no-referrer";

            var user = await _users.FindByIdAsync(req.UserId.ToString());
            if (user is null)
                return BadRequest("Invalid request.");

            // Decode the Base64URL token wire form back to the Identity-issued string.
            string rawToken;
            try
            {
                rawToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(req.Token));
            }
            catch (FormatException)
            {
                // Token didn't survive transport — treat as invalid without leaking detail.
                return BadRequest("Token invalid.");
            }

            var result = await _users.ResetPasswordAsync(user, rawToken, req.NewPassword);
            if (!result.Succeeded)
                return BadRequest(result.Errors.Select(e => e.Description));

            // Activate the account after password is set.
            user.IsActive = true;
            user.EmailConfirmed = true;
            await _users.UpdateAsync(user);

            return Ok();
        }
    }
}
