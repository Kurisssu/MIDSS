using Midss.Core.Entities;

namespace Midss.Core.Services;

/// <summary>
/// Dedicated model inference engine abstraction.
/// Handles only model execution and raw output parsing.
/// </summary>
public interface IInferenceEngine
{
    Task<InferenceOutput> RunAsync(
        Stream modelStream,
        InferenceInput input,
        AIModel model,
        CancellationToken cancellationToken = default);
}

public sealed record InferenceInput(float[] Values, int[] Shape);

public sealed record InferenceOutput(int PredictionClass, decimal Confidence, float[] Probabilities);
