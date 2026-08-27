using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal sealed class HomeAssistantWebSocketCache : IDisposable
{
    sealed record CachedState(string EntityId, string State, string FriendlyName, JsonElement Attributes);

    static readonly string[] RelevantDomains =
    {
        "light", "switch", "climate", "cover", "fan", "media_player", "lock", "scene", "script",
        "input_boolean", "input_number", "input_select", "button", "vacuum", "water_heater"
    };

    static readonly object globalSync = new();
    static HomeAssistantWebSocketCache? global;

    readonly ConcurrentDictionary<string, CachedState> states = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource lifetime = new();
    Task? loopTask;
    volatile bool connected;
    long lastUpdateTicks;
    int disposed;

    public static void StartGlobal()
    {
        lock (globalSync)
        {
            if (global is not null) return;
            global = new HomeAssistantWebSocketCache();
            global.Start();
        }
    }

    public static void DisposeGlobal()
    {
        lock (globalSync)
        {
            global?.Dispose();
            global = null;
        }
    }

    public static string GetGlobalContext(int maxEntities = 80)
    {
        lock (globalSync) return global?.GetCompactContext(maxEntities) ?? "";
    }

    void Start() => loopTask ??= Task.Run(() => RunLoopAsync(lifetime.Token));

    string GetCompactContext(int maxEntities)
    {
        if (!connected || states.IsEmpty) return "";
        var lines = states.Values
            .Where(s => RelevantDomains.Contains(DomainOf(s.EntityId), StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => DomainOf(s.EntityId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maxEntities, 1, 300))
            .Select(FormatState)
            .ToArray();
        if (lines.Length == 0) return "";

        var ticks = Interlocked.Read(ref lastUpdateTicks);
        var age = ticks <= 0
            ? "unknown"
            : Math.Max(0, (DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds).ToString("0.0") + "s";
        return "HOME ASSISTANT LIVE CACHE (age " + age + ")\n" + string.Join("\n", lines);
    }

    async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!AppSettings.HomeAssistantEnabled)
            {
                connected = false;
                await Delay(token, 2000);
                continue;
            }

            var accessToken = RealtimeMirrorSettings.HomeAssistantAccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                connected = false;
                await Delay(token, 1500);
                continue;
            }

            using var ws = new ClientWebSocket();
            try
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(BuildWebSocketUri(AppSettings.HomeAssistantBaseUrl), token);

                var first = await ReceiveJsonAsync(ws, token);
                if (!first.TryGetProperty("type", out var firstType) || firstType.GetString() != "auth_required")
                    throw new InvalidOperationException("HA WebSocket did not request authentication.");

                await SendJsonAsync(ws, new { type = "auth", access_token = accessToken }, token);
                var auth = await ReceiveJsonAsync(ws, token);
                if (!auth.TryGetProperty("type", out var authType) || authType.GetString() != "auth_ok")
                    throw new UnauthorizedAccessException("Home Assistant WebSocket authentication failed.");

                connected = true;
                Console.WriteLine("HA context cache · WebSocket connected");
                await SendJsonAsync(ws, new { id = 1, type = "get_states" }, token);
                await SendJsonAsync(ws, new { id = 2, type = "subscribe_events", event_type = "state_changed" }, token);

                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var root = await ReceiveJsonAsync(ws, token);
                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    if (type == "result" && root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id) && id == 1)
                    {
                        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
                            root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                            ReplaceStates(result);
                        continue;
                    }
                    if (type == "event") ApplyStateChanged(root);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                connected = false;
                Console.WriteLine("HA context cache · reconnect: " + ex.Message);
                await Delay(token, 1800);
            }
            finally { connected = false; }
        }
    }

    void ReplaceStates(JsonElement array)
    {
        states.Clear();
        foreach (var item in array.EnumerateArray())
        {
            var parsed = ParseState(item);
            if (parsed is not null) states[parsed.EntityId] = parsed;
        }
        Touch();
        Console.WriteLine("HA context cache · primed " + states.Count + " states");
    }

    void ApplyStateChanged(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt) || !evt.TryGetProperty("data", out var data)) return;
        var entityId = data.TryGetProperty("entity_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(entityId)) return;
        if (!data.TryGetProperty("new_state", out var newState) || newState.ValueKind == JsonValueKind.Null)
        {
            states.TryRemove(entityId, out _);
            Touch();
            return;
        }
        var parsed = ParseState(newState);
        if (parsed is not null)
        {
            states[parsed.EntityId] = parsed;
            Touch();
        }
    }

    static CachedState? ParseState(JsonElement item)
    {
        var entityId = item.TryGetProperty("entity_id", out var entity) ? entity.GetString() : null;
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        var state = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "" : "";
        var attrs = item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
            ? attributes.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        var friendly = attrs.TryGetProperty("friendly_name", out var friendlyProp) ? friendlyProp.GetString() ?? entityId : entityId;
        return new CachedState(entityId, state, friendly, attrs);
    }

    static string FormatState(CachedState state)
    {
        var details = new List<string>();
        AddAttribute(details, state.Attributes, "current_temperature", "current");
        AddAttribute(details, state.Attributes, "temperature", "target");
        AddAttribute(details, state.Attributes, "hvac_action", "hvac");
        AddAttribute(details, state.Attributes, "brightness", "brightness");
        AddAttribute(details, state.Attributes, "percentage", "percentage");
        AddAttribute(details, state.Attributes, "current_position", "position");
        return state.EntityId + " | " + state.FriendlyName + " | " + state.State +
               (details.Count == 0 ? "" : " · " + string.Join(" · ", details));
    }

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

    static string DomainOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot <= 0 ? entityId : entityId[..dot];
    }

    static Uri BuildWebSocketUri(string baseUrl)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/'));
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/api/websocket",
            Query = ""
        };
        if ((builder.Scheme == "ws" && builder.Port == 80) || (builder.Scheme == "wss" && builder.Port == 443))
            builder.Port = -1;
        return builder.Uri;
    }

    static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Home Assistant closed the WebSocket.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    static Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken token)
        => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text, true, token);

    static async Task Delay(CancellationToken token, int ms)
    {
        try { await Task.Delay(ms, token); }
        catch (OperationCanceledException) { }
    }

    void Touch() => Interlocked.Exchange(ref lastUpdateTicks, DateTimeOffset.UtcNow.Ticks);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}