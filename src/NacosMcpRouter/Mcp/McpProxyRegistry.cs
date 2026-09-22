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
