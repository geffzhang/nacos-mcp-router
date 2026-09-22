using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NacosMcpRouter.Configuration;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.VectorStore;

namespace NacosMcpRouter.Mcp;

public sealed class McpRouterTools
{
    private readonly INacosMcpRegistry _registry;
    private readonly IVectorStore _store;
    private readonly IEmbeddingGenerator _embedder;
    private readonly IMcpProxyRegistry _proxy;
    private readonly RouterOptions _options;
    private readonly ILogger<McpRouterTools> _logger;

    public McpRouterTools(
        INacosMcpRegistry registry,
        IVectorStore store,
        IEmbeddingGenerator embedder,
        IMcpProxyRegistry proxy,
        IOptions<RouterOptions> options,
        ILogger<McpRouterTools> logger)
    {
        _registry = registry;
        _store = store;
        _embedder = embedder;
        _proxy = proxy;
        _options = options.Value;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "search_mcp_server")]
    public async Task<string> SearchMcpServer(
        [System.ComponentModel.Description("用户中文和英文任务描述")] string task_description,
        [System.ComponentModel.Description("英文逗号分隔的关键词, 最多4个")] string key_words,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var keywords = key_words.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var fromKeyword = new List<McpServerEntry>();
            foreach (var keyword in keywords)
            {
                var hits = await _registry.SearchByKeywordAsync(keyword, cancellationToken).ConfigureAwait(false);
                foreach (var hit in hits)
                {
                    // multiple keywords can hit the same server
                    if (fromKeyword.Any(e => e.Name == hit.Name)) continue;
                    fromKeyword.Add(hit);
                }
            }

            var all = new List<McpServerEntry>(fromKeyword);
            if (all.Count < 5)
            {
                try
                {
                    var vector = await _embedder.GenerateAsync(task_description, cancellationToken).ConfigureAwait(false);
                    var hits = await _store.SearchAsync(vector, 5 - all.Count, cancellationToken).ConfigureAwait(false);
                    var maxDistance = 1.0 - _options.SearchMinSimilarity;
                    foreach (var hit in hits)
                    {
                        if (hit.Document.Protocol is not ("stdio" or "mcp-streamable")) continue;
                        if (hit.Distance > maxDistance) continue;
                        var entry = new McpServerEntry(hit.Document.Name, hit.Document.Description, new Dictionary<string, object>(), null, hit.Document.McpId, hit.Document.Version);
                        if (!all.Any(e => e.Name == entry.Name))
                        {
                            all.Add(entry);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "vector search failed; falling back to keyword results");
                }
            }

            var truncated = all.Take(_options.SearchResultLimit).ToList();
            var dict = truncated.ToDictionary(e => e.Name, e => new { name = e.Name, description = e.Description });
            var json = JsonSerializer.Serialize(dict, JsonOpts);
            return "## 获取" + task_description + "的步骤如下:\n### 1. 当前可用的mcp server列表为:" + json + "\n### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_mcp_server failed");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "add_mcp_server")]
    public async Task<string> AddMcpServer(
        [System.ComponentModel.Description("MCP Server 名称")] string mcp_server_name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await _registry.GetByNameAsync(mcp_server_name, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return mcp_server_name + " is not found, use search_mcp_server to get mcp servers";
            }

            var proxyEntry = await _proxy.EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false);
            var filtered = ToolFilter.Apply(proxyEntry.Tools, entry.McpConfigDetail);
            var toolList = filtered.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.JsonSchema }).ToList();
            var json = JsonSerializer.Serialize(toolList, JsonOpts);
            return "1. " + mcp_server_name + "安装完成, tool 列表为: " + json + "\n2." + mcp_server_name + "的工具需要通过nacos-mcp-router的use_tool工具代理使用";
        }
        catch (NotSupportedException ex)
        {
            return mcp_server_name + " 安装失败: " + ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "add_mcp_server failed");
            return mcp_server_name + " 安装失败: " + ex.Message;
        }
    }

    [McpServerTool(Name = "use_tool")]
    public async Task<string> UseTool(
        [System.ComponentModel.Description("目标 MCP Server 名称")] string mcp_server_name,
        [System.ComponentModel.Description("目标工具名")] string mcp_tool_name,
        [System.ComponentModel.Description("工具参数 JSON 字符串")] string @params,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await _registry.GetByNameAsync(mcp_server_name, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return "mcp server not found, use search_mcp_server to get mcp servers";
            }

            var proxy = await _proxy.EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, object?>? arguments = null;
            if (!string.IsNullOrWhiteSpace(@params))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(@params);
                    arguments = parsed?.ToDictionary(k => k.Key, v => (object?)v.Value);
                }
                catch (JsonException)
                {
                    return "Error: use_tool params must be a JSON object";
                }
            }
            arguments ??= new Dictionary<string, object?>();

            var result = await proxy.Session.CallToolAsync(mcp_tool_name, arguments, cancellationToken).ConfigureAwait(false);
            var sb = new StringBuilder();
            foreach (var block in result.Content)
            {
                if (sb.Length > 0) sb.Append('\n');
                if (block is ModelContextProtocol.Protocol.TextContentBlock text)
                {
                    sb.Append(text.Text);
                }
                else
                {
                    sb.Append($"[unsupported content type: {block.Type}]");
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "use_tool failed");
            return "failed to use tool: " + mcp_tool_name;
        }
    }
}