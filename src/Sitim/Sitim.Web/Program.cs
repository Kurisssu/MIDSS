using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using Radzen;
using Sitim.Web.Components;
using Sitim.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor + Blazor Server
builder.Services.AddRazorComponents()
      .AddInteractiveServerComponents()
      .AddHubOptions(options => options.MaximumReceiveMessageSize = 10 * 1024 * 1024);

builder.Services.AddControllers();

// ── Localization ─────────────────────────────────────────
// English is the hard default: only an explicit user choice (culture cookie set
// via /culture/set) switches the language. Browser Accept-Language must NOT
// override it, hence the cookie-only provider list.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[] { "en-US", "ro-RO" };
    options.SetDefaultCulture("en-US")
           .AddSupportedCultures(supportedCultures)
           .AddSupportedUICultures(supportedCultures);
    options.RequestCultureProviders = new List<IRequestCultureProvider>
    {
        new CookieRequestCultureProvider()
    };
});

// Radzen
builder.Services.AddRadzenComponents();
builder.Services.AddRadzenCookieThemeService(options =>
{
    options.Name = "Sitim.WebTheme";
    options.Duration = TimeSpan.FromDays(365);
});

// ── Auth services ────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthTokenStore>();
builder.Services.AddScoped<SitimAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<SitimAuthStateProvider>());

// ASP.NET Core authorization middleware needs a registered scheme
// even though Blazor uses AuthenticationStateProvider for auth state.
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "BlazorServer";
    options.DefaultChallengeScheme = "BlazorServer";
})
.AddCookie("BlazorServer", options =>
{
    options.LoginPath = "/login";
});
builder.Services.AddAuthorizationCore();

// ── HTTP client for API calls ────────────────────────────
var apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? "https://localhost:7006";
var webBaseUrl = builder.Configuration["Web:BaseUrl"] ?? "https://localhost:5001";

// NOTE: SitimApiClient sets the Bearer header per-call via AttachToken(). We do NOT
// register AuthTokenHandler as a DelegatingHandler in the pipeline — HttpMessageHandler
// instances are reused for ~2 min across Blazor circuits (HandlerLifetime), and any
// scoped service captured by the handler at construction would freeze to the wrong
// circuit. Refresh-on-expiry is driven proactively from SitimAuthStateProvider instead.

builder.Services.AddHttpClient<SitimApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Dev: accept self-signed certs from API
    ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

// Named client used by AuthTokenHandler to call /api/auth/refresh WITHOUT routing
// back through AuthTokenHandler (that would recurse). Same primary handler as the
// typed client so the dev cert override applies.
builder.Services.AddHttpClient("sitim-auth-raw", c =>
{
    c.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

// Named client targeting the WEB app itself (for /auth/cookie/set persistence after refresh).
builder.Services.AddHttpClient("sitim-cookie-persist", c =>
{
    c.BaseAddress = new Uri(webBaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

var app = builder.Build();

var forwardingOptions = new ForwardedHeadersOptions()
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardingOptions.KnownIPNetworks.Clear();
forwardingOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardingOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();
app.UseRequestLocalization();
app.MapStaticAssets();
app.UseAntiforgery();
app.MapControllers();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();