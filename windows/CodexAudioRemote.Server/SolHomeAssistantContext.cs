using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

internal static class SolHomeAssistantContext
{
    // Query the HA cache by domain instead of taking the first 100 global matches and
    // filtering afterwards. The latter silently dropped useful entities in larger homes.
    // Order is intentional: compact controllable domains first, high-cardinality sensors last.
    static readonly string[] SnapshotDomains =
    {
        "climate", "light", "switch", "cover", "fan", "media_player", "lock", "alarm_control_panel",
        "vacuum", "water_heater", "scene", "script", "input_boolean", "input_number", "input_select",
        "select", "number", "button", "person", "binary_sensor", "sensor", "device_tracker", "weather"
    };

    sealed record DomainSnapshot(
        string Domain,
        List<JsonElement> Results,
        bool Connected,
        string? LastEvent,
        string? Error);

    public static async Task<string> GetContextAsync(int maxEntities, CancellationToken token)
    {
        if (!SolPluginHost.Enabled) return "";
        var baseUrl = (Environment.GetEnvironmentVariable("SOL_PLUGIN_API_URL")
                       ?? Environment.GetEnvironmentVariable("SOL_CORE_URL")
                       ?? "").Trim().TrimEnd('/');
        var authToken = (Environment.GetEnvironmentVariable("SOL_PLUGIN_TOKEN") ?? "").Trim();
        if (baseUrl.Length == 0 || authToken.Length == 0) return "";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var queries = SnapshotDomains
                .Select(domain => QueryDomainAsync(http, baseUrl, authToken, domain, token))
                .ToArray();
            var snapshots = await Task.WhenAll(queries);

            var failures = snapshots.Where(item => !string.IsNullOrWhiteSpace(item.Error)).ToArray();
            if (failures.Length > 0)
                SolPluginHost.Log("warn", $"SOL Home Assistant startup snapshot partial · failedDomains={failures.Length}/{snapshots.Length} · {string.Join(", ", failures.Take(4).Select(item => item.Domain + ":" + item.Error))}");

            var limit = Math.Clamp(maxEntities, 1, 100);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var snapshot in snapshots)
            {
                foreach (var item in snapshot.Results)
                {
                    if (lines.Count >= limit) break;
                    var entityId = ReadString(item, "entityId");
                    if (string.IsNullOrWhiteSpace(entityId) || !seen.Add(entityId)) continue;
                    var dot = entityId.IndexOf('.');
                    var domain = dot > 0 ? entityId[..dot] : entityId;
                    if (!SnapshotDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) continue;
                    var state = ReadString(item, "state") ?? "";
                    var friendly = ReadString(item, "friendlyName") ?? entityId;
                    var details = new List<string>();
                    if (item.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                    {
                        AddAttribute(details, attrs, "current_temperature", "current");
                        AddAttribute(details, attrs, "temperature", "target");
                        AddAttribute(details, attrs, "hvac_action", "hvac");
                        AddAttribute(details, attrs, "brightness", "brightness");
                        AddAttribute(details, attrs, "percentage", "percentage");
                        AddAttribute(details, attrs, "current_position", "position");
                        AddAttribute(details, attrs, "unit_of_measurement", "unit");
                    }
                    lines.Add(entityId + " | " + friendly + " | " + state +
                              (details.Count == 0 ? "" : " · " + string.Join(" · ", details)));
                }
                if (lines.Count >= limit) break;
            }
            if (lines.Count == 0) return "";

            var connected = snapshots.Any(item => item.Connected);
            var lastEvent = snapshots
                .Select(item => item.LastEvent)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderByDescending(value => value, StringComparer.Ordinal)
                .FirstOrDefault();
            var source = connected ? "LIVE" : "PERSISTED/OFFLINE";
            return $"HOME ASSISTANT STARTUP SNAPSHOT {source} VIA SOL PLUGIN" +
                   (string.IsNullOrWhiteSpace(lastEvent) ? "" : $" · updated={lastEvent}") +
                   $" · entities={lines.Count}" +
                   "\n" + string.Join("\n", lines);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "";
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "SOL Home Assistant context unavailable: " + ex.Message);
            return "";
        }
    }

    static async Task<DomainSnapshot> QueryDomainAsync(
        HttpClient http,
        string baseUrl,
        string authToken,
        string domain,
        CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/plugin-api/mcp/tools/invoke-read");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            request.Content = JsonContent.Create(new
            {
                name = "home_assistant_search_states",
                arguments = new { query = domain + ".", limit = 100 }
            });
            using var response = await http.SendAsync(request, token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                return new DomainSnapshot(domain, new List<JsonElement>(), false, null, $"HTTP {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = document.RootElement;
            var results = new List<JsonElement>();
            if (root.TryGetProperty("results", out var array) && array.ValueKind == JsonValueKind.Array)
                results.AddRange(array.EnumerateArray().Select(item => item.Clone()));

            var connected = false;
            string? lastEvent = null;
            if (root.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                connected = cache.TryGetProperty("connected", out var connectedProp) && connectedProp.ValueKind == JsonValueKind.True;
                lastEvent = ReadString(cache, "lastEventAt") ?? ReadString(cache, "lastSnapshotAt");
            }
            return new DomainSnapshot(domain, results, connected, lastEvent, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DomainSnapshot(domain, new List<JsonElement>(), false, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static void AddAttribute(List<string> output, JsonElement attrs, string name, string label)
    {
        if (!attrs.TryGetProperty(name, out var value)) return;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(text)) output.Add(label + "=" + text);
    }
}
