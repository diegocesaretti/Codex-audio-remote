using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal sealed class HomeAssistantWebSocketCache : IDisposable
{
    sealed record CachedState(string EntityId, string State, string FriendlyName, JsonElement Attributes, long UpdatedTicks);

    static readonly string[] RelevantDomains =
    {
        "light", "switch", "climate", "cover", "fan", "media_player", "lock", "scene", "script",
        "input_boolean", "input_number", "input_select", "button", "vacuum", "water_heater"
    };

    static readonly object globalSync = new();
    static HomeAssistantWebSocketCache? global;

    readonly ConcurrentDictionary<string, CachedState> states = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingResults = new();
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly object socketSync = new();

    ClientWebSocket? activeSocket;
    Task? loopTask;
    volatile bool connected;
    long lastUpdateTicks;
    int nextCommandId = 10;
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

    public static bool IsGlobalConnected
    {
        get { lock (globalSync) return global?.connected == true; }
    }

    public static Task<string> GetGlobalStateJsonAsync(string entityId, CancellationToken cancellationToken = default)
    {
        HomeAssistantWebSocketCache? instance;
        lock (globalSync) instance = global;
        return instance is null
            ? Task.FromResult(FailureJson("Home Assistant cache is not running."))
            : Task.FromResult(instance.GetStateJson(entityId));
    }

    public static Task<string> ControlGlobalJsonAsync(
        string entityId,
        string action,
        JsonElement? value,
        CancellationToken cancellationToken = default)
    {
        HomeAssistantWebSocketCache? instance;
        lock (globalSync) instance = global;
        return instance is null
            ? Task.FromResult(FailureJson("Home Assistant cache is not running."))
            : instance.ControlAsync(entityId, action, value, cancellationToken);
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

    string GetStateJson(string entityId)
    {
        entityId = (entityId ?? "").Trim();
        if (!connected) return FailureJson("Home Assistant WebSocket is not connected.", entityId);
        if (!states.TryGetValue(entityId, out var state))
            return FailureJson("Entity is not present in the live Home Assistant cache.", entityId);
        return StateJson(state, success: true, confirmed: true, latencyMs: 0, service: null, action: "get_state");
    }

    async Task<string> ControlAsync(string entityId, string action, JsonElement? value, CancellationToken cancellationToken)
    {
        entityId = (entityId ?? "").Trim();
        action = (action ?? "").Trim().ToLowerInvariant();
        if (!connected) return FailureJson("Home Assistant WebSocket is not connected.", entityId, action);
        if (!states.TryGetValue(entityId, out var before))
            return FailureJson("Entity is not present in the live Home Assistant cache.", entityId, action);

        var domain = DomainOf(entityId);
        if (!RelevantDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return FailureJson("Entity domain is not allowed for the fast control tool.", entityId, action);

        if (!TryMapAction(domain, action, value, out var service, out var serviceData, out var expectation, out var mappingError))
            return FailureJson(mappingError ?? "Unsupported action for this entity domain.", entityId, action);

        var started = Stopwatch.GetTimestamp();
        JsonElement response;
        try
        {
            response = await CallServiceAsync(domain, service, entityId, serviceData, cancellationToken);
        }
        catch (Exception ex)
        {
            return FailureJson("Home Assistant call_service failed: " + ex.Message, entityId, action);
        }

        if (!response.TryGetProperty("success", out var successProp) || successProp.ValueKind != JsonValueKind.True)
        {
            var error = response.TryGetProperty("error", out var errorProp) ? errorProp.ToString() : "Home Assistant returned success=false.";
            return FailureJson(error, entityId, action);
        }

        var confirmed = false;
        CachedState? current = states.TryGetValue(entityId, out var immediate) ? immediate : before;
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.6);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states.TryGetValue(entityId, out var candidate))
            {
                current = candidate;
                if (MatchesExpectation(candidate, expectation, before.UpdatedTicks))
                {
                    confirmed = true;
                    break;
                }
            }
            await Task.Delay(25, cancellationToken);
        }

        current ??= before;
        var elapsedMs = (long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Console.WriteLine($"HA WS control · {entityId} · {domain}.{service} · confirmed={confirmed} · {elapsedMs} ms");
        return StateJson(current, success: true, confirmed, elapsedMs, domain + "." + service, action);
    }

    async Task<JsonElement> CallServiceAsync(
        string domain,
        string service,
        string entityId,
        Dictionary<string, object?> serviceData,
        CancellationToken cancellationToken)
    {
        ClientWebSocket ws;
        lock (socketSync)
        {
            ws = activeSocket ?? throw new InvalidOperationException("Home Assistant WebSocket is not connected.");
            if (ws.State != WebSocketState.Open)
                throw new InvalidOperationException("Home Assistant WebSocket is not open.");
        }

        var id = Interlocked.Increment(ref nextCommandId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResults[id] = tcs;
        try
        {
            await SendJsonAsync(ws, new
            {
                id,
                type = "call_service",
                domain,
                service,
                target = new { entity_id = entityId },
                service_data = serviceData
            }, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            pendingResults.TryRemove(id, out _);
        }
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
                lock (socketSync) activeSocket = ws;
                await ws.ConnectAsync(BuildWebSocketUri(AppSettings.HomeAssistantBaseUrl), token);

                var first = await ReceiveJsonAsync(ws, token);
                if (!first.TryGetProperty("type", out var firstType) || firstType.GetString() != "auth_required")
                    throw new InvalidOperationException("HA WebSocket did not request authentication.");

                await SendJsonAsync(ws, new { type = "auth", access_token = accessToken }, token);
                var auth = await ReceiveJsonAsync(ws, token);
                if (!auth.TryGetProperty("type", out var authType) || authType.GetString() != "auth_ok")
                    throw new UnauthorizedAccessException("Home Assistant WebSocket authentication failed.");

                connected = true;
                Console.WriteLine("HA context/control cache · WebSocket connected");
                await SendJsonAsync(ws, new { id = 1, type = "get_states" }, token);
                await SendJsonAsync(ws, new { id = 2, type = "subscribe_events", event_type = "state_changed" }, token);

                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var root = await ReceiveJsonAsync(ws, token);
                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    if (type == "result" && root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                    {
                        if (id == 1)
                        {
                            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
                                root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                                ReplaceStates(result);
                        }
                        else if (pendingResults.TryRemove(id, out var pending))
                        {
                            pending.TrySetResult(root.Clone());
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
                Console.WriteLine("HA context/control cache · reconnect: " + ex.Message);
                FailPending(ex);
                await Delay(token, 1800);
            }
            finally
            {
                connected = false;
                lock (socketSync)
                {
                    if (ReferenceEquals(activeSocket, ws)) activeSocket = null;
                }
                FailPending(new WebSocketException("Home Assistant WebSocket disconnected."));
            }
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
        Console.WriteLine("HA context/control cache · primed " + states.Count + " states");
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
        JsonElement attrs;
        if (item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
            attrs = attributes.Clone();
        else
        {
            using var empty = JsonDocument.Parse("{}");
            attrs = empty.RootElement.Clone();
        }
        var friendly = attrs.TryGetProperty("friendly_name", out var friendlyProp) ? friendlyProp.GetString() ?? entityId : entityId;
        return new CachedState(entityId, state, friendly, attrs, DateTimeOffset.UtcNow.Ticks);
    }

    static bool TryMapAction(
        string domain,
        string action,
        JsonElement? value,
        out string service,
        out Dictionary<string, object?> data,
        out string expectation,
        out string? error)
    {
        service = "";
        data = new Dictionary<string, object?>();
        expectation = "changed";
        error = null;

        switch (action)
        {
            case "turn_on":
            case "turn_off":
            case "toggle":
                if (domain is not ("light" or "switch" or "fan" or "media_player" or "climate" or "input_boolean" or "water_heater" or "scene" or "script"))
                    return FailMap("turn_on/turn_off/toggle is not supported for this domain.", out error);
                service = action;
                expectation = action == "turn_on" && domain is "light" or "switch" or "fan" or "input_boolean" ? "state:on"
                    : action == "turn_off" ? "state:off" : "changed";
                return true;

            case "set_temperature":
                if (domain is not ("climate" or "water_heater")) return FailMap("set_temperature requires climate or water_heater.", out error);
                if (!TryGetDouble(value, out var temperature)) return FailMap("set_temperature requires a numeric value.", out error);
                service = "set_temperature";
                data["temperature"] = temperature;
                expectation = "attr:temperature=" + temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "set_hvac_mode":
                if (domain != "climate") return FailMap("set_hvac_mode requires climate.", out error);
                if (!TryGetString(value, out var hvacMode)) return FailMap("set_hvac_mode requires a string value.", out error);
                service = "set_hvac_mode";
                data["hvac_mode"] = hvacMode;
                expectation = "state:" + hvacMode;
                return true;

            case "set_brightness":
                if (domain != "light") return FailMap("set_brightness requires light.", out error);
                if (!TryGetDouble(value, out var brightness)) return FailMap("set_brightness requires a percentage 0-100.", out error);
                service = "turn_on";
                data["brightness_pct"] = Math.Clamp(brightness, 0, 100);
                expectation = "brightness_pct:" + Math.Clamp(brightness, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "set_percentage":
                if (domain != "fan") return FailMap("set_percentage requires fan.", out error);
                if (!TryGetDouble(value, out var percentage)) return FailMap("set_percentage requires a percentage 0-100.", out error);
                service = "set_percentage";
                data["percentage"] = Math.Clamp(percentage, 0, 100);
                expectation = "attr:percentage=" + Math.Clamp(percentage, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "open":
            case "close":
            case "stop":
                if (domain != "cover") return FailMap("open/close/stop requires cover.", out error);
                service = action == "open" ? "open_cover" : action == "close" ? "close_cover" : "stop_cover";
                expectation = action == "open" ? "state:open|opening" : action == "close" ? "state:closed|closing" : "changed";
                return true;

            case "set_position":
                if (domain != "cover") return FailMap("set_position requires cover.", out error);
                if (!TryGetDouble(value, out var position)) return FailMap("set_position requires a percentage 0-100.", out error);
                service = "set_cover_position";
                data["position"] = Math.Clamp(position, 0, 100);
                expectation = "attr:current_position=" + Math.Clamp(position, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "lock":
            case "unlock":
                if (domain != "lock") return FailMap("lock/unlock requires lock.", out error);
                service = action;
                expectation = "state:" + (action == "lock" ? "locked" : "unlocked");
                return true;

            case "play":
            case "pause":
            case "play_pause":
                if (domain != "media_player") return FailMap("play/pause/play_pause requires media_player.", out error);
                service = action == "play" ? "media_play" : action == "pause" ? "media_pause" : "media_play_pause";
                expectation = action == "play" ? "state:playing" : action == "pause" ? "state:paused" : "changed";
                return true;

            case "set_volume":
                if (domain != "media_player") return FailMap("set_volume requires media_player.", out error);
                if (!TryGetDouble(value, out var volume)) return FailMap("set_volume requires 0-100.", out error);
                service = "volume_set";
                data["volume_level"] = Math.Clamp(volume, 0, 100) / 100d;
                expectation = "volume_pct:" + Math.Clamp(volume, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "start":
            case "pause_vacuum":
            case "stop_vacuum":
            case "return_to_base":
                if (domain != "vacuum") return FailMap("Vacuum action requires vacuum.", out error);
                service = action == "pause_vacuum" ? "pause" : action == "stop_vacuum" ? "stop" : action;
                expectation = action == "start" ? "state:cleaning" : action == "return_to_base" ? "state:returning|docked" : "changed";
                return true;

            case "press":
                if (domain != "button") return FailMap("press requires button.", out error);
                service = "press";
                expectation = "changed";
                return true;

            case "set_value":
                if (domain != "input_number") return FailMap("set_value requires input_number.", out error);
                if (!TryGetDouble(value, out var numberValue)) return FailMap("set_value requires a numeric value.", out error);
                service = "set_value";
                data["value"] = numberValue;
                expectation = "state_num:" + numberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "select_option":
                if (domain != "input_select") return FailMap("select_option requires input_select.", out error);
                if (!TryGetString(value, out var option)) return FailMap("select_option requires a string value.", out error);
                service = "select_option";
                data["option"] = option;
                expectation = "state:" + option;
                return true;

            default:
                return FailMap("Unsupported semantic Home Assistant action: " + action, out error);
        }
    }

    static bool MatchesExpectation(CachedState state, string expectation, long beforeTicks)
    {
        if (expectation == "changed") return state.UpdatedTicks > beforeTicks;
        if (expectation.StartsWith("state:", StringComparison.Ordinal))
        {
            var allowed = expectation[6..].Split('|', StringSplitOptions.RemoveEmptyEntries);
            return allowed.Any(v => string.Equals(v, state.State, StringComparison.OrdinalIgnoreCase));
        }
        if (expectation.StartsWith("state_num:", StringComparison.Ordinal) &&
            double.TryParse(expectation[10..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var expectedState) &&
            double.TryParse(state.State, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var actualState))
            return Math.Abs(expectedState - actualState) < 0.01;
        if (expectation.StartsWith("attr:", StringComparison.Ordinal))
        {
            var parts = expectation[5..].Split('=', 2);
            return parts.Length == 2 && AttributeNear(state.Attributes, parts[0], parts[1]);
        }
        if (expectation.StartsWith("brightness_pct:", StringComparison.Ordinal) &&
            double.TryParse(expectation[15..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) &&
            state.Attributes.TryGetProperty("brightness", out var brightnessProp) && brightnessProp.TryGetDouble(out var rawBrightness))
            return Math.Abs((rawBrightness / 255d * 100d) - pct) <= 2.0;
        if (expectation.StartsWith("volume_pct:", StringComparison.Ordinal) &&
            double.TryParse(expectation[11..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var volumePct) &&
            state.Attributes.TryGetProperty("volume_level", out var volumeProp) && volumeProp.TryGetDouble(out var rawVolume))
            return Math.Abs((rawVolume * 100d) - volumePct) <= 1.0;
        return state.UpdatedTicks > beforeTicks;
    }

    static bool AttributeNear(JsonElement attrs, string name, string expectedText)
    {
        if (!attrs.TryGetProperty(name, out var value)) return false;
        if (double.TryParse(expectedText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var expected) &&
            value.TryGetDouble(out var actual)) return Math.Abs(expected - actual) < 0.11;
        return string.Equals(value.ToString(), expectedText, StringComparison.OrdinalIgnoreCase);
    }

    static bool TryGetDouble(JsonElement? value, out double result)
    {
        result = 0;
        if (value is not JsonElement element) return false;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out result);
        if (element.ValueKind == JsonValueKind.String)
            return double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        return false;
    }

    static bool TryGetString(JsonElement? value, out string result)
    {
        result = "";
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.String) return false;
        result = element.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(result);
    }

    static bool FailMap(string message, out string? error)
    {
        error = message;
        return false;
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
        return state.EntityId + " | " + state.FriendlyName + " | " + state.State +
               (details.Count == 0 ? "" : " · " + string.Join(" · ", details));
    }

    static string StateJson(CachedState state, bool success, bool confirmed, long latencyMs, string? service, string action)
    {
        var selected = new Dictionary<string, object?>();
        foreach (var name in new[] { "temperature", "current_temperature", "hvac_action", "brightness", "percentage", "current_position", "volume_level" })
            if (state.Attributes.TryGetProperty(name, out var value)) selected[name] = JsonElementToObject(value);

        return JsonSerializer.Serialize(new
        {
            success,
            confirmed,
            entity_id = state.EntityId,
            friendly_name = state.FriendlyName,
            state = state.State,
            attributes = selected,
            action,
            service,
            latency_ms = latencyMs
        });
    }

    static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when element.TryGetDouble(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.ToString()
    };

    static string FailureJson(string message, string? entityId = null, string? action = null)
        => JsonSerializer.Serialize(new { success = false, confirmed = false, entity_id = entityId, action, error = message });

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

    async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await sendGate.WaitAsync(token);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        finally { sendGate.Release(); }
    }

    void FailPending(Exception ex)
    {
        foreach (var pair in pendingResults.ToArray())
            if (pendingResults.TryRemove(pair.Key, out var pending)) pending.TrySetException(ex);
    }

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
        lock (socketSync)
        {
            try { activeSocket?.Abort(); } catch { }
            activeSocket = null;
        }
        FailPending(new ObjectDisposedException(nameof(HomeAssistantWebSocketCache)));
        lifetime.Dispose();
        sendGate.Dispose();
    }
}