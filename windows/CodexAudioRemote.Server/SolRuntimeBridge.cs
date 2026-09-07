using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

internal sealed record SolAudioRuntimeSnapshot(
    string State,
    string SessionId,
    long Revision,
    string Reason,
    long ClientGeneration,
    string DeviceKey);

internal sealed class SolRuntimeBridge : IDisposable
{
    readonly Func<SolAudioRuntimeSnapshot> snapshot;
    readonly Func<string, Task> endSession;
    readonly HttpClient http;
    readonly HttpListener listener = new();
    readonly SpeakerBindingStore bindings;
    readonly CancellationTokenSource lifetime = new();
    readonly object sync = new();
    readonly bool ingestTranscripts;
    readonly string baseUrl;
    readonly string token;
    readonly string callbackPath;
    readonly int port;

    Task? listenerTask;
    string? inputId;
    string activeDeviceKey = "unknown";
    string activeSessionId = "";
    SpeakerResolution? sessionSpeaker;
    long transcriptSequence;
    int disposed;

    SolRuntimeBridge(
        Func<SolAudioRuntimeSnapshot> snapshot,
        Func<string, Task> endSession,
        string baseUrl,
        string token,
        string dataDir,
        int port,
        bool ingestTranscripts)
    {
        this.snapshot = snapshot;
        this.endSession = endSession;
        this.baseUrl = baseUrl.TrimEnd('/');
        this.token = token;
        this.port = port;
        this.ingestTranscripts = ingestTranscripts;
        callbackPath = "/mcp/" + Guid.NewGuid().ToString("N");
        bindings = new SpeakerBindingStore(dataDir);
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public static SolRuntimeBridge? TryCreate(
        Func<SolAudioRuntimeSnapshot> snapshot,
        Func<string, Task> endSession)
    {
        if (!SolPluginHost.Enabled) return null;
        var baseUrl = (Environment.GetEnvironmentVariable("SOL_PLUGIN_API_URL")
                       ?? Environment.GetEnvironmentVariable("SOL_CORE_URL")
                       ?? "").Trim();
        var token = (Environment.GetEnvironmentVariable("SOL_PLUGIN_TOKEN") ?? "").Trim();
        if (baseUrl.Length == 0 || token.Length == 0)
        {
            SolPluginHost.Health("degraded", "SOL runtime API/token unavailable; audio remains operational without SOL MCP/input bridge.");
            return null;
        }

        var dataDir = (Environment.GetEnvironmentVariable("SOL_PLUGIN_DATA_DIR") ?? "").Trim();
        if (dataDir.Length == 0) dataDir = Path.Combine(AppContext.BaseDirectory, ".sol-data");
        Directory.CreateDirectory(dataDir);
        var port = Math.Clamp(SolPluginHost.IntSetting("sol_api_port") ?? 8771, 1024, 65535);
        var ingest = SolPluginHost.BoolSetting("ingest_transcripts") ?? true;
        return new SolRuntimeBridge(snapshot, endSession, baseUrl, token, dataDir, port, ingest);
    }

    public async Task StartAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            listener.Start();
            listenerTask = Task.Run(() => ListenLoopAsync(lifetime.Token));
            await EnsureInputAsync();
            await SetInputStatusAsync("connected", DateTimeOffset.UtcNow);
            await RegisterMcpToolsAsync();
            SolPluginHost.Log("info", $"SOL native bridge ready · MCP=127.0.0.1:{port} · transcript-ingest={ingestTranscripts}");
        }
        catch (Exception ex)
        {
            SolPluginHost.Health("degraded", "SOL native bridge startup failed: " + ex.Message);
            SolPluginHost.Log("warn", "SOL native bridge unavailable; realtime audio continues: " + ex.Message);
        }
    }

    public void ObserveRemoteClient(EndPoint? endpoint)
    {
        var address = endpoint switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => endpoint?.ToString() ?? "unknown"
        };
        SetActiveDeviceKey("remote:" + NormalizeKey(address, 120));
    }

    public void ObserveClientHello(JsonElement root)
    {
        var explicitSpeakerKey = ReadString(root, "speakerKey");
        var deviceId = ReadString(root, "deviceId");
        var clientId = ReadString(root, "clientId");
        var candidate = explicitSpeakerKey ?? deviceId ?? clientId;
        if (!string.IsNullOrWhiteSpace(candidate))
            SetActiveDeviceKey("device:" + NormalizeKey(candidate, 160));
    }

    void SetActiveDeviceKey(string value)
    {
        lock (sync)
        {
            activeDeviceKey = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            if (!string.IsNullOrEmpty(activeSessionId) && sessionSpeaker is null)
                sessionSpeaker = bindings.GetDeviceDefault(activeDeviceKey)?.WithResolution("manual-device-default");
        }
    }

    public void OnSessionStarted(string sessionId)
    {
        lock (sync)
        {
            activeSessionId = sessionId;
            sessionSpeaker = bindings.GetDeviceDefault(activeDeviceKey)?.WithResolution("manual-device-default");
        }
    }

    public void OnSessionEnded(string sessionId)
    {
        lock (sync)
        {
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal)) return;
            activeSessionId = "";
            sessionSpeaker = null;
        }
    }

    // Future speaker-identification engines plug in here. The rest of the SOL pipeline already
    // consumes the same canonical personEntityId regardless of whether resolution is manual or voiceprint.
    public void ApplyVoiceprintResolution(string profileId, string personEntityId, string? personLabel, double confidence)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(personEntityId)) return;
        var resolution = new SpeakerResolution(
            PersonEntityId: personEntityId.Trim(),
            PersonLabel: string.IsNullOrWhiteSpace(personLabel) ? null : personLabel.Trim(),
            Resolution: "voiceprint",
            ProfileId: profileId.Trim(),
            Confidence: Math.Clamp(confidence, 0, 1),
            UpdatedAt: DateTimeOffset.UtcNow);
        bindings.SetVoiceProfile(profileId.Trim(), resolution);
        lock (sync) sessionSpeaker = resolution;
    }

    public async Task IngestTranscriptAsync(string role, string text, bool done, string sessionId)
    {
        if (!done || !ingestTranscripts || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var input = await EnsureInputAsync();
            if (input is null) return;
            SpeakerResolution? speaker;
            string deviceKey;
            lock (sync)
            {
                speaker = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? sessionSpeaker : null;
                deviceKey = activeDeviceKey;
            }
            var sequence = Interlocked.Increment(ref transcriptSequence);
            var now = DateTimeOffset.UtcNow;
            var metadata = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["role"] = role,
                ["deviceKey"] = deviceKey,
                ["voiceBackend"] = "codex-realtime-v3",
                ["speakerResolution"] = speaker?.Resolution ?? "unknown",
                ["personEntityId"] = speaker?.PersonEntityId,
                ["personLabel"] = speaker?.PersonLabel,
                ["speakerProfileId"] = speaker?.ProfileId,
                ["speakerConfidence"] = speaker?.Confidence
            };
            await RequestAsync($"/v1/plugin-api/inputs/{input}/items", new
            {
                externalId = $"{sessionId}:{role}:{sequence}",
                kind = "message",
                occurredAt = now,
                observedAt = now,
                title = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "Voice · user" : "Voice · assistant",
                text,
                origin = "realtime",
                metadata
            });
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "SOL transcript ingest failed without interrupting audio: " + ex.Message);
        }
    }

    async Task<string?> EnsureInputAsync()
    {
        if (inputId is not null) return inputId;
        var result = await RequestAsync("/v1/plugin-api/inputs/register", new
        {
            provider = "codex_audio_remote",
            externalAccountId = "windows:" + Environment.MachineName,
            label = "Codex Audio Remote"
        });
        if (result.TryGetProperty("input", out var input) && input.TryGetProperty("id", out var id))
            inputId = id.GetString();
        return inputId;
    }

    async Task SetInputStatusAsync(string status, DateTimeOffset? lastSyncAt = null)
    {
        try
        {
            var input = await EnsureInputAsync();
            if (input is null) return;
            await RequestAsync($"/v1/plugin-api/inputs/{input}/status", new
            {
                status,
                lastSyncAt
            });
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "SOL input status update failed: " + ex.Message);
        }
    }

    async Task RegisterMcpToolsAsync()
    {
        var callbackUrl = $"http://127.0.0.1:{port}{callbackPath}";
        var emptySchema = new { type = "object", properties = new { }, additionalProperties = false };
        var tools = new object[]
        {
            new
            {
                name = "codex_audio_status",
                description = "Return Codex Audio Remote realtime/session/device state and the current SOL speaker resolution without touching the audio path.",
                inputSchema = emptySchema,
                requiresSubmit = false
            },
            new
            {
                name = "codex_audio_get_speaker",
                description = "Return the current session speaker resolution and any remembered manual device binding. Unknown means no speaker identity has been asserted.",
                inputSchema = emptySchema,
                requiresSubmit = false
            },
            new
            {
                name = "codex_audio_bind_current_speaker",
                description = "Bind the current human speaker to an existing canonical SOL person entity. Resolve the person in SOL first. This is a manual assertion, not biometric identification.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["confirmedByUser"] = new { type = "boolean", @const = true },
                        ["personEntityId"] = new { type = "string", description = "Canonical SOL person entity id." },
                        ["personLabel"] = new { type = "string", description = "Optional display label for diagnostics only." },
                        ["rememberForDevice"] = new { type = "boolean", description = "If true, use this person as the manual default for this satellite/device until speaker recognition is implemented." }
                    },
                    required = new[] { "confirmedByUser", "personEntityId" },
                    additionalProperties = false
                },
                requiresSubmit = true
            },
            new
            {
                name = "codex_audio_clear_speaker_binding",
                description = "Clear the current manual speaker assertion and optionally the remembered device default.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["confirmedByUser"] = new { type = "boolean", @const = true },
                        ["clearDeviceDefault"] = new { type = "boolean" }
                    },
                    required = new[] { "confirmedByUser" },
                    additionalProperties = false
                },
                requiresSubmit = true
            },
            new
            {
                name = "codex_audio_end_session",
                description = "End the active Codex Audio Remote voice session. Requires explicit user confirmation.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["confirmedByUser"] = new { type = "boolean", @const = true },
                        ["reason"] = new { type = "string" }
                    },
                    required = new[] { "confirmedByUser" },
                    additionalProperties = false
                },
                requiresSubmit = true
            }
        };
        await RequestAsync("/v1/plugin-api/mcp/tools/register", new { callbackUrl, tools });
    }

    async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) when (token.IsCancellationRequested || !listener.IsListening) { break; }
            _ = Task.Run(() => HandleCallbackAsync(context), token);
        }
    }

    async Task HandleCallbackAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != callbackPath)
            {
                await WriteJsonAsync(context.Response, 404, new { error = "not_found" });
                return;
            }
            var root = await ReadJsonAsync(context.Request);
            var tool = ReadString(root, "tool") ?? "";
            var args = root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
                ? arguments : default;

            switch (tool)
            {
                case "codex_audio_status":
                    await WriteJsonAsync(context.Response, 200, StatusPayload());
                    return;
                case "codex_audio_get_speaker":
                    await WriteJsonAsync(context.Response, 200, SpeakerPayload());
                    return;
                case "codex_audio_bind_current_speaker":
                    if (!Confirmed(args)) { await WriteJsonAsync(context.Response, 403, new { error = "explicit_user_confirmation_required" }); return; }
                    var personEntityId = ReadString(args, "personEntityId")?.Trim();
                    if (string.IsNullOrWhiteSpace(personEntityId)) { await WriteJsonAsync(context.Response, 400, new { error = "personEntityId_required" }); return; }
                    var personLabel = ReadString(args, "personLabel");
                    var remember = ReadBool(args, "rememberForDevice");
                    if (!BindCurrentSpeaker(personEntityId, personLabel, remember, out var bindError))
                    {
                        await WriteJsonAsync(context.Response, 409, new { error = bindError });
                        return;
                    }
                    await WriteJsonAsync(context.Response, 200, SpeakerPayload());
                    return;
                case "codex_audio_clear_speaker_binding":
                    if (!Confirmed(args)) { await WriteJsonAsync(context.Response, 403, new { error = "explicit_user_confirmation_required" }); return; }
                    ClearCurrentSpeaker(ReadBool(args, "clearDeviceDefault"));
                    await WriteJsonAsync(context.Response, 200, SpeakerPayload());
                    return;
                case "codex_audio_end_session":
                    if (!Confirmed(args)) { await WriteJsonAsync(context.Response, 403, new { error = "explicit_user_confirmation_required" }); return; }
                    await endSession(ReadString(args, "reason") ?? "sol_mcp");
                    await WriteJsonAsync(context.Response, 200, new { ok = true, state = snapshot().State });
                    return;
                default:
                    await WriteJsonAsync(context.Response, 404, new { error = "tool_not_found" });
                    return;
            }
        }
        catch (Exception ex)
        {
            try { await WriteJsonAsync(context.Response, 500, new { error = ex.Message }); } catch { }
        }
    }

    bool BindCurrentSpeaker(string personEntityId, string? personLabel, bool rememberForDevice, out string? error)
    {
        lock (sync)
        {
            if (string.IsNullOrEmpty(activeSessionId))
            {
                error = "no_active_voice_session";
                return false;
            }
            sessionSpeaker = new SpeakerResolution(
                PersonEntityId: personEntityId,
                PersonLabel: string.IsNullOrWhiteSpace(personLabel) ? null : personLabel.Trim(),
                Resolution: "manual",
                ProfileId: null,
                Confidence: null,
                UpdatedAt: DateTimeOffset.UtcNow);
            if (rememberForDevice && activeDeviceKey != "unknown")
                bindings.SetDeviceDefault(activeDeviceKey, sessionSpeaker);
            error = null;
            return true;
        }
    }

    void ClearCurrentSpeaker(bool clearDeviceDefault)
    {
        lock (sync)
        {
            sessionSpeaker = null;
            if (clearDeviceDefault && activeDeviceKey != "unknown") bindings.ClearDeviceDefault(activeDeviceKey);
        }
    }

    object StatusPayload()
    {
        var state = snapshot();
        SpeakerResolution? speaker;
        lock (sync) speaker = sessionSpeaker;
        return new
        {
            state.State,
            state.SessionId,
            state.Revision,
            state.Reason,
            state.ClientGeneration,
            deviceKey = state.DeviceKey,
            speaker = SpeakerDto(speaker),
            transcriptIngest = ingestTranscripts,
            speakerIdentification = "not-implemented",
            futureResolverContract = "voiceprint -> canonical SOL personEntityId"
        };
    }

    object SpeakerPayload()
    {
        string deviceKey;
        string sessionId;
        SpeakerResolution? speaker;
        SpeakerResolution? deviceDefault;
        lock (sync)
        {
            deviceKey = activeDeviceKey;
            sessionId = activeSessionId;
            speaker = sessionSpeaker;
            deviceDefault = bindings.GetDeviceDefault(activeDeviceKey);
        }
        return new
        {
            sessionId,
            deviceKey,
            current = SpeakerDto(speaker),
            rememberedDeviceDefault = SpeakerDto(deviceDefault),
            identificationImplemented = false
        };
    }

    static object? SpeakerDto(SpeakerResolution? value) => value is null ? null : new
    {
        value.PersonEntityId,
        value.PersonLabel,
        value.Resolution,
        value.ProfileId,
        value.Confidence,
        value.UpdatedAt
    };

    async Task<JsonElement> RequestAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, lifetime.Token);
        var text = await response.Content.ReadAsStringAsync(lifetime.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SOL Plugin API HTTP {(int)response.StatusCode}: {text}");
        using var document = string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    static async Task<JsonElement> ReadJsonAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > 1024 * 1024) throw new InvalidOperationException("request_too_large");
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var text = await reader.ReadToEndAsync();
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new InvalidOperationException("request_too_large");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return document.RootElement.Clone();
    }

    static async Task WriteJsonAsync(HttpListenerResponse response, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    static bool Confirmed(JsonElement args)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("confirmedByUser", out var value) && value.ValueKind == JsonValueKind.True;

    static bool ReadBool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string NormalizeKey(string value, int max)
    {
        var normalized = new string(value.Trim().Where(ch => !char.IsControl(ch)).ToArray());
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { _ = SetInputStatusAsync("disconnected"); } catch { }
        lifetime.Cancel();
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        http.Dispose();
        lifetime.Dispose();
    }

    sealed record SpeakerResolution(
        string PersonEntityId,
        string? PersonLabel,
        string Resolution,
        string? ProfileId,
        double? Confidence,
        DateTimeOffset UpdatedAt)
    {
        public SpeakerResolution WithResolution(string resolution) => this with { Resolution = resolution, UpdatedAt = DateTimeOffset.UtcNow };
    }

    sealed class SpeakerBindingStore
    {
        readonly object fileSync = new();
        readonly string path;
        BindingFile data = new();

        public SpeakerBindingStore(string dataDir)
        {
            path = Path.Combine(dataDir, "speaker-bindings.json");
            Load();
        }

        public SpeakerResolution? GetDeviceDefault(string deviceKey)
        {
            lock (fileSync) return data.DeviceDefaults.TryGetValue(deviceKey, out var value) ? value : null;
        }

        public void SetDeviceDefault(string deviceKey, SpeakerResolution value)
        {
            lock (fileSync)
            {
                data.DeviceDefaults[deviceKey] = value with { Resolution = "manual-device-default", UpdatedAt = DateTimeOffset.UtcNow };
                PersistUnsafe();
            }
        }

        public void ClearDeviceDefault(string deviceKey)
        {
            lock (fileSync)
            {
                if (data.DeviceDefaults.Remove(deviceKey)) PersistUnsafe();
            }
        }

        public void SetVoiceProfile(string profileId, SpeakerResolution value)
        {
            lock (fileSync)
            {
                data.VoiceProfiles[profileId] = value;
                PersistUnsafe();
            }
        }

        void Load()
        {
            lock (fileSync)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    var loaded = JsonSerializer.Deserialize<BindingFile>(File.ReadAllText(path));
                    if (loaded is not null) data = loaded;
                }
                catch (Exception ex)
                {
                    SolPluginHost.Log("warn", "Speaker binding cache load failed: " + ex.Message);
                    data = new BindingFile();
                }
            }
        }

        void PersistUnsafe()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }

        sealed class BindingFile
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, SpeakerResolution> DeviceDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, SpeakerResolution> VoiceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
