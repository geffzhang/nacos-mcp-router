using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NacosMcpRouter.Configuration;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.Mcp;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.Tests.Embeddings;
using NacosMcpRouter.VectorStore;
using NSubstitute;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class McpRouterToolsTests
{
    private static McpRouterTools CreateTools(
        IVectorStore store,
        IEmbeddingGenerator embedder,
        IMcpProxyRegistry proxy,
        List<McpServerEntry> entries,
        RouterOptions? options = null)
    {
        var registry = Substitute.For<INacosMcpRegistry>();
        registry.SearchByKeywordAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var kw = call.ArgAt<string>(0);
                return entries
                    .Where(s => s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            });
        registry.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var name = call.ArgAt<string>(0);
                return entries.FirstOrDefault(s => s.Name == name);
            });

        return new McpRouterTools(
            registry,
            store,
            embedder,
            proxy,
            Options.Create(options ?? new RouterOptions()),
            NullLogger<McpRouterTools>.Instance);
    }

    [Fact]
    public async Task SearchMcpServer_CombinesKeywordAndVector()
    {
        var store = new FakeVectorStoreWithEmbedder();
        await store.UpsertAsync(new McpServerDocument("alpha", "alpha search", "stdio", "id-a", "1.0.0"), new float[] { 1, 0, 0, 0 }, default);
        await store.UpsertAsync(new McpServerDocument("weather", "weather forecast", "mcp-streamable", "id-w", "1.0.0"), new float[] { 0, 1, 0, 0 }, default);
        var entries = new List<McpServerEntry>
        {
            new McpServerEntry("weather", "weather forecast", new Dictionary<string, object>(), null, "id-w", "1.0.0"),
        };
        var embedder = new FakeEmbeddingGenerator(4);
        var tools = CreateTools(store, embedder, new FakeMcpProxyRegistry(), entries);

        var result = await tools.SearchMcpServer("find me a", "weather", default);
        result.Should().Contain("weather");
        result.Should().Contain("步骤");
    }

    [Fact]
    public async Task SearchMcpServer_IncludesSupportedStdioHits()
    {
        var store = new FakeVectorStoreWithEmbedder();
        await store.UpsertAsync(new McpServerDocument("local", "weather tool local", "stdio", "id-l", "1.0.0"), new float[] { 1, 0, 0, 0 }, default);
        await store.UpsertAsync(new McpServerDocument("weather", "weather forecast", "mcp-streamable", "id-w", "1.0.0"), new float[] { 0, 1, 0, 0 }, default);
        // Query vector is equidistant to both docs so both pass the similarity threshold.
        var embedder = Substitute.For<IEmbeddingGenerator>();
        embedder.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 1, 1, 0, 0 }));
        var tools = CreateTools(store, embedder, new FakeMcpProxyRegistry(), new List<McpServerEntry>());

        var result = await tools.SearchMcpServer("find me a", "weather", default);

        result.Should().Contain("weather").And.Contain("local");
    }

    [Fact]
    public async Task SearchMcpServer_DedupesOverlappingKeywordHits()
    {
        var entries = new List<McpServerEntry>
        {
            new McpServerEntry("weather", "weather forecast", new Dictionary<string, object>(), null, "id-w", "1.0.0"),
        };
        var tools = CreateTools(new FakeVectorStoreWithEmbedder(), new FakeEmbeddingGenerator(4), new FakeMcpProxyRegistry(), entries);

        // both keywords hit the same server; previously threw "same key has already been added"
        var result = await tools.SearchMcpServer("find me a", "wea,weather", default);
        result.Should().Contain("weather").And.NotContain("Error");
    }

    [Fact]
    public async Task AddMcpServer_UnknownName_ReturnsError()
    {
        var tools = CreateTools(new FakeVectorStoreWithEmbedder(), new FakeEmbeddingGenerator(4), new FakeMcpProxyRegistry(), new List<McpServerEntry>());
        var result = await tools.AddMcpServer("ghost", default);
        result.Should().Contain("not found").And.Contain("search_mcp_server");
    }

    [Fact]
    public async Task UseTool_UnknownServer_ReturnsHint()
    {
        var tools = CreateTools(new FakeVectorStoreWithEmbedder(), new FakeEmbeddingGenerator(4), new FakeMcpProxyRegistry(), new List<McpServerEntry>());
        var result = await tools.UseTool("missing", "any_tool", "{}", default);
        result.Should().Contain("not found").And.Contain("search_mcp_server");
    }

    [Fact]
    public async Task UseTool_KnownServer_ConnectsLazily()
    {
        var entries = new List<McpServerEntry>
        {
            new McpServerEntry("weather", "weather forecast", new Dictionary<string, object>(), null, "id-w", "1.0.0"),
        };
        var proxy = new FakeMcpProxyRegistry();
        var tools = CreateTools(new FakeVectorStoreWithEmbedder(), new FakeEmbeddingGenerator(4), proxy, entries);

        var result = await tools.UseTool("weather", "forecast", "{}", default);

        result.Should().Contain("ok");
        proxy.EnsureRequests.Should().ContainSingle().Which.Should().Be("weather");
    }
}