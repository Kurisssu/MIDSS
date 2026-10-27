using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Sitim.Web.Services;

/// <summary>
/// Blazor Server AuthenticationStateProvider with cookie-based persistence.
///
/// Flow:
///   1. On first GetAuthenticationStateAsync (new circuit), check AuthTokenStore.
///   2. If empty, try to restore JWT from the "sitim_auth" HttpOnly cookie via IHttpContextAccessor.
///   3. Parse JWT claims (email, roles, exp) — no extra API call needed.
///   4. On login: save token in memory + call sitimAuth.setCookie via JS interop.
///   5. On logout: clear memory + call sitimAuth.clearCookie via JS interop.
/// </summary>
public sealed class SitimAuthStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly AuthTokenStore _store;
    private readonly SitimApiClient _api;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IJSRuntime _js;
    private readonly ILogger<SitimAuthStateProvider> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private bool _cookieChecked;

    // Proactive refresh timer — fires before the access token expires so the user
    // never sees a 401 while the circuit is alive.
    private System.Timers.Timer? _refreshTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SitimAuthStateProvider(
        AuthTokenStore store,
        SitimApiClient api,
        IHttpContextAccessor httpContextAccessor,
        IJSRuntime js,
        ILogger<SitimAuthStateProvider> logger,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _api = api;
        _httpContextAccessor = httpContextAccessor;
        _js = js;
        _logger = logger;
        _localizer = localizer;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_store.IsAuthenticated)
            return Task.FromResult(BuildAuthState());

        if (!_cookieChecked)
        {
            _cookieChecked = true;
            TryRestoreFromCookie();

            if (_store.IsAuthenticated)
                return Task.FromResult(BuildAuthState());
        }

        return Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private void TryRestoreFromCookie()
    {
        try
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return;

            var accessCookie = ctx.Request.Cookies["sitim_auth"];
            var refreshCookie = ctx.Request.Cookies["sitim_refresh"];

            // Refresh-only restore: even if the access JWT is missing/expired, the
            // refresh cookie alone is enough — AuthTokenHandler will mint a fresh
            // access token on the first API call. Note: refresh cookie is scoped to
            // /auth/cookie, so it only shows up here on circuit start through that path
            // — for the broader page tree, the JWT (if present) is used to seed claims.
            if (!string.IsNullOrWhiteSpace(refreshCookie))
                _store.RefreshToken = refreshCookie;

            if (string.IsNullOrWhiteSpace(accessCookie))
                return;

            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(accessCookie))
                return;

            var jwt = handler.ReadJwtToken(accessCookie);

            // Don't bail out on expiry: AuthTokenHandler will refresh on the first 401.
            // We still seed claims so AuthorizeView can render correctly during the
            // initial render pass; expired JWTs still carry valid claims metadata.

            var email = jwt.Claims.FirstOrDefault(c =>
                c.Type == ClaimTypes.Email ||
                c.Type == JwtRegisteredClaimNames.Email)?.Value;

            var userId = jwt.Claims.FirstOrDefault(c =>
                c.Type == ClaimTypes.NameIdentifier ||
                c.Type == JwtRegisteredClaimNames.Sub)?.Value;

            var roles = jwt.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                .Select(c => c.Value)
                .ToList();

            _store.Token = accessCookie;
            _store.Email = email;
            _store.UserId = Guid.TryParse(userId, out var uid) ? uid : null;
            _store.Roles = roles;

            // Schedule the next refresh based on the JWT's remaining lifetime. If the
            // token is already past expiry, the timer fires immediately and the API
            // refresh swaps it out.
            var remainingSec = Math.Max(5, (int)(jwt.ValidTo - DateTime.UtcNow).TotalSeconds);
            SchedulePreemptiveRefresh(remainingSec);
        }
        catch
        {
            // Invalid cookie — ignore
        }
    }

    public async Task<string?> LoginAsync(string email, string password)
    {
        var loginResult = await _api.LoginAsync(email, password);
        if (loginResult is null)
            return _localizer["Login.InvalidCredentials"];

        _store.Token = loginResult.AccessToken;
        _store.RefreshToken = loginResult.RefreshToken;

        var me = await _api.GetMeAsync();
        if (me is null)
        {
            _store.Clear();
            return _localizer["Login.UserFetchFailed"];
        }

        _store.UserId = me.UserId;
        _store.Email = me.Email;
        _store.Roles = [.. me.Roles];
        _store.InstitutionId = me.InstitutionId;
        _store.InstitutionName = me.InstitutionName;

        // Persist BOTH tokens to HttpOnly cookies via Web's AuthCookieController.
        var refreshExpiresIn = (int)Math.Max(60, (loginResult.RefreshExpiresAtUtc - DateTime.UtcNow).TotalSeconds);
        await _js.InvokeVoidAsync("sitimAuth.setCookie",
            loginResult.AccessToken,
            loginResult.ExpiresInSeconds,
            loginResult.RefreshToken,
            refreshExpiresIn);

        SchedulePreemptiveRefresh(loginResult.ExpiresInSeconds);

        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        return null;
    }

    public async Task LogoutAsync()
    {
        StopRefreshTimer();

        // Revoke the refresh token server-side before tearing down the cookie.
        // Best-effort: the API call is idempotent and swallows network errors.
        if (!string.IsNullOrWhiteSpace(_store.RefreshToken))
            await _api.LogoutAsync(_store.RefreshToken);

        _store.Clear();
        await _js.InvokeVoidAsync("sitimAuth.clearCookie");
        // NotifyAuthenticationStateChanged is intentionally omitted here.
        // The caller must navigate with forceLoad: true, which tears down the entire Blazor
        // circuit and starts a fresh one — no token in store, no cookie → user is anonymous.
        // Calling Notify before navigation would cause all mounted components to re-render
        // in an unauthenticated state (triggering API calls that return 401).
    }

    private AuthenticationState BuildAuthState()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _store.UserId?.ToString() ?? ""),
            new(ClaimTypes.Email, _store.Email ?? ""),
            new(ClaimTypes.Name, _store.Email ?? ""),
        };

        foreach (var role in _store.Roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    // ── Proactive refresh ─────────────────────────────────────────────────

    /// <summary>
    /// Schedule a background refresh at ~80% of the access token's lifetime so the
    /// user never sees a 401 from natural expiry. The timer runs once; the refresh
    /// itself schedules the next iteration based on the new lifespan.
    /// </summary>
    private void SchedulePreemptiveRefresh(int accessExpiresInSeconds)
    {
        StopRefreshTimer();

        // Refresh slightly before expiry. Floor at 30 s so a misconfigured server
        // (e.g. 1-minute tokens during testing) can't spin us into a tight loop.
        var refreshInMs = Math.Max(30, (int)(accessExpiresInSeconds * 0.80)) * 1000;

        _refreshTimer = new System.Timers.Timer(refreshInMs) { AutoReset = false };
        _refreshTimer.Elapsed += async (_, _) => await SafeRefreshAsync();
        _refreshTimer.Start();
    }

    private void StopRefreshTimer()
    {
        if (_refreshTimer is null) return;
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _refreshTimer = null;
    }

    private async Task SafeRefreshAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(_store.RefreshToken))
                return;

            var result = await _api.RefreshAsync(_store.RefreshToken);
            if (result is null)
            {
                // Refresh refused — leave store as-is; next API call will 401, UI
                // routes to /login. We don't auto-logout here to avoid surprising
                // the user mid-page.
                _logger.LogWarning("Preemptive refresh refused by API; user will be redirected to /login on next 401.");
                return;
            }

            _store.Token = result.AccessToken;
            _store.RefreshToken = result.RefreshToken;

            var refreshExpiresIn = (int)Math.Max(60, (result.RefreshExpiresAtUtc - DateTime.UtcNow).TotalSeconds);
            try
            {
                await _js.InvokeVoidAsync("sitimAuth.setCookie",
                    result.AccessToken,
                    result.ExpiresInSeconds,
                    result.RefreshToken,
                    refreshExpiresIn);
            }
            catch
            {
                // JS interop unavailable on prerender or disposed circuit — non-fatal.
            }

            // Re-arm for the next cycle.
            SchedulePreemptiveRefresh(result.ExpiresInSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preemptive token refresh failed.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        StopRefreshTimer();
        _refreshLock.Dispose();
    }
}
