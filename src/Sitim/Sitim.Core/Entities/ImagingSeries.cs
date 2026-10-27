namespace Sitim.Core.Entities
{
    /// <summary>
    /// Minimal series record (we only keep Orthanc series ids for now).
    /// </summary>
    public sealed class ImagingSeries
    {
        public Guid Id { get; set; }
        public Guid StudyDbId { get; set; }
        public ImagingStudy Study { get; set; } = default!;
        /// <summary>
        /// Orthanc series id (REST id).
        /// </summary>
        public string OrthancSeriesId { get; set; } = default!;
        public DateTime CreatedAtUtc { get; set; }
    }
}
