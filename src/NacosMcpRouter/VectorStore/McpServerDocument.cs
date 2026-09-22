namespace NacosMcpRouter.VectorStore;

public sealed record McpServerDocument(
    string Name,
    string Description,
    string Protocol,
    string McpId,
    string Version);

public sealed record McpServerHit(McpServerDocument Document, double Distance);
