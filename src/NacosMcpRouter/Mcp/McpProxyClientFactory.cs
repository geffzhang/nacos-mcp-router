using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NacosMcpRouter.Mcp;

public sealed class McpProxyClientFactory : IMcpProxyClientFactory
{
    private readonly ILogger<McpProxyClientFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly OAuthTokenService _oauthTokenService;

    public McpProxyClientFactory(
        ILogger<McpProxyClientFactory> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _oauthTokenService = new OAuthTokenService(new HttpClient());
    }

    public async Task<IMcpProxySession> CreateAsync(IReadOnlyDictionary<string, object> agentConfig, CancellationToken cancellationToken = default)
    {
        if (!agentConfig.TryGetValue("mcpServers", out var serversObj) || serversObj is not IDictionary<string, object> servers || servers.Count == 0)
        {
            throw new InvalidOperationException("agentConfig.mcpServers is missing or empty");
        }

        var pair = servers.First();
        var serverConfig = pair.Value as IDictionary<string, object>
            ?? throw new InvalidOperationException($"server entry '{pair.Key}' is not an object");
        var protocol = serverConfig.TryGetValue("protocol", out var pObj) && pObj is string p ? p : "stdio";

        var client = protocol switch
        {
            "stdio" => await CreateStdioAsync(serverConfig, cancellationToken).ConfigureAwait(false),
            "mcp-streamable" => await CreateStreamableHttpAsync(serverConfig, cancellationToken).ConfigureAwait(false),
            "mcp-sse" => throw new NotSupportedException("sse protocol not supported in v1"),
            _ => throw new NotSupportedException($"unknown protocol '{protocol}'"),
        };
        return new McpClientSessionAdapter(client);
    }

    private async Task<McpClient> CreateStreamableHttpAsync(IDictionary<string, object> cfg, CancellationToken cancellationToken)
    {
        var url = RequireString(cfg, "url");
        var headers = cfg.TryGetValue("headers", out var hObj) && hObj is IDictionary<string, object> hDict
            ? hDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty)
            : new Dictionary<string, string>();

        var httpClient = new HttpClient { BaseAddress = new Uri(url) };
        foreach (var header in headers)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (cfg.TryGetValue("oauth", out var oauthValue) && oauthValue is IDictionary<string, object> oauthConfig)
        {
            var options = OAuthOptionsParser.Parse(oauthConfig);
            httpClient.Dispose();
            httpClient = new HttpClient(new OAuthBearerHandler(_oauthTokenService, options)) { BaseAddress = new Uri(url) };
            foreach (var header in headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, httpClient);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpClient> CreateStdioAsync(IDictionary<string, object> cfg, CancellationToken cancellationToken)
    {
        var command = RequireString(cfg, "command");
        var options = new StdioClientTransportOptions
        {
            Command = command,
            Name = cfg.TryGetValue("name", out var name) ? name?.ToString() ?? command : command,
            WorkingDirectory = cfg.TryGetValue("workingDirectory", out var workingDirectory)
                ? workingDirectory?.ToString()
                : null,
        };
        if (cfg.TryGetValue("args", out var args) && args is IEnumerable<object> objectArgs)
        {
            options.Arguments = objectArgs.Select(a => a?.ToString() ?? string.Empty).ToList();
        }
        if (cfg.TryGetValue("env", out var env) && env is IDictionary<string, object> objectEnv)
        {
            var environmentVariables = new Dictionary<string, string?>();
            foreach (var pair in objectEnv)
            {
                environmentVariables[pair.Key] = pair.Value?.ToString();
            }
            options.EnvironmentVariables = environmentVariables;
        }

        var transport = new StdioClientTransport(options, _loggerFactory);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string RequireString(IDictionary<string, object> cfg, string key) =>
        cfg.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new InvalidOperationException($"server config missing required string field '{key}'");
}

internal sealed class McpClientSessionAdapter : IMcpProxySession
{
    private readonly McpClient _client;

    public McpClientSessionAdapter(McpClient client) => _client = client;

    public Implementation? ServerInfo => _client.ServerInfo;

    public async ValueTask PingAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        await _client.PingAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<McpClientTool>> ListToolsAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tools = await _client.ListToolsAsync(options, cancellationToken).ConfigureAwait(false);
        return (IReadOnlyList<McpClientTool>)tools;
    }

    public ValueTask<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

internal static class OAuthOptionsParser
{
    public static OAuthOptions Parse(IDictionary<string, object> cfg)
    {
        var scopes = cfg.TryGetValue("scopes", out var value) switch
        {
            true when value is IEnumerable<object> values => values.Select(v => v?.ToString() ?? string.Empty).Where(v => v.Length > 0).ToList(),
            true when value is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Array
                => element.EnumerateArray().Select(v => v.GetString() ?? string.Empty).Where(v => v.Length > 0).ToList(),
            _ => [],
        };
        return new OAuthOptions(
            Get(cfg, "flow") ?? throw new InvalidOperationException("OAuth flow is required"),
            Get(cfg, "clientId"), Get(cfg, "clientSecret"), Get(cfg, "tokenUrl"),
            Get(cfg, "authorizationUrl"), Get(cfg, "deviceAuthorizationUrl"),
            Get(cfg, "authorizationCode"), Get(cfg, "deviceCode"), scopes);
    }

    private static string? Get(IDictionary<string, object> cfg, string key) =>
        cfg.TryGetValue(key, out var value) ? value?.ToString() : null;
}