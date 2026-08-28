$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

# This MUST layer on top of the already validated official V3 + HA-context pipeline.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'Refusing dynamic-tool patch: V3 missing.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Refusing dynamic-tool patch: realtime model changed.' }
if ($source -notmatch 'type = "webrtc"') { throw 'Refusing dynamic-tool patch: official WebRTC transport missing.' }
if ($source -match 'type = "existingCall"') { throw 'Refusing dynamic-tool patch: existingCall present.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'Refusing dynamic-tool patch: direct realtime call present.' }
if ($source -notmatch 'StartOrResumeThreadAsync\(cwd, cancellationToken\)') { throw 'Refusing dynamic-tool patch: persistent thread flow missing.' }
if ($source -notmatch 'realtimeStartInstructions') { throw 'Refusing dynamic-tool patch: HA context layer missing.' }

# One-time thread migration: an old saved thread predating dynamicTools cannot gain tools after
# thread/start. Preserve normal continuity once the new tool-bearing thread has been created.
$oldCanResume = '        var canResume = !forceNew && mode != AppSettings.ThreadContinuityAlwaysNew && !string.IsNullOrWhiteSpace(savedThreadId);'
$newCanResume = @'
        var toolThreadMigrationRequired = HomeAssistantDynamicTools.RequiresNewToolThread(savedThreadId);
        if (toolThreadMigrationRequired)
            Console.WriteLine($"HA dynamic tools · saved thread {savedThreadId} predates direct WS tools; creating one new tool-capable thread");
        var canResume = !forceNew && mode != AppSettings.ThreadContinuityAlwaysNew && !string.IsNullOrWhiteSpace(savedThreadId) && !toolThreadMigrationRequired;
'@.TrimEnd()
if (-not $source.Contains($oldCanResume)) { throw 'Persistent thread canResume anchor not found.' }
$source = $source.Replace($oldCanResume, $newCanResume)

$oldNewThread = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);
        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        var newThreadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(newThreadId))
            throw new InvalidOperationException("thread/start did not return a thread id.");
'@

$newNewThread = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        var dynamicToolsRequested = HomeAssistantDynamicTools.AddToThreadStart(threadParams);
        JsonElement thread;
        try
        {
            thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        }
        catch (Exception ex) when (dynamicToolsRequested && ex is not OperationCanceledException && HomeAssistantDynamicTools.IsCompatibilityError(ex))
        {
            // Compatibility fallback only. Never sacrifice the working voice path for this feature.
            HomeAssistantDynamicTools.MarkRuntimeUnsupported(ex.Message);
            threadParams.Remove("dynamicTools");
            dynamicToolsRequested = false;
            thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        }

        var newThreadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(newThreadId))
            throw new InvalidOperationException("thread/start did not return a thread id.");
        if (dynamicToolsRequested)
        {
            HomeAssistantDynamicTools.MarkThreadRegistered(newThreadId);
            Console.WriteLine($"HA dynamic tools registered · thread={newThreadId} · tools=home_assistant.control,get_state");
        }
'@

if (-not $source.Contains($oldNewThread)) { throw 'Persistent new-thread block not found.' }
$source = $source.Replace($oldNewThread, $newNewThread)

# Dynamic tool calls are JSON-RPC server requests and therefore contain BOTH method and id.
# Handle them before the existing id=response path, otherwise they are intentionally ignored.
$handleAnchor = @'
    async Task HandleMessageAsync(JsonElement root)
    {
'@
if (-not $source.Contains($handleAnchor)) { throw 'HandleMessageAsync anchor not found.' }
$handleReplacement = @'
    async Task HandleMessageAsync(JsonElement root)
    {
        if (root.TryGetProperty("method", out var serverMethodProp) &&
            string.Equals(serverMethodProp.GetString(), "item/tool/call", StringComparison.Ordinal) &&
            root.TryGetProperty("id", out var serverRequestId))
        {
            var serverParams = root.TryGetProperty("params", out var serverParamsProp)
                ? serverParamsProp.Clone()
                : default;
            _ = HandleHomeAssistantDynamicToolRequestAsync(serverRequestId.Clone(), serverParams);
            return;
        }

'@
$source = $source.Replace($handleAnchor, $handleReplacement)

$methodAnchor = '    static byte[] ResamplePcm16Mono(byte[] input, int sourceRate, int targetRate)'
$methodIndex = $source.IndexOf($methodAnchor, [StringComparison]::Ordinal)
if ($methodIndex -lt 0) { throw 'Dynamic-tool handler insertion anchor not found.' }
$toolHandler = @'
    async Task HandleHomeAssistantDynamicToolRequestAsync(JsonElement requestId, JsonElement parameters)
    {
        try
        {
            var toolName = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tool", out var toolProp)
                ? toolProp.GetString() ?? "unknown"
                : "unknown";
            var entityId = parameters.ValueKind == JsonValueKind.Object &&
                           parameters.TryGetProperty("arguments", out var arguments) &&
                           arguments.ValueKind == JsonValueKind.Object &&
                           arguments.TryGetProperty("entity_id", out var entityProp)
                ? entityProp.GetString() ?? ""
                : "";

            var started = Stopwatch.GetTimestamp();
            var outcome = await HomeAssistantDynamicTools.InvokeAsync(parameters, lifetime.Token);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Console.WriteLine($"HA dynamic tool · {toolName} · entity={entityId} · success={outcome.Success} · {elapsed:0} ms");

            await SendJsonAsync(new
            {
                id = requestId,
                result = new
                {
                    contentItems = new object[] { new { type = "inputText", text = outcome.Text } },
                    success = outcome.Success
                }
            }, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine("HA dynamic tool response failed · " + ex.Message);
            try
            {
                await SendJsonAsync(new
                {
                    id = requestId,
                    result = new
                    {
                        contentItems = new object[] { new { type = "inputText", text = JsonSerializer.Serialize(new { success = false, error = ex.Message }) } },
                        success = false
                    }
                }, lifetime.Token);
            }
            catch { }
        }
    }

'@
$source = $source.Substring(0, $methodIndex) + $toolHandler + $source.Substring($methodIndex)

# Make the realtime instruction explicit: the cache resolves the entity; the dynamic tool performs
# the mutation over the persistent HA WebSocket and returns confirmed state.
$oldInstruction = 'For simple home-control requests, use these exact entity ids/states and call the existing Home Assistant tool directly. '
$newInstruction = 'For Home Assistant actions, use the home_assistant.control dynamic tool with the exact entity id from this cache. Use home_assistant.get_state only when a precise fresh state is needed. Do not use a slower generic Home Assistant path when this dynamic tool can perform the action. '
if (-not $source.Contains($oldInstruction)) { throw 'HA realtime instruction anchor not found.' }
$source = $source.Replace($oldInstruction, $newInstruction)

# Guardrails: this feature is allowed to change only thread tools + server-request handling.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'REGRESSION: V3 lost.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'REGRESSION: model changed.' }
if ($source -notmatch 'type = "webrtc"') { throw 'REGRESSION: WebRTC transport lost.' }
if ($source -match 'type = "existingCall"') { throw 'REGRESSION: existingCall introduced.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'REGRESSION: direct call introduced.' }
if ($source -notmatch 'HomeAssistantDynamicTools\.AddToThreadStart') { throw 'dynamicTools registration missing.' }
if ($source -notmatch 'item/tool/call') { throw 'dynamic tool server request handler missing.' }
if ($source -notmatch 'home_assistant\.control') { throw 'HA control instruction missing.' }
if ($source -match 'threadParams\["ephemeral"\]') { throw 'Direct WS tool build must preserve original thread lifecycle.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'Wired native Codex dynamicTools to direct Home Assistant WebSocket control without changing Realtime V3/WebRTC.'