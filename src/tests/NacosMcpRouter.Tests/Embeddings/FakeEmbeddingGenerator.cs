using NacosMcpRouter.Embeddings;

namespace NacosMcpRouter.Tests.Embeddings;

internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    public FakeEmbeddingGenerator(int dimensions) => Dimensions = dimensions;

    public int Dimensions { get; }

    public Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(new float[Dimensions]);

    public Task<float[][]> GenerateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.Select(_ => new float[Dimensions]).ToArray());
}
