namespace JeekWindowsOptimizer.Mcp;

/// <summary>Registers this GUI build for the fixed per-user MCP adapter.</summary>
internal static class McpAdapterRegistration
{
    private static bool IsDebugBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    private static string WorkspaceRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, ".."));

    private static string InstanceId { get; } = IsDebugBuild
        ? McpPipeNames.InstanceId(AppContext.BaseDirectory)
        : "release";

    private static string DebugMcpPipeName { get; } =
        McpPipeNames.Debug(IsDebugBuild ? InstanceId : null);

    private static string ProductMcpPipeName { get; } =
        McpPipeNames.Product(IsDebugBuild ? InstanceId : null);

    public static McpRegisteredInstance RegisterCurrentInstance()
    {
        var sourceAdapter = Path.Combine(AppContext.BaseDirectory, "JeekWindowsOptimizerMcp.exe");
        // Always refresh the fixed adapter when the side-by-side publish next to this app
        // differs. Agents launch that fixed path (not bin\), so Debug worktrees must be able
        // to push routing fixes even if a Release instance is also registered. A locked
        // destination keeps the previous file — safe for this protocol-agnostic forwarder.
        if (!McpAdapterRegistry.EnsureAdapterInstalled(sourceAdapter, allowUpdate: true))
        {
            throw new IOException("Could not install the fixed JeekWindowsOptimizer MCP adapter.");
        }

        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath))
            throw new InvalidOperationException("The current JeekWindowsOptimizer executable path is unavailable.");

        var registration = new McpRegisteredInstance(
            InstanceId,
            appPath,
            ProductMcpPipeName,
            IsDebugBuild ? DebugMcpPipeName : "",
            IsDebugBuild,
            WorkspaceRoot);
        McpAdapterRegistry.WriteInstance(registration);
        return registration;
    }
}
