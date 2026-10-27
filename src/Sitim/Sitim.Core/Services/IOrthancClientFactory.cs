namespace Sitim.Core.Services
{
    /// <summary>
    /// Factory for creating IOrthancClient instances for specific institutions.
    /// In multi-Orthanc architecture, each institution has its own dedicated Orthanc PACS instance.
    /// </summary>
    public interface IOrthancClientFactory
    {
        /// <summary>
        /// Creates an IOrthancClient configured for the specified institution's Orthanc instance.
        /// </summary>
        /// <param name="institutionId">The institution ID</param>
        /// <returns>OrthancClient configured with the institution's OrthancBaseUrl</returns>
        /// <exception cref="InvalidOperationException">If institution not found or OrthancBaseUrl not configured</exception>
        Task<IOrthancClient> CreateClientAsync(Guid institutionId, CancellationToken ct = default);

        /// <summary>
        /// Creates an IOrthancClient for the current tenant's institution (from ITenantContext).
        /// </summary>
        /// <returns>OrthancClient for current tenant's institution</returns>
        /// <exception cref="InvalidOperationException">If no tenant context or institution not found</exception>
        Task<IOrthancClient> CreateClientForCurrentTenantAsync(CancellationToken ct = default);
    }
}
