using Microsoft.AspNetCore.Mvc;

namespace Sitim.Web.Controllers;

/// <summary>
/// Manages the HttpOnly auth cookies that persist tokens across page refreshes.
/// Lives in Sitim.Web (not the API) so the cookies are scoped to the Web origin.
///
/// Two cookies are managed:
///  <list type="bullet">
///    <item><c>sitim_auth</c> — short-lived JWT access token (15 min).</item>
///    <item><c>sitim_refresh</c> — long-lived refresh token (7 days). Path is
///          /auth/cookie so the browser never sends it to anything else.</item>
///  </list>
/// </summary>
[ApiController]
[Route("auth/cookie")]
[IgnoreAntiforgeryToken]
public sealed class AuthCookieController : ControllerBase
{
    public const string AccessCookieName = "sitim_auth";
    public const string RefreshCookieName = "sitim_refresh";
    // Refresh cookie shares the root path so the Web circuit can read it during a
    // normal page request (cookie restore on reload). It stays HttpOnly + Secure +
    // SameSite=Strict, so JS can't see it and cross-site requests can't carry it.
    public const string RefreshCookiePath = "/";

    public sealed record SetCookieRequest(
        string Token,
        int ExpiresInSeconds,
        string? RefreshToken,
        int? RefreshExpiresInSeconds);

    /// <summary>Stores the JWT + refresh token in HttpOnly Secure SameSite=Strict cookies.</summary>
    [HttpPost("set")]
    public IActionResult Set([FromBody] SetCookieRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequest();

        Response.Cookies.Append(AccessCookieName, request.Token, AccessCookieOptions(request.ExpiresInSeconds));

        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            Response.Cookies.Append(
                RefreshCookieName,
                request.RefreshToken,
                RefreshCookieOptions(request.RefreshExpiresInSeconds ?? 7 * 24 * 3600));
        }

        return Ok();
    }

    /// <summary>Deletes both auth cookies (called on logout).</summary>
    [HttpPost("clear")]
    public IActionResult Clear()
    {
        Response.Cookies.Delete(AccessCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath
        });
        return Ok();
    }

    private static CookieOptions AccessCookieOptions(int seconds) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = TimeSpan.FromSeconds(seconds > 0 ? seconds : 900) // fallback 15 min
    };

    private static CookieOptions RefreshCookieOptions(int seconds) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        MaxAge = TimeSpan.FromSeconds(seconds > 0 ? seconds : 7 * 24 * 3600) // fallback 7 days
    };
}
