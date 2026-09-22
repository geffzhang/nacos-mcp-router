using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.Hosting;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.Tests.Embeddings;
using NacosMcpRouter.VectorStore;
using NSubstitute;

namespace NacosMcpRouter.Tests.Hosting;

public sealed class McpRouterHostedServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"nacos-mcp-router-host-{Guid.NewGuid():N}");
    private readonly ServiceProvider _sp;

    public McpRouterHostedServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator>(new FakeEmbeddingGenerator(4));
        services.AddSingleton<IVectorStore>(new SonnetDbVectorStore(_dataDir, dimensions: 4));
        services.AddSingleton<INacosMcpRegistry>(Substitute.For<INacosMcpRegistry>());
        services.AddSingleton<McpRouterHostedService>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task StartAsync_EnablesVectorStoreSchema()
    {
        var svc = _sp.GetRequiredService<McpRouterHostedService>();
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);
        var store = _sp.GetRequiredService<IVectorStore>();
        (await store.IsEmptyAsync()).Should().BeTrue();
    }
}