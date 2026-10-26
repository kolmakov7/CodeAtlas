using CodeRag.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression test: the MCP server must register tools.
/// Passing a List&lt;Type&gt; straight to WithTools binds to the SDK overload
/// WithTools&lt;TToolType&gt;(TToolType target), which scans the list instance for
/// [McpServerTool] methods, finds none and registers zero tools. The server then
/// advertises only the "logging" capability and answers tools/list with -32601,
/// so clients (opencode) report "connected, tools=0".
/// </summary>
public sealed class McpToolRegistrationTests
{
    [Fact]
    public void WithCodeAtlasTools_registers_tools_for_every_tool_type()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithCodeAtlasTools(new List<Type>
        {
            typeof(CodeSearchTools),
            typeof(DocumentationTools),
            typeof(TaskTools),
            typeof(KnowledgeTools),
            typeof(WorkContextTools)
        });

        services.Count(descriptor => descriptor.ServiceType == typeof(McpServerTool))
            .Should().BeGreaterThan(0, "the server must expose tools, otherwise tools/list returns -32601");
    }
}
