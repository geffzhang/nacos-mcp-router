# NacosMcpRouter 集群化改造 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 NacosMcpRouter 的 streamable_http 模式支持无粘滞的水平扩展集群部署。

**Architecture:** 消除服务内全部跨请求可变状态与协议层会话依赖:代理会话改为惰性连接(并发安全),上游支持 stdio 与 mcp-streamable,入站拒绝旧版 MCP 客户端(强制 2026-07-28 协议)。向量库改为双实现共用 `IVectorStore` 接口:单机环境默认保留 SonnetDB,集群环境通过 `VECTOR_STORE=postgres` 切换为共享 PostgreSQL + pgvector。Embedding 推理仍为本地 ONNX(无状态计算),实现升级为 OpenClaw.Routing.Onnx 版本。

**Tech Stack:** .NET 10、ModelContextProtocol 2.2.0(MCP 2026-07-28 修订)、Npgsql 10.0.3 + pgvector(集群)、SonnetDB 3.1.0(单机默认)、Microsoft.ML.OnnxRuntime 1.27.0、xUnit 2.9.2 + FluentAssertions + NSubstitute + Testcontainers.PostgreSql。

**Spec:** 无独立 spec 文件 —— 本计划即为设计载体(bounded 改动,入站仅 HTTP、下游支持 stdio 与旧版客户端拒绝均为**无条件**行为,不引入开关;向量库保留 SonnetDB 单机默认 + `VECTOR_STORE=postgres` 切换 pgvector 集群实现)。

> Transport note: the historical Task 2 snippets below describe the earlier stdio rejection policy. The current implementation supersedes them: inbound transport is HTTP-only, while downstream `stdio` and `mcp-streamable` are supported.

## Global Constraints

- 目标框架 net10.0;测试框架 xUnit 2.9.2 + FluentAssertions 6.12.2 + NSubstitute 5.1.0。
- 提交信息用 conventional commits,scope 为 `dotnet`,结尾附 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。
- 测试命令(仓库根目录执行): `dotnet test src/dotnet/tests/NacosMcpRouter.Tests`
- 代码注释与提交信息用英文,计划文档与 README_cn.md 用中文。
- 上游协议常量:`McpProtocolVersions.July2026ProtocolVersion == "2026-07-28"`;错误码 `(int)McpErrorCode.UnsupportedProtocolVersion == -32022`;头名 `McpHttpHeaders.ProtocolVersion == "MCP-Protocol-Version"`(已通过反射验证 SDK 2.2.0 实际形状)。

---

### Task 1: McpProxyRegistry 并发安全惰性连接 + use_tool 惰性连接

**Files:**

- Modify: `src/dotnet/src/NacosMcpRouter/Mcp/McpProxyRegistry.cs`(整个文件重写)
- Modify: `src/dotnet/src/NacosMcpRouter/Mcp/McpRouterTools.cs`(仅 UseTool 方法)
- Modify: `src/dotnet/tests/NacosMcpRouter.Tests/Mcp/McpProxyRegistryTests.cs`(追加 4 个测试)
- Modify: `src/dotnet/tests/NacosMcpRouter.Tests/Mcp/McpRouterToolsTests.cs`(更新 UseTool 测试)
- Modify: `src/dotnet/tests/NacosMcpRouter.Tests/Mcp/FakeMcpProxyRegistry.cs`(自动创建会话)

**Interfaces:**

- Consumes: `IMcpProxyClientFactory.CreateAsync(IReadOnlyDictionary<string, object> agentConfig, CancellationToken)`(不变);`IMcpProxySession.PingAsync/ListToolsAsync/DisposeAsync`(不变);`INacosMcpRegistry.GetByNameAsync(string, CancellationToken) → McpServerEntry?`(不变)。
- Produces: `McpProxyRegistry.EnsureConnectedAsync(McpServerEntry, CancellationToken) → Task<McpProxyEntry>`(签名不变,语义变为并发安全 + 惰性);`TryGet(string)` 仅返回已成功建立的缓存条目。

- [x] **Step 1: 写失败测试 —— 并发调用只建一次会话**

在 `McpProxyRegistryTests.cs` 追加:

```csharp
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
```

再在 `McpRouterToolsTests.cs` 把 `UseTool_NoCachedServer_ReturnsHint` 替换为两个测试:

```csharp
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
```

同步更新 `FakeMcpProxyRegistry.cs`(整文件):

```csharp
using ModelContextProtocol;
using NacosMcpRouter.Mcp;
using NacosMcpRouter.Nacos;
using NSubstitute;

namespace NacosMcpRouter.Tests.Mcp;

internal sealed class FakeMcpProxyRegistry : IMcpProxyRegistry
{
    public Dictionary<string, McpProxyEntry> Entries { get; } = new();
    public List<string> EnsureRequests { get; } = new();

    public Task<McpProxyEntry> EnsureConnectedAsync(McpServerEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureRequests.Add(entry.Name);
        if (!Entries.TryGetValue(entry.Name, out var e))
        {
            var session = Substitute.For<IMcpProxySession>();
            session.CallToolAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<CallToolResult>(new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] }));
            e = new McpProxyEntry(entry.Name, session, [], "1.0");
            Entries[entry.Name] = e;
        }
        return Task.FromResult(e);
    }

    public McpProxyEntry? TryGet(string name) => Entries.TryGetValue(name, out var e) ? e : null;

    public Task DisposeAllAsync() { Entries.Clear(); return Task.CompletedTask; }
}
```

注意:`CallToolResult` 与 `TextContentBlock` 在 SDK 2.2.0 中均为无参构造 + 可写 `Content`/`Text` 属性(已反射验证),上述初始化器合法。若编译器报错,以 `~/.nuget/packages/modelcontextprotocol.core/2.2.0` 的 XML 文档为准核对属性名。

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~McpProxyRegistryTests|FullyQualifiedName~McpRouterToolsTests"`
Expected: 新增的并发测试失败(`CreateAsync` 收到 8 次调用或结果不全是同一实例);`UseTool_KnownServer_ConnectsLazily` 失败(现有 FakeMcpProxyRegistry 对未知条目抛异常);`UseTool_UnknownServer_ReturnsHint` 暂可通过。

- [x] **Step 3: 重写 McpProxyRegistry.cs**

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NacosMcpRouter.Nacos;

namespace NacosMcpRouter.Mcp;

public sealed class McpProxyRegistry : IMcpProxyRegistry, IHostedService
{
    private readonly IMcpProxyClientFactory _factory;
    private readonly ILogger<McpProxyRegistry> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpProxyEntry>>> _entries = new();

    public McpProxyRegistry(IMcpProxyClientFactory factory, ILogger<McpProxyRegistry> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public Task<McpProxyEntry> EnsureConnectedAsync(McpServerEntry entry, CancellationToken cancellationToken = default)
        => EnsureConnectedCoreAsync(entry, cancellationToken, allowRetry: true);

    private async Task<McpProxyEntry> EnsureConnectedCoreAsync(McpServerEntry entry, CancellationToken cancellationToken, bool allowRetry)
    {
        // The shared Lazy runs the connect with CancellationToken.None so one caller's
        // cancellation cannot fault the entry used by concurrent callers.
        var lazy = _entries.GetOrAdd(entry.Name, _ => new Lazy<Task<McpProxyEntry>>(
            () => ConnectAsync(entry), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var proxyEntry = await lazy.Value.ConfigureAwait(false);
            await proxyEntry.Session.PingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return proxyEntry;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Connect failed or the cached session died: evict this exact Lazy, dispose the
            // session if one was created, then retry once so the caller gets a fresh connection.
            _entries.TryRemove(KeyValuePair.Create(entry.Name, lazy));
            if (lazy.IsValueCreated && lazy.Value.Status == TaskStatus.RanToCompletion)
            {
                try { await lazy.Value.Result.Session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose proxy session {Name}", entry.Name); }
            }
            if (!allowRetry)
            {
                throw;
            }
            return await EnsureConnectedCoreAsync(entry, cancellationToken, allowRetry: false).ConfigureAwait(false);
        }
    }

    private async Task<McpProxyEntry> ConnectAsync(McpServerEntry entry)
    {
        var session = await _factory.CreateAsync(entry.AgentConfig, CancellationToken.None).ConfigureAwait(false);
        var tools = await session.ListToolsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return new McpProxyEntry(
            Name: entry.Name,
            Session: session,
            Tools: tools,
            Version: session.ServerInfo?.Version ?? "1.0.0");
    }

    public McpProxyEntry? TryGet(string name) =>
        _entries.TryGetValue(name, out var lazy) && lazy.IsValueCreated && lazy.Value.Status == TaskStatus.RanToCompletion
            ? lazy.Value.Result
            : null;

    public async Task DisposeAllAsync()
    {
        var entries = _entries.Values.ToList();
        _entries.Clear();
        foreach (var lazy in entries)
        {
            if (!lazy.IsValueCreated || lazy.Value.Status != TaskStatus.RanToCompletion)
            {
                continue;
            }
            try { await lazy.Value.Result.Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose proxy session {Name}", lazy.Value.Result.Name); }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAllAsync().ConfigureAwait(false);
}
```

- [x] **Step 4: 修改 McpRouterTools.UseTool 为惰性连接**

将 `UseTool` 方法体的开头(从 `var proxy = _proxy.TryGet(mcp_server_name);` 到对应的 `if (proxy is null)` 块)替换为:

```csharp
var entry = await _registry.GetByNameAsync(mcp_server_name, cancellationToken).ConfigureAwait(false);
if (entry is null)
{
    return "mcp server not found, use search_mcp_server to get mcp servers";
}

var proxy = await _proxy.EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false);
```

其余部分(`@params` 解析、`CallToolAsync`、结果拼接、catch)保持不变。方法顶部的 `try` 保持不变。

- [x] **Step 5: 运行测试确认全部通过**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~McpProxyRegistryTests|FullyQualifiedName~McpRouterToolsTests"`
Expected: 全部 PASS。

- [x] **Step 6: 全量测试 + 提交**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests` — 全绿。

```bash
git add src/dotnet/src/NacosMcpRouter/Mcp/McpProxyRegistry.cs src/dotnet/src/NacosMcpRouter/Mcp/McpRouterTools.cs src/dotnet/tests/NacosMcpRouter.Tests/Mcp/
git commit -m "feat(dotnet): make proxy sessions lazy and concurrency-safe

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 拒绝 stdio 上游 MCP 服务器(无条件)

**Files:**

- Modify: `src/dotnet/src/NacosMcpRouter/Nacos/NacosMcpRegistry.cs`(IsSupportedProtocol)
- Modify: `src/dotnet/src/NacosMcpRouter/Mcp/McpProxyClientFactory.cs`(stdio 分支 + 删除 CreateStdioAsync)
- Modify: `src/dotnet/tests/NacosMcpRouter.Tests/Nacos/NacosMcpRegistryTests.cs`
- Create: `src/dotnet/tests/NacosMcpRouter.Tests/Mcp/McpProxyClientFactoryTests.cs`

**Interfaces:**

- Consumes: 无新增。
- Produces: `McpProxyClientFactory.CreateAsync` 对 `protocol == "stdio"` 抛 `NotSupportedException("stdio upstream MCP servers are not supported")`;`NacosMcpRegistry.IsSupportedProtocol` 只接受 `"mcp-streamable"`(search/backfill 由此过滤 stdio)。

- [x] **Step 1: 写失败测试**

`McpProxyClientFactoryTests.cs`(新文件):

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NacosMcpRouter.Mcp;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class McpProxyClientFactoryTests
{
    private readonly McpProxyClientFactory _factory = new(NullLogger<McpProxyClientFactory>.Instance);

    [Fact]
    public async Task Create_StdioProtocol_ThrowsNotSupported()
    {
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["time"] = new Dictionary<string, object>
                {
                    ["protocol"] = "stdio",
                    ["command"] = "uvx",
                    ["args"] = new[] { "mcp-server-time" },
                },
            },
        };

        var act = () => _factory.CreateAsync(config);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*stdio*not supported*");
    }

    [Fact]
    public async Task Create_UnknownProtocol_ThrowsNotSupported()
    {
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["x"] = new Dictionary<string, object> { ["protocol"] = "mcp-sse" },
            },
        };

        await new Func<Task>(() => _factory.CreateAsync(config))
            .Should().ThrowAsync<NotSupportedException>().WithMessage("*mcp-sse*");
    }
}
```

`NacosMcpRegistryTests.cs` 修改三处:
1. `Backfill_StoresAllServers` 期望 `2` 改为 `1`(calendar 是 stdio,不再入库);
2. `SearchByKeyword_MatchesDescriptionCaseInsensitively` 改为针对 mcp-streamable 条目(weather,描述 "weather forecast"):

```csharp
[Fact]
public async Task SearchByKeyword_MatchesDescriptionCaseInsensitively()
{
    var registry = CreateRegistry();
    var hits = await registry.SearchByKeywordAsync("FORE", CancellationToken.None);
    hits.Should().HaveCount(1);
    hits[0].Name.Should().Be("weather");
}
```

3. `SearchByKeyword_FiltersUnsupportedProtocols` 追加 stdio 断言:

```csharp
[Fact]
public async Task SearchByKeyword_FiltersUnsupportedProtocols()
{
    _servers.Add(new McpServerBasicInfo { Name = "sse-only", Description = "uses sse", Protocol = "mcp-sse" });
    _servers.Add(new McpServerBasicInfo { Name = "local-tool", Description = "local stdio tool", Protocol = "stdio" });
    var registry = CreateRegistry();

    var sseHits = await registry.SearchByKeywordAsync("sse", CancellationToken.None);
    var stdioHits = await registry.SearchByKeywordAsync("local", CancellationToken.None);

    sseHits.Should().BeEmpty();
    stdioHits.Should().BeEmpty();
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~McpProxyClientFactoryTests|FullyQualifiedName~NacosMcpRegistryTests"`
Expected: `Create_StdioProtocol_ThrowsNotSupported` 失败(当前会尝试拉起 stdio 进程);`Backfill_StoresAllServers` 失败(当前为 2)。

- [x] **Step 3: 修改 IsSupportedProtocol**

`NacosMcpRegistry.cs` 中:

```csharp
private static bool IsSupportedProtocol(string? protocol) =>
    protocol is "mcp-streamable";
```

(删除 `"stdio"` 分支。注释说明:stdio 上游进程有实例亲和性,集群部署下不可用。)

- [x] **Step 4: 修改 McpProxyClientFactory 拒绝 stdio**

将 `CreateAsync` 中的 switch 改为:

```csharp
var client = protocol switch
{
    "mcp-streamable" => await CreateStreamableHttpAsync(serverConfig, cancellationToken).ConfigureAwait(false),
    "stdio" => throw new NotSupportedException("stdio upstream MCP servers are not supported"),
    "mcp-sse" => throw new NotSupportedException("sse protocol not supported in v1"),
    _ => throw new NotSupportedException($"unknown protocol '{protocol}'"),
};
```

并删除 `CreateStdioAsync` 方法整块(约 17 行,含 `RequireString(cfg, "command")` 等调用 —— 该方法删除后 `RequireString` 仍被 `CreateStreamableHttpAsync` 使用,保留)。

- [x] **Step 5: 运行测试确认通过**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~McpProxyClientFactoryTests|FullyQualifiedName~NacosMcpRegistryTests"`
Expected: 全部 PASS。注意 `GetByName_Stdio_MapsLocalServerConfigAsAgentConfig` 仍然通过 —— `GetByNameAsync` 不做协议过滤,拒绝点统一在 factory(该测试验证的映射行为不变)。

- [x] **Step 6: 全量测试 + 提交**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests` — 全绿。

```bash
git add src/dotnet/src/NacosMcpRouter/Nacos/NacosMcpRegistry.cs src/dotnet/src/NacosMcpRouter/Mcp/McpProxyClientFactory.cs src/dotnet/tests/NacosMcpRouter.Tests/Nacos/NacosMcpRegistryTests.cs src/dotnet/tests/NacosMcpRouter.Tests/Mcp/McpProxyClientFactoryTests.cs
git commit -m "feat(dotnet): reject stdio upstream MCP servers

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 拒绝旧版 MCP 客户端(MCP-Protocol-Version 门禁)

**Files:**

- Create: `src/dotnet/src/NacosMcpRouter/Mcp/McpProtocolVersionGate.cs`
- Modify: `src/dotnet/src/NacosMcpRouter/Program.cs`(RunHttpAsync 注册中间件)
- Create: `src/dotnet/tests/NacosMcpRouter.Tests/Mcp/McpProtocolVersionGateTests.cs`

**Interfaces:**

- Consumes: `McpHttpHeaders.ProtocolVersion`(`"MCP-Protocol-Version"`)、`McpProtocolVersions.July2026ProtocolVersion`(`"2026-07-28"`)、`McpErrorCode.UnsupportedProtocolVersion`(枚举,-32022)。
- 执行修正(反射验证):SDK 2.2.0 中 `McpHttpHeaders` 与 `McpProtocolVersions` 均为 **internal** 类型,跨程序集不可引用 —— 实现与测试改用字面量 `"MCP-Protocol-Version"` / `"2026-07-28"`;`McpErrorCode`(`ModelContextProtocol` 命名空间)为 public,正常引用。
- Produces: `McpProtocolVersionGate.InvokeAsync(HttpContext, RequestDelegate)` — POST `/mcp` 且头缺失或版本早于 2026-07-28 时返回 HTTP 400 + JSON-RPC -32022 错误体;其余请求放行。SDK 2.2.0 默认 `SessionMode=Stateless`(无需额外配置),本门禁补齐 SDK 对"缺失版本头"的向后兼容放行缺口。

- [x] **Step 1: 写失败测试**

`McpProtocolVersionGateTests.cs`(新文件):

```csharp
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using NacosMcpRouter.Mcp;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class McpProtocolVersionGateTests
{
    private static HttpContext CreateContext(string method, string path, string? protocolVersion)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (protocolVersion is not null)
        {
            ctx.Request.Headers[McpHttpHeaders.ProtocolVersion] = protocolVersion;
        }
        return ctx;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Post_MissingVersionHeader_Returns400WithError()
    {
        var ctx = CreateContext("POST", "/mcp", protocolVersion: null);
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        nextInvoked.Should().BeFalse();
        var body = await ReadBodyAsync(ctx);
        body.Should().Contain("-32022").And.Contain("2026-07-28");
    }

    [Fact]
    public async Task Post_PreJuly2026Version_Returns400()
    {
        var ctx = CreateContext("POST", "/mcp", "2025-11-25");

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(ctx)).Should().Contain("-32022");
    }

    [Fact]
    public async Task Post_July2026Version_PassesThrough()
    {
        var ctx = CreateContext("POST", "/mcp", "2026-07-28");
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        nextInvoked.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Get_WithoutHeader_PassesThrough()
    {
        // GET/DELETE 由 SDK 在 stateless 模式下返回 405,门禁只拦 POST
        var ctx = CreateContext("GET", "/mcp", protocolVersion: null);
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task Post_OtherPath_PassesThrough()
    {
        var ctx = CreateContext("POST", "/health", protocolVersion: null);
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        nextInvoked.Should().BeTrue();
    }
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~McpProtocolVersionGateTests"`
Expected: 编译失败(`McpProtocolVersionGate` 不存在)。

- [x] **Step 3: 实现门禁中间件**

`McpProtocolVersionGate.cs`(新文件):

```csharp
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace NacosMcpRouter.Mcp;

/// <summary>
/// Rejects MCP requests that do not declare the 2026-07-28 (or later) protocol revision.
/// Legacy clients rely on the initialize handshake and Mcp-Session-Id, which the server does
/// not serve; rejecting them early keeps the /mcp endpoint stateless for every request.
/// </summary>
public static class McpProtocolVersionGate
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/mcp"))
        {
            var version = context.Request.Headers[McpHttpHeaders.ProtocolVersion].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(version) || string.CompareOrdinal(version, McpProtocolVersions.July2026ProtocolVersion) < 0)
            {
                // Protocol revisions are date strings (YYYY-MM-DD), so ordinal comparison orders them correctly.
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new
                    {
                        code = (int)McpErrorCode.UnsupportedProtocolVersion,
                        message = "Unsupported protocol version",
                        data = new { supported = new[] { McpProtocolVersions.July2026ProtocolVersion } },
                    },
                }).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
```

- [x] **Step 4: 在 RunHttpAsync 注册中间件**

`Program.cs` 的 `RunHttpAsync` 中,在 `app.MapMcp("/mcp");` **之前**插入:

```csharp
app.Use(McpProtocolVersionGate.InvokeAsync);
```

- [x] **Step 5: 运行测试确认通过**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~McpProtocolVersionGateTests"`
Expected: 全部 PASS。

- [x] **Step 6: 手动冒烟(可选但建议)**

```powershell
$env:TRANSPORT_TYPE="streamable_http"; $env:PORT="8000"
dotnet run --project src/dotnet/src/NacosMcpRouter
# 另一终端:
curl.exe -i -X POST http://localhost:8000/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
# 预期 HTTP 400, 响应体含 -32022
```

- [x] **Step 7: 全量测试 + 提交**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests` — 全绿。

```bash
git add src/dotnet/src/NacosMcpRouter/Mcp/McpProtocolVersionGate.cs src/dotnet/src/NacosMcpRouter/Program.cs src/dotnet/tests/NacosMcpRouter.Tests/Mcp/McpProtocolVersionGateTests.cs
git commit -m "feat(dotnet): reject pre-2026-07-28 MCP clients at /mcp

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: /health 探针端点

**Files:**

- Modify: `src/dotnet/src/NacosMcpRouter/Program.cs`(RunHttpAsync)

**Interfaces:**

- Produces: `GET /health` → `200 {"status":"healthy"}`。仅 HTTP 模式挂载(stdio 模式无 HTTP 服务器)。不检查依赖(纯存活探针),K8s readiness 可后续升级。

- [x] **Step 1: 添加端点**

`RunHttpAsync` 中,`app.MapMcp("/mcp");` 之后、`await app.RunAsync(...)` 之前插入:

```csharp
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
```

- [x] **Step 2: 编译验证**

Run: `dotnet build src/dotnet/src/NacosMcpRouter` — 成功。

- [x] **Step 3: 手动冒烟**

```powershell
$env:TRANSPORT_TYPE="streamable_http"; $env:PORT="8000"
dotnet run --project src/dotnet/src/NacosMcpRouter
curl.exe -i http://localhost:8000/health
# 预期: HTTP/1.1 200 OK, 响应体 {"status":"healthy"}
curl.exe -i -X POST http://localhost:8000/health
# 预期: 405(端点只挂 GET;POST 被 Task 3 门禁放行后由路由返回 405)
```

- [x] **Step 4: 全量测试 + 提交**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests` — 全绿。

```bash
git add src/dotnet/src/NacosMcpRouter/Program.cs
git commit -m "feat(dotnet): add /health probe endpoint

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: Embedding 实现升级(移植 OpenClaw.Routing.Onnx)

**Files:**

- Modify: `src/dotnet/src/NacosMcpRouter/Embeddings/OnnxEmbeddingGenerator.cs`
- Modify: `src/dotnet/src/NacosMcpRouter/Embeddings/HuggingFaceTokenizerLoader.cs`
- Modify: `src/dotnet/src/NacosMcpRouter/Embeddings/OnnxEmbeddingModelRunner.cs`
- 参考源(只读): `e:\gitee\kingcrab\src\OpenClaw.Routing.Onnx\LocalOnnxEmbeddingGenerator.cs`

**Interfaces:**

- Consumes: 无新增依赖(OnnxRuntime / Tokenizers 已在 csproj)。
- Produces: `OnnxEmbeddingGenerator(ModelPaths paths, int dimensions = 384)` 实现 `IEmbeddingGenerator`(保留 `Dimensions`、`GenerateAsync`、`GenerateBatchAsync`);构造仍校验 `model.onnx` / `tokenizer.json` 存在并抛带路径的 `FileNotFoundException`(现有测试依赖该消息);推理升级为:取消传播(RunOptions.Terminate)、输出自适应(rank-1/2 直接嵌入 → rank-3 注意力掩码 mean-pooling → 报错)、更完整的 tokenizer 加载(BPE/WordPiece/SEQUENCE 预分词器)。

- [x] **Step 1: 移植 OnnxEmbeddingModelRunner**

以参考源 `OnnxEmbeddingModelRunner`(393-534 行)替换 `OnnxEmbeddingModelRunner.cs` 的实现,保持 `namespace NacosMcpRouter.Embeddings;` 与内部类结构不变。移植要点:

- `Run` 使用 `RunOptions` + `cancellationToken.Register(...Terminate = true)`,取消时抛 `OperationCanceledException`(包裹 `OnnxRuntimeException`)。
- `ExtractEmbedding`:先找 rank 1 或 rank 2(batch=1)的直出张量;找不到再对 rank 3 隐藏状态按 attention mask 做 mean-pooling;都失败时抛包含输出描述的 `InvalidOperationException`。
- `NormalizeDimensions` 保持维度校验错误信息(含 `_modelPath`)。

```csharp
internal sealed class OnnxEmbeddingModelRunner
{
    private readonly InferenceSession _session;
    private readonly int _dimensions;
    private readonly string _modelPath;
    private readonly string[] _outputNames;

    public OnnxEmbeddingModelRunner(string modelPath, int dimensions)
    {
        _modelPath = modelPath;
        _dimensions = dimensions;
        _session = new InferenceSession(modelPath);
        InputNames = _session.InputMetadata.Keys.ToArray();
        _outputNames = _session.OutputMetadata.Keys.ToArray();
    }

    public IReadOnlyCollection<string> InputNames { get; }

    public float[] Run(IReadOnlyCollection<NamedOnnxValue> inputs, long[] attentionMask, CancellationToken cancellationToken)
    {
        using var runOptions = new RunOptions();
        using var cancellationRegistration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var results = _session.Run(inputs, _outputNames, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            return ExtractEmbedding(results, attentionMask);
        }
        catch (OnnxRuntimeException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Embedding inference was canceled.", ex, cancellationToken);
        }
    }

    public void Dispose() => _session.Dispose();

    private float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, long[] attentionMask)
    {
        var seenOutputs = new List<string>();

        var directEmbedding = results
            .Select(result =>
            {
                seenOutputs.Add(DescribeOutput(result));
                return TryGetDirectEmbedding(result, out var embedding) ? embedding : null;
            })
            .FirstOrDefault(static embedding => embedding is not null);
        if (directEmbedding is not null)
        {
            return directEmbedding;
        }

        var pooledEmbedding = results
            .Where(static result =>
                result.Value is Tensor<float> tensor &&
                tensor.Rank == 3 &&
                tensor.Dimensions[0] == 1)
            .Select(result => TryGetPooledEmbedding(result, attentionMask, out var embedding) ? embedding : null)
            .FirstOrDefault(static embedding => embedding is not null);
        if (pooledEmbedding is not null)
        {
            return pooledEmbedding;
        }

        throw new InvalidOperationException(
            $"Embedding model '{_modelPath}' did not return a supported output tensor. Observed outputs: {string.Join("; ", seenOutputs)}.");
    }

    // DescribeOutput / TryGetDirectEmbedding / TryGetPooledEmbedding / NormalizeDimensions
    // 逐行照搬参考源 461-533 行,不改逻辑。
}
```

(上面省略的部分从参考源逐行复制,不改变任何逻辑 —— 参考源即权威实现。)

- [x] **Step 2: 移植 HuggingFaceTokenizerLoader**

以参考源 `HuggingFaceTokenizerLoader`(178-390 行)替换 `HuggingFaceTokenizerLoader.cs` 的实现,保持现有文件的顶层类形式(参考源中是嵌套类,此处保持 `internal static class HuggingFaceTokenizerLoader`)。改动要点:

- 保留 BPE / WordPiece 两条路径,新增 `ResolvePreTokenizer` / `ResolveSequencePreTokenizer`(BYTELEVEL、SEQUENCE 等)。
- **省略参考源中未被任何调用点引用的 `ExtractSpecialTokens`**(参考源自身也是死代码),其余逻辑逐行照搬。

- [x] **Step 3: 移植 OnnxEmbeddingGenerator(保留 IEmbeddingGenerator 契约)**

`OnnxEmbeddingGenerator.cs` 重写为参考源 `LocalOnnxEmbeddingGenerator` 的移植版,差异点:

1. 构造函数签名保持 `public OnnxEmbeddingGenerator(ModelPaths paths, int dimensions = 384)`,并**保留现有文件存在性校验**(现有测试 `Constructor_MissingModelFile_Throws` 断言 `FileNotFoundException` 消息含 "model.onnx"):

```csharp
public OnnxEmbeddingGenerator(ModelPaths paths, int dimensions = 384)
{
    if (!File.Exists(paths.ModelPath))
    {
        throw new FileNotFoundException(
            $"Embedding model not found at '{paths.ModelPath}'. Set EMBEDDING_MODEL_DIR or run scripts/download-model.sh",
            paths.ModelPath);
    }
    if (!File.Exists(paths.TokenizerPath))
    {
        throw new FileNotFoundException(
            $"Tokenizer file not found at '{paths.TokenizerPath}'. Set EMBEDDING_MODEL_DIR or run scripts/download-model.sh",
            paths.TokenizerPath);
    }

    Dimensions = dimensions;
    _runner = new OnnxEmbeddingModelRunner(paths.ModelPath, dimensions);
    try
    {
        (_tokenizer, _workingDirectory) = HuggingFaceTokenizerLoader.Load(paths.TokenizerPath);
        _inputIdsName = FindRequiredInputName(["input_ids", "inputIds", "ids", "input", "tokens", "token_ids"]);
        _attentionMaskName = FindOptionalInputName(["attention_mask", "attentionMask", "mask"]);
        _tokenTypeIdsName = FindOptionalInputName(["token_type_ids", "tokenTypeIds"]);
    }
    catch
    {
        _runner.Dispose();
        TryCleanupWorkingDir();
        throw;
    }
}
```

2. `public int Dimensions { get; }` 属性保留(接口要求)。
3. `GenerateAsync` 返回 `Task<float[]>`:`return Task.Run(() => GenerateCore(text, cancellationToken), cancellationToken);`
4. `GenerateBatchAsync` 保留现有顺序实现(不并发 —— InferenceSession 非线程安全)。
5. `GenerateCore`、`FindRequiredInputName`、`FindOptionalInputName`、`TryCleanupWorkingDir`、`Dispose` 按参考源逻辑移植(`IsExpectedCleanupException` 含 4 种异常)。
6. 内部测试构造器(参考源的 `internal OnnxEmbeddingGenerator(runner, tokenizer, ...)`)在路由端无测试消费,省略。

- [x] **Step 4: 编译 + 全量测试**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests`
Expected: 全绿。`OnnxEmbeddingGeneratorTests.Constructor_MissingModelFile_Throws` 与 `OnnxEmbeddingGeneratorE2ETests` 通过(直出 rank-2 路径对 all-MiniLM-L6-v2 的推理结果与原实现一致)。若 E2E 测试因模型未下载被跳过属正常(测试自带 skip 逻辑)。

- [x] **Step 5: 提交**

```bash
git add src/dotnet/src/NacosMcpRouter/Embeddings/
git commit -m "refactor(dotnet): adopt OpenClaw.Routing.Onnx embedding pipeline

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: 向量库双实现:保留 SonnetDB(单机默认)+ 新增 PostgreSQL pgvector(集群切换)

**Files:**

- Rename: `src/dotnet/src/NacosMcpRouter/VectorStore/ISonnetDbVectorStore.cs` → `IVectorStore.cs`(接口更名 `IVectorStore`)
- Keep: `src/dotnet/src/NacosMcpRouter/VectorStore/SonnetDbVectorStore.cs`(类名与逻辑不变,仅实现接口更名)
- Create: `src/dotnet/src/NacosMcpRouter/VectorStore/PgVectorStore.cs`
- Modify: `src/dotnet/src/NacosMcpRouter/VectorStore/SchemaInitializer.cs`、`Nacos/NacosMcpRegistry.cs`、`Hosting/McpRouterHostedService.cs`、`Mcp/McpRouterTools.cs`、`Program.cs`(接口名 + 存储切换)
- Modify: `src/dotnet/src/NacosMcpRouter/Configuration/RouterOptions.cs`、`Configuration/EnvConfigurator.cs`
- Modify: `src/dotnet/src/NacosMcpRouter/NacosMcpRouter.csproj`(保留 SonnetDB,加 Npgsql)
- Modify: `src/dotnet/tests/NacosMcpRouter.Tests/NacosMcpRouter.Tests.csproj`(加 Testcontainers.PostgreSql)
- Keep: `src/dotnet/tests/NacosMcpRouter.Tests/VectorStore/SonnetDbVectorStoreTests.cs`(单机路径回归,不删)
- Create: `src/dotnet/tests/NacosMcpRouter.Tests/VectorStore/PgVectorStoreTests.cs`
- Modify: `src/dotnet/tests/NacosMcpRouter.Tests/Mcp/FakeVectorStoreWithEmbedder.cs`、`Mcp/McpRouterToolsTests.cs`、`Configuration/EnvConfiguratorTests.cs`、`Hosting/McpRouterHostedServiceTests.cs`、`ProgramCompositionTests.cs`(接口更名 + 切换测试)

**Interfaces:**

- Consumes: `NpgsqlDataSource`(Npgsql 10.0.3)、`NpgsqlDbType.Vector`(float[] 直接映射)。
- Produces: `IVectorStore`(方法签名与 `ISonnetDbVectorStore` 完全一致,仅更名);`PgVectorStore(NpgsqlDataSource dataSource, int dimensions = 384)`;`RouterOptions.VectorStore: VectorStoreKind`(`SonnetDb` 默认 | `Postgres`);`RouterOptions.PostgresConnectionString`;`SonnetDbDataDir` 保留。
- pgvector 表结构 `mcp_servers(name text PK, description text, protocol text, mcp_id text, version text, embedding vector(<dims>))` + HNSW 余弦索引 `idx_mcp_embedding`;upsert 用 `ON CONFLICT (name) DO UPDATE`(原子)。两种实现距离语义一致(余弦距离),`McpRouterTools` 的 `1.0 - SearchMinSimilarity` 阈值不变。

- [x] **Step 1: csproj 依赖调整**

`NacosMcpRouter.csproj` 的 ItemGroup **追加**(SonnetDB 包保留不动):

```xml
<PackageReference Include="Npgsql" Version="10.0.3" />
```

`NacosMcpRouter.Tests.csproj` 的 ItemGroup 追加:

```xml
<PackageReference Include="Testcontainers.PostgreSql" Version="4.5.0" />
```

- [x] **Step 2: 配置层改造**

`RouterOptions.cs`:**保留** `SonnetDbDataDir` 属性,在其后追加:

```csharp
public VectorStoreKind VectorStore { get; init; } = VectorStoreKind.SonnetDb;

public string PostgresConnectionString { get; init; } =
    "Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres";
```

并在同文件(命名空间内、类外)追加枚举:

```csharp
public enum VectorStoreKind
{
    SonnetDb,
    Postgres,
}
```

`EnvConfigurator.cs`:保留 `SONNETDB_DATA_DIR` 行,在其后追加:

```csharp
VectorStore = ParseVectorStore(ReadString("VECTOR_STORE")),
PostgresConnectionString = ReadString("POSTGRES_CONNECTION_STRING")
    ?? "Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres",
```

并追加解析方法:

```csharp
private static VectorStoreKind ParseVectorStore(string? raw) => raw?.Trim().ToLowerInvariant() switch
{
    null or "" or "sonnetdb" => VectorStoreKind.SonnetDb,
    "postgres" or "postgresql" => VectorStoreKind.Postgres,
    _ => throw new InvalidOperationException(
        $"Unknown VECTOR_STORE '{raw}'. Valid values: sonnetdb (default), postgres"),
};
```

`EnvConfiguratorTests.cs`:把 `VECTOR_STORE` 与 `POSTGRES_CONNECTION_STRING` 加入保存/恢复列表(照文件内现有 `_savedAddr` 模式),新增:

```csharp
[Fact]
public void DefaultVectorStore_IsSonnetDb()
{
    Environment.SetEnvironmentVariable("VECTOR_STORE", null);
    EnvConfigurator.LoadFromEnvironment().VectorStore.Should().Be(VectorStoreKind.SonnetDb);
}

[Fact]
public void ReadsVectorStorePostgres()
{
    Environment.SetEnvironmentVariable("VECTOR_STORE", "postgres");
    EnvConfigurator.LoadFromEnvironment().VectorStore.Should().Be(VectorStoreKind.Postgres);
}

[Fact]
public void UnknownVectorStore_Throws()
{
    Environment.SetEnvironmentVariable("VECTOR_STORE", "redis");
    new Action(() => EnvConfigurator.LoadFromEnvironment()).Should().Throw<InvalidOperationException>();
}

[Fact]
public void ReadsPostgresConnectionString()
{
    Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", "Host=pg.example;Port=5432;Database=router;Username=u;Password=p");
    EnvConfigurator.LoadFromEnvironment().PostgresConnectionString.Should().Be("Host=pg.example;Port=5432;Database=router;Username=u;Password=p");
}

[Fact]
public void DefaultPostgresConnectionString_WhenUnset()
{
    Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", null);
    EnvConfigurator.LoadFromEnvironment().PostgresConnectionString.Should().Contain("Database=mcp_router");
}
```

- [x] **Step 3: 接口更名 IVectorStore**

`ISonnetDbVectorStore.cs` 重命名为 `IVectorStore.cs`,内容:

```csharp
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
```

机械替换以下文件中的 `ISonnetDbVectorStore` → `IVectorStore`(类型引用与构造参数名):
`SonnetDbVectorStore.cs`(类声明)、`SchemaInitializer.cs`、`NacosMcpRegistry.cs`、`McpRouterHostedService.cs`、`McpRouterTools.cs`、`Program.cs`、`FakeVectorStoreWithEmbedder.cs`、`McpRouterHostedServiceTests.cs`、`ProgramCompositionTests.cs`。`NacosMcpRegistryTests.cs` 继续使用真实 `SonnetDbVectorStore`,无需改动。

- [x] **Step 4: 实现 PgVectorStore**

> **执行修正(Npgsql 10 API,已按实际实现调整):** Npgsql 9+ 主程序集已移除内置 pgvector 支持,`NpgsqlDbType.Vector` 不存在(8.0.7/9.0.4/10.0.3 反射验证均无该枚举成员),且 NuGet 上不存在 `Npgsql.PgVector` 包。正确做法是引用 `Pgvector` 0.3.2 包(pgvector-dotnet):构造函数接收**连接字符串**并用 `new NpgsqlDataSourceBuilder(connString).UseVector().Build()` 构建 data source,参数直接传 `new Pgvector.Vector(float[])`(由 CLR 类型推断 pgtype)。`Program.cs` 相应改为 `new PgVectorStore(options.PostgresConnectionString, dimensions: 384)`。下方代码块保留原计划内容供对照,实际实现见已落盘的 `PgVectorStore.cs`。

`PgVectorStore.cs`(新文件):

```csharp
using Npgsql;
using NpgsqlTypes;

namespace NacosMcpRouter.VectorStore;

/// <summary>
/// PostgreSQL-backed vector store using the pgvector extension. Shared by every router
/// instance so search results are identical across the cluster (no per-instance state).
/// </summary>
public sealed class PgVectorStore : IVectorStore, IDisposable
{
    private const string TableName = "mcp_servers";
    private const string IndexName = "idx_mcp_embedding";

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _dimensions;
    private int _schemaEnsured;

    public PgVectorStore(NpgsqlDataSource dataSource, int dimensions = 384)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (dimensions < 1) throw new ArgumentOutOfRangeException(nameof(dimensions));
        _dataSource = dataSource;
        _dimensions = dimensions;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _schemaEnsured, 1) != 0) return;

        await ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                name text PRIMARY KEY,
                description text NOT NULL,
                protocol text NOT NULL,
                mcp_id text NOT NULL,
                version text NOT NULL,
                embedding vector({_dimensions}) NOT NULL
            );
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            $"CREATE INDEX IF NOT EXISTS {IndexName} ON {TableName} USING hnsw (embedding vector_cosine_ops);",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(McpServerDocument document, float[] embedding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length != _dimensions)
        {
            throw new ArgumentException($"embedding length {embedding.Length} != configured {_dimensions}", nameof(embedding));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync($"""
            INSERT INTO {TableName} (name, description, protocol, mcp_id, version, embedding)
            VALUES (@name, @description, @protocol, @mcp_id, @version, @embedding)
            ON CONFLICT (name) DO UPDATE SET
                description = EXCLUDED.description,
                protocol = EXCLUDED.protocol,
                mcp_id = EXCLUDED.mcp_id,
                version = EXCLUDED.version,
                embedding = EXCLUDED.embedding;
            """, cancellationToken,
            ("@name", document.Name, null),
            ("@description", document.Description, null),
            ("@protocol", document.Protocol, null),
            ("@mcp_id", document.McpId, null),
            ("@version", document.Version, null),
            ("@embedding", embedding, NpgsqlDbType.Vector)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync($"DELETE FROM {TableName} WHERE name = @name;", cancellationToken, ("@name", name, null)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpServerHit>> SearchAsync(float[] queryVector, int k, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        if (queryVector.Length != _dimensions)
        {
            throw new ArgumentException($"query length {queryVector.Length} != configured {_dimensions}", nameof(queryVector));
        }
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = _dataSource.CreateCommand(
            $"SELECT name, description, protocol, mcp_id, version, embedding <=> @query AS distance " +
            $"FROM {TableName} ORDER BY distance LIMIT @k;");
        cmd.Parameters.AddWithValue("@query", queryVector, NpgsqlDbType.Vector);
        cmd.Parameters.AddWithValue("@k", k);

        var hits = new List<McpServerHit>(k);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var doc = new McpServerDocument(
                Name: reader.GetString(0),
                Description: reader.GetString(1),
                Protocol: reader.GetString(2),
                McpId: reader.GetString(3),
                Version: reader.GetString(4));
            var distance = reader.IsDBNull(5) ? double.NaN : reader.GetDouble(5);
            hits.Add(new McpServerHit(doc, distance));
        }
        return hits;
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        return await CountAsync(cancellationToken).ConfigureAwait(false) == 0;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _dataSource.CreateCommand($"SELECT count(*) FROM {TableName};");
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    public void Dispose() => _dataSource.Dispose();

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value, NpgsqlDbType? DbType)[] parameters)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value, dbType) in parameters)
        {
            if (dbType is { } t)
            {
                cmd.Parameters.AddWithValue(name, t, value);
            }
            else
            {
                cmd.Parameters.AddWithValue(name, value);
            }
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

注意:`ExecuteAsync` 的元组参数第三项为可空 `NpgsqlDbType?`,普通参数调用写 `("@name", document.Name, null)`。

- [x] **Step 5: DI 切换**

`Program.cs` `BuildServices` 中,把 `ISonnetDbVectorStore` 注册行替换为:

```csharp
services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(new ModelPaths(options.EmbeddingModelDir)));
services.AddSingleton<IVectorStore>(_ => options.VectorStore switch
{
    VectorStoreKind.Postgres => new PgVectorStore(
        NpgsqlDataSource.Create(options.PostgresConnectionString), dimensions: 384),
    _ => new SonnetDbVectorStore(options.SonnetDbDataDir, dimensions: 384),
});
```

文件顶部加 `using Npgsql;`。

`ProgramCompositionTests.cs` 把 `ISonnetDbVectorStore` 断言改为 `IVectorStore`,并追加:

```csharp
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
```

(`Build` 为文件内现有辅助方法;`NpgsqlDataSource.Create` 不建立实际连接,无数据库也能通过。)

- [x] **Step 6: 搜索时过滤非 mcp-streamable 命中**

`McpRouterTools.cs` 的 `SearchMcpServer` 中,遍历向量命中 `hits` 的循环开头插入:

```csharp
if (hit.Document.Protocol != "mcp-streamable") continue; // stdio upstreams are rejected unconditionally
```

(兜底:旧 `.data` 目录中可能残留 stdio 索引;关键词路径已由 `IsSupportedProtocol` 过滤。)

`McpRouterToolsTests.cs` 追加(执行修正:`FakeEmbeddingGenerator` 返回零向量,使所有命中的余弦距离为 1.0、超过 0.8 阈值被全部过滤;改用 NSubstitute 返回 `[1,1,0,0]` 使两个文档都通过阈值,协议过滤成为唯一排除因素):

```csharp
[Fact]
public async Task SearchMcpServer_ExcludesUnsupportedProtocolHits()
{
    var store = new FakeVectorStoreWithEmbedder();
    await store.UpsertAsync(new McpServerDocument("local", "weather tool local", "stdio", "id-l", "1.0.0"), new float[] { 1, 0, 0, 0 }, default);
    await store.UpsertAsync(new McpServerDocument("weather", "weather forecast", "mcp-streamable", "id-w", "1.0.0"), new float[] { 0, 1, 0, 0 }, default);
    var embedder = Substitute.For<IEmbeddingGenerator>();
    embedder.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new float[] { 1, 1, 0, 0 }));
    var tools = CreateTools(store, embedder, new FakeMcpProxyRegistry(), new List<McpServerEntry>());

    var result = await tools.SearchMcpServer("find me a", "weather", default);

    result.Should().Contain("weather").And.NotContain("local");
}
```

- [x] **Step 7: 写 PgVectorStoreTests(Testcontainers)**

`PgVectorStoreTests.cs`(新文件):

```csharp
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
        public NpgsqlDataSource DataSource { get; }

        public Fixture()
        {
            Container = new PostgreSqlBuilder()
                .WithImage("pgvector/pgvector:pg17")
                .Build();
            Container.StartAsync().GetAwaiter().GetResult();
            DataSource = NpgsqlDataSource.Create(Container.GetConnectionString());
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
        var fixture = LazyFixture.Value ?? throw Xunit.Sdk.SkipException.ForSkip("Docker not available for pgvector Testcontainers"); // 执行修正:xUnit v2 的静态工厂是 ForSkip(v3 才是 For)
        return new PgVectorStore(fixture.DataSource, dimensions);
    }

    private static async Task ClearAsync()
    {
        var fixture = LazyFixture.Value!;
        await fixture.ClearAsync();
    }

    // 执行修正:首个测试运行时 mcp_servers 表尚不存在,直接 DELETE 会抛 relation does not exist;
    // 实际实现先经 store.EnsureSchemaAsync()(幂等)建表再清理,构造函数改收连接字符串。计划中其余测试体不变。

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
```

- [x] **Step 8: 运行测试确认通过**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests --filter "FullyQualifiedName~PgVectorStoreTests|FullyQualifiedName~EnvConfiguratorTests|FullyQualifiedName~SonnetDbVectorStoreTests|FullyQualifiedName~ProgramCompositionTests|FullyQualifiedName~McpRouterToolsTests"`
Expected: Docker 可用时 PgVectorStore 测试全 PASS(镜像 `pgvector/pgvector:pg17` 首次拉取需几分钟);Docker 不可用时显示 Skip。SonnetDbVectorStoreTests(单机路径回归)与其余全部 PASS。

- [x] **Step 9: 全量测试 + 提交**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests` — 全绿(Skip 可接受)。

```bash
git add -A src/dotnet
git commit -m "feat(dotnet): add pgvector store for cluster mode alongside SonnetDB

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 7: 文档更新

**Files:**

- Modify: `src/dotnet/README.md`
- Modify: `src/dotnet/README_cn.md`
- 检查:`src/dotnet/Dockerfile` 与 `src/dotnet/scripts/` 中残留的 SonnetDB/`.data` 引用。

**Interfaces:**

- Consumes: 无。Produces: 文档与实现一致。

- [x] **Step 1: 检索文档引用**

Run: `rg -n -i "sonnetdb|SONNETDB|\.data" src/dotnet/README.md src/dotnet/README_cn.md src/dotnet/Dockerfile src/dotnet/scripts`
Expected: 代码中保留 SonnetDB 属正常(单机实现);本任务只修正文档 —— Dockerfile/scripts 若引用 `.data` 挂载或 SonnetDB 说明,补充 `VECTOR_STORE` 切换说明(单机模式继续挂载 `.data` 目录)。

- [x] **Step 2: 更新 README_cn.md**

环境变量表(约 65-71 行):保留 `SONNETDB_DATA_DIR` 行并追加两行:

```markdown
| `SONNETDB_DATA_DIR` | 单机模式向量库数据目录(`VECTOR_STORE=sonnetdb`), 默认 `./.data/mcp-router` |
| `VECTOR_STORE` | 向量库实现: `sonnetdb`(默认,单机)或 `postgres`(集群,需 pgvector 扩展) |
| `POSTGRES_CONNECTION_STRING` | 集群模式 PostgreSQL 连接串, 默认 `Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres` |
```

在 streamable_http 小节(24-25 行代码块后)追加一段:

```markdown
集群部署说明:

- `/mcp` 端点仅接受 MCP 2026-07-28 及之后的客户端(旧版客户端返回 400 / -32022)。
- 上游 MCP 服务器仅支持 `mcp-streamable` 协议,stdio 上游已被拒绝。
- 向量库默认 SonnetDB(单机,数据在 `SONNETDB_DATA_DIR`);集群部署设 `VECTOR_STORE=postgres`,多实例共用同一 PostgreSQL + pgvector 即可水平扩展;每个实例仍在本地做 ONNX 嵌入(无状态)。
- 升级提示:若旧 `.data` 目录中有 stdio 服务器索引,删除该目录后重启即重建(搜索侧也会过滤非 mcp-streamable 命中)。
- 健康探针: `GET /health` 返回 200 `{"status":"healthy"}`。
```

(README.md 同段落用英文写,内容一致。)

- [x] **Step 3: 全量测试 + 提交**

Run: `dotnet test src/dotnet/tests/NacosMcpRouter.Tests` — 全绿(文档改动不影响,确认无回归)。

```bash
git add src/dotnet/README.md src/dotnet/README_cn.md
git commit -m "docs(dotnet): document cluster restrictions, vector store switch and /health

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Self-Review 记录

- **Spec coverage**:四个需求点(惰性连接 + Lazy、stdio 拒绝、旧客户端拒绝、/health)分别对应 Task 1/2/3/4;向量库双实现(SonnetDB 单机保留 + pgvector 集群切换)对应 Task 6(含搜索侧协议过滤兜底);embedding 升级对应 Task 5;文档 Task 7。已知限制已记录:单机升级场景旧 `.data` 中的 stdio 索引不主动删除 —— 缓解措施 = 搜索侧过滤 + 文档提示删除目录重建;`use_tool` 每次调用多一次 Nacos `GetByNameAsync` 查询(可接受的取舍,后续可加缓存)。
- **Placeholder scan**:无 TBD/TODO;所有步骤含完整代码。
- **Type consistency**:`IVectorStore` 六方法签名与 `ISonnetDbVectorStore` 逐字一致;`VectorStoreKind`/`VectorStore`/`PostgresConnectionString` 在 Task 6 各步骤与 Task 7 文档中一致;`McpProxyEntry`/`IMcpProxySession`/`EnsureConnectedAsync` 签名在各任务间一致;SDK 常量已反射验证(`TextContentBlock`/`CallToolResult` 可初始化、`McpErrorCode.UnsupportedProtocolVersion == -32022`、`McpProtocolVersions.July2026ProtocolVersion == "2026-07-28"`、`McpHttpHeaders.ProtocolVersion` 存在)。
