using Microsoft.EntityFrameworkCore;
using Sitim.Core.Entities;
using Sitim.Core.Models;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Sitim.Infrastructure.Services
{
    public sealed class StudyCacheService : IStudyCacheService
    {
        private readonly AppDbContext _db;
        private readonly IOrthancClientFactory _orthancFactory;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ITenantContext _tenantContext;

        public StudyCacheService(
            AppDbContext db,
            IOrthancClientFactory orthancFactory,
            IServiceScopeFactory scopeFactory,
            ITenantContext tenantContext)
        {
            _db = db;
            _orthancFactory = orthancFactory;
            _scopeFactory = scopeFactory;
            _tenantContext = tenantContext;
        }

        public async Task<IReadOnlyList<StudySummary>> ListLocalAsync(CancellationToken ct)
        {
            var studies = await _db.ImagingStudies
                .AsNoTracking()
                .Include(s => s.Patient)
                .OrderByDescending(s => s.StudyDate)
                .ThenByDescending(s => s.UpdatedAtUtc)
                .ToListAsync(ct);

            return studies.Select(ToSummary).ToList();
        }

        public async Task<StudyDetails?> GetLocalAsync(string orthancStudyId, CancellationToken ct)
        {
            var s = await _db.ImagingStudies
                .AsNoTracking()
                .Include(x => x.Patient)
                .Include(x => x.Series)
                .FirstOrDefaultAsync(x => x.OrthancStudyId == orthancStudyId, ct);

            return s is null ? null : ToDetails(s);
        }

        public async Task<ImagingStudy?> GetStudyEntityAsync(string orthancStudyId, CancellationToken ct)
        {
            var query = _tenantContext.IsSuperAdmin
                ? _db.ImagingStudies.IgnoreQueryFilters()
                : _db.ImagingStudies;

            return await query
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrthancStudyId == orthancStudyId, ct);
        }

        public async Task<StudyDetails?> SyncFromOrthancAsync(string orthancStudyId, CancellationToken ct)
        {
            var (orthanc, institutionId) = await ResolveOrthancContextForStudyAsync(orthancStudyId, ct);
            if (orthanc is null)
                return null;

            return await SyncStudyCoreAsync(orthancStudyId, orthanc, institutionId, ct);
        }

        public async Task<int> SyncAllFromOrthancAsync(CancellationToken ct)
        {
            if (_tenantContext.IsSuperAdmin)
                return await SyncAllInstitutionsAsync(ct);

            // Regular admin: single institution
            if (!_tenantContext.InstitutionId.HasValue)
                return 0;

            var orthanc = await _orthancFactory.CreateClientAsync(_tenantContext.InstitutionId.Value, ct);
            var remoteIds = await orthanc.GetStudyIdsAsync(ct);

            // Remove local studies that disappeared from Orthanc
            var localIds = await _db.ImagingStudies.Select(x => x.OrthancStudyId).ToListAsync(ct);
            var idsToDelete = localIds.Except(remoteIds).ToList();
            if (idsToDelete.Count > 0)
            {
                var studiesToDelete = await _db.ImagingStudies
                    .Where(x => idsToDelete.Contains(x.OrthancStudyId))
                    .ToListAsync(ct);
                _db.ImagingStudies.RemoveRange(studiesToDelete);
                await _db.SaveChangesAsync(ct);
            }

            if (remoteIds.Count == 0) return 0;

            // Parallel sync with bounded concurrency; each scope gets its own DbContext
            var gate = new SemaphoreSlim(3);
            var tasks = remoteIds.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scopedService = scope.ServiceProvider.GetRequiredService<IStudyCacheService>();
                    await scopedService.SyncFromOrthancAsync(id, ct);
                }
                finally { gate.Release(); }
            }).ToArray();

            await Task.WhenAll(tasks);
            return remoteIds.Count;
        }

        public async Task<bool> DeleteStudyAsync(string orthancStudyId, CancellationToken ct)
        {
            // Resolve the study first — we need its InstitutionId to route to the correct Orthanc.
            var study = await _db.ImagingStudies
                .IgnoreQueryFilters()
                .Include(x => x.Patient)
                .FirstOrDefaultAsync(x => x.OrthancStudyId == orthancStudyId, ct);

            var institutionId = _tenantContext.InstitutionId ?? study?.InstitutionId;
            if (institutionId is null)
                return false;

            var orthanc = await _orthancFactory.CreateClientAsync(institutionId.Value, ct);

            // 1. Delete from Orthanc first — if this fails, DB stays consistent.
            var deleted = await orthanc.DeleteStudyAsync(orthancStudyId, ct);
            if (!deleted)
                return false;

            // 2. Remove from local DB.
            if (study is null)
                return true; // Already gone from DB — that's fine.

            var patientDbId = study.PatientDbId;
            _db.ImagingStudies.Remove(study);
            await _db.SaveChangesAsync(ct);

            // 3. Remove patient if it has no remaining studies.
            if (patientDbId.HasValue)
            {
                var hasOtherStudies = await _db.ImagingStudies
                    .IgnoreQueryFilters()
                    .AnyAsync(x => x.PatientDbId == patientDbId, ct);

                if (!hasOtherStudies)
                {
                    var patient = await _db.Patients
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.Id == patientDbId, ct);

                    if (patient is not null)
                    {
                        _db.Patients.Remove(patient);
                        await _db.SaveChangesAsync(ct);
                    }
                }
            }

            return true;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the Orthanc client and institution ID to use for a specific study operation.
        /// For regular admins: uses the JWT-derived institution.
        /// For SuperAdmin: derives the institution from the study's DB record (study must already exist in DB).
        /// </summary>
        private async Task<(IOrthancClient? client, Guid? institutionId)> ResolveOrthancContextForStudyAsync(
            string orthancStudyId, CancellationToken ct)
        {
            if (_tenantContext.InstitutionId.HasValue)
            {
                var client = await _orthancFactory.CreateClientAsync(_tenantContext.InstitutionId.Value, ct);
                return (client, _tenantContext.InstitutionId);
            }

            // SuperAdmin path: look up which institution owns this study
            var institutionId = await _db.ImagingStudies
                .IgnoreQueryFilters()
                .Where(x => x.OrthancStudyId == orthancStudyId)
                .Select(x => x.InstitutionId)
                .FirstOrDefaultAsync(ct);

            if (institutionId is null)
                return (null, null);

            var orthancClient = await _orthancFactory.CreateClientAsync(institutionId.Value, ct);
            return (orthancClient, institutionId);
        }

        /// <summary>
        /// SuperAdmin SyncAll: iterates every active institution and syncs its Orthanc independently.
        /// </summary>
        private async Task<int> SyncAllInstitutionsAsync(CancellationToken ct)
        {
            var institutions = await _db.Institutions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i => i.IsActive && !string.IsNullOrWhiteSpace(i.OrthancBaseUrl))
                .ToListAsync(ct);

            int total = 0;
            foreach (var institution in institutions)
            {
                try
                {
                    var orthanc = await _orthancFactory.CreateClientAsync(institution.Id, ct);
                    var remoteIds = await orthanc.GetStudyIdsAsync(ct);

                    // Remove local studies that disappeared from this institution's Orthanc
                    var localIds = await _db.ImagingStudies
                        .IgnoreQueryFilters()
                        .Where(x => x.InstitutionId == institution.Id)
                        .Select(x => x.OrthancStudyId)
                        .ToListAsync(ct);

                    var toDelete = localIds.Except(remoteIds).ToList();
                    if (toDelete.Count > 0)
                    {
                        var studiesToRemove = await _db.ImagingStudies
                            .IgnoreQueryFilters()
                            .Where(x => x.InstitutionId == institution.Id && toDelete.Contains(x.OrthancStudyId))
                            .ToListAsync(ct);
                        _db.ImagingStudies.RemoveRange(studiesToRemove);
                        await _db.SaveChangesAsync(ct);
                    }

                    // Sync each study sequentially to avoid DbContext concurrency on this scope
                    foreach (var id in remoteIds)
                    {
                        try { await SyncStudyCoreAsync(id, orthanc, institution.Id, ct); }
                        catch { /* Skip individual study failures */ }
                    }

                    total += remoteIds.Count;
                }
                catch { /* Skip institutions whose Orthanc is unreachable */ }
            }

            return total;
        }

        /// <summary>
        /// Core upsert logic: accepts an explicit Orthanc client and institution ID instead of
        /// reading from ITenantContext, so it works for both regular admins and SuperAdmin.
        /// </summary>
        private async Task<StudyDetails?> SyncStudyCoreAsync(
            string orthancStudyId,
            IOrthancClient orthanc,
            Guid? institutionId,
            CancellationToken ct)
        {
            var d = await orthanc.GetStudyAsync(orthancStudyId, ct);

            if (d is null)
            {
                var existing = await _db.ImagingStudies
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.OrthancStudyId == orthancStudyId, ct);

                if (existing is not null)
                {
                    _db.ImagingStudies.Remove(existing);
                    await _db.SaveChangesAsync(ct);
                }
                return null;
            }

            // 1) Patient upsert (IgnoreQueryFilters to avoid cross-tenant duplicates)
            Patient? patient = null;
            if (!string.IsNullOrWhiteSpace(d.PatientId))
            {
                patient = await _db.Patients
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.PatientId == d.PatientId, ct);

                if (patient is null)
                {
                    patient = new Patient
                    {
                        Id = Guid.NewGuid(),
                        PatientId = d.PatientId,
                        PatientName = d.PatientName,
                        InstitutionId = institutionId,
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    _db.Patients.Add(patient);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(d.PatientName) && patient.PatientName != d.PatientName)
                        patient.PatientName = d.PatientName;
                    patient.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            // 2) Study upsert (IgnoreQueryFilters prevents false cross-tenant duplicates)
            var study = await _db.ImagingStudies
                .IgnoreQueryFilters()
                .Include(x => x.Series)
                .FirstOrDefaultAsync(x => x.OrthancStudyId == d.OrthancStudyId, ct);

            if (study is not null
                && study.InstitutionId.HasValue
                && study.InstitutionId != institutionId
                && !_tenantContext.IsSuperAdmin)
            {
                // Study belongs to a different institution — do not touch it.
                return await GetLocalAsync(d.OrthancStudyId, ct);
            }

            if (study is null)
            {
                study = new ImagingStudy
                {
                    Id = Guid.NewGuid(),
                    OrthancStudyId = d.OrthancStudyId,
                    InstitutionId = institutionId,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
                _db.ImagingStudies.Add(study);
            }

            study.StudyInstanceUid = d.StudyInstanceUid;
            study.StudyDate = d.StudyDate;
            study.ModalitiesInStudy = d.ModalitiesInStudy.ToArray();
            study.Patient = patient;
            study.PatientDbId = patient?.Id;
            study.UpdatedAtUtc = DateTime.UtcNow;

            // 3) Series upsert (minimal — only add new series)
            var existingSeriesIds = study.Series.Select(x => x.OrthancSeriesId).ToHashSet(StringComparer.Ordinal);
            foreach (var sid in d.SeriesOrthancIds)
            {
                if (existingSeriesIds.Contains(sid)) continue;
                study.Series.Add(new ImagingSeries
                {
                    Id = Guid.NewGuid(),
                    StudyDbId = study.Id,
                    OrthancSeriesId = sid,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(ct);

            var saved = await _db.ImagingStudies
                .AsNoTracking()
                .Include(x => x.Patient)
                .Include(x => x.Series)
                .FirstAsync(x => x.OrthancStudyId == orthancStudyId, ct);

            return ToDetails(saved);
        }

        // ── Mappers ───────────────────────────────────────────────────────────

        private static StudySummary ToSummary(ImagingStudy s) => new(
            OrthancStudyId: s.OrthancStudyId,
            StudyInstanceUid: s.StudyInstanceUid,
            PatientId: s.Patient?.PatientId,
            PatientName: s.Patient?.PatientName,
            StudyDate: s.StudyDate,
            ModalitiesInStudy: s.ModalitiesInStudy
        );

        private static StudyDetails ToDetails(ImagingStudy s) => new(
            OrthancStudyId: s.OrthancStudyId,
            StudyInstanceUid: s.StudyInstanceUid,
            PatientId: s.Patient?.PatientId,
            PatientName: s.Patient?.PatientName,
            StudyDate: s.StudyDate,
            ModalitiesInStudy: s.ModalitiesInStudy,
            SeriesOrthancIds: s.Series.Select(x => x.OrthancSeriesId).ToList(),
            DbStudyId: s.Id
        );
    }
}
