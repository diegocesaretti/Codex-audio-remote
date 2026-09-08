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

internal sealed record SolCanonicalPerson(
    string EntityId,
    string Name,
    bool LinkedToSolMember,
    string? Source,
    string? SourcePluginId);

internal sealed class SolRuntimeBridge : IDisposable
{
    readonly Func<SolAudioRuntimeSnapshot> snapshot;
    readonly Func<string, Task> endSession;
    readonly HttpClient http;
    readonly SpeakerBindingStore bindings;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim inputGate = new(1, 1);
    readonly object sync = new();
    readonly bool ingestTranscripts;
    readonly string baseUrl;
    readonly string token;
    readonly string callbackPath;
    readonly int callbackPort;

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
        int callbackPort,
        bool ingestTranscripts)
    {
        this.snapshot = snapshot;
        this.endSession = endSession;
        this.baseUrl = baseUrl.TrimEnd('/');
        this.token = token;
        this.callbackPort = callbackPort;
        this.ingestTranscripts = ingestTranscripts;
        callbackPath = "/ws/_sol/" + Guid.NewGuid().ToString("N");
        bindings = new SpeakerBindingStore(dataDir);
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static SolRuntimeBridge? TryCreate(
        Func<SolAudioRuntimeSnapshot> snapshot,
        Func<string, Task> endSession,
        int callbackPort)
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
        var ingest = SolPluginHost.BoolSetting("ingest_transcripts") ?? true;
        return new SolRuntimeBridge(snapshot, endSession, baseUrl, token, dataDir, callbackPort, ingest);
    }

    public async Task StartAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            await EnsureInputAsync();
            await SetInputStatusAsync("connected", DateTimeOffset.UtcNow);
            await RegisterMcpToolsAsync();
            SolPluginHost.Log("info", $"SOL native bridge ready · MCP=127.0.0.1:{callbackPort}{callbackPath} · transcript-ingest={ingestTranscripts} · speaker-person-validation=on");
        }
        catch (Exception ex)
        {
            SolPluginHost.Health("degraded", "SOL native bridge startup failed: " + ex.Message);
            SolPluginHost.Log("warn", "SOL native bridge unavailable; realtime audio continues: " + ex.Message);
        }
    }

    public async Task<bool> TryHandleCallbackAsync(HttpListenerContext context)
    {
        if (!string.Equals(context.Request.Url?.AbsolutePath, callbackPath, StringComparison.Ordinal)) return false;
        var remoteAddress = (context.Request.RemoteEndPoint as IPEndPoint)?.Address;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            await WriteJsonAsync(context.Response, 403, new { error = "loopback_required" });
            return true;
        }
        await HandleCallbackAsync(context);
        return true;
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

    // Future biometric/voiceprint implementations must resolve to a currently visible
    // canonical SOL Person before a profile is accepted. A voiceprint is identity evidence,
    // never a SOL login, role or permission grant.
    public async Task<bool> ApplyVoiceprintResolutionAsync(string profileId, string personEntityId, double confidence)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(personEntityId)) return false;
        var person = await ResolveCanonicalPersonAsync(personEntityId.Trim());
        if (person is null) return false;
        var resolution = new SpeakerResolution(
            person.EntityId,
            person.Name,
            "voiceprint",
            profileId.Trim(),
            Math.Clamp(confidence, 0, 1),
            DateTimeOffset.UtcNow);
        bindings.SetVoiceProfile(profileId.Trim(), resolution);
        lock (sync) sessionSpeaker = resolution;
        return true;
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
            await RequestAsync($"/v1/plugin-api/inputs/{input}/items", new
            {
                externalId = $"{sessionId}:{role}:{sequence}",
                kind = "message",
                occurredAt = now,
                observedAt = now,
                title = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "Voice · user" : "Voice · assistant",
                text,
                origin = "realtime",
                metadata = new Dictionary<string, object?>
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
                }
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
        await inputGate.WaitAsync(lifetime.Token);
        try
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
        finally { inputGate.Release(); }
    }

    async Task SetInputStatusAsync(string status, DateTimeOffset? lastSyncAt = null)
    {
        try
        {
            var input = await EnsureInputAsync();
            if (input is null) return;
            await RequestAsync($"/v1/plugin-api/inputs/{input}/status", new { status, lastSyncAt });
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "SOL input status update failed: " + ex.Message);
        }
    }

    async Task RegisterMcpToolsAsync()
    {
        var callbackUrl = $"http://127.0.0.1:{callbackPort}{callbackPath}";
        var emptySchema = new { type = "object", properties = new { }, additionalProperties = false };
        var tools = new object[]
        {
            new { name = "codex_audio_status", description = "Return Codex Audio Remote realtime/session/device state and current SOL speaker resolution.", inputSchema = emptySchema, requiresSubmit = false },
            new { name = "codex_audio_get_speaker", description = "Return the current session speaker resolution and any remembered manual device binding. Unknown means no identity has been asserted.", inputSchema = emptySchema, requiresSubmit = false },
            new { name = "codex_audio_list_people", description = "List canonical SOL Persons visible to this plugin for speaker binding. A Person is a human identity and does not imply a SOL account, role or access grant.", inputSchema = emptySchema, requiresSubmit = false },
            new
            {
                name = "codex_audio_bind_current_speaker",
                description = "Bind the current human speaker to an existing canonical SOL Person. The id is validated against SOL before it is stored. This is a manual identity assertion, not biometric identification or an access grant.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["confirmedByUser"] = new { type = "boolean", @const = true },
                        ["personEntityId"] = new { type = "string", description = "Canonical SOL Person entity id returned by codex_audio_list_people or another trusted SOL Person resolver." },
                        ["personLabel"] = new { type = "string", description = "Deprecated display hint; SOL's canonical Person name is authoritative." },
                        ["rememberForDevice"] = new { type = "boolean", description = "Use this Person as the manual default for this satellite until speaker recognition supersedes it." }
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

    async Task HandleCallbackAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                await WriteJsonAsync(context.Response, 405, new { error = "method_not_allowed" });
                return;
            }
            var root = await ReadJsonAsync(context.Request);
            var tool = ReadString(root, "tool") ?? "";
            var args = root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object ? arguments : default;

            switch (tool)
            {
                case "codex_audio_status":
                    await WriteJsonAsync(context.Response, 200, StatusPayload());
                    return;
                case "codex_audio_get_speaker":
                    await WriteJsonAsync(context.Response, 200, SpeakerPayload());
                    return;
                case "codex_audio_list_people":
                    await WriteJsonAsync(context.Response, 200, await ListCanonicalPeopleAsync());
                    return;
                case "codex_audio_bind_current_speaker":
                    if (!Confirmed(args)) { await WriteJsonAsync(context.Response, 403, new { error = "explicit_user_confirmation_required" }); return; }
                    var personEntityId = ReadString(args, "personEntityId")?.Trim();
                    if (string.IsNullOrWhiteSpace(personEntityId)) { await WriteJsonAsync(context.Response, 400, new { error = "personEntityId_required" }); return; }
                    var canonicalPerson = await ResolveCanonicalPersonAsync(personEntityId);
                    if (canonicalPerson is null) { await WriteJsonAsync(context.Response, 404, new { error = "canonical_sol_person_not_found" }); return; }
                    if (!BindCurrentSpeaker(canonicalPerson.EntityId, canonicalPerson.Name, ReadBool(args, "rememberForDevice"), out var bindError))
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

    bool BindCurrentSpeaker(string personEntityId, string personLabel, bool rememberForDevice, out string? error)
    {
        lock (sync)
        {
            if (string.IsNullOrEmpty(activeSessionId))
            {
                error = "no_active_voice_session";
                return false;
            }
            sessionSpeaker = new SpeakerResolution(personEntityId, personLabel, "manual-validated", null, null, DateTimeOffset.UtcNow);
            if (rememberForDevice && activeDeviceKey != "unknown") bindings.SetDeviceDefault(activeDeviceKey, sessionSpeaker);
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
        string deviceKey;
        lock (sync) { speaker = sessionSpeaker; deviceKey = activeDeviceKey; }
        return new
        {
            state.State,
            state.SessionId,
            state.Revision,
            state.Reason,
            state.ClientGeneration,
            deviceKey,
            speaker = SpeakerDto(speaker),
            transcriptIngest = ingestTranscripts,
            speakerIdentification = "not-implemented",
            personContract = "speaker bindings must resolve to a visible canonical SOL Person; Person identity grants no SOL access",
            futureResolverContract = "voiceprint -> validate canonical SOL personEntityId -> speaker binding"
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
            identificationImplemented = false,
            personIdentityGrantsAccess = false
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

    async Task<JsonElement> ListCanonicalPeopleAsync()
    {
        var result = await GetAsync("/v1/plugin-api/identities/people");
        if (result is null) throw new InvalidOperationException("SOL Person directory returned no payload");
        return result.Value;
    }

    async Task<SolCanonicalPerson?> ResolveCanonicalPersonAsync(string personEntityId)
    {
        if (!Guid.TryParse(personEntityId, out _)) return null;
        var result = await GetAsync("/v1/plugin-api/identities/people/" + Uri.EscapeDataString(personEntityId), allowNotFound: true);
        if (result is null || !result.Value.TryGetProperty("person", out var person) || person.ValueKind != JsonValueKind.Object) return null;
        var entityId = ReadString(person, "entityId")?.Trim();
        var name = ReadString(person, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(name)) return null;
        return new SolCanonicalPerson(
            entityId,
            name,
            ReadBool(person, "linkedToSolMember"),
            ReadString(person, "source"),
            ReadString(person, "sourcePluginId"));
    }

    async Task<JsonElement?> GetAsync(string path, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, lifetime.Token);
        var text = await response.Content.ReadAsStringAsync(lifetime.Token);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"SOL Plugin API HTTP {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return document.RootElement.Clone();
    }

    async Task<JsonElement> RequestAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, lifetime.Token);
        var text = await response.Content.ReadAsStringAsync(lifetime.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"SOL Plugin API HTTP {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return document.RootElement.Clone();
    }

    static async Task<JsonElement> ReadJsonAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > 1024 * 1024) throw new InvalidOperationException("request_too_large");
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, true, leaveOpen: false);
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
        _ = SetInputStatusAsync("disconnected");
        lifetime.Cancel();
        http.Dispose();
        inputGate.Dispose();
        lifetime.Dispose();
    }

    sealed record SpeakerResolution(string PersonEntityId, string? PersonLabel, string Resolution, string? ProfileId, double? Confidence, DateTimeOffset UpdatedAt)
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
            lock (fileSync) if (data.DeviceDefaults.Remove(deviceKey)) PersistUnsafe();
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
