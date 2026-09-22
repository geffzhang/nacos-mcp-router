using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NacosMcpRouter.Configuration;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.Mcp;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.VectorStore;
using RedNb.Nacos.Ai;

namespace NacosMcpRouter.Tests;

public sealed class ProgramCompositionTests
{
    [Fact]
    public async Task BuildServices_ResolvesCoreGraph()
    {
        var services = Program.BuildServices(new RouterOptions(), NullLoggerFactory.Instance);
        // NacosAiClient implements IAsyncDisposable only, so the provider must be disposed asynchronously
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAiService>().Should().NotBeNull();
        provider.GetRequiredService<IMcpProxyClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<IMcpProxyRegistry>().Should().NotBeNull();
        provider.GetRequiredService<IVectorStore>().Should().NotBeNull();
        // The embedding generator loads the model in its constructor, and INacosMcpRegistry /
        // McpRouterTools depend on it, so assert registrations only (real-model construction is
        // covered by the E2E test)
        services.Should().ContainSingle(d => d.ServiceType == typeof(IEmbeddingGenerator));
        services.Should().ContainSingle(d => d.ServiceType == typeof(INacosMcpRegistry));
        services.Should().ContainSingle(d => d.ServiceType == typeof(McpRouterTools));
    }

    [Fact]
    public void DefaultVectorStore_IsSonnetDb()
    {
        Build(new RouterOptions()).GetRequiredService<IVectorStore>().Should().BeOfType<SonnetDbVectorStore>();
    }

    [Fact]
    public void PostgresMode_RegistersPgVectorStore()
    {
        var options = new RouterOptions { VectorStore = VectorStoreKind.Postgres };
        Build(options).GetRequiredService<IVectorStore>().Should().BeOfType<PgVectorStore>();
    }

    private static ServiceProvider Build(RouterOptions options) =>
        Program.BuildServices(options, NullLoggerFactory.Instance).BuildServiceProvider();
}
