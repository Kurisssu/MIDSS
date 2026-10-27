using Microsoft.AspNetCore.Mvc;

namespace Sitim.Web.Controllers;

/// <summary>
/// Flips the appearance cookie and redirects back. Mirrors <see cref="CultureController"/>:
/// a full page reload is what makes the change stick, because the Radzen stylesheet and the
/// <c>data-theme</c> attribute that drives <c>site.css</c> are both emitted during the initial
/// server render of <c>App.razor</c>.
///
/// The cookie name and payload match what <c>AddRadzenCookieThemeService</c> reads, so Radzen's
/// own client-side theme service stays in agreement with what the server rendered.
/// </summary>
[Route("theme")]
public sealed class ThemeController : Controller
{
    /// <summary>Cookie name — must match <c>CookieThemeServiceOptions.Name</c> in Program.cs.</summary>
    public const string CookieName = "Sitim.WebTheme";

    /// <summary>Radzen theme used for the light appearance.</summary>
    public const string LightTheme = "standard";

    /// <summary>Radzen theme used for the dark appearance (the product default).</summary>
    public const string DarkTheme = "standard-dark";

    /// <summary>
    /// Toggles between the light and dark appearance. The caller does not pass the current
    /// theme — the cookie is the single source of truth, so a stale link can never write the
    /// wrong value.
    /// </summary>
    [HttpGet("toggle")]
    public IActionResult Toggle(string? redirectUri)
    {
        var current = Request.Cookies[CookieName];
        var next = current == LightTheme ? DarkTheme : LightTheme;

        Response.Cookies.Append(
            CookieName,
            next,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });

        var target = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri;
        return LocalRedirect(Url.IsLocalUrl(target) ? target : "/");
    }
}
