namespace Sitim.Core.Services
{
    /// <summary>
    /// Provides the current tenant (institution) context for the active request or operation.
    /// In HTTP requests this is populated from JWT claims.
    /// In background jobs (Hangfire) this is null, meaning no scoping is applied.
    /// </summary>
    public interface ITenantContext
    {
        /// <summary>
        /// The current institution's ID. Null for SuperAdmin users and background jobs.
        /// When null, no tenant filter is applied and all records are visible.
        /// </summary>
        Guid? InstitutionId { get; }

        /// <summary>
        /// True when the current user has the SuperAdmin role (platform-level administrator).
        /// SuperAdmin users can see data across all institutions.
        /// </summary>
        bool IsSuperAdmin { get; }
    }
}
