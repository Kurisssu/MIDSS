namespace Sitim.Core.Entities;

/// <summary>
/// Aggregated metrics for one federated learning round.
/// </summary>
public sealed class FLRound
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public FLSession Session { get; set; } = null!;

    public int RoundNumber { get; set; }

    public decimal? AggregatedLoss { get; set; }
    public decimal? AggregatedAccuracy { get; set; }

    /// <summary>
    /// Macro-averaged F1 over all classes, aggregated across clients (weighted by
    /// example count). Mirrors the control plane's <c>aggregated_macro_f1</c>.
    /// </summary>
    public decimal? AggregatedMacroF1 { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
