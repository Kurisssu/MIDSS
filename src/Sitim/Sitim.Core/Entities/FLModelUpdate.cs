namespace Sitim.Core.Entities;

/// <summary>
/// Metadata about one model update uploaded by a participant for a specific round.
/// Stores only metadata, not model weights content.
/// </summary>
public sealed class FLModelUpdate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public FLSession Session { get; set; } = null!;

    public Guid InstitutionId { get; set; }
    public Institution Institution { get; set; } = null!;

    public int RoundNumber { get; set; }

    public decimal? TrainingLoss { get; set; }
    public decimal? ValidationAccuracy { get; set; }

    /// <summary>
    /// Bytes this client transmitted for this round (the model parameters payload).
    /// Source for the federated communication-cost metric.
    /// </summary>
    public long? PayloadBytes { get; set; }

    /// <summary>
    /// Artifact path/key where client update payload is stored (if persisted).
    /// </summary>
    public string? UpdateArtifactPath { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
