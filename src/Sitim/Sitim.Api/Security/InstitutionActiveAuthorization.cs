using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sitim.Core.Options;
using Sitim.Infrastructure.Data;

namespace Sitim.Api.Security;

/// <summary>
/// Marker requirement for the "InstitutionMustBeActive" authorization policy.
/// Attached to the global fallback so every authenticated endpoint is gated.
/// </summary>
public sealed class InstitutionActiveRequirement : IAuthorizationRequirement { }

/// <summary>
/// Verifies that the JWT's <c>institution_id</c> claim still points to an active
/// institution. SuperAdmin requests (no <c>institution_id</c> claim) are exempt.
///
/// Uses <see cref="IMemoryCache"/> with a configurable TTL so we don't hit the DB
/// on every single authorized request — admins deactivating an institution see
/// effect within a few seconds, which is good enough for this medical-app context.
/// </summary>
public sealed class InstitutionActiveAuthorizationHandler
    : AuthorizationHandler<InstitutionActiveRequirement>
{
    private const string CacheKeyPrefix = "inst-active:";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly AuthOptions _options;
    private readonly ILogger<InstitutionActiveAuthorizationHandler> _logger;

    public InstitutionActiveAuthorizationHandler(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        IOptions<AuthOptions> options,
        ILogger<InstitutionActiveAuthorizationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        InstitutionActiveRequirement requirement)
    {
        // Anonymous endpoints (Login, Refresh, Logout, set-password) don't carry an
        // identity yet — let them through; their own [AllowAnonymous] gates apply.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Succeed(requirement);
            return;
        }

        var instClaim = context.User.FindFirst("institution_id")?.Value;
        if (string.IsNullOrWhiteSpace(instClaim))
        {
            // SuperAdmin / platform-wide accounts have no institution by design.
            context.Succeed(requirement);
            return;
        }

        if (!Guid.TryParse(instClaim, out var institutionId))
        {
            _logger.LogWarning("Auth JWT has malformed institution_id claim: '{Claim}'", instClaim);
            context.Fail(new AuthorizationFailureReason(this, "institution_claim_malformed"));
            return;
        }

        var ttl = TimeSpan.FromSeconds(Math.Clamp(_options.InstitutionActiveCacheSeconds, 5, 300));
        var key = CacheKeyPrefix + institutionId;

        var isActive = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;

            // Authorization handlers are singletons but AppDbContext is scoped — open a
            // throwaway scope per cache miss so we don't depend on the request's scope.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Institutions
                .AsNoTracking()
                .Where(i => i.Id == institutionId)
                .Select(i => (bool?)i.IsActive)
                .FirstOrDefaultAsync();
        });

        if (isActive is true)
        {
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogInformation(
                "Blocking request: institution {InstitutionId} is deactivated or missing.",
                institutionId);
            context.Fail(new AuthorizationFailureReason(this, "institution_deactivated"));
        }
    }
}
