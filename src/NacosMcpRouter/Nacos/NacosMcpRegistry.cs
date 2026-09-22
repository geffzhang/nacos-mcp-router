using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NacosMcpRouter.Configuration;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.VectorStore;
using RedNb.Nacos.Ai;
using RedNb.Nacos.Ai.Models.Mcp;

namespace NacosMcpRouter.Nacos;

public sealed class NacosMcpRegistry : INacosMcpRegistry, IHostedService
{
    private readonly IAiService _aiService;
    private readonly IVectorStore _store;
    private readonly IEmbeddingGenerator _embedder;
    private readonly RouterOptions _options;
    private readonly ILogger<NacosMcpRegistry> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NacosMcpRegistry(
        IAiService aiService,
        IVectorStore store,
        IEmbeddingGenerator embedder,
        RouterOptions options,
        ILogger<NacosMcpRegistry> logger)
    {
        _aiService = aiService;
        _store = store;
        _embedder = embedder;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<McpServerEntry>> SearchByKeywordAsync(string keyword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        // Nacos has no free-text keyword API (search is accurate/blur mode only), so fetch the
        // full list and substring-match locally, mirroring the Python implementation's cache scan
        var result = new List<McpServerEntry>();
        var pageNo = 1;
        const int pageSize = 100;
        var totalSeen = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await _aiService.ListMcpServersAsync(mcpName: null, search: "blur", pageNo: pageNo, pageSize: pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.PageItems.Count == 0) break;
            foreach (var item in page.PageItems)
            {
                totalSeen++;
                if (string.IsNullOrWhiteSpace(item.Description)) continue;
                if (!IsSupportedProtocol(item.Protocol)) continue;
                var haystack = $"{item.Name} {item.Description}";
                if (!haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(MapToEntry(item));
            }
            if (totalSeen >= page.TotalCount) break;
            pageNo++;
        }
        return result;
    }

    public async Task<McpServerEntry?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var detail = await _aiService.GetMcpServerAsync(name!, cancellationToken).ConfigureAwait(false);
        return detail is null ? null : MapToEntry(detail);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected */ }
        }
    }

    internal async Task BackfillAsync(CancellationToken cancellationToken)
    {
        await _store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var pageNo = 1;
        const int pageSize = 100;
        var totalSeen = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await _aiService.ListMcpServersAsync(mcpName: null, search: "blur", pageNo: pageNo, pageSize: pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.PageItems.Count == 0) break;
            foreach (var item in page.PageItems)
            {
                if (string.IsNullOrWhiteSpace(item.Description)) continue;
                if (!IsSupportedProtocol(item.Protocol)) continue;

                var detail = await _aiService.GetMcpServerAsync(item.Name!, cancellationToken).ConfigureAwait(false);
                if (detail is null || string.IsNullOrWhiteSpace(detail.Name) || string.IsNullOrWhiteSpace(detail.Description)) continue;

                var embedding = await _embedder.GenerateAsync(detail.Description, cancellationToken).ConfigureAwait(false);
                var doc = new McpServerDocument(
                    Name: detail.Name,
                    Description: detail.Description,
                    Protocol: detail.Protocol ?? string.Empty,
                    McpId: detail.Id ?? string.Empty,
                    Version: detail.VersionDetail?.Version ?? detail.Version ?? "0.0.0");
                await _store.UpsertAsync(doc, embedding, cancellationToken).ConfigureAwait(false);
                totalSeen++;
            }
            if (totalSeen >= page.TotalCount) break;
            pageNo++;
        }
        _logger.LogInformation("Backfill complete: {Count} servers indexed", totalSeen);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.UpdateIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            await BackfillAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial backfill failed; will retry on next tick");
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await BackfillAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refresh iteration failed");
            }
        }
    }

    private static bool IsSupportedProtocol(string? protocol) =>
        protocol is "stdio" or "mcp-streamable";

    private static McpServerEntry MapToEntry(McpServerBasicInfo info) =>
        new(
            Name: info.Name ?? string.Empty,
            Description: info.Description ?? string.Empty,
            AgentConfig: new Dictionary<string, object>(),
            McpConfigDetail: null,
            Id: info.Id ?? string.Empty,
            Version: info.VersionDetail?.Version ?? info.Version ?? "0.0.0");

    private McpServerEntry MapToEntry(McpServerDetailInfo detail)
    {
        var protocol = detail.Protocol ?? string.Empty;
        var agentConfig = BuildAgentConfig(detail, protocol, _options);
        return new McpServerEntry(
            Name: detail.Name ?? string.Empty,
            Description: detail.Description ?? string.Empty,
            AgentConfig: agentConfig,
            McpConfigDetail: MapConfigDetail(detail, protocol),
            Id: detail.Id ?? string.Empty,
            Version: detail.VersionDetail?.Version ?? detail.Version ?? "0.0.0");
    }

    private static Dictionary<string, object> BuildAgentConfig(McpServerDetailInfo detail, string protocol, RouterOptions options)
    {
        // Python contract: agentConfig = {"protocol": ..., "mcpServers": { "<name>": {...} }}
        var agentConfig = new Dictionary<string, object>(StringComparer.Ordinal);
        if (protocol == "stdio")
        {
            if (detail.LocalServerConfig is { Count: > 0 } local)
            {
                var serverConfig = local.TryGetValue("mcpServers", out var configuredServers)
                    ? configuredServers
                    : new Dictionary<string, object>(local);
                agentConfig["mcpServers"] = serverConfig;
                if (serverConfig is IDictionary<string, object> servers && !servers.ContainsKey(detail.Name!))
                {
                    servers[detail.Name!] = new Dictionary<string, object>(local);
                }
                agentConfig["protocol"] = protocol;
            }
        }
        else
        {
            var endpoint = detail.BackendEndpoints?.FirstOrDefault();
            if (endpoint is not null && !string.IsNullOrWhiteSpace(endpoint.Address) && !string.IsNullOrWhiteSpace(detail.RemoteServerConfig?.ExportPath))
            {
                var scheme = endpoint.Protocol == "https" ? "https" : "http";
                var path = detail.RemoteServerConfig!.ExportPath!.StartsWith('/')
                    ? detail.RemoteServerConfig.ExportPath
                    : "/" + detail.RemoteServerConfig.ExportPath;
                var serverConfig = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = detail.Name!,
                    ["description"] = string.Empty,
                    ["url"] = $"{scheme}://{endpoint.Address}:{endpoint.Port}{path}",
                    ["protocol"] = protocol,
                };
                if (options.OAuthConfigs.TryGetValue(detail.Name!, out var oauthConfig))
                {
                    if (oauthConfig.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException($"OAuth config for MCP server '{detail.Name}' must be an object");
                    }
                    serverConfig["oauth"] = JsonSerializer.Deserialize<Dictionary<string, object>>(oauthConfig.GetRawText())
                        ?? throw new InvalidOperationException($"OAuth config for MCP server '{detail.Name}' is empty");
                }
                agentConfig["mcpServers"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [detail.Name!] = serverConfig,
                };
                agentConfig["protocol"] = protocol;
            }
        }
        return agentConfig;
    }

    private static McpConfigDetail? MapConfigDetail(McpServerDetailInfo detail, string protocol)
    {
        var toolSpec = detail.ToolSpec;
        if (toolSpec is null) return null;

        var tools = toolSpec.Tools
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => new McpToolSpecEntry(
                t.Name!,
                t.Description,
                t.InputSchema is null ? null : JsonSerializer.SerializeToElement(t.InputSchema)))
            .ToList();
        var toolsMeta = toolSpec.ToolsMeta.ToDictionary(
            kv => kv.Key,
            kv => new ToolMeta(kv.Value.Enabled ?? true));
        var toolsDict = tools.ToDictionary(
            t => t.Name,
            t => new ToolInfo(t.Description ?? string.Empty, t.InputSchema ?? default));
        var remote = detail.RemoteServerConfig is null
            ? null
            : new RemoteServerConfig(
                detail.RemoteServerConfig.ServiceRef?.ServiceName ?? string.Empty,
                detail.RemoteServerConfig.ExportPath ?? string.Empty);
        var endpoints = detail.BackendEndpoints?
            .Where(e => !string.IsNullOrWhiteSpace(e.Address))
            .Select(e => new BackendEndpoint(e.Address!, e.Port))
            .ToList() ?? new List<BackendEndpoint>();
        return new McpConfigDetail(protocol, new ToolSpec(tools, toolsMeta, toolsDict), remote, endpoints);
    }
}
