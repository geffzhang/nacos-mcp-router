using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NacosMcpRouter.Mcp;
using NacosMcpRouter.Nacos;
using NSubstitute;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class McpProxyRegistryTests
{
    [Fact]
    public async Task EnsureConnected_CachesByName()
    {
        var factory = Substitute.For<IMcpProxyClientFactory>();
        var fakeSession = Substitute.For<IMcpProxySession>();
        var emptyTools = new ValueTask<IReadOnlyList<McpClientTool>>(new List<McpClientTool>());
        fakeSession.ListToolsAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>()).Returns(emptyTools);
        fakeSession.ServerInfo.Returns(new Implementation { Name = "x", Version = "1.0" });

        factory.CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fakeSession));

        var registry = new McpProxyRegistry(factory, NullLogger<McpProxyRegistry>.Instance);
        var entry = new McpServerEntry("a", "alpha", new Dictionary<string, object>(), null, "", "1.0.0");

        var first = await registry.EnsureConnectedAsync(entry);
        var second = await registry.EnsureConnectedAsync(entry);

        first.Should().BeSameAs(second);
        await factory.Received(1).CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsNull()
    {
        var registry = new McpProxyRegistry(Substitute.For<IMcpProxyClientFactory>(), NullLogger<McpProxyRegistry>.Instance);
        registry.TryGet("missing").Should().BeNull();
    }

    [Fact]
    public async Task EnsureConnected_ConcurrentCalls_CreateSessionOnlyOnce()
    {
        var factory = Substitute.For<IMcpProxyClientFactory>();
        var fakeSession = Substitute.For<IMcpProxySession>();
        fakeSession.ListToolsAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<McpClientTool>>(new List<McpClientTool>()));
        fakeSession.ServerInfo.Returns(new Implementation { Name = "x", Version = "1.0" });
        factory.CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(100); return fakeSession; });

        var registry = new McpProxyRegistry(factory, NullLogger<McpProxyRegistry>.Instance);
        var entry = new McpServerEntry("a", "alpha", new Dictionary<string, object>(), null, "", "1.0.0");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => registry.EnsureConnectedAsync(entry)));

        results.Should().OnlyContain(r => ReferenceEquals(r, results[0]));
        await factory.Received(1).CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnected_PingFailure_DisposesAndReconnects()
    {
        var factory = Substitute.For<IMcpProxyClientFactory>();
        var dead = Substitute.For<IMcpProxySession>();
        var fresh = Substitute.For<IMcpProxySession>();
        foreach (var s in new[] { dead, fresh })
        {
            s.ListToolsAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<IReadOnlyList<McpClientTool>>(new List<McpClientTool>()));
            s.ServerInfo.Returns(new Implementation { Name = "x", Version = "1.0" });
        }
        dead.PingAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("connection lost"));
        factory.CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(dead), Task.FromResult(fresh));

        var registry = new McpProxyRegistry(factory, NullLogger<McpProxyRegistry>.Instance);
        var entry = new McpServerEntry("a", "alpha", new Dictionary<string, object>(), null, "", "1.0.0");

        var result = await registry.EnsureConnectedAsync(entry);

        result.Session.Should().BeSameAs(fresh);
        await dead.Received(1).DisposeAsync();
        await factory.Received(2).CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnected_CreateFailure_EvictsAndRetriesOnce()
    {
        var factory = Substitute.For<IMcpProxyClientFactory>();
        var session = Substitute.For<IMcpProxySession>();
        session.ListToolsAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<McpClientTool>>(new List<McpClientTool>()));
        session.ServerInfo.Returns(new Implementation { Name = "x", Version = "1.0" });
        factory.CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("transient failure"),
                _ => Task.FromResult(session));

        var registry = new McpProxyRegistry(factory, NullLogger<McpProxyRegistry>.Instance);
        var entry = new McpServerEntry("a", "alpha", new Dictionary<string, object>(), null, "", "1.0.0");

        var result = await registry.EnsureConnectedAsync(entry);

        result.Session.Should().BeSameAs(session);
        await factory.Received(2).CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnected_CanceledCaller_DoesNotEvictSharedEntry()
    {
        var factory = Substitute.For<IMcpProxyClientFactory>();
        var fakeSession = Substitute.For<IMcpProxySession>();
        fakeSession.ListToolsAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<McpClientTool>>(new List<McpClientTool>()));
        fakeSession.ServerInfo.Returns(new Implementation { Name = "x", Version = "1.0" });
        fakeSession.PingAsync(Arg.Any<RequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => { ((CancellationToken)call[1]).ThrowIfCancellationRequested(); return default; });
        factory.CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(100); return fakeSession; });

        var registry = new McpProxyRegistry(factory, NullLogger<McpProxyRegistry>.Instance);
        var entry = new McpServerEntry("a", "alpha", new Dictionary<string, object>(), null, "", "1.0.0");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = registry.EnsureConnectedAsync(entry, cts.Token);
        var healthy = await registry.EnsureConnectedAsync(entry);

        healthy.Session.Should().BeSameAs(fakeSession);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await factory.Received(1).CreateAsync(Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>());
    }
}