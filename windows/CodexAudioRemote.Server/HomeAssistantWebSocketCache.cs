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
    readonly SemaphoreSlim persistGate = new(1, 1);
    readonly object persistSync = new();
    readonly string? persistPath;
    readonly int flushMs;

    Task? loopTask;
    Timer? persistTimer;
    volatile bool connected;
    volatile bool loadedFromDisk;
    long lastUpdateTicks;
    long lastPersistedTicks;
    long eventCount;
    int disposed;

    HomeAssistantWebSocketCache()
    {
        flushMs = Math.Clamp(SolPluginHost.IntSetting("home_assistant_cache_flush_ms") ?? 2000, 250, 60000);
        if (SolPluginHost.Enabled)
        {
            var dataDir = (Environment.GetEnvironmentVariable("SOL_PLUGIN_DATA_DIR") ?? "").Trim();
            if (dataDir.Length > 0)
            {
                Directory.CreateDirectory(dataDir);
                persistPath = Path.Combine(dataDir, "home-assistant-state-cache.json");
                LoadPersisted();
            }
        }
    }

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
        if (states.IsEmpty) return "";
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
        var source = connected ? "LIVE" : loadedFromDisk ? "PERSISTED/OFFLINE" : "CACHED/OFFLINE";
        return $"HOME ASSISTANT {source} CACHE (age {age})\n" + string.Join("\n", lines);
    }

    async Task RunLoopAsync(CancellationToken token)
    {
        var failures = 0;
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

                // Match the SOL Home Assistant plugin's race-free startup: subscribe first,
                // then buffer state_changed events while get_states is in flight.
                await SendJsonAsync(ws, new { id = 2, type = "subscribe_events", event_type = "state_changed" }, token);
                await WaitForSuccessfulResultAsync(ws, 2, token);

                var buffered = new List<JsonElement>();
                await SendJsonAsync(ws, new { id = 1, type = "get_states" }, token);
                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var root = await ReceiveJsonAsync(ws, token);
                    if (IsResult(root, 1))
                    {
                        if (!IsSuccess(root)) throw new InvalidOperationException("HA get_states failed during cache prime.");
                        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException("HA get_states returned an invalid snapshot.");
                        ReplaceStates(result);
                        break;
                    }
                    if (IsStateEvent(root)) buffered.Add(root.Clone());
                }
                foreach (var evt in buffered) ApplyStateChanged(evt);

                connected = true;
                failures = 0;
                Touch(schedulePersist: true);
                Console.WriteLine($"HA context cache · WebSocket connected · primed={states.Count} · buffered={buffered.Count} · disk={loadedFromDisk}");

                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var root = await ReceiveJsonAsync(ws, token);
                    if (IsStateEvent(root)) ApplyStateChanged(root);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                connected = false;
                failures++;
                var backoff = Math.Min(30000, 1000 * (1 << Math.Min(5, Math.Max(0, failures - 1)))) + Random.Shared.Next(0, 500);
                Console.WriteLine($"HA context cache · reconnect in {backoff}ms: {ex.Message}");
                await Delay(token, backoff);
            }
            finally
            {
                connected = false;
                SchedulePersist();
            }
        }
    }

    static async Task WaitForSuccessfulResultAsync(ClientWebSocket ws, int expectedId, CancellationToken token)
    {
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var root = await ReceiveJsonAsync(ws, token);
            if (!IsResult(root, expectedId)) continue;
            if (!IsSuccess(root)) throw new InvalidOperationException($"HA WebSocket command {expectedId} failed.");
            return;
        }
        throw new WebSocketException("Home Assistant closed while waiting for subscription result.");
    }

    static bool IsResult(JsonElement root, int id)
        => root.TryGetProperty("type", out var type) && type.GetString() == "result" &&
           root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var parsed) && parsed == id;

    static bool IsSuccess(JsonElement root)
        => root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    static bool IsStateEvent(JsonElement root)
        => root.TryGetProperty("type", out var type) && type.GetString() == "event" &&
           root.TryGetProperty("event", out var evt) && evt.ValueKind == JsonValueKind.Object &&
           evt.TryGetProperty("event_type", out var eventType) && eventType.GetString() == "state_changed";

    void ReplaceStates(JsonElement array)
    {
        states.Clear();
        foreach (var item in array.EnumerateArray())
        {
            var parsed = ParseState(item);
            if (parsed is not null) states[parsed.EntityId] = parsed;
        }
        Touch(schedulePersist: true);
    }

    void ApplyStateChanged(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt) || !evt.TryGetProperty("data", out var data)) return;
        var entityId = data.TryGetProperty("entity_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(entityId)) return;
        if (!data.TryGetProperty("new_state", out var newState) || newState.ValueKind == JsonValueKind.Null)
        {
            states.TryRemove(entityId, out _);
            Interlocked.Increment(ref eventCount);
            Touch(schedulePersist: true);
            return;
        }
        var parsed = ParseState(newState);
        if (parsed is not null)
        {
            states[parsed.EntityId] = parsed;
            Interlocked.Increment(ref eventCount);
            Touch(schedulePersist: true);
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

    void Touch(bool schedulePersist)
    {
        Interlocked.Exchange(ref lastUpdateTicks, DateTimeOffset.UtcNow.Ticks);
        if (schedulePersist) SchedulePersist();
    }

    void SchedulePersist()
    {
        if (persistPath is null || Volatile.Read(ref disposed) != 0) return;
        lock (persistSync)
        {
            persistTimer ??= new Timer(_ =>
            {
                lock (persistSync)
                {
                    persistTimer?.Dispose();
                    persistTimer = null;
                }
                _ = PersistAsync();
            }, null, flushMs, Timeout.Infinite);
        }
    }

    async Task PersistAsync()
    {
        if (persistPath is null) return;
        await persistGate.WaitAsync();
        try
        {
            var snapshot = states.Values
                .OrderBy(value => value.EntityId, StringComparer.OrdinalIgnoreCase)
                .Select(value => new
                {
                    entityId = value.EntityId,
                    state = value.State,
                    friendlyName = value.FriendlyName,
                    attributes = value.Attributes
                })
                .ToArray();
            var payload = JsonSerializer.Serialize(new
            {
                version = 1,
                updatedAt = new DateTimeOffset(Math.Max(Interlocked.Read(ref lastUpdateTicks), DateTimeOffset.UtcNow.Ticks), TimeSpan.Zero),
                eventCount = Interlocked.Read(ref eventCount),
                states = snapshot
            });
            var directory = Path.GetDirectoryName(persistPath)!;
            Directory.CreateDirectory(directory);
            var temporary = persistPath + "." + Environment.ProcessId + ".tmp";
            await File.WriteAllTextAsync(temporary, payload);
            File.Move(temporary, persistPath, true);
            Interlocked.Exchange(ref lastPersistedTicks, DateTimeOffset.UtcNow.Ticks);
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "HA cache persist failed: " + ex.Message);
        }
        finally { persistGate.Release(); }
    }

    void LoadPersisted()
    {
        if (persistPath is null || !File.Exists(persistPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(persistPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("states", out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var item in array.EnumerateArray())
            {
                var entityId = item.TryGetProperty("entityId", out var entity) ? entity.GetString() : null;
                if (string.IsNullOrWhiteSpace(entityId)) continue;
                var state = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "" : "";
                var friendly = item.TryGetProperty("friendlyName", out var friendlyProp) ? friendlyProp.GetString() ?? entityId : entityId;
                var attrs = item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
                    ? attributes.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                states[entityId] = new CachedState(entityId, state, friendly, attrs);
            }
            if (root.TryGetProperty("updatedAt", out var updatedAt) && updatedAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(updatedAt.GetString(), out var parsed))
                Interlocked.Exchange(ref lastUpdateTicks, parsed.UtcTicks);
            else if (File.GetLastWriteTimeUtc(persistPath) is var fileTime)
                Interlocked.Exchange(ref lastUpdateTicks, new DateTimeOffset(fileTime, TimeSpan.Zero).Ticks);
            if (root.TryGetProperty("eventCount", out var count) && count.TryGetInt64(out var parsedCount))
                Interlocked.Exchange(ref eventCount, parsedCount);
            loadedFromDisk = states.Count > 0;
            Console.WriteLine($"HA context cache · loaded {states.Count} persisted states before WebSocket connect");
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "HA cache load failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lock (persistSync)
        {
            persistTimer?.Dispose();
            persistTimer = null;
        }
        try { PersistAsync().GetAwaiter().GetResult(); } catch { }
        lifetime.Dispose();
        persistGate.Dispose();
    }
}
