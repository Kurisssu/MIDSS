namespace Sitim.Core.Contracts.Studies;

/// <summary>Result of POST /api/studies/sync-all — how many studies were re-cached from Orthanc.</summary>
public sealed record SyncAllResult(int Synced);

/// <summary>One-time URL returned by /api/studies/{id}/viewer-link — passed to OHIF.</summary>
public sealed record ViewerLinkResult(string Url);

/// <summary>Outcome of POST /api/studies/import (DICOM archive ingest).</summary>
public sealed record ImportResult(
    int UploadedInstances,
    List<string> OrthancStudyIds,
    int SyncedStudies,
    List<string> Errors);
