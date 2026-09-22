namespace NacosMcpRouter.Nacos;

public sealed record McpServerEntry(
    string Name,
    string Description,
    IReadOnlyDictionary<string, object> AgentConfig,
    McpConfigDetail? McpConfigDetail,
    string Id,
    string Version);
