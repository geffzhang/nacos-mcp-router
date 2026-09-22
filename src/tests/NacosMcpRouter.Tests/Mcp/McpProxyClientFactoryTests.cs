using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NacosMcpRouter.Mcp;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class McpProxyClientFactoryTests
{
    [Fact]
    public async Task Create_StdioProtocol_AttemptsConnection()
    {
        var factory = new McpProxyClientFactory(NullLogger<McpProxyClientFactory>.Instance);
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["server1"] = new Dictionary<string, object>
                {
                    ["protocol"] = "stdio",
                    ["command"] = "definitely-not-a-real-command",
                },
            },
        };

        var act = () => factory.CreateAsync(config);

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().NotBeOfType<NotSupportedException>();
    }

    [Fact]
    public async Task Create_UnknownProtocol_ThrowsNotSupported()
    {
        var factory = new McpProxyClientFactory(NullLogger<McpProxyClientFactory>.Instance);
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["server1"] = new Dictionary<string, object> { ["protocol"] = "grpc" },
            },
        };

        var act = () => factory.CreateAsync(config);

        var ex = (await act.Should().ThrowAsync<NotSupportedException>()).Which;
        ex.Message.Should().Contain("unknown protocol");
    }
}
