using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

namespace NacosMcpRouter.Mcp;

/// <summary>
/// Rejects MCP requests that do not declare the 2026-07-28 (or later) protocol revision.
/// Legacy clients rely on the initialize handshake and Mcp-Session-Id, which the server does
/// not serve; rejecting them early keeps the /mcp endpoint stateless for every request.
/// </summary>
public static class McpProtocolVersionGate
{
    // SDK 2.2.0 keeps these as internal consts (McpHttpHeaders.ProtocolVersion,
    // McpProtocolVersions.July2026ProtocolVersion), so literals are used here.
    private const string ProtocolVersionHeader = "MCP-Protocol-Version";
    private const string July2026ProtocolVersion = "2026-07-28";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/mcp"))
        {
            var version = context.Request.Headers[ProtocolVersionHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(version) || string.CompareOrdinal(version, July2026ProtocolVersion) < 0)
            {
                // Protocol revisions are date strings (YYYY-MM-DD), so ordinal comparison orders them correctly.
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new
                    {
                        code = (int)McpErrorCode.UnsupportedProtocolVersion,
                        message = "Unsupported protocol version",
                        data = new { supported = new[] { July2026ProtocolVersion } },
                    },
                }, JsonOpts).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
