using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal sealed class HomeAssistantWebSocketCache : IDisposable
{
    internal sealed record Snapshot(bool Configured, bool Connected, string Status, string Version,
        int EntityCount, DateTimeOffset? LastUpdateUtc, IReadOnlyList<string> Preview);

    sealed record CachedState(string EntityId, string State, string FriendlyName, JsonElement Attributes,
        DateTimeOffset? LastChanged, DateTimeOffset? LastUpdated);

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
    volatile string status = "not started";
    volatile string version = "";
    long lastUpdateTicks;
    int disposed;

    public static HomeAssistantWebSocketCache? Current { get; private set; }

    public HomeAssistantWebSocketCache()
    {
        Current = this;
    }

    public void Start()
    {
        if (loopTask is not null) return;
        loopTask = Task.Run(() => RunLoopAsync(lifetime.Token));
    }

    public void RequestReconnect()
    {
        connected = false;
        status = "reconnecting";
        lock (socketSync)
        {
            try { socket?.Abort(); } catch { }
        }
    }

    public Snapshot GetSnapshot(int previewCount = 12)
    {
        var token = TrayController.HomeAssistantAccessToken;
        var preview = states.Values
            .Where(s => IsRelevant(s.EntityId))
            .OrderBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.EntityId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, previewCount))
            .Select(FormatState)
            .ToArray();
        var ticks = Interlocked.Read(ref lastUpdateTicks);
        return new Snapshot(!string.IsNullOrWhiteSpace(token), connected, status, version, states.Count,
            ticks <= 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero), preview);
    }

    public string GetCompactContext(int maxEntities = 120)
    {
        var snapshot = GetSnapshot(0);
        if (!snapshot.Connected || snapshot.EntityCount == 0) return "Home Assistant cache unavailable.";

        var lines = states.Values
            .Where(s => IsRelevant(s.EntityId))
            .OrderBy(s => DomainOf(s.EntityId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maxEntities, 1, 500))
            .Select(FormatState)
            .ToArray();

        var age = snapshot.LastUpdateUtc is null
            ? "unknown"
            : Math.Max(0, (DateTimeOffset.UtcNow - snapshot.LastUpdateUtc.Value).TotalSeconds).ToString("0.0") + "s";
        return "HOME ASSISTANT LIVE CACHE (age " + age + ", " + snapshot.EntityCount + " states)\n" + string.Join("\n", lines);
    }

    async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var baseUrl = TrayController.HomeAssistantBaseUrl;
            var accessToken = TrayController.HomeAssistantAccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                connected = false;
                status = "access token required";
                await DelayReconnect(token, 1500);
                continue;
            }

            ClientWebSocket? ws = null;
            try
            {
                status = "connecting";
                ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                lock (socketSync) socket = ws;
                var uri = BuildWebSocketUri(baseUrl);
                await ws.ConnectAsync(uri, token);

                var first = await ReceiveJsonAsync(ws, token);
                if (!first.TryGetProperty("type", out var firstType) || firstType.GetString() != "auth_required")
                    throw new InvalidOperationException("Home Assistant did not request WebSocket authentication.");

                await SendJsonAsync(ws, new { type = "auth", access_token = accessToken }, token);
                var auth = await ReceiveJsonAsync(ws, token);
                var authType = auth.TryGetProperty("type", out var authTypeProp) ? authTypeProp.GetString() : null;
                if (!string.Equals(authType, "auth_ok", StringComparison.Ordinal))
                {
                    var message = auth.TryGetProperty("message", out var msg) ? msg.GetString() : "authentication failed";
                    throw new UnauthorizedAccessException(message);
                }

                version = auth.TryGetProperty("ha_version", out var versionProp) ? versionProp.GetString() ?? "" : "";
                connected = true;
                status = string.IsNullOrWhiteSpace(version) ? "connected" : "connected · HA " + version;
                Console.WriteLine("HA WebSocket connected · " + uri + (string.IsNullOrWhiteSpace(version) ? "" : " · " + version));

                const int getStatesId = 1;
                const int subscribeId = 2;
                await SendJsonAsync(ws, new { id = getStatesId, type = "get_states" }, token);
                await SendJsonAsync(ws, new { id = subscribeId, type = "subscribe_events", event_type = "state_changed" }, token);

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
                        if (id == getStatesId && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                        {
                            ReplaceStates(result);
                            Console.WriteLine("HA WebSocket cache primed · " + states.Count + " states");
                        }
                        else if (id == subscribeId)
                        {
                            Console.WriteLine("HA WebSocket subscribed to state_changed");
                        }
                        continue;
                    }

                    if (type == "event") ApplyStateChanged(root);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (UnauthorizedAccessException ex)
            {
                connected = false;
                status = "authentication failed";
                Console.WriteLine("HA WebSocket authentication failed: " + ex.Message);
                await DelayReconnect(token, 5000);
            }
            catch (Exception ex)
            {
                connected = false;
                status = "disconnected · " + ex.Message;
                Console.WriteLine("HA WebSocket reconnect: " + ex.Message);
                await DelayReconnect(token, 1500);
            }
            finally
            {
                connected = false;
                lock (socketSync)
                {
                    if (ReferenceEquals(socket, ws)) socket = null;
                }
                if (ws is not null)
                {
                    try { ws.Dispose(); } catch { }
                }
            }
        }
    }

    void ReplaceStates(JsonElement array)
    {
        var replacement = new Dictionary<string, CachedState>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            var state = ParseState(item);
            if (state is not null) replacement[state.EntityId] = state;
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
            : JsonDocument.Parse("{}").RootElement.Clone();
        var friendly = attributes.TryGetProperty("friendly_name", out var friendlyProp)
            ? friendlyProp.GetString() ?? entityId
            : entityId;
        return new CachedState(entityId, state, friendly, attributes,
            ReadTimestamp(item, "last_changed"), ReadTimestamp(item, "last_updated"));
    }

    static DateTimeOffset? ReadTimestamp(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(prop.GetString(), out var value) ? value : null;
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
    {
        var domain = DomainOf(entityId);
        return RelevantDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
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
                throw new WebSocketException("Home Assistant closed the WebSocket connection.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
            throw new InvalidOperationException("Unexpected non-text Home Assistant WebSocket message.");
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    static async Task DelayReconnect(CancellationToken token, int ms)
    {
        try { await Task.Delay(ms, token); } catch (OperationCanceledException) { }
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
        try { loopTask?.Wait(750); } catch { }
        lifetime.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
