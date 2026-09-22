using System.Text.Json;

namespace NacosMcpRouter.Nacos;

public sealed record McpConfigDetail(
    string Protocol,
    ToolSpec? ToolSpec,
    RemoteServerConfig? RemoteServerConfig,
    IReadOnlyList<BackendEndpoint> BackendEndpoints);

public sealed record ToolSpec(
    IReadOnlyList<McpToolSpecEntry> Tools,
    IReadOnlyDictionary<string, ToolMeta> ToolsMeta,
    IReadOnlyDictionary<string, ToolInfo> ToolsDict);

public sealed record McpToolSpecEntry(string Name, string? Description, JsonElement? InputSchema);

public sealed record ToolMeta(bool Enabled);

public sealed record ToolInfo(string Description, JsonElement InputSchema);

public sealed record RemoteServerConfig(string ServiceRef, string ExportPath);

public sealed record BackendEndpoint(string Address, int Port);
