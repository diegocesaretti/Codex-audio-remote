using Microsoft.Win32;
using System.Text.Json;

internal static class HomeAssistantDynamicTools
{
    const string RegistryPath = @"Software\CodexAudioRemote";
    const string RegisteredThreadValue = "HaDynamicToolsThreadIdV1";
    static volatile bool runtimeUnsupported;

    public static object[] Specs => new object[]
    {
        new
        {
            type = "namespace",
            name = "home_assistant",
            description = "Low-latency Home Assistant tools backed by the companion's persistent live WebSocket cache.",
            tools = new object[]
            {
                new
                {
                    type = "function",
                    name = "control",
                    description = "Control an exact Home Assistant entity through the persistent WebSocket. Use entity ids from HOME ASSISTANT LIVE CACHE. The tool validates domain/action mapping and confirms the resulting state_changed event when possible.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object?>
                        {
                            ["entity_id"] = new { type = "string", description = "Exact Home Assistant entity_id, e.g. light.cocina or climate.aire_cocina." },
                            ["action"] = new
                            {
                                type = "string",
                                @enum = new[]
                                {
                                    "turn_on", "turn_off", "toggle",
                                    "set_temperature", "set_hvac_mode",
                                    "set_brightness", "set_percentage",
                                    "open", "close", "stop", "set_position",
                                    "lock", "unlock",
                                    "play", "pause", "play_pause", "set_volume",
                                    "start", "pause_vacuum", "stop_vacuum", "return_to_base",
                                    "press", "set_value", "select_option"
                                }
                            },
                            ["value"] = new { description = "Optional value required by set_* and select actions. Temperatures and percentages are numbers; modes/options are strings." }
                        },
                        required = new[] { "entity_id", "action" },
                        additionalProperties = false
                    },
                    deferLoading = false
                },
                new
                {
                    type = "function",
                    name = "get_state",
                    description = "Read one exact entity directly from the live in-memory Home Assistant cache. Prefer the startup cache context; call this only when a precise fresh state is needed.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object?>
                        {
                            ["entity_id"] = new { type = "string", description = "Exact Home Assistant entity_id." }
                        },
                        required = new[] { "entity_id" },
                        additionalProperties = false
                    },
                    deferLoading = false
                }
            }
        }
    };

    public static bool RequiresNewToolThread(string savedThreadId)
    {
        if (runtimeUnsupported || string.IsNullOrWhiteSpace(savedThreadId)) return false;
        var registered = ReadRegisteredThreadId();
        return !string.Equals(registered, savedThreadId, StringComparison.Ordinal);
    }

    public static bool AddToThreadStart(Dictionary<string, object?> threadParams)
    {
        if (runtimeUnsupported) return false;
        threadParams["dynamicTools"] = Specs;
        return true;
    }

    public static bool IsCompatibilityError(Exception ex)
    {
        var text = ex.Message ?? "";
        return text.Contains("dynamicTools", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dynamic tools", StringComparison.OrdinalIgnoreCase)
            || text.Contains("experimental", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown field", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid params", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    public static void MarkThreadRegistered(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            key.SetValue(RegisteredThreadValue, threadId, RegistryValueKind.String);
        }
        catch { }
    }

    public static void MarkRuntimeUnsupported(string error)
    {
        runtimeUnsupported = true;
        Console.WriteLine("HA dynamic tools unavailable in this Codex app-server; voice continues without direct WS tool · " + error);
    }

    public static async Task<(bool Success, string Text)> InvokeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        try
        {
            var toolNamespace = parameters.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.String
                ? ns.GetString() ?? ""
                : "";
            var tool = parameters.TryGetProperty("tool", out var toolProp) ? toolProp.GetString() ?? "" : "";
            var arguments = parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                ? args
                : default;

            if (!string.Equals(toolNamespace, "home_assistant", StringComparison.Ordinal))
                return (false, JsonSerializer.Serialize(new { success = false, error = "Unsupported dynamic tool namespace: " + toolNamespace }));

            if (arguments.ValueKind != JsonValueKind.Object)
                return (false, JsonSerializer.Serialize(new { success = false, error = "Tool arguments must be a JSON object." }));

            var entityId = arguments.TryGetProperty("entity_id", out var entityProp) ? entityProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(entityId))
                return (false, JsonSerializer.Serialize(new { success = false, error = "entity_id is required." }));

            switch (tool)
            {
                case "get_state":
                {
                    var text = await HomeAssistantWebSocketCache.GetGlobalStateJsonAsync(entityId, cancellationToken);
                    return (JsonSaysSuccess(text), text);
                }
                case "control":
                {
                    var action = arguments.TryGetProperty("action", out var actionProp) ? actionProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(action))
                        return (false, JsonSerializer.Serialize(new { success = false, error = "action is required." }));
                    JsonElement? value = arguments.TryGetProperty("value", out var valueProp) ? valueProp.Clone() : null;
                    var text = await HomeAssistantWebSocketCache.ControlGlobalJsonAsync(entityId, action, value, cancellationToken);
                    return (JsonSaysSuccess(text), text);
                }
                default:
                    return (false, JsonSerializer.Serialize(new { success = false, error = "Unsupported home_assistant tool: " + tool }));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, JsonSerializer.Serialize(new { success = false, error = ex.Message }));
        }
    }

    static bool JsonSaysSuccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    static string ReadRegisteredThreadId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return key?.GetValue(RegisteredThreadValue)?.ToString() ?? "";
        }
        catch { return ""; }
    }
}