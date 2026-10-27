using Sitim.Core.Models;

namespace Sitim.Core.Services
{
    /// <summary>
    /// Orthanc REST client for DICOM PACS operations.
    /// In multi-Orthanc architecture, each institution has its own dedicated Orthanc instance.
    /// </summary>
    public interface IOrthancClient
    {
        Task<IReadOnlyList<string>> GetStudyIdsAsync(CancellationToken ct);
        Task<OrthancStudyDetails?> GetStudyAsync(string orthancStudyId, CancellationToken ct);
        
        /// <summary>
        /// Upload a single DICOM instance into Orthanc (POST /instances).
        /// </summary>
        Task<OrthancUploadResult> UploadInstanceAsync(Stream dicomStream, CancellationToken ct);

        /// <summary>
        /// Downloads the Orthanc study archive (ZIP) (GET /studies/{id}/archive).
        /// </summary>
        Task DownloadStudyArchiveAsync(string orthancStudyId, Stream destination, CancellationToken ct);

        /// <summary>
        /// Permanently deletes a study from Orthanc (DELETE /studies/{id}).
        /// Returns true if deleted, false if not found.
        /// Throws on other errors.
        /// </summary>
        Task<bool> DeleteStudyAsync(string orthancStudyId, CancellationToken ct);
    }
}
