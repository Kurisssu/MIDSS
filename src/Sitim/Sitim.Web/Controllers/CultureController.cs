using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace Sitim.Web.Controllers;

/// <summary>
/// Sets the ASP.NET Core culture cookie and redirects back. A full page reload
/// through this endpoint is the only way to change the culture of a Blazor
/// Server circuit — the culture is captured once, when the circuit starts.
/// </summary>
[Route("culture")]
public sealed class CultureController : Controller
{
    private static readonly HashSet<string> SupportedCultures =
        new(StringComparer.OrdinalIgnoreCase) { "en-US", "ro-RO" };

    [HttpGet("set")]
    public IActionResult Set(string? culture, string? redirectUri)
    {
        if (!string.IsNullOrEmpty(culture) && SupportedCultures.Contains(culture))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/"
                });
        }

        var target = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri;
        return LocalRedirect(Url.IsLocalUrl(target) ? target : "/");
    }
}
