using FluentAssertions;
using NacosMcpRouter.Configuration;

namespace NacosMcpRouter.Tests.Configuration;

public sealed class EnvConfiguratorTests : IDisposable
{
    private readonly string? _savedAddr = Environment.GetEnvironmentVariable("NACOS_ADDR");
    private readonly string? _savedTransport = Environment.GetEnvironmentVariable("TRANSPORT_TYPE");
    private readonly string? _savedUpdate = Environment.GetEnvironmentVariable("UPDATE_INTERVAL");
    private readonly string? _savedSimilarity = Environment.GetEnvironmentVariable("SEARCH_MIN_SIMILARITY");
    private readonly string? _savedAk = Environment.GetEnvironmentVariable("ACCESS_KEY_ID");
    private readonly string? _savedSk = Environment.GetEnvironmentVariable("ACCESS_KEY_SECRET");
    private readonly string? _savedPort = Environment.GetEnvironmentVariable("PORT");
    private readonly string? _savedVectorStore = Environment.GetEnvironmentVariable("VECTOR_STORE");
    private readonly string? _savedPgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
    private readonly string? _savedOAuth = Environment.GetEnvironmentVariable("MCP_OAUTH_CONFIG");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NACOS_ADDR", _savedAddr);
        Environment.SetEnvironmentVariable("TRANSPORT_TYPE", _savedTransport);
        Environment.SetEnvironmentVariable("UPDATE_INTERVAL", _savedUpdate);
        Environment.SetEnvironmentVariable("SEARCH_MIN_SIMILARITY", _savedSimilarity);
        Environment.SetEnvironmentVariable("ACCESS_KEY_ID", _savedAk);
        Environment.SetEnvironmentVariable("ACCESS_KEY_SECRET", _savedSk);
        Environment.SetEnvironmentVariable("PORT", _savedPort);
        Environment.SetEnvironmentVariable("VECTOR_STORE", _savedVectorStore);
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _savedPgConn);
        Environment.SetEnvironmentVariable("MCP_OAUTH_CONFIG", _savedOAuth);
    }

    [Fact]
    public void Defaults_WhenNoEnvSet()
    {
        Environment.SetEnvironmentVariable("NACOS_ADDR", null);
        var opts = EnvConfigurator.LoadFromEnvironment();
        opts.Nacos.ServerAddresses.Should().Be("127.0.0.1:8848");
        opts.Nacos.Username.Should().Be("nacos");
        opts.Transport.Should().Be(McpTransport.StreamableHttp);
        opts.UpdateIntervalSeconds.Should().Be(60);
        opts.SearchMinSimilarity.Should().Be(0.2);
        opts.SearchResultLimit.Should().Be(10);
        opts.HttpPort.Should().Be(8000);
    }

    [Fact]
    public void ReadsNacosAddr()
    {
        Environment.SetEnvironmentVariable("NACOS_ADDR", "10.0.0.1:8848");
        var opts = EnvConfigurator.LoadFromEnvironment();
        opts.Nacos.ServerAddresses.Should().Be("10.0.0.1:8848");
    }

    [Fact]
    public void ClampsUpdateIntervalToMinimumTen()
    {
        Environment.SetEnvironmentVariable("UPDATE_INTERVAL", "3");
        var opts = EnvConfigurator.LoadFromEnvironment();
        opts.UpdateIntervalSeconds.Should().Be(10);
    }

    [Fact]
    public void ClampsSimilarityToZeroOne()
    {
        Environment.SetEnvironmentVariable("SEARCH_MIN_SIMILARITY", "1.5");
        var high = EnvConfigurator.LoadFromEnvironment();
        high.SearchMinSimilarity.Should().Be(1.0);

        Environment.SetEnvironmentVariable("SEARCH_MIN_SIMILARITY", "-0.2");
        var low = EnvConfigurator.LoadFromEnvironment();
        low.SearchMinSimilarity.Should().Be(0.0);
    }

    [Fact]
    public void ParsesOnlyStreamableHttpTransport()
    {
        Environment.SetEnvironmentVariable("TRANSPORT_TYPE", "streamable_http");
        EnvConfigurator.LoadFromEnvironment().Transport.Should().Be(McpTransport.StreamableHttp);
        Environment.SetEnvironmentVariable("TRANSPORT_TYPE", "stdio");
        new Action(() => EnvConfigurator.LoadFromEnvironment())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*streamable_http*");

        Environment.SetEnvironmentVariable("TRANSPORT_TYPE", "garbage");
        new Action(() => EnvConfigurator.LoadFromEnvironment())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*streamable_http*");
    }

    [Fact]
    public void ParsesAccessKeyOptional()
    {
        Environment.SetEnvironmentVariable("ACCESS_KEY_ID", "ak");
        Environment.SetEnvironmentVariable("ACCESS_KEY_SECRET", "sk");
        var opts = EnvConfigurator.LoadFromEnvironment();
        opts.Nacos.AccessKeyId.Should().Be("ak");
        opts.Nacos.AccessKeySecret.Should().Be("sk");
    }

    [Fact]
    public void DefaultVectorStore_IsSonnetDb()
    {
        Environment.SetEnvironmentVariable("VECTOR_STORE", null);
        EnvConfigurator.LoadFromEnvironment().VectorStore.Should().Be(VectorStoreKind.SonnetDb);
    }

    [Fact]
    public void ReadsVectorStorePostgres()
    {
        Environment.SetEnvironmentVariable("VECTOR_STORE", "postgres");
        EnvConfigurator.LoadFromEnvironment().VectorStore.Should().Be(VectorStoreKind.Postgres);
    }

    [Fact]
    public void UnknownVectorStore_Throws()
    {
        Environment.SetEnvironmentVariable("VECTOR_STORE", "redis");
        new Action(() => EnvConfigurator.LoadFromEnvironment()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReadsPostgresConnectionString()
    {
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", "Host=pg.example;Port=5432;Database=router;Username=u;Password=p");
        EnvConfigurator.LoadFromEnvironment().PostgresConnectionString.Should().Be("Host=pg.example;Port=5432;Database=router;Username=u;Password=p");
    }

    [Fact]
    public void DefaultPostgresConnectionString_WhenUnset()
    {
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", null);
        EnvConfigurator.LoadFromEnvironment().PostgresConnectionString.Should().Contain("Database=mcp_router");
    }

    [Fact]
    public void ReadsMcpOAuthConfigByServerName()
    {
        Environment.SetEnvironmentVariable("MCP_OAUTH_CONFIG", "{\"weather\":{\"flow\":\"client_credentials\",\"clientId\":\"router\",\"tokenUrl\":\"https://idp.test/token\"}}");

        var config = EnvConfigurator.LoadFromEnvironment().OAuthConfigs;

        config.Should().ContainKey("weather");
        config["weather"].GetProperty("flow").GetString().Should().Be("client_credentials");
        config["weather"].GetProperty("tokenUrl").GetString().Should().Be("https://idp.test/token");
    }

    [Fact]
    public void InvalidMcpOAuthConfig_Throws()
    {
        Environment.SetEnvironmentVariable("MCP_OAUTH_CONFIG", "not-json");

        new Action(() => EnvConfigurator.LoadFromEnvironment())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*MCP_OAUTH_CONFIG*");
    }
}
