using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

internal static class SolHomeAssistantContext
{
    static readonly HashSet<string> RelevantDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "switch", "climate", "cover", "fan", "media_player", "lock", "scene", "script",
        "input_boolean", "input_number", "input_select", "button", "vacuum", "water_heater", "person"
    };

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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/plugin-api/mcp/tools/invoke-read");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            request.Content = JsonContent.Create(new
            {
                name = "home_assistant_search_states",
                // Every Home Assistant entity_id contains a dot. The HA plugin search
                // therefore returns a bounded snapshot without Audio Remote owning HA state.
                arguments = new { query = ".", limit = 100 }
            });
            using var response = await http.SendAsync(request, token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                SolPluginHost.Log("warn", $"SOL Home Assistant context unavailable · HTTP {(int)response.StatusCode}: {text}");
                return "";
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = document.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return "";

            var lines = new List<string>();
            foreach (var item in results.EnumerateArray())
            {
                if (lines.Count >= Math.Clamp(maxEntities, 1, 100)) break;
                var entityId = ReadString(item, "entityId");
                if (string.IsNullOrWhiteSpace(entityId)) continue;
                var dot = entityId.IndexOf('.');
                var domain = dot > 0 ? entityId[..dot] : entityId;
                if (!RelevantDomains.Contains(domain)) continue;
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
                }
                lines.Add(entityId + " | " + friendly + " | " + state +
                          (details.Count == 0 ? "" : " · " + string.Join(" · ", details)));
            }
            if (lines.Count == 0) return "";

            var connected = false;
            string? lastEvent = null;
            if (root.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                connected = cache.TryGetProperty("connected", out var connectedProp) && connectedProp.ValueKind == JsonValueKind.True;
                lastEvent = ReadString(cache, "lastEventAt") ?? ReadString(cache, "lastSnapshotAt");
            }
            var source = connected ? "LIVE" : "PERSISTED/OFFLINE";
            return $"HOME ASSISTANT {source} CACHE VIA SOL PLUGIN" +
                   (string.IsNullOrWhiteSpace(lastEvent) ? "" : $" · updated={lastEvent}") +
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
