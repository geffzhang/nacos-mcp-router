using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using NacosMcpRouter.Configuration;
using NacosMcpRouter.Embeddings;
using NacosMcpRouter.Hosting;
using NacosMcpRouter.Logging;
using NacosMcpRouter.Mcp;
using NacosMcpRouter.Nacos;
using NacosMcpRouter.VectorStore;
using RedNb.Nacos;
using RedNb.Nacos.DependencyInjection;

namespace NacosMcpRouter;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = EnvConfigurator.LoadFromEnvironment();
        var loggerFactory = RouterLogger.CreateFactory();
        var bootstrapLogger = loggerFactory.CreateLogger("NacosMcpRouter.Bootstrap");
        bootstrapLogger.LogInformation(
            "Starting NacosMcpRouter: transport={Transport} mode=router nacos={Server}",
            options.Transport, options.Nacos.ServerAddresses);

        return await RunHttpAsync(options, loggerFactory).ConfigureAwait(false);
    }

    internal static IServiceCollection BuildServices(RouterOptions options, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(options.Nacos);
        services.AddSingleton(loggerFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(new ModelPaths(options.EmbeddingModelDir)));
        services.AddSingleton<IVectorStore>(_ => options.VectorStore switch
        {
            VectorStoreKind.Postgres => new PgVectorStore(options.PostgresConnectionString, dimensions: 384),
            _ => new SonnetDbVectorStore(options.SonnetDbDataDir, dimensions: 384),
        });
        Action<NacosClientOptions> configureNacos = n =>
        {
            n.ServerAddresses = options.Nacos.ServerAddresses;
            n.Username = options.Nacos.Username;
            n.Password = options.Nacos.Password;
            n.Namespace = options.Nacos.Namespace;
            n.AccessKey = options.Nacos.AccessKeyId;
            n.SecretKey = options.Nacos.AccessKeySecret;
        };
        // AddNacos registers Config/Naming only; AI services (IAiService etc.) are opt-in
        services.AddNacos(configureNacos);
        services.AddNacosAi(configureNacos);
        services.AddSingleton<INacosMcpRegistry, NacosMcpRegistry>();
        services.AddSingleton<IMcpProxyClientFactory, McpProxyClientFactory>();
        services.AddSingleton<IMcpProxyRegistry, McpProxyRegistry>();
        services.AddSingleton<McpRouterTools>();
        services.AddHostedService<McpRouterHostedService>();
        return services;
    }

    private static async Task<int> RunHttpAsync(RouterOptions options, ILoggerFactory loggerFactory)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new DelegatingLoggerProvider(loggerFactory));
        var services = BuildServices(options, loggerFactory);
        foreach (var s in services)
        {
            builder.Services.Add(s);
        }
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<McpRouterTools>();
        var app = builder.Build();
        app.Use(McpProtocolVersionGate.InvokeAsync);
        app.MapMcp("/mcp");
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        await app.RunAsync($"http://0.0.0.0:{options.HttpPort}").ConfigureAwait(false);
        return 0;
    }
}

internal sealed class DelegatingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _factory;
    public DelegatingLoggerProvider(ILoggerFactory factory) => _factory = factory;
    public ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);
    public void Dispose() { }
}