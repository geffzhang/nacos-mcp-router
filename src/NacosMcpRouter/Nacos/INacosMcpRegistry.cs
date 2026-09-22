namespace NacosMcpRouter.Nacos;

public interface INacosMcpRegistry
{
    Task<IReadOnlyList<McpServerEntry>> SearchByKeywordAsync(string keyword, CancellationToken cancellationToken = default);
    Task<McpServerEntry?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
