using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Minio;
using Sitim.Api.HangfireJobs;
using Sitim.Api.Options;
using Sitim.Api.Security;
using Sitim.Api.Services;
using Sitim.Core.Options;
using Sitim.Core.Services;
using Sitim.Infrastructure.Auth;
using Sitim.Infrastructure.Data;
using Sitim.Infrastructure.Email;
using Sitim.Infrastructure.Identity;
using Sitim.Infrastructure.Orthanc;
using Sitim.Infrastructure.Services;
using System.Text;
using System.Threading.RateLimiting;
using FellowOakDicom;

new DicomSetupBuilder()
    .SkipValidation()
    .Build();

var builder = WebApplication.CreateBuilder(args);

// Large uploads (dev/MVP). Adjust down in production.
// This controls the maximum size for multipart/form-data (file uploads) and request body size.
const long MaxUploadBytes = 1024L * 1024 * 1024; // 1 GB

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = MaxUploadBytes;
});

// CORS — allow Blazor UI (Sitim.Web) to call this API
builder.Services.AddCors(options =>
{
    options.AddPolicy("SitimWeb", policy =>
    {
        policy.WithOrigins(
                "https://localhost:5001",
                "http://localhost:5000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Controllers
builder.Services.AddControllers();

// IHttpContextAccessor (needed by HttpContextTenantContext)
builder.Services.AddHttpContextAccessor();

// Rate limiting — anti-brute-force protection on sensitive auth endpoints.
// Policies are attached on specific endpoints via [EnableRateLimiting("...")].
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Used by /api/auth/set-password — keeps an attacker who knows a UserId
    // from grinding through tokens at line speed. 10 attempts / minute / IP.
    options.AddPolicy("set-password", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

// Swagger (better for rapid manual testing than the minimal OpenAPI endpoint)

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SITIM API", Version = "v1" });

    const string schemeId = "Bearer";

    c.AddSecurityDefinition(schemeId, new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer", // lowercase per RFC 7235
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme."
    });

    c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(schemeId, document)] = new List<string>()
    });
});

// Options
builder.Services.Configure<OrthancOptions>(builder.Configuration.GetSection("Orthanc"));
builder.Services.Configure<OhifOptions>(builder.Configuration.GetSection("Ohif"));
builder.Services.Configure<FederatedLearningOptions>(builder.Configuration.GetSection(FederatedLearningOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));

// Auth (JWT)
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// Local storage
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

// Database (PostgreSQL)
var dbConnectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");

builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
{
    opt.UseNpgsql(dbConnectionString);
});
// Tenant context – reads institution_id from current HTTP request's JWT claims
builder.Services.AddScoped<ITenantContext, HttpContextTenantContext>();
builder.Services.AddIdentityCore<ApplicationUser>(opt =>
{
    // Keep dev password policy reasonable; tighten in production.
    opt.Password.RequiredLength = 8;
    opt.Password.RequireDigit = false;
    opt.Password.RequireNonAlphanumeric = false;
    opt.Password.RequireUppercase = false;
    opt.Password.RequireLowercase = false;
})
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Shorter invitation-token lifespan than the Identity default (24h):
// invitations should be acted on quickly. Configurable via Smtp:InvitationLifetimeHours.
builder.Services.Configure<DataProtectionTokenProviderOptions>(opt =>
{
    var smtpSection = builder.Configuration.GetSection(SmtpOptions.SectionName);
    var hours = smtpSection.GetValue<int?>(nameof(SmtpOptions.InvitationLifetimeHours)) ?? 2;
    opt.TokenLifespan = TimeSpan.FromHours(Math.Clamp(hours, 1, 24));
});

builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IViewerTokenService, ViewerTokenService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddMemoryCache(); // for the institution-active cache
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddHttpClient(FederatedLearningOptions.ControlPlaneHttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FederatedLearningOptions>>().Value;
    client.BaseAddress = new Uri(options.ControlPlaneBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.HttpTimeoutSeconds));
});

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

// Authorization: every authenticated endpoint must also pass the institution-active
// check. Anonymous endpoints (login/refresh/logout/set-password) carry [AllowAnonymous]
// which bypasses this fallback by design.
builder.Services.AddSingleton<IAuthorizationHandler, InstitutionActiveAuthorizationHandler>();
builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("InstitutionMustBeActive", p =>
    {
        p.RequireAuthenticatedUser();
        p.AddRequirements(new InstitutionActiveRequirement());
    });
    opt.FallbackPolicy = opt.GetPolicy("InstitutionMustBeActive");
});

// Hangfire (durable background jobs)
builder.Services.AddHangfire(cfg =>
{
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
       .UseSimpleAssemblyNameTypeSerializer()
       .UseRecommendedSerializerSettings()
       .UsePostgreSqlStorage(
           options =>
           {
               options.UseNpgsqlConnection(dbConnectionString);
           },
           new PostgreSqlStorageOptions
           {
               SchemaName = "hangfire",
               PrepareSchemaIfNecessary = true
           });
});

builder.Services.AddHangfireServer();

// AI Analysis Job Scheduler
builder.Services.AddScoped<AIAnalysisJobScheduler>();
builder.Services.AddScoped<FLSessionMonitorHangfireJob>();

// MinIO client for AI model storage
// MinIO Client (for AI model storage)
builder.Services.AddSingleton<Minio.IMinioClient>(sp =>
{
    var config = builder.Configuration;
    var endpoint = config["MinIO:Endpoint"] ?? throw new InvalidOperationException("MinIO:Endpoint not configured");
    var accessKey = config["MinIO:AccessKey"] ?? throw new InvalidOperationException("MinIO:AccessKey not configured");
    var secretKey = config["MinIO:SecretKey"] ?? throw new InvalidOperationException("MinIO:SecretKey not configured");
    var useSSL = config.GetValue<bool>("MinIO:UseSSL", false);

    // MinIO SDK 6.x/7.x fluent API
    var client = new Minio.MinioClient()
        .WithEndpoint(endpoint)
        .WithCredentials(accessKey, secretKey);
    
    if (useSSL)
    {
        client = client.WithSSL();
    }
    
    return client.Build();
});

// AI Services
builder.Services.AddScoped<Sitim.Core.Services.IModelStorageService, Sitim.Infrastructure.Storage.MinIOModelStorageService>();
builder.Services.AddScoped<IInferenceEngine, OnnxInferenceEngine>();
builder.Services.AddScoped<IAIInferenceService, AIInferenceService>();
builder.Services.AddScoped<IAIModelSelectorService, AIModelSelectorService>();
builder.Services.AddScoped<IFLOrchestrationService, FLOrchestrationService>();

// Orthanc client factory (multi-Orthanc architecture)
// Each institution has its own Orthanc instance, factory creates clients per institution
builder.Services.AddScoped<IOrthancClientFactory, OrthancClientFactory>();

// Legacy: Single OrthancClient (kept for backward compatibility, uses OrthancOptions.BaseUrl)
// TODO: Remove this after migrating all consumers to use IOrthancClientFactory
builder.Services.AddHttpClient<IOrthancClient, OrthancClient>((sp, client) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OrthancOptions>>().Value;
    client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(300); // 5 minutes for large archives
});

// Local cache (PostgreSQL) for Orthanc studies
builder.Services.AddScoped<IStudyCacheService, StudyCacheService>();

var app = builder.Build();

{
    using var scope = app.Services.CreateScope();
    var options = scope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<FederatedLearningOptions>>()
        .Value;
    var logger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("FLStartup");
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    var intervalMinutes = Math.Clamp(options.MonitorIntervalMinutes, 1, 59);
    var cronExpression = intervalMinutes == 1 ? Cron.Minutely() : $"*/{intervalMinutes} * * * *";
    recurringJobs.AddOrUpdate<FLSessionMonitorHangfireJob>(
        recurringJobId: "fl-session-monitor",
        methodCall: job => job.RunAsync(),
        cronExpression: cronExpression);

    try
    {
        var flOrchestrationService = scope.ServiceProvider.GetRequiredService<IFLOrchestrationService>();
        var refreshedCount = await flOrchestrationService.RefreshRunningSessionsAsync(CancellationToken.None);
        logger.LogInformation("FL startup reconciliation refreshed {SessionCount} active session(s).", refreshedCount);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "FL startup reconciliation failed.");
    }
}

// Seed roles + dev admin user (idempotent)
await IdentitySeeder.SeedAsync(app.Services, app.Configuration, CancellationToken.None);
// DB schema is managed via EF Core migrations.
//   dotnet ef database update -p Sitim.Infrastructure -s Sitim.Api

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("SitimWeb");

app.UseAuthentication();

app.UseAuthorization();

app.UseRateLimiter();
if (app.Environment.IsDevelopment())
{
    // Hangfire dashboard (DEV)
    app.UseHangfireDashboard("/hangfire", new Hangfire.DashboardOptions
    {
        Authorization = new[] { new HangfireDashboardAuthFilter() }
    });
}


app.MapControllers();

await app.RunAsync();
