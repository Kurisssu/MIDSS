using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Midss.Infrastructure.Services;

namespace Midss.Infrastructure.Data
{
    /// <summary>
    /// Used by EF Core CLI tools (migrations, scaffolding) at design-time.
    /// Reads ConnectionStrings:Default from the Midss.Api user-secrets (or the
    /// ConnectionStrings__Default environment variable) and uses a NullTenantContext.
    /// </summary>
    public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        // Must match <UserSecretsId> in Midss.Api.csproj.
        private const string ApiUserSecretsId = "efbb775f-7e58-449e-ac3c-9774ed4271b3";

        public AppDbContext CreateDbContext(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddUserSecrets(ApiUserSecretsId)
                .AddEnvironmentVariables()
                .Build();

            var connectionString = configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException(
                    "Missing ConnectionStrings:Default. Set it with 'dotnet user-secrets set \"ConnectionStrings:Default\" \"...\"' " +
                    "in the Midss.Api folder, or with the ConnectionStrings__Default environment variable.");

            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            optionsBuilder.UseNpgsql(connectionString);

            return new AppDbContext(optionsBuilder.Options, NullTenantContext.Instance);
        }
    }
}
