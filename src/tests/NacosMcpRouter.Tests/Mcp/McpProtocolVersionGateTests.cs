using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NacosMcpRouter.Mcp;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class McpProtocolVersionGateTests
{
    private static HttpContext CreateContext(string method, string path, string? protocolVersion)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (protocolVersion is not null)
        {
            // SDK 2.2.0 的 McpHttpHeaders 是 internal,测试用字面量
            ctx.Request.Headers["MCP-Protocol-Version"] = protocolVersion;
        }
        return ctx;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Post_MissingVersionHeader_Returns400WithError()
    {
        var ctx = CreateContext("POST", "/mcp", protocolVersion: null);
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        nextInvoked.Should().BeFalse();
        var body = await ReadBodyAsync(ctx);
        body.Should().Contain("-32022").And.Contain("2026-07-28");
    }

    [Fact]
    public async Task Post_PreJuly2026Version_Returns400()
    {
        var ctx = CreateContext("POST", "/mcp", "2025-11-25");

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(ctx)).Should().Contain("-32022");
    }

    [Fact]
    public async Task Post_July2026Version_PassesThrough()
    {
        var ctx = CreateContext("POST", "/mcp", "2026-07-28");
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        nextInvoked.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Get_WithoutHeader_PassesThrough()
    {
        // GET/DELETE 由 SDK 在 stateless 模式下返回 405,门禁只拦 POST
        var ctx = CreateContext("GET", "/mcp", protocolVersion: null);
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task Post_OtherPath_PassesThrough()
    {
        var ctx = CreateContext("POST", "/health", protocolVersion: null);
        var nextInvoked = false;

        await McpProtocolVersionGate.InvokeAsync(ctx, _ => { nextInvoked = true; return Task.CompletedTask; });

        nextInvoked.Should().BeTrue();
    }
}
