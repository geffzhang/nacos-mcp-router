using System.Text.Json;

namespace NacosMcpRouter.Configuration;

public enum McpTransport
{
    StreamableHttp,
}

public sealed class RouterOptions
{
    public const string SectionName = "NacosMcpRouter";

    public NacosOptions Nacos { get; init; } = new();
    public McpTransport Transport { get; init; } = McpTransport.StreamableHttp;
    public string EmbeddingModelDir { get; init; } = "./models/all-MiniLM-L6-v2";
    public string SonnetDbDataDir { get; init; } = "./.data/mcp-router";
    public VectorStoreKind VectorStore { get; init; } = VectorStoreKind.SonnetDb;

    public string PostgresConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres";
    public int UpdateIntervalSeconds { get; init; } = 60;
    public double SearchMinSimilarity { get; init; } = 0.2;
    public int SearchResultLimit { get; init; } = 10;
    public int HttpPort { get; init; } = 8000;
    public IReadOnlyDictionary<string, JsonElement> OAuthConfigs { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public enum VectorStoreKind
{
    SonnetDb,
    Postgres,
}
