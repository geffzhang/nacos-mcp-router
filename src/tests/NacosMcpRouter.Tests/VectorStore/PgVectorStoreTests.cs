using FluentAssertions;
using NacosMcpRouter.VectorStore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NacosMcpRouter.Tests.VectorStore;

[CollectionDefinition("PgVectorStore", DisableParallelization = true)]
public sealed class PgVectorStoreCollection;

[Collection("PgVectorStore")]
public sealed class PgVectorStoreTests : IDisposable
{
    private sealed class Fixture : IDisposable
    {
        public PostgreSqlContainer Container { get; }
        public string ConnectionString { get; }
        public NpgsqlDataSource DataSource { get; }

        public Fixture()
        {
            Container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
                .Build();
            Container.StartAsync().GetAwaiter().GetResult();
            ConnectionString = Container.GetConnectionString();
            DataSource = NpgsqlDataSource.Create(ConnectionString);
        }

        public async Task ClearAsync()
        {
            await using var cmd = DataSource.CreateCommand("DELETE FROM mcp_servers;");
            await cmd.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            DataSource.Dispose();
            Container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static readonly Lazy<Fixture?> LazyFixture = new(() =>
    {
        try { return new Fixture(); }
        catch { return null; } // Docker unavailable: tests skip below
    });

    private PgVectorStore CreateStore(int dimensions = 4)
    {
        var fixture = LazyFixture.Value ?? throw Xunit.Sdk.SkipException.ForSkip("Docker not available for pgvector Testcontainers");
        return new PgVectorStore(fixture.ConnectionString, dimensions);
    }

    private static async Task ClearAsync()
    {
        var fixture = LazyFixture.Value!;
        // EnsureSchemaAsync is idempotent and creates the table on first use,
        // so clearing before the first schema creation cannot fail
        var store = new PgVectorStore(fixture.ConnectionString, dimensions: 4);
        await store.EnsureSchemaAsync();
        await fixture.ClearAsync();
    }

    public void Dispose() { }

    private static McpServerDocument Doc(string name, string description = "") =>
        new(name, description, "mcp-streamable", "id", "1.0.0");

    [Fact]
    public async Task EnsureSchema_CreatesTableAndIndex()
    {
        var store = CreateStore();
        await store.EnsureSchemaAsync();

        var fixture = LazyFixture.Value!;
        await using (var cmd = fixture.DataSource.CreateCommand("SELECT count(*) FROM pg_indexes WHERE tablename = 'mcp_servers' AND indexname = 'idx_mcp_embedding';"))
        {
            (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(1);
        }
    }

    [Fact]
    public async Task Upsert_Search_ReturnsCosineOrderedHits()
    {
        await ClearAsync();
        var store = CreateStore();
        await store.UpsertAsync(Doc("weather"), new float[] { 1, 0, 0, 0 });
        await store.UpsertAsync(Doc("calendar"), new float[] { 0, 1, 0, 0 });

        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, 2);

        hits.Should().HaveCount(2);
        hits[0].Document.Name.Should().Be("weather");
        hits[0].Distance.Should().BeApproximately(0.0, 1e-4);
        hits[1].Document.Name.Should().Be("calendar");
        hits[1].Distance.Should().BeApproximately(1.0, 1e-4);
    }

    [Fact]
    public async Task Upsert_SameName_UpdatesInPlace()
    {
        await ClearAsync();
        var store = CreateStore();
        await store.UpsertAsync(Doc("weather", "old"), new float[] { 1, 0, 0, 0 });
        await store.UpsertAsync(Doc("weather", "new"), new float[] { 1, 0, 0, 0 });

        (await store.CountAsync()).Should().Be(1);
        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, 1);
        hits.Single().Document.Description.Should().Be("new");
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        await ClearAsync();
        var store = CreateStore();
        await store.UpsertAsync(Doc("weather"), new float[] { 1, 0, 0, 0 });
        await store.UpsertAsync(Doc("calendar"), new float[] { 0, 1, 0, 0 });

        await store.DeleteAsync("weather");

        (await store.CountAsync()).Should().Be(1);
        (await store.IsEmptyAsync()).Should().BeFalse();
        await store.DeleteAsync("calendar");
        (await store.IsEmptyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_WrongDimension_Throws()
    {
        await ClearAsync();
        var store = CreateStore(dimensions: 4);
        await new Func<Task>(() => store.UpsertAsync(Doc("x"), new float[] { 1, 2, 3 }))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Search_EmptyStore_ReturnsEmpty()
    {
        await ClearAsync();
        var store = CreateStore();
        (await store.SearchAsync(new float[] { 1, 0, 0, 0 }, 5)).Should().BeEmpty();
    }
}
