using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sitim.Core.Entities;
using Sitim.Core.Services;

namespace Sitim.Infrastructure.Services;

public sealed class OnnxInferenceEngine : IInferenceEngine
{
    public async Task<InferenceOutput> RunAsync(
        Stream modelStream,
        InferenceInput input,
        AIModel model,
        CancellationToken cancellationToken = default)
    {
        if (input.Shape.Length == 0)
            throw new InvalidOperationException($"Inference input shape is empty for model '{model.Name}'.");

        var expectedLength = input.Shape.Aggregate(1, (acc, dimension) => acc * dimension);
        if (expectedLength != input.Values.Length)
        {
            throw new InvalidOperationException(
                $"Inference input payload size mismatch for model '{model.Name}'. " +
                $"Expected {expectedLength} values from shape [{string.Join(", ", input.Shape)}], " +
                $"but got {input.Values.Length}.");
        }

        var inputSpecs = OnnxTensorSpecHandler.ParseSpecFromJson(model.OnnxInputSpec);
        var outputSpecs = OnnxTensorSpecHandler.ParseSpecFromJson(model.OnnxOutputSpec);

        using var memoryStream = new MemoryStream();
        await modelStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        using var session = new InferenceSession(memoryStream.ToArray());
        var inputName = session.InputMetadata.Keys.First();
        var inputTensor = new DenseTensor<float>(input.Values, input.Shape);

        if (inputSpecs.Count > 0)
        {
            var inputSpec = inputSpecs.FirstOrDefault(s => s.Name == inputName) ?? inputSpecs[0];
            OnnxTensorSpecHandler.ValidateInputShape(inputTensor, inputSpec, model.Name);
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
        };

        using var results = session.Run(inputs);
        var (predictionClass, probabilities) = OnnxTensorSpecHandler.ExtractOutputs(results, outputSpecs, model.Name);
        var confidence = (decimal)probabilities[predictionClass];

        return new InferenceOutput(predictionClass, confidence, probabilities);
    }
}
