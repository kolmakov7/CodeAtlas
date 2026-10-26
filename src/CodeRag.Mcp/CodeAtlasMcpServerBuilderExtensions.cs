namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers shared by the API host (remote /mcp) and the MCP sidecar.
/// </summary>
public static class CodeAtlasMcpServerBuilderExtensions
{
    /// <summary>
    /// Registers CodeAtlas tool types on the MCP server.
    /// </summary>
    /// <remarks>
    /// The cast to <see cref="IEnumerable{T}"/> is load-bearing. The SDK also exposes
    /// <c>WithTools&lt;TToolType&gt;(TToolType target)</c>, and passing a <c>List&lt;Type&gt;</c> binds to
    /// that overload instead: it scans the list instance for <c>[McpServerTool]</c> methods, finds
    /// none and silently registers zero tools (the server then advertises only the logging
    /// capability and answers tools/list with -32601). Keep the cast, or the tools disappear.
    /// </remarks>
    public static IMcpServerBuilder WithCodeAtlasTools(
        this IMcpServerBuilder builder,
        IEnumerable<Type> toolTypes)
        => builder.WithTools((IEnumerable<Type>)toolTypes);
}
