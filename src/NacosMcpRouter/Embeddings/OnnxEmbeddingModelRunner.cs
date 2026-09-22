using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NacosMcpRouter.Embeddings;

internal sealed class OnnxEmbeddingModelRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _dimensions;
    private readonly string _modelPath;
    private readonly string[] _outputNames;

    public OnnxEmbeddingModelRunner(string modelPath, int dimensions)
    {
        _modelPath = modelPath;
        _dimensions = dimensions;
        _session = new InferenceSession(modelPath);
        InputNames = _session.InputMetadata.Keys.ToArray();
        _outputNames = _session.OutputMetadata.Keys.ToArray();
    }

    public IReadOnlyCollection<string> InputNames { get; }

    public float[] Run(IReadOnlyCollection<NamedOnnxValue> inputs, long[] attentionMask, CancellationToken cancellationToken)
    {
        using var runOptions = new RunOptions();
        using var cancellationRegistration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var results = _session.Run(inputs, _outputNames, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            return ExtractEmbedding(results, attentionMask);
        }
        catch (OnnxRuntimeException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Embedding inference was canceled.", ex, cancellationToken);
        }
    }

    public void Dispose() => _session.Dispose();

    private float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, long[] attentionMask)
    {
        var seenOutputs = new List<string>();

        // First pass: prefer direct embeddings (rank 1 or 2) - no pooling needed.
        var directEmbedding = results
            .Select(result =>
            {
                seenOutputs.Add(DescribeOutput(result));
                return TryGetDirectEmbedding(result, out var embedding) ? embedding : null;
            })
            .FirstOrDefault(static embedding => embedding is not null);
        if (directEmbedding is not null)
        {
            return directEmbedding;
        }

        // Second pass: fall back to mean-pooling over a rank-3 hidden-state tensor.
        var pooledEmbedding = results
            .Where(static result =>
                result.Value is Tensor<float> tensor
                && tensor.Rank == 3
                && tensor.Dimensions[0] == 1)
            .Select(result => TryGetPooledEmbedding(result, attentionMask, out var embedding) ? embedding : null)
            .FirstOrDefault(static embedding => embedding is not null);
        if (pooledEmbedding is not null)
        {
            return pooledEmbedding;
        }

        throw new InvalidOperationException(
            $"Embedding model '{_modelPath}' did not return a supported output tensor. Observed outputs: {string.Join("; ", seenOutputs)}.");
    }

    private static string DescribeOutput(DisposableNamedOnnxValue result)
    {
        if (result.Value is Tensor<float> tensor)
        {
            return $"{result.Name}:Tensor<float>[{string.Join(",", tensor.Dimensions.ToArray())}]";
        }

        return $"{result.Name}:{result.Value.GetType().Name}";
    }

    private bool TryGetDirectEmbedding(DisposableNamedOnnxValue result, out float[] embedding)
    {
        if (result.Value is Tensor<float> floatTensor)
        {
            if (floatTensor.Rank == 1)
            {
                embedding = NormalizeDimensions(floatTensor.ToArray());
                return true;
            }

            if (floatTensor.Rank == 2 && floatTensor.Dimensions[0] == 1)
            {
                embedding = NormalizeDimensions(floatTensor.ToArray());
                return true;
            }
        }
        embedding = [];
        return false;
    }

    private bool TryGetPooledEmbedding(DisposableNamedOnnxValue result, long[] attentionMask, out float[] embedding)
    {
        if (result.Value is not Tensor<float> floatTensor || floatTensor.Rank != 3 || floatTensor.Dimensions[0] != 1)
        {
            embedding = [];
            return false;
        }

        var sequenceLength = floatTensor.Dimensions[1];
        var hiddenSize = floatTensor.Dimensions[2];
        var pooled = new float[hiddenSize];
        var tokenCount = 0f;
        for (var tokenIndex = 0; tokenIndex < sequenceLength && tokenIndex < attentionMask.Length; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }
            tokenCount += 1f;
            for (var featureIndex = 0; featureIndex < hiddenSize; featureIndex++)
            {
                pooled[featureIndex] += floatTensor[0, tokenIndex, featureIndex];
            }
        }
        if (tokenCount <= 0f)
        {
            tokenCount = 1f;
        }
        for (var featureIndex = 0; featureIndex < pooled.Length; featureIndex++)
        {
            pooled[featureIndex] /= tokenCount;
        }
        embedding = NormalizeDimensions(pooled);
        return true;
    }

    private float[] NormalizeDimensions(float[] values)
    {
        if (values.Length == 0)
        {
            throw new InvalidOperationException($"Embedding model '{_modelPath}' returned an empty output tensor.");
        }
        if (values.Length == _dimensions)
        {
            return values;
        }
        throw new InvalidOperationException(
            $"Embedding model '{_modelPath}' returned {values.Length} dimensions but {_dimensions} were configured.");
    }
}
