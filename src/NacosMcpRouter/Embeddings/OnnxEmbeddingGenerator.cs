using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace NacosMcpRouter.Embeddings;

public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator, IDisposable
{
    private readonly OnnxEmbeddingModelRunner _runner;
    private readonly Tokenizer _tokenizer;
    private readonly string? _workingDirectory;
    private readonly string _inputIdsName;
    private readonly string? _attentionMaskName;
    private readonly string? _tokenTypeIdsName;
    private int _disposed;

    public OnnxEmbeddingGenerator(ModelPaths paths, int dimensions = 384)
    {
        if (!File.Exists(paths.ModelPath))
        {
            throw new FileNotFoundException(
                $"Embedding model not found at '{paths.ModelPath}'. Set EMBEDDING_MODEL_DIR or run scripts/download-model.sh",
                paths.ModelPath);
        }
        if (!File.Exists(paths.TokenizerPath))
        {
            throw new FileNotFoundException(
                $"Tokenizer file not found at '{paths.TokenizerPath}'. Set EMBEDDING_MODEL_DIR or run scripts/download-model.sh",
                paths.TokenizerPath);
        }

        Dimensions = dimensions;
        _runner = new OnnxEmbeddingModelRunner(paths.ModelPath, dimensions);
        try
        {
            (_tokenizer, _workingDirectory) = HuggingFaceTokenizerLoader.Load(paths.TokenizerPath);
            _inputIdsName = FindRequiredInputName(["input_ids", "inputIds", "ids", "input", "tokens", "token_ids"]);
            _attentionMaskName = FindOptionalInputName(["attention_mask", "attentionMask", "mask"]);
            _tokenTypeIdsName = FindOptionalInputName(["token_type_ids", "tokenTypeIds"]);
        }
        catch
        {
            _runner.Dispose();
            TryCleanupWorkingDir();
            throw;
        }
    }

    public int Dimensions { get; }

    public Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Task.Run(() => GenerateCore(text, cancellationToken), cancellationToken);
    }

    public Task<float[][]> GenerateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return Task.Run(async () =>
        {
            var results = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
            {
                results[i] = await Task.Run(() => GenerateCore(texts[i], cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            return results;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _runner.Dispose();
        TryCleanupWorkingDir();
    }

    private float[] GenerateCore(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var tokenIds = _tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true);
        if (tokenIds.Count == 0)
        {
            return new float[Dimensions];
        }

        const int maxTokens = 512;
        var inputTokenIds = tokenIds
            .Take(maxTokens)
            .Select(static id => (long)id)
            .ToArray();
        var attentionMask = Enumerable.Repeat(1L, inputTokenIds.Length).ToArray();
        var tokenTypeIds = new long[inputTokenIds.Length];

        var inputIdsTensor = new DenseTensor<long>(inputTokenIds, [1, inputTokenIds.Length]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, tokenTypeIds.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIdsTensor),
        };
        if (_attentionMaskName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMaskTensor));
        }
        if (_tokenTypeIdsName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, tokenTypeIdsTensor));
        }

        return _runner.Run(inputs, attentionMask, cancellationToken);
    }

    private string FindRequiredInputName(string[] candidates) =>
        FindOptionalInputName(candidates)
            ?? throw new InvalidOperationException("Embedding model does not expose a supported input tensor.");

    private string? FindOptionalInputName(string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = _runner.InputNames.FirstOrDefault(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    private void TryCleanupWorkingDir()
    {
        if (_workingDirectory is null || !Directory.Exists(_workingDirectory))
        {
            return;
        }
        try { Directory.Delete(_workingDirectory, recursive: true); }
        catch (Exception ex) when (IsExpectedCleanupException(ex)) { /* best-effort */ }
    }

    private static bool IsExpectedCleanupException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
