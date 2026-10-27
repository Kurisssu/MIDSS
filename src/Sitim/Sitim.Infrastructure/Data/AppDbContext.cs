using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sitim.Core.Entities;
using Sitim.Core.Services;
using Sitim.Infrastructure.Identity;

namespace Sitim.Infrastructure.Data
{
    public sealed class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
    {
        private readonly ITenantContext _tenantContext;

        public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
            : base(options)
        {
            _tenantContext = tenantContext;
        }

        public DbSet<Institution> Institutions => Set<Institution>();
        public DbSet<Patient> Patients => Set<Patient>();
        public DbSet<ImagingStudy> ImagingStudies => Set<ImagingStudy>();
        public DbSet<ImagingSeries> ImagingSeries => Set<ImagingSeries>();
        public DbSet<AIModel> AIModels => Set<AIModel>();
        public DbSet<AIAnalysisJob> AIAnalysisJobs => Set<AIAnalysisJob>();
        public DbSet<FLSession> FLSessions => Set<FLSession>();
        public DbSet<FLRound> FLRounds => Set<FLRound>();
        public DbSet<FLParticipant> FLParticipants => Set<FLParticipant>();
        public DbSet<FLModelUpdate> FLModelUpdates => Set<FLModelUpdate>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Institution>(b =>
            {
                b.ToTable("institutions");
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(256).IsRequired();
                b.Property(x => x.Slug).HasMaxLength(64).IsRequired();
                b.HasIndex(x => x.Slug).IsUnique();
                b.Property(x => x.OrthancBaseUrl).HasMaxLength(256).IsRequired();
                b.Property(x => x.OrthancUsername).HasMaxLength(128);
                b.Property(x => x.OrthancPassword).HasMaxLength(256);
                b.Property(x => x.IsActive).HasDefaultValue(true);
                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            });

            modelBuilder.Entity<Patient>(b =>
            {
                b.ToTable("patients");
                b.HasKey(x => x.Id);
                b.Property(x => x.PatientId).HasMaxLength(128);
                b.Property(x => x.PatientName).HasMaxLength(256);
                b.HasIndex(x => x.PatientId);
                b.Property(x => x.InstitutionId).HasColumnName("institution_id");
                b.HasIndex(x => x.InstitutionId);
                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
                b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

                // Global Query Filter: scope to current institution.
                // When InstitutionId is null (SuperAdmin / background jobs), all records are visible.
                b.HasQueryFilter(p =>
                    _tenantContext.InstitutionId == null ||
                    p.InstitutionId == _tenantContext.InstitutionId);
            });

            modelBuilder.Entity<ImagingStudy>(b =>
            {
                b.ToTable("imaging_studies");
                b.HasKey(x => x.Id);

                b.Property(x => x.OrthancStudyId).HasColumnName("orthanc_study_id").HasMaxLength(64).IsRequired();
                b.HasIndex(x => x.OrthancStudyId).IsUnique();

                b.Property(x => x.StudyInstanceUid).HasColumnName("study_instance_uid").HasMaxLength(128);
                b.HasIndex(x => x.StudyInstanceUid);

                b.Property(x => x.StudyDate).HasColumnName("study_date").HasMaxLength(16);

                // PostgreSQL text[] mapping via Npgsql
                b.Property(x => x.ModalitiesInStudy).HasColumnName("modalities_in_study");

                b.Property(x => x.InstitutionId).HasColumnName("institution_id");
                b.HasIndex(x => x.InstitutionId);

                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
                b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

                b.HasOne(x => x.Patient)
                    .WithMany(p => p.Studies)
                    .HasForeignKey(x => x.PatientDbId)
                    .OnDelete(DeleteBehavior.SetNull);

                // Global Query Filter
                b.HasQueryFilter(s =>
                    _tenantContext.InstitutionId == null ||
                    s.InstitutionId == _tenantContext.InstitutionId);
            });

            modelBuilder.Entity<ImagingSeries>(b =>
            {
                b.ToTable("imaging_series");
                b.HasKey(x => x.Id);

                b.Property(x => x.OrthancSeriesId).HasColumnName("orthanc_series_id").HasMaxLength(64).IsRequired();
                b.HasIndex(x => new { x.StudyDbId, x.OrthancSeriesId }).IsUnique();

                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

                b.HasOne(x => x.Study)
                    .WithMany(s => s.Series)
                    .HasForeignKey(x => x.StudyDbId)
                    .OnDelete(DeleteBehavior.Cascade);
            });



            modelBuilder.Entity<AIModel>(b =>
            {
                b.ToTable("ai_models");
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(256).IsRequired();
                b.Property(x => x.Task).HasMaxLength(100).IsRequired();
                b.Property(x => x.Version).HasMaxLength(50).IsRequired();
                b.Property(x => x.StorageFileName).HasMaxLength(500).IsRequired();
                b.Property(x => x.Accuracy).HasPrecision(5, 4);
                b.Property(x => x.InputShape).HasMaxLength(100);
                b.Property(x => x.TrainingSource).HasMaxLength(100);
                b.HasIndex(x => new { x.Task, x.IsActive });
            });

            modelBuilder.Entity<AIAnalysisJob>(b =>
            {
                b.ToTable("ai_analysis_jobs");
                b.HasKey(x => x.Id);
                
                // Foreign keys
                b.HasOne(x => x.Study)
                    .WithMany()
                    .HasForeignKey(x => x.StudyId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.Model)
                    .WithMany(m => m.AnalysisJobs)
                    .HasForeignKey(x => x.ModelId)
                    .OnDelete(DeleteBehavior.Restrict);
                
                // Required fields
                b.Property(x => x.PerformedByUserId).IsRequired();
                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
                
                // Hangfire tracking
                b.Property(x => x.HangfireJobId).HasColumnName("hangfire_job_id").HasMaxLength(100);
                b.Property(x => x.Status).HasColumnName("status").HasMaxLength(50).HasDefaultValue("Queued");
                b.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
                b.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");
                b.Property(x => x.PerformedByUserName).HasColumnName("performed_by_user_name").HasMaxLength(256);
                
                // Results (nullable until job completes)
                b.Property(x => x.Confidence).HasPrecision(5, 4);
                b.Property(x => x.ProcessingTimeMs);
                b.Property(x => x.ErrorMessage);
                
                // Indexes for efficient queries
                b.HasIndex(x => x.StudyId);
                b.HasIndex(x => x.HangfireJobId);
                b.HasIndex(x => new { x.Status, x.CreatedAtUtc });
                b.HasIndex(x => x.PerformedByUserId);
            });

            modelBuilder.Entity<FLSession>(b =>
            {
                b.ToTable("fl_sessions");
                b.HasKey(x => x.Id);
                b.Property(x => x.ModelKey).HasColumnName("model_key").HasMaxLength(128).IsRequired();
                b.Property(x => x.Architecture).HasColumnName("architecture").HasMaxLength(64);
                b.Property(x => x.Status).HasColumnName("status").IsRequired();
                b.Property(x => x.TotalRounds).HasColumnName("total_rounds");
                b.Property(x => x.CurrentRound).HasColumnName("current_round");
                b.Property(x => x.ExternalSessionId).HasColumnName("external_session_id").HasMaxLength(128);
                b.Property(x => x.OutputModelPath).HasColumnName("output_model_path").HasMaxLength(512);
                b.Property(x => x.LastError).HasColumnName("last_error");
                b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
                b.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
                b.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");

                b.HasIndex(x => x.Status);
                b.HasIndex(x => x.CreatedAtUtc);
            });

            modelBuilder.Entity<FLRound>(b =>
            {
                b.ToTable("fl_rounds");
                b.HasKey(x => x.Id);

                b.Property(x => x.RoundNumber).HasColumnName("round_number").IsRequired();
                b.Property(x => x.AggregatedLoss).HasColumnName("aggregated_loss").HasPrecision(8, 6);
                b.Property(x => x.AggregatedAccuracy).HasColumnName("aggregated_accuracy").HasPrecision(5, 4);
                b.Property(x => x.AggregatedMacroF1).HasColumnName("aggregated_macro_f1").HasPrecision(5, 4);
                b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");

                b.HasOne(x => x.Session)
                    .WithMany(s => s.Rounds)
                    .HasForeignKey(x => x.SessionId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasIndex(x => new { x.SessionId, x.RoundNumber }).IsUnique();
            });

            modelBuilder.Entity<FLParticipant>(b =>
            {
                b.ToTable("fl_participants");
                b.HasKey(x => x.Id);

                b.Property(x => x.Status).HasColumnName("status").IsRequired();
                b.Property(x => x.LastHeartbeatUtc).HasColumnName("last_heartbeat_utc");
                b.Property(x => x.ClassHistogramJson).HasColumnName("class_histogram_json");

                b.HasOne(x => x.Session)
                    .WithMany(s => s.Participants)
                    .HasForeignKey(x => x.SessionId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.Institution)
                    .WithMany()
                    .HasForeignKey(x => x.InstitutionId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasIndex(x => new { x.SessionId, x.InstitutionId }).IsUnique();
            });

            modelBuilder.Entity<FLModelUpdate>(b =>
            {
                b.ToTable("fl_model_updates");
                b.HasKey(x => x.Id);

                b.Property(x => x.RoundNumber).HasColumnName("round_number").IsRequired();
                b.Property(x => x.TrainingLoss).HasColumnName("training_loss").HasPrecision(8, 6);
                b.Property(x => x.ValidationAccuracy).HasColumnName("validation_accuracy").HasPrecision(5, 4);
                b.Property(x => x.PayloadBytes).HasColumnName("payload_bytes");
                b.Property(x => x.UpdateArtifactPath).HasColumnName("update_artifact_path").HasMaxLength(512);
                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

                b.HasOne(x => x.Session)
                    .WithMany(s => s.ModelUpdates)
                    .HasForeignKey(x => x.SessionId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.Institution)
                    .WithMany()
                    .HasForeignKey(x => x.InstitutionId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasIndex(x => new { x.SessionId, x.InstitutionId, x.RoundNumber });
            });

            modelBuilder.Entity<RefreshToken>(b =>
            {
                b.ToTable("refresh_tokens");
                b.HasKey(x => x.Id);

                b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
                b.HasIndex(x => x.TokenHash).IsUnique();

                b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
                b.HasIndex(x => x.UserId);

                b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
                b.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
                b.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
                b.Property(x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(64);
                b.Property(x => x.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(128);

                b.Property(x => x.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
                b.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(256);

                // Ignore the computed property — EF would try to map it otherwise.
                b.Ignore(x => x.IsActive);
            });
        }
    }
}
