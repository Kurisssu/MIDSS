using System;
using System.Collections.Generic;
using System.Text;

namespace Sitim.Core.Entities
{
    /// <summary>
    /// A study imported in Orthanc and optionally cached/persisted in our DB.
    /// </summary>
    public sealed class ImagingStudy
    {
        public Guid Id { get; set; }
        /// <summary>
        /// Orthanc internal identifier (REST id).
        /// We use this as the primary "link" to Orthanc.
        /// </summary>
        public string OrthancStudyId { get; set; } = default!;
        /// <summary>
        /// DICOM StudyInstanceUID (0020,000D).
        /// Required for viewer deep-linking (OHIF).
        /// </summary>
        public string? StudyInstanceUid { get; set; }
        /// <summary>
        /// DICOM StudyDate (0008,0020) - usually YYYYMMDD.
        /// Stored as string to avoid date parsing edge cases.
        /// </summary>
        public string? StudyDate { get; set; }
        /// <summary>
        /// Modalities in study, e.g. ["CT"], ["MR"], etc.
        /// Stored as a PostgreSQL text[] array.
        /// </summary>
        public string[] ModalitiesInStudy { get; set; } = Array.Empty<string>();
        public Guid? PatientDbId { get; set; }
        public Patient? Patient { get; set; }

        /// <summary>
        /// Tenant identifier – the institution that owns this study.
        /// Null only for records created before multi-tenancy was introduced.
        /// </summary>
        public Guid? InstitutionId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public List<ImagingSeries> Series { get; set; } = new();
    }
}
