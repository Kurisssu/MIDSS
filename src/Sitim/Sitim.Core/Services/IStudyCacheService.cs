using Sitim.Core.Entities;
using Sitim.Core.Models;

namespace Sitim.Core.Services
{
    /// <summary>
    /// Keeps a minimal local cache (PostgreSQL) of studies that exist in Orthanc.
    /// This is useful for:
    /// - faster UI lists (no "N calls" to Orthanc each time),
    /// - being able to store our own metadata later (analysis jobs, reports, etc.).
    /// </summary>
    public interface IStudyCacheService
    {
        /// <summary>
        /// Reads from our local DB (does NOT contact Orthanc).
        /// </summary>
        Task<IReadOnlyList<StudySummary>> ListLocalAsync(CancellationToken ct);
        /// <summary>
        /// Reads a single study from our local DB (does NOT contact Orthanc).
        /// </summary>
        Task<StudyDetails?> GetLocalAsync(string orthancStudyId, CancellationToken ct);
        /// <summary>
        /// Gets the study entity (with InstitutionId) from DB.
        /// </summary>
        Task<ImagingStudy?> GetStudyEntityAsync(string orthancStudyId, CancellationToken ct);
        /// <summary>
        /// Fetches details from Orthanc and upserts them into DB, then returns the DB view.
        /// </summary>
        Task<StudyDetails?> SyncFromOrthancAsync(string orthancStudyId, CancellationToken ct);
        /// <summary>
        /// Sync all studies currently present in Orthanc(MVP).
        /// Returns how many studies were synced.
        /// </summary>
        Task<int> SyncAllFromOrthancAsync(CancellationToken ct);

        /// <summary>
        /// Permanently deletes a study from Orthanc and removes it from the local DB.
        /// Also removes the patient record if it has no remaining studies.
        /// Returns true if deleted, false if not found.
        /// </summary>
        Task<bool> DeleteStudyAsync(string orthancStudyId, CancellationToken ct);
    }
}
