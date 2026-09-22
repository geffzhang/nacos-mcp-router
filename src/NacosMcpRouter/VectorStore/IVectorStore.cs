namespace NacosMcpRouter.VectorStore;

public interface IVectorStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(McpServerDocument document, float[] embedding, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpServerHit>> SearchAsync(float[] queryVector, int k, CancellationToken cancellationToken = default);
    Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
