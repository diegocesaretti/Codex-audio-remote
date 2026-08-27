using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
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

    readonly ConcurrentDictionary<string, CachedState> states = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource lifetime = new();
    readonly object socketSync = new();
    ClientWebSocket? socket;
    Task? loopTask;
    volatile bool connected;
    volatile string status = "starting";
    long lastUpdateTicks;
    int disposed;

    public static HomeAssistantWebSocketCache? Current { get; private set; }
    public bool IsConnected => connected;
    public string Status => status;
    public int EntityCount => states.Count;

    public HomeAssistantWebSocketCache() => Current = this;

    public void Start()
    {
        if (loopTask is null) loopTask = Task.Run(() => RunLoopAsync(lifetime.Token));
    }

    public string GetCompactContext(int maxEntities = 80)
    {
        if (!connected || states.IsEmpty) return "";

        var lines = states.Values
            .Where(s => IsRelevant(s.EntityId))
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

        return "HOME ASSISTANT LIVE STATE SNAPSHOT (cache age " + age + ", total states " + states.Count + ")\n"
             + string.Join("\n", lines);
    }

    async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!AppSettings.HomeAssistantEnabled)
            {
                connected = false;
                status = "disabled";
                await DelayAsync(token, 2000);
                continue;
            }

            var accessToken = RealtimeMirrorSettings.HomeAssistantAccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                connected = false;
                status = "token missing";
                await DelayAsync(token, 1500);
                continue;
            }

            ClientWebSocket? ws = null;
            try
            {
                status = "connecting";
                ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                lock (socketSync) socket = ws;
                var uri = BuildWebSocketUri(AppSettings.HomeAssistantBaseUrl);
                await ws.ConnectAsync(uri, token);

                var first = await ReceiveJsonAsync(ws, token);
                if (!first.TryGetProperty("type", out var firstType) || firstType.GetString() != "auth_required")
                    throw new InvalidOperationException("Home Assistant did not request WebSocket authentication.");

                await SendJsonAsync(ws, new { type = "auth", access_token = accessToken }, token);
                var auth = await ReceiveJsonAsync(ws, token);
                if (!auth.TryGetProperty("type", out var authType) || authType.GetString() != "auth_ok")
                    throw new UnauthorizedAccessException("Home Assistant WebSocket authentication failed.");

                var version = auth.TryGetProperty("ha_version", out var versionProp) ? versionProp.GetString() ?? "" : "";
                connected = true;
                status = string.IsNullOrWhiteSpace(version) ? "connected" : "connected · HA " + version;
                Console.WriteLine("HA fast-path WebSocket connected" + (string.IsNullOrWhiteSpace(version) ? "" : " · " + version));

                await SendJsonAsync(ws, new { id = 1, type = "get_states" }, token);
                await SendJsonAsync(ws, new { id = 2, type = "subscribe_events", event_type = "state_changed" }, token);

                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var root = await ReceiveJsonAsync(ws, token);
                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    if (type == "result")
                    {
                        var id = root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var parsedId) ? parsedId : -1;
                        var success = root.TryGetProperty("success", out var successProp) && successProp.ValueKind == JsonValueKind.True;
                        if (!success)
                        {
                            var error = root.TryGetProperty("error", out var err) ? err.ToString() : "unknown error";
                            throw new InvalidOperationException("HA WebSocket command failed: " + error);
                        }
                        if (id == 1 && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                        {
                            ReplaceStates(result);
                            Console.WriteLine("HA fast-path cache primed · " + states.Count + " states");
                        }
                        else if (id == 2)
                        {
                            Console.WriteLine("HA fast-path subscribed to state_changed");
                        }
                        continue;
                    }

                    if (type == "event") ApplyStateChanged(root);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                connected = false;
                status = "disconnected · " + ex.Message;
                Console.WriteLine("HA fast-path reconnect: " + ex.Message);
                await DelayAsync(token, 1800);
            }
            finally
            {
                connected = false;
                lock (socketSync)
                {
                    if (ReferenceEquals(socket, ws)) socket = null;
                }
                try { ws?.Dispose(); } catch { }
            }
        }
    }

    void ReplaceStates(JsonElement array)
    {
        var replacement = new Dictionary<string, CachedState>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            var parsed = ParseState(item);
            if (parsed is not null) replacement[parsed.EntityId] = parsed;
        }
        states.Clear();
        foreach (var pair in replacement) states[pair.Key] = pair.Value;
        Touch();
    }

    void ApplyStateChanged(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object) return;
        if (!evt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var entityId = data.TryGetProperty("entity_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(entityId)) return;

        if (!data.TryGetProperty("new_state", out var newState) || newState.ValueKind == JsonValueKind.Null)
        {
            states.TryRemove(entityId, out _);
            Touch();
            return;
        }
        if (newState.ValueKind != JsonValueKind.Object) return;
        var parsed = ParseState(newState);
        if (parsed is null) return;
        states[parsed.EntityId] = parsed;
        Touch();
    }

    static CachedState? ParseState(JsonElement item)
    {
        var entityId = item.TryGetProperty("entity_id", out var entity) ? entity.GetString() : null;
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        var state = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "" : "";
        var attributes = item.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? attrs.Clone()
            : EmptyObject();
        var friendly = attributes.TryGetProperty("friendly_name", out var friendlyProp)
            ? friendlyProp.GetString() ?? entityId
            : entityId;
        return new CachedState(entityId, state, friendly, attributes);
    }

    static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
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
        AddAttribute(details, state.Attributes, "volume_level", "volume");
        var suffix = details.Count == 0 ? "" : " · " + string.Join(" · ", details);
        return state.EntityId + " | " + state.FriendlyName + " | " + state.State + suffix;
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

    static bool IsRelevant(string entityId)
        => RelevantDomains.Contains(DomainOf(entityId), StringComparer.OrdinalIgnoreCase);

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
                throw new WebSocketException("Home Assistant closed the WebSocket connection.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
            throw new InvalidOperationException("Unexpected Home Assistant WebSocket message type.");
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    static async Task DelayAsync(CancellationToken token, int ms)
    {
        try { await Task.Delay(ms, token); }
        catch (OperationCanceledException) { }
    }

    void Touch() => Interlocked.Exchange(ref lastUpdateTicks, DateTimeOffset.UtcNow.Ticks);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lock (socketSync)
        {
            try { socket?.Abort(); } catch { }
            try { socket?.Dispose(); } catch { }
            socket = null;
        }
        try { loopTask?.Wait(500); } catch { }
        lifetime.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}

internal static class HomeAssistantFastPathBootstrap
{
    static HomeAssistantWebSocketCache? cache;

    [ModuleInitializer]
    internal static void Initialize()
    {
        cache = new HomeAssistantWebSocketCache();
        cache.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { cache?.Dispose(); } catch { }
        };
    }
}
