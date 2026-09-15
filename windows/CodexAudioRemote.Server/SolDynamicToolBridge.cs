using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    bool CatalogChanged,
    int ToolCount,
    string CatalogSignature);

/// <summary>
/// Makes the SOL plugin tool catalog available to Codex App Server as thread-scoped
/// dynamic tools. Home Assistant remains the single owner of its WebSocket/cache;
/// Audio Remote only consumes the tools exposed by SOL.
/// </summary>
internal sealed class SolDynamicToolBridge : IDisposable
{
    const string CatalogMarker = ".realtime-dynamic-tools-v1";
    static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromSeconds(30);

    readonly HttpClient http;
    readonly string baseUrl;
    readonly string token;
    readonly string markerPath;
    readonly object sync = new();
    Dictionary<string, SolDynamicToolDescriptor> tools = new(StringComparer.Ordinal);
    DateTimeOffset catalogLoadedAt = DateTimeOffset.MinValue;
    bool disposed;

    SolDynamicToolBridge(string baseUrl, string token, string dataDir)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.token = token;
        markerPath = Path.Combine(dataDir, CatalogMarker);
        // This is a loopback SOL call. A slow/broken catalog must not stall voice wake for 8s.
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
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

        List<SolDynamicToolDescriptor>? catalog = null;
        var loadedFresh = false;
        lock (sync)
        {
            if (tools.Count > 0 && DateTimeOffset.UtcNow - catalogLoadedAt < CatalogRefreshInterval)
                catalog = tools.Values.OrderBy(item => item.PluginId, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
        }

        if (catalog is null)
        {
            try
            {
                catalog = await LoadCatalogAsync(cancellationToken);
                lock (sync)
                {
                    tools = catalog.ToDictionary(item => item.Name, item => item, StringComparer.Ordinal);
                    catalogLoadedAt = DateTimeOffset.UtcNow;
                }
                loadedFresh = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (sync)
                    catalog = tools.Values.OrderBy(item => item.PluginId, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
                SolPluginHost.Log("warn", $"Realtime SOL tool catalog refresh failed; continuing voice with cached tools · cached={catalog.Count} · {ex.GetType().Name}: {ex.Message}");
                SolPluginHost.Health("degraded", "SOL dynamic tool catalog temporarily unavailable; voice remains available.");
            }
        }

        catalog ??= new List<SolDynamicToolDescriptor>();
        var dynamicTools = catalog.Select(item => (object)new
        {
            name = item.Name,
            description = item.Description + $"\nProvided live by SOL plugin {item.PluginId}. Scope: {item.RequiredScope}.",
            inputSchema = item.InputSchema
        }).ToArray();

        var names = string.Join(", ", catalog.Select(item => item.Name));
        var instructions = catalog.Count == 0
            ? "SOL PLUGIN TOOLS are temporarily unavailable for this session. Continue normal voice conversation, but do not pretend to know current Home Assistant state or claim a local action succeeded."
            : "SOL PLUGIN TOOLS ARE AUTHORITATIVE FOR LIVE LOCAL/PLUGIN DATA. " +
              "When a SOL tool can answer or perform the request, use it before browser, web search, computer use, shell, or UI automation. " +
              "For any question about CURRENT Home Assistant state, call a home_assistant_get_state or home_assistant_search_states tool before answering, even when a startup snapshot contains the entity. " +
              "Treat the startup Home Assistant snapshot as an entity-name/id hint, not as permanently current state. " +
              "For Home Assistant devices, areas, services, or control, always use the available home_assistant_* SOL tools; never open a browser to determine a local device state. " +
              "Read-only Home Assistant tools are backed by SOL's event-driven local cache and are the low-latency path. " +
              "For an action tool, invoke it only when the current human speech explicitly requested that action, and supply confirmedByUser=true only in that case. " +
              "Do not infer authorization from prior conversation. Available SOL tools: " + names + ".";

        var signature = ComputeCatalogSignature(catalog);
        var rememberedSignature = ReadRememberedSignature();
        // A transient catalog failure must never force a fresh Codex thread. Only a
        // successfully refreshed catalog is allowed to rotate the persistent thread.
        var catalogChanged = loadedFresh && !string.Equals(rememberedSignature, signature, StringComparison.Ordinal);
        SolPluginHost.Log("info", $"Realtime SOL dynamic tools prepared · count={catalog.Count} · fresh={loadedFresh} · catalogChanged={catalogChanged} · catalog={signature[..Math.Min(12, signature.Length)]} · tools={names}");
        return new SolDynamicToolSession(dynamicTools, instructions, catalogChanged, catalog.Count, signature);
    }

    public void MarkThreadReady(string catalogSignature)
    {
        if (disposed || string.IsNullOrWhiteSpace(catalogSignature)) return;
        try { File.WriteAllText(markerPath, catalogSignature.Trim() + Environment.NewLine); }
        catch (Exception ex) { SolPluginHost.Log("warn", "Could not persist Realtime SOL tool catalog signature: " + ex.Message); }
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
        var safeArguments = arguments.ValueKind == JsonValueKind.Object
            ? arguments
            : JsonDocument.Parse("{}").RootElement.Clone();
        var payload = new Dictionary<string, object?>
        {
            ["name"] = tool.Name,
            ["arguments"] = safeArguments
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
        return result.OrderBy(item => item.PluginId, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
    }

    string ReadRememberedSignature()
    {
        try { return File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : ""; }
        catch { return ""; }
    }

    static string ComputeCatalogSignature(IEnumerable<SolDynamicToolDescriptor> catalog)
    {
        var canonical = string.Join("\n", catalog.Select(item =>
            item.PluginId + "\t" + item.Name + "\t" + item.RequiredScope + "\t" + item.Description + "\t" + item.InputSchema.GetRawText()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
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
