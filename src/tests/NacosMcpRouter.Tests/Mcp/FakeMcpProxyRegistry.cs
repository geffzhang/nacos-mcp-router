using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NacosMcpRouter.Mcp;
using NacosMcpRouter.Nacos;
using NSubstitute;

namespace NacosMcpRouter.Tests.Mcp;

internal sealed class FakeMcpProxyRegistry : IMcpProxyRegistry
{
    public Dictionary<string, McpProxyEntry> Entries { get; } = new();
    public List<string> EnsureRequests { get; } = new();

    public Task<McpProxyEntry> EnsureConnectedAsync(McpServerEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureRequests.Add(entry.Name);
        if (!Entries.TryGetValue(entry.Name, out var e))
        {
            var session = Substitute.For<IMcpProxySession>();
            session.CallToolAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<CallToolResult>(new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] }));
            e = new McpProxyEntry(entry.Name, session, [], "1.0");
            Entries[entry.Name] = e;
        }
        return Task.FromResult(e);
    }

    public McpProxyEntry? TryGet(string name) => Entries.TryGetValue(name, out var e) ? e : null;

    public Task DisposeAllAsync() { Entries.Clear(); return Task.CompletedTask; }
}
