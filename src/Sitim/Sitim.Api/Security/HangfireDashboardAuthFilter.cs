using Hangfire.Dashboard;

namespace Sitim.Api.Security
{
    /// <summary>
    /// Allows Hangfire dashboard access for:
    /// - Admin users (authenticated, role Admin)
    /// - OR loopback requests (localhost) for development convenience
    /// </summary>
    public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var http = context.GetHttpContext();

            // Allow localhost acces (for dev)
            var remoteIp = http.Connection.RemoteIpAddress;
            if (remoteIp is not null && System.Net.IPAddress.IsLoopback(remoteIp))
                return true;

            // Allow Admin role
            var user = http.User;
            return user?.Identity?.IsAuthenticated == true && user.IsInRole(SitimRoles.Admin) == true;
        }
    }
}
