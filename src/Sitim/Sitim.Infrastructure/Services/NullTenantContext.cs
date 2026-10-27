using Sitim.Core.Services;

namespace Sitim.Infrastructure.Services
{
    /// <summary>
    /// No-op tenant context used in background jobs (Hangfire) and EF Core design-time tools.
    /// Returns null InstitutionId, which bypasses all tenant query filters.
    /// </summary>
    public sealed class NullTenantContext : ITenantContext
    {
        public static readonly NullTenantContext Instance = new();

        public Guid? InstitutionId => null;
        public bool IsSuperAdmin => false;
    }
}
