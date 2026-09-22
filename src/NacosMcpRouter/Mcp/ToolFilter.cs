using ModelContextProtocol.Client;
using NacosMcpRouter.Nacos;

namespace NacosMcpRouter.Mcp;

public static class ToolFilter
{
    public static IReadOnlyList<McpClientTool> Apply(IReadOnlyList<McpClientTool> tools, McpConfigDetail? detail)
    {
        if (detail?.ToolSpec is null)
        {
            return tools;
        }

        var meta = detail.ToolSpec.ToolsMeta;
        var dict = detail.ToolSpec.ToolsDict;
        var result = new List<McpClientTool>();
        foreach (var tool in tools)
        {
            if (meta.TryGetValue(tool.Name, out var m) && !m.Enabled)
            {
                continue;
            }
            if (dict.TryGetValue(tool.Name, out var info))
            {
                result.Add(tool.WithDescription(info.Description));
            }
            else
            {
                result.Add(tool);
            }
        }
        return result;
    }
}