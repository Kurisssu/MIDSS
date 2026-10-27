namespace Sitim.Core.Models
{
    /// <summary>
    /// Minimal data we need from Orthanc for MVP: listing studies + building an OHIF deep-link.
    /// </summary>
    public sealed record OrthancStudyDetails(
        string OrthancStudyId,
        string? StudyInstanceUid,
        string? StudyDate,
        string? PatientId,
        string? PatientName,
        IReadOnlyList<string> ModalitiesInStudy,
        IReadOnlyList<string> SeriesOrthancIds
    );

    public sealed record StudySummary(
        string OrthancStudyId,
        string? StudyInstanceUid,
        string? PatientId,
        string? PatientName,
        string? StudyDate,
        IReadOnlyList<string> ModalitiesInStudy
    );

    public sealed record StudyDetails(
        string OrthancStudyId,
        string? StudyInstanceUid,
        string? PatientId,
        string? PatientName,
        string? StudyDate,
        IReadOnlyList<string> ModalitiesInStudy,
        IReadOnlyList<string> SeriesOrthancIds,
        Guid? DbStudyId = null  // Optional - only set when queried from DB
    );
    /// <summary>
    /// Result returned by Orthanc after POST /instances (upload one DICOM instance).
    /// We only keep the fields we need to link the uploaded instance to its parent study.
    /// </summary>
    public sealed record OrthancUploadResult(
        string? Id,
        string? ParentPatient,
        string? ParentStudy,
        string? ParentSeries,
        string? Path,
        string? Status
    );
}