using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed record SolDynamicToolDescriptor(
    string PluginId,
    string Name,
    string Description,
    JsonElement InputSchema,
    string RequiredScope);

internal sealed record SolDynamicToolSession(
    object[] DynamicTools,
    string DeveloperInstructions,
    bool ForceNewThread,
    int ToolCount);

/// <summary>
/// Makes the SOL plugin tool catalog available to Codex App Server as thread-scoped
/// dynamic tools. Home Assistant remains the single owner of its WebSocket/cache;
/// Audio Remote only consumes the tools exposed by SOL.
/// </summary>
internal sealed class SolDynamicToolBridge : IDisposable
{
    const string MigrationMarker = ".realtime-dynamic-tools-v1";

    readonly HttpClient http;
    readonly string baseUrl;
    readonly string token;
    readonly string markerPath;
    readonly object sync = new();
    Dictionary<string, SolDynamicToolDescriptor> tools = new(StringComparer.Ordinal);
    bool disposed;

    SolDynamicToolBridge(string baseUrl, string token, string dataDir)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.token = token;
        markerPath = Path.Combine(dataDir, MigrationMarker);
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public static SolDynamicToolBridge? TryCreate()
    {
        if (!SolPluginHost.Enabled) return null;
        var baseUrl = (Environment.GetEnvironmentVariable("SOL_PLUGIN_API_URL")
                       ?? Environment.GetEnvironmentVariable("SOL_CORE_URL")
                       ?? "").Trim();
        var token = (Environment.GetEnvironmentVariable("SOL_PLUGIN_TOKEN") ?? "").Trim();
        if (baseUrl.Length == 0 || token.Length == 0) return null;
        var dataDir = (Environment.GetEnvironmentVariable("SOL_PLUGIN_DATA_DIR") ?? "").Trim();
        if (dataDir.Length == 0) dataDir = Path.Combine(AppContext.BaseDirectory, ".sol-data");
        Directory.CreateDirectory(dataDir);
        return new SolDynamicToolBridge(baseUrl, token, dataDir);
    }

    public async Task<SolDynamicToolSession?> PrepareSessionAsync(CancellationToken cancellationToken)
    {
        if (disposed) return null;
        var catalog = await LoadCatalogAsync(cancellationToken);
        lock (sync) tools = catalog.ToDictionary(item => item.Name, item => item, StringComparer.Ordinal);

        var dynamicTools = catalog.Select(item => (object)new
        {
            name = item.Name,
            description = item.Description + $"\nProvided live by SOL plugin {item.PluginId}. Scope: {item.RequiredScope}.",
            inputSchema = item.InputSchema
        }).ToArray();

        var names = string.Join(", ", catalog.Select(item => item.Name));
        var instructions = catalog.Count == 0
            ? "SOL PLUGIN TOOLS: none are currently available. Do not pretend to know live Home Assistant state."
            : "SOL PLUGIN TOOLS ARE AUTHORITATIVE FOR LIVE LOCAL/PLUGIN DATA. " +
              "When a SOL tool can answer or perform the request, use it before browser, web search, computer use, shell, or UI automation. " +
              "For Home Assistant state, devices, areas, services, or control, always use the available home_assistant_* SOL tools; never open a browser to determine a local device state. " +
              "Read-only Home Assistant tools are backed by SOL's event-driven local cache and are the low-latency path. " +
              "For an action tool, invoke it only when the current human speech explicitly requested that action, and supply confirmedByUser=true only in that case. " +
              "Do not infer authorization from prior conversation. Available SOL tools: " + names + ".";

        var forceNew = !File.Exists(markerPath) && !string.IsNullOrWhiteSpace(AppSettings.RealtimePersistentThreadId);
        SolPluginHost.Log("info", $"Realtime SOL dynamic tools prepared · count={catalog.Count} · forceNewThread={forceNew} · tools={names}");
        return new SolDynamicToolSession(dynamicTools, instructions, forceNew, catalog.Count);
    }

    public void MarkThreadReady()
    {
        if (disposed || File.Exists(markerPath)) return;
        try { File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine); }
        catch (Exception ex) { SolPluginHost.Log("warn", "Could not persist Realtime dynamic-tools migration marker: " + ex.Message); }
    }

    public async Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (disposed) throw new ObjectDisposedException(nameof(SolDynamicToolBridge));
        SolDynamicToolDescriptor? tool;
        lock (sync) tools.TryGetValue(toolName, out tool);
        if (tool is null) throw new InvalidOperationException("SOL dynamic tool is not available in this session: " + toolName);

        var route = string.Equals(tool.RequiredScope, "actions", StringComparison.Ordinal)
            ? "/v1/plugin-api/mcp/tools/invoke-action"
            : "/v1/plugin-api/mcp/tools/invoke-read";
        var payload = new Dictionary<string, object?>
        {
            ["name"] = tool.Name,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Object ? arguments : JsonDocument.Parse("{}").RootElement.Clone()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SOL tool {tool.Name} failed HTTP {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        SolPluginHost.Log("info", $"Realtime SOL tool invoked · plugin={tool.PluginId} · tool={tool.Name} · scope={tool.RequiredScope}");
        return document.RootElement.Clone();
    }

    async Task<List<SolDynamicToolDescriptor>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/plugin-api/mcp/tools/available");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SOL tool catalog failed HTTP {(int)response.StatusCode}: {text}");

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (!document.RootElement.TryGetProperty("tools", out var array) || array.ValueKind != JsonValueKind.Array)
            return new List<SolDynamicToolDescriptor>();

        var result = new List<SolDynamicToolDescriptor>();
        foreach (var item in array.EnumerateArray())
        {
            var name = ReadString(item, "name");
            var pluginId = ReadString(item, "pluginId");
            var scope = ReadString(item, "requiredScope") ?? "read";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pluginId)) continue;
            // Audio Remote's own registered MCP tools would recurse back into this process.
            if (string.Equals(pluginId, "codex-audio-remote", StringComparison.OrdinalIgnoreCase)) continue;
            if (scope != "read" && scope != "actions") continue;
            var description = ReadString(item, "description") ?? name;
            var schema = item.TryGetProperty("inputSchema", out var inputSchema) && inputSchema.ValueKind == JsonValueKind.Object
                ? inputSchema.Clone()
                : JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
            result.Add(new SolDynamicToolDescriptor(pluginId, name, description, schema, scope));
        }
        return result;
    }

    static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        http.Dispose();
    }
}
