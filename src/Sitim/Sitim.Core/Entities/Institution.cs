namespace Sitim.Core.Entities
{
    /// <summary>
    /// Represents a medical institution (hospital, clinic) that acts as a tenant in the platform.
    /// All business data (patients, studies, analyses) is scoped to an institution.
    /// Each institution has its own dedicated Orthanc PACS instance.
    /// </summary>
    public sealed class Institution
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Display name, e.g. "Cluj County Clinical Hospital".</summary>
        public string Name { get; set; } = default!;

        /// <summary>URL-safe unique identifier, e.g. "scj-cluj". Used in logging and API paths.</summary>
        public string Slug { get; set; } = default!;

        /// <summary>
        /// Base URL of the dedicated Orthanc PACS instance for this institution.
        /// Example: "http://orthanc-mci:8042" or "https://orthanc-scj-cluj.sitim.local".
        /// Each institution has its own isolated Orthanc instance for data privacy and Federated Learning.
        /// </summary>
        public string OrthancBaseUrl { get; set; } = default!;

        /// <summary>HTTP Basic Auth username for Orthanc. Null if Orthanc allows anonymous access.</summary>
        public string? OrthancUsername { get; set; }

        /// <summary>HTTP Basic Auth password for Orthanc. Null if Orthanc allows anonymous access.</summary>
        public string? OrthancPassword { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
