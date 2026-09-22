using System.Globalization;
using System.Text.Json;

namespace NacosMcpRouter.Configuration;

public static class EnvConfigurator
{
    public static RouterOptions LoadFromEnvironment()
    {
        var nacos = new NacosOptions
        {
            ServerAddresses = ReadString("NACOS_ADDR") ?? "127.0.0.1:8848",
            Username = ReadString("NACOS_USERNAME") ?? "nacos",
            Password = ReadString("NACOS_PASSWORD") ?? string.Empty,
            Namespace = ReadString("NACOS_NAMESPACE") ?? string.Empty,
            AccessKeyId = ReadString("ACCESS_KEY_ID"),
            AccessKeySecret = ReadString("ACCESS_KEY_SECRET"),
        };

        var opts = new RouterOptions
        {
            Nacos = nacos,
            Transport = ParseTransport(ReadString("TRANSPORT_TYPE")),
            EmbeddingModelDir = ReadString("EMBEDDING_MODEL_DIR") ?? "./models/all-MiniLM-L6-v2",
            SonnetDbDataDir = ReadString("SONNETDB_DATA_DIR") ?? "./.data/mcp-router",
            VectorStore = ParseVectorStore(ReadString("VECTOR_STORE")),
            PostgresConnectionString = ReadString("POSTGRES_CONNECTION_STRING")
                ?? "Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres",
            UpdateIntervalSeconds = ClampInt(ReadInt("UPDATE_INTERVAL") ?? 60, min: 10),
            SearchMinSimilarity = ClampDouble(ReadDouble("SEARCH_MIN_SIMILARITY") ?? 0.2, 0.0, 1.0),
            SearchResultLimit = Math.Max(1, ReadInt("SEARCH_RESULT_LIMIT") ?? 10),
            HttpPort = ReadInt("PORT") ?? 8000,
            OAuthConfigs = ParseOAuthConfigs(ReadString("MCP_OAUTH_CONFIG")),
        };
        Apply(opts);
        return opts;
    }

    public static void Apply(RouterOptions opts)
    {
        if (opts.UpdateIntervalSeconds < 10)
        {
            throw new InvalidOperationException("UpdateIntervalSeconds must be >= 10");
        }
    }

    private static McpTransport ParseTransport(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "streamable_http" or "streamable-http" or "streamable" => McpTransport.StreamableHttp,
        null or "" => McpTransport.StreamableHttp,
        _ => throw new InvalidOperationException(
            $"Unknown TRANSPORT_TYPE '{value}'. Valid values: streamable_http (default)"),
    };

    private static string? ReadString(string name) => Environment.GetEnvironmentVariable(name);

    private static IReadOnlyDictionary<string, JsonElement> ParseOAuthConfigs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("root must be an object");
            }
            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("MCP_OAUTH_CONFIG must be a JSON object keyed by MCP server name", ex);
        }
    }

    private static VectorStoreKind ParseVectorStore(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sonnetdb" => VectorStoreKind.SonnetDb,
        "postgres" or "postgresql" => VectorStoreKind.Postgres,
        _ => throw new InvalidOperationException(
            $"Unknown VECTOR_STORE '{raw}'. Valid values: sonnetdb (default), postgres"),
    };

    private static int? ReadInt(string name)
    {
        var v = ReadString(name);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static double? ReadDouble(string name)
    {
        var v = ReadString(name);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int ClampInt(int value, int min) => Math.Max(min, value);

    private static double ClampDouble(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
