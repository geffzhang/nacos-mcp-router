namespace NacosMcpRouter.Embeddings;

public interface IEmbeddingGenerator
{
    int Dimensions { get; }

    Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default);

    Task<float[][]> GenerateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
