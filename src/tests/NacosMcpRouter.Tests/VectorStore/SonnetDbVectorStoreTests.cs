using FluentAssertions;
using NacosMcpRouter.VectorStore;

namespace NacosMcpRouter.Tests.VectorStore;

public sealed class SonnetDbVectorStoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"nacos-mcp-router-sndb-{Guid.NewGuid():N}");
    private readonly IVectorStore _store;

    public SonnetDbVectorStoreTests()
    {
        _store = new SonnetDbVectorStore(_dataDir, dimensions: 3);
        _store.EnsureSchemaAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        (_store as IDisposable)?.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task IsEmpty_OnFreshStore()
    {
        (await _store.IsEmptyAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_ThenSearch_FindsClosest()
    {
        await _store.UpsertAsync(
            new McpServerDocument("alpha", "alpha", "stdio", "id-a", "1.0.0"),
            new float[] { 1.0f, 0.0f, 0.0f },
            CancellationToken.None);
        await _store.UpsertAsync(
            new McpServerDocument("beta", "beta", "stdio", "id-b", "1.0.0"),
            new float[] { 0.0f, 1.0f, 0.0f },
            CancellationToken.None);

        var hits = await _store.SearchAsync(new float[] { 0.9f, 0.1f, 0.0f }, k: 1, CancellationToken.None);
        hits.Should().HaveCount(1);
        hits[0].Document.Name.Should().Be("alpha");
        hits[0].Distance.Should().BeLessThan(0.1);
    }

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        await _store.UpsertAsync(
            new McpServerDocument("a", "alpha", "stdio", "id-a", "1.0.0"),
            new float[] { 1.0f, 0.0f, 0.0f },
            CancellationToken.None);
        await _store.DeleteAsync("a", CancellationToken.None);
        (await _store.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task EnsureSchema_Idempotent()
    {
        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync(); // should not throw
        (await _store.IsEmptyAsync()).Should().BeTrue();
    }
}
