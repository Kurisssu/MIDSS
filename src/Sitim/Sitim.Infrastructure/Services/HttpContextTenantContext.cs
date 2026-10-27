using Microsoft.AspNetCore.Http;
using Sitim.Core.Services;
using System.Security.Claims;

namespace Sitim.Infrastructure.Services
{
    /// <summary>
    /// Reads the tenant context from JWT claims in the current HTTP request.
    /// Registered as Scoped – one instance per request.
    /// </summary>
    public sealed class HttpContextTenantContext : ITenantContext
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpContextTenantContext(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        /// <inheritdoc/>
        public Guid? InstitutionId
        {
            get
            {
                var claim = _httpContextAccessor.HttpContext?.User
                    .FindFirstValue("institution_id");
                return Guid.TryParse(claim, out var id) ? id : null;
            }
        }

        /// <inheritdoc/>
        public bool IsSuperAdmin =>
            _httpContextAccessor.HttpContext?.User
                .IsInRole("SuperAdmin") ?? false;
    }
}
