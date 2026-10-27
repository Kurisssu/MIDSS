namespace Sitim.Core.Contracts.Institutions;

/// <summary>Institution row returned by /api/institutions endpoints.</summary>
public sealed record InstitutionResult(
    Guid Id,
    string Name,
    string Slug,
    string OrthancBaseUrl,
    string? OrthancUsername,
    bool IsActive,
    DateTime CreatedAtUtc);

/// <summary>Body for POST /api/institutions (SuperAdmin only).</summary>
public sealed record CreateInstitutionRequest(
    string Name,
    string Slug,
    string OrthancBaseUrl,
    string? OrthancUsername = null,
    string? OrthancPassword = null);

/// <summary>Body for PUT /api/institutions/{id} (SuperAdmin only).</summary>
public sealed record UpdateInstitutionRequest(
    string Name,
    string OrthancBaseUrl,
    bool IsActive,
    string? OrthancUsername = null,
    string? OrthancPassword = null);
