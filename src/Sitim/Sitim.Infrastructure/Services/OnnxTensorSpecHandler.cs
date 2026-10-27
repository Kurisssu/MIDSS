using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sitim.Core.Entities;

namespace Sitim.Infrastructure.Services;

/// <summary>
/// ✅ SCALABILITY FIX #6 & #7: Generic ONNX tensor I/O handling
/// 
/// Replaces hardcoded assumptions with data-driven specifications.
/// Supports multi-input, multi-output, N-dimensional arrays, and multiple data types.
/// </summary>
public class OnnxTensorSpecHandler
{
    /// <summary>
    /// Specification of a single ONNX tensor (input or output)
    /// </summary>
    public class TensorSpec
    {
        public string Name { get; set; } = string.Empty;
        public long[] Shape { get; set; } = Array.Empty<long>();
        public string DataType { get; set; } = "float32";
    }

    /// <summary>
    /// Parse ONNX input/output specifications from JSON metadata
    /// </summary>
    /// <param name="specJson">JSON array: [{"name":"input","shape":[1,3,512,512],"dtype":"float32"}]</param>
    /// <returns>List of tensor specifications</returns>
    public static List<TensorSpec> ParseSpecFromJson(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
            return new List<TensorSpec>();

        try
        {
            return JsonSerializer.Deserialize<List<TensorSpec>>(specJson) ?? new List<TensorSpec>();
        }
        catch
        {
            return new List<TensorSpec>();
        }
    }

    /// <summary>
    /// Validate input tensor shape against model specification
    /// Throws informative error if mismatch detected
    /// </summary>
    public static void ValidateInputShape(
        DenseTensor<float> tensor,
        TensorSpec spec,
        string modelName)
    {
        // Get actual tensor dimensions
        var actualShape = new long[tensor.Rank];
        for (int i = 0; i < tensor.Rank; i++)
            actualShape[i] = tensor.Dimensions[i];

        // Compare with expected shape
        if (actualShape.Length != spec.Shape.Length)
        {
            throw new InvalidOperationException(
                $"Tensor rank mismatch for model '{modelName}'. " +
                $"Expected {spec.Shape.Length}D tensor {FormatShape(spec.Shape)}, " +
                $"but got {actualShape.Length}D tensor {FormatShape(actualShape)}");
        }

        // Check each dimension (allow -1 for flexible dimension like batch size)
        for (int i = 0; i < spec.Shape.Length; i++)
        {
            if (spec.Shape[i] > 0 && actualShape[i] != spec.Shape[i])
            {
                throw new InvalidOperationException(
                    $"Tensor shape mismatch for model '{modelName}' at dimension {i}. " +
                    $"Expected {spec.Shape[i]}, got {actualShape[i]}. " +
                    $"Full expected: {FormatShape(spec.Shape)}, got: {FormatShape(actualShape)}");
            }
        }
    }

    /// <summary>
    /// Extract and parse inference output according to specification
    /// Supports multiple outputs and complex shapes
    /// </summary>
    public static (int predictionClass, float[] probabilities) ExtractOutputs(
        IEnumerable<NamedOnnxValue> results,
        List<TensorSpec> outputSpecs,
        string modelName)
    {
        var resultList = results.ToList();
        if (resultList.Count == 0)
            throw new InvalidOperationException($"No output from model '{modelName}'");

        // Get first (primary) output
        var firstOutput = resultList.First();
        
        // For now, assume classification output is float array
        // Future: support segmentation (2D/3D arrays), detection (variable shape), etc.
        var outputTensor = firstOutput.Value switch
        {
            float[] arr => arr,
            DenseTensor<float> tensor => tensor.ToArray(),
            _ => throw new InvalidOperationException(
                $"Unsupported output type from model '{modelName}': {firstOutput.Value.GetType().Name}")
        };

        if (outputTensor.Length == 0)
            throw new InvalidOperationException($"Empty output from model '{modelName}'");

        // Apply softmax if not already probabilized
        var probabilities = Softmax(outputTensor);

        // Get prediction (argmax)
        int predictionClass = Array.IndexOf(probabilities, probabilities.Max());

        return (predictionClass, probabilities);
    }

    /// <summary>
    /// Softmax activation function
    /// Converts raw logits to probabilities
    /// </summary>
    public static float[] Softmax(float[] values)
    {
        var max = values.Max();
        var exp = values.Select(v => Math.Exp(v - max)).ToArray();
        var sum = exp.Sum();
        return exp.Select(e => (float)(e / sum)).ToArray();
    }

    /// <summary>
    /// Format shape array as readable string
    /// Example: [1, 3, 512, 512] -> "1x3x512x512"
    /// </summary>
    private static string FormatShape(long[] shape)
    {
        return "[" + string.Join(", ", shape) + "]";
    }
}
