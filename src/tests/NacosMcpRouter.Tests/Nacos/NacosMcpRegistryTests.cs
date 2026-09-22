using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NacosMcpRouter.Configuration;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.Tests.Embeddings;
using NacosMcpRouter.VectorStore;
using NSubstitute;
using RedNb.Nacos.Ai;
using RedNb.Nacos.Ai.Models;
using RedNb.Nacos.Ai.Models.Mcp;

namespace NacosMcpRouter.Tests.Nacos;

public sealed class NacosMcpRegistryTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"nacos-mcp-router-reg-{Guid.NewGuid():N}");
    private readonly IAiService _ai = Substitute.For<IAiService>();
    private readonly List<McpServerBasicInfo> _servers = new();

    public NacosMcpRegistryTests()
    {
        _servers.Add(new McpServerBasicInfo
        {
            Name = "weather",
            Description = "weather forecast",
            Protocol = "mcp-streamable",
            Id = "id-weather",
        });
        _servers.Add(new McpServerBasicInfo
        {
            Name = "calendar",
            Description = "calendar MCP",
            Protocol = "stdio",
            Id = "id-calendar",
        });

        _ai.ListMcpServersAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var mcpName = callInfo.ArgAt<string?>(0);
                var pageNo = callInfo.ArgAt<int>(2);
                var pageSize = callInfo.ArgAt<int>(3);
                IEnumerable<McpServerBasicInfo> matched = _servers;
                if (!string.IsNullOrEmpty(mcpName))
                {
                    matched = matched.Where(s => s.Name != null && s.Name.Contains(mcpName, StringComparison.OrdinalIgnoreCase));
                }
                var list = matched.ToList();
                var page = list.Skip((pageNo - 1) * pageSize).Take(pageSize).ToList();
                return Task.FromResult(new PageResult<McpServerBasicInfo>
                {
                    TotalCount = list.Count,
                    PageNumber = pageNo,
                    PageSize = pageSize,
                    PageItems = page,
                });
            });

        _ai.GetMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var name = callInfo.ArgAt<string>(0);
                var basic = _servers.FirstOrDefault(s => s.Name == name);
                return Task.FromResult<McpServerDetailInfo?>(basic is null ? null : ToDetail(basic));
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    private NacosMcpRegistry CreateRegistry(IVectorStore? store = null, IEmbeddingGenerator? embedder = null)
    {
        store ??= new SonnetDbVectorStore(_dataDir, dimensions: 4);
        embedder ??= new FakeEmbeddingGenerator(4);
        return new NacosMcpRegistry(
            _ai,
            store,
            embedder,
            new RouterOptions(),
            NullLogger<NacosMcpRegistry>.Instance);
    }

    [Fact]
    public async Task Backfill_StoresSupportedServers()
    {
        var registry = CreateRegistry();
        await registry.BackfillAsync(CancellationToken.None);
        var store = new SonnetDbVectorStore(_dataDir, dimensions: 4);
        (await store.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task SearchByKeyword_ReturnsMatching()
    {
        var registry = CreateRegistry();
        var hits = await registry.SearchByKeywordAsync("weather", CancellationToken.None);
        hits.Should().HaveCount(1);
        hits[0].Name.Should().Be("weather");
    }

    [Fact]
    public async Task SearchByKeyword_MatchesDescriptionCaseInsensitively()
    {
        var registry = CreateRegistry();
        var hits = await registry.SearchByKeywordAsync("FORE", CancellationToken.None);
        hits.Should().HaveCount(1);
        hits[0].Name.Should().Be("weather");
    }

    [Fact]
    public async Task SearchByKeyword_QueriesFullListFromNacos()
    {
        var registry = CreateRegistry();
        await registry.SearchByKeywordAsync("weather", CancellationToken.None);
        // Nacos has no free-text keyword API; the full list is fetched and matched locally
        await _ai.Received().ListMcpServersAsync(null, "blur", 1, 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchByKeyword_FiltersUnsupportedProtocols_ButKeepsStdio()
    {
        _servers.Add(new McpServerBasicInfo { Name = "sse-only", Description = "uses sse", Protocol = "mcp-sse" });
        _servers.Add(new McpServerBasicInfo { Name = "stdio-only", Description = "uses stdio", Protocol = "stdio" });
        var registry = new NacosMcpRegistry(
            _ai,
            new SonnetDbVectorStore(_dataDir, dimensions: 4),
            new FakeEmbeddingGenerator(4),
            new RouterOptions(),
            NullLogger<NacosMcpRegistry>.Instance);
        var hits = await registry.SearchByKeywordAsync("sse", CancellationToken.None);
        hits.Should().BeEmpty();
        var stdioHits = await registry.SearchByKeywordAsync("stdio", CancellationToken.None);
        stdioHits.Should().ContainSingle().Which.Name.Should().Be("stdio-only");
    }

    [Fact]
    public async Task GetByName_ReturnsEntry()
    {
        var registry = CreateRegistry();
        var entry = await registry.GetByNameAsync("weather", CancellationToken.None);
        entry.Should().NotBeNull();
        entry!.Name.Should().Be("weather");
    }

    [Fact]
    public async Task GetByName_Stdio_MapsLocalServerConfigAsAgentConfig()
    {
        var detail = ToDetail(_servers[0]);
        detail.Protocol = "stdio";
        detail.LocalServerConfig = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["weather"] = new Dictionary<string, object> { ["command"] = "uvx", ["args"] = new[] { "mcp-server-time" } },
            },
        };
        _ai.GetMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<McpServerDetailInfo?>(detail));

        var entry = await CreateRegistry().GetByNameAsync("weather", CancellationToken.None);

        entry.Should().NotBeNull();
        var servers = entry!.AgentConfig.Should().ContainKey("mcpServers").WhoseValue.As<IDictionary<string, object>>();
        servers.Should().ContainKey("weather");
        entry.AgentConfig["protocol"].Should().Be("stdio");
    }

    [Fact]
    public async Task GetByName_Streamable_BuildsUrlFromBackendEndpoint()
    {
        var detail = ToDetail(_servers[1]);
        detail.Protocol = "mcp-streamable";
        detail.BackendEndpoints = new List<McpEndpointInfo>
        {
            new() { Protocol = "https", Address = "mcp.example.cn", Port = 443, Path = "/cal/mcp" },
        };
        detail.RemoteServerConfig = new McpServerRemoteServiceConfig { ExportPath = "/cal/mcp" };
        detail.ToolSpec = new McpToolSpecification
        {
            Tools = new List<McpTool> { new() { Name = "list_events", Description = "list calendar events" } },
            ToolsMeta = new Dictionary<string, McpToolMeta> { ["list_events"] = new() { Enabled = true } },
        };
        _ai.GetMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<McpServerDetailInfo?>(detail));

        var entry = await CreateRegistry().GetByNameAsync("calendar", CancellationToken.None);

        entry.Should().NotBeNull();
        var servers = entry!.AgentConfig.Should().ContainKey("mcpServers").WhoseValue.As<IDictionary<string, object>>();
        var server = servers.Should().ContainKey("calendar").WhoseValue.As<IDictionary<string, object>>();
        server["url"].Should().Be("https://mcp.example.cn:443/cal/mcp");
        server["protocol"].Should().Be("mcp-streamable");
        entry.McpConfigDetail.Should().NotBeNull();
        entry.McpConfigDetail!.ToolSpec!.ToolsMeta.Should().ContainKey("list_events");
    }

    private static McpServerDetailInfo ToDetail(McpServerBasicInfo basic)
    {
        return new McpServerDetailInfo
        {
            Name = basic.Name,
            Id = basic.Id,
            NamespaceId = basic.NamespaceId,
            Protocol = basic.Protocol,
            FrontProtocol = basic.FrontProtocol,
            Description = basic.Description,
            Repository = basic.Repository,
            Packages = basic.Packages,
            Icons = basic.Icons,
            WebsiteUrl = basic.WebsiteUrl,
            Version = basic.Version,
            VersionDetail = basic.VersionDetail,
            RemoteServerConfig = basic.RemoteServerConfig,
            LocalServerConfig = basic.LocalServerConfig,
            Enabled = basic.Enabled,
            Status = basic.Status,
            Capabilities = basic.Capabilities,
        };
    }
}