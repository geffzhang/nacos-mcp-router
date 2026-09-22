using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.VectorStore;

namespace NacosMcpRouter.Hosting;

public sealed class McpRouterHostedService : IHostedService
{
    private readonly INacosMcpRegistry _registry;
    private readonly IVectorStore _store;
    private readonly ILogger<McpRouterHostedService> _logger;

    public McpRouterHostedService(
        INacosMcpRegistry registry,
        IVectorStore store,
        ILogger<McpRouterHostedService> logger)
    {
        _registry = registry;
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ensuring SonnetDB schema...");
        await _store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        if (await _store.IsEmptyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Vector store empty; initial backfill will run in Nacos registry");
        }

        await _registry.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Nacos registry refresh loop started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Nacos registry...");
        await _registry.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}