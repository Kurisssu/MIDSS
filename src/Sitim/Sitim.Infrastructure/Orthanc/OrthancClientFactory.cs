using Microsoft.EntityFrameworkCore;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;

namespace Sitim.Infrastructure.Orthanc
{
    /// <summary>
    /// Factory that creates IOrthancClient instances configured for specific institutions.
    /// Each institution has its own dedicated Orthanc PACS instance with a unique OrthancBaseUrl.
    /// </summary>
    public sealed class OrthancClientFactory : IOrthancClientFactory
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ITenantContext _tenantContext;

        public OrthancClientFactory(
            AppDbContext db,
            IHttpClientFactory httpClientFactory,
            ITenantContext tenantContext)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _tenantContext = tenantContext;
        }

        public async Task<IOrthancClient> CreateClientAsync(Guid institutionId, CancellationToken ct = default)
        {
            var institution = await _db.Institutions
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == institutionId, ct);

            if (institution is null)
                throw new InvalidOperationException($"Institution with ID '{institutionId}' not found.");

            if (string.IsNullOrWhiteSpace(institution.OrthancBaseUrl))
                throw new InvalidOperationException($"Institution '{institution.Name}' does not have OrthancBaseUrl configured.");

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(institution.OrthancBaseUrl.TrimEnd('/') + "/");
            httpClient.Timeout = TimeSpan.FromSeconds(300);

            if (!string.IsNullOrWhiteSpace(institution.OrthancUsername) &&
                !string.IsNullOrWhiteSpace(institution.OrthancPassword))
            {
                var token = Convert.ToBase64String(
                    System.Text.Encoding.ASCII.GetBytes(
                        $"{institution.OrthancUsername}:{institution.OrthancPassword}"));
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }

            return new OrthancClient(httpClient);
        }

        public async Task<IOrthancClient> CreateClientForCurrentTenantAsync(CancellationToken ct = default)
        {
            var institutionId = _tenantContext.InstitutionId;

            if (institutionId is null)
                throw new InvalidOperationException("No tenant context available. Cannot create OrthancClient without institution ID.");

            return await CreateClientAsync(institutionId.Value, ct);
        }
    }
}
