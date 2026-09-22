using NacosMcpRouter.VectorStore;

namespace NacosMcpRouter.Tests.Mcp;

internal sealed class FakeVectorStoreWithEmbedder : IVectorStore
{
    public List<(McpServerDocument Doc, float[] Embedding)> Docs { get; } = new();

    public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpsertAsync(McpServerDocument document, float[] embedding, CancellationToken cancellationToken = default)
    {
        Docs.RemoveAll(d => d.Doc.Name == document.Name);
        Docs.Add((document, embedding));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Docs.RemoveAll(d => d.Doc.Name == name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<McpServerHit>> SearchAsync(float[] queryVector, int k, CancellationToken cancellationToken = default)
    {
        var hits = Docs.Select(d =>
        {
            var dot = 0f; var na = 0f; var nb = 0f;
            for (int i = 0; i < queryVector.Length; i++)
            {
                dot += queryVector[i] * d.Embedding[i];
                na += queryVector[i] * queryVector[i];
                nb += d.Embedding[i] * d.Embedding[i];
            }
            var cos = dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-8f);
            var dist = 1.0f - cos;
            return new McpServerHit(d.Doc, dist);
        }).OrderBy(h => h.Distance).Take(k).ToList();
        return Task.FromResult<IReadOnlyList<McpServerHit>>(hits);
    }

    public Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Docs.Count == 0);
    public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Docs.Count);
}