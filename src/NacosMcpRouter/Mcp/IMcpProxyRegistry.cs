using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NacosMcpRouter.Nacos;

namespace NacosMcpRouter.Mcp;

public interface IMcpProxyRegistry
{
    Task<McpProxyEntry> EnsureConnectedAsync(McpServerEntry entry, CancellationToken cancellationToken = default);
    McpProxyEntry? TryGet(string name);
    Task DisposeAllAsync();
}

public interface IMcpProxyClientFactory
{
    Task<IMcpProxySession> CreateAsync(IReadOnlyDictionary<string, object> agentConfig, CancellationToken cancellationToken = default);
}

public interface IMcpProxySession : IAsyncDisposable
{
    Implementation? ServerInfo { get; }
    ValueTask PingAsync(RequestOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<McpClientTool>> ListToolsAsync(RequestOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default);
}

public sealed record McpProxyEntry(
    string Name,
    IMcpProxySession Session,
    IReadOnlyList<McpClientTool> Tools,
    string Version);