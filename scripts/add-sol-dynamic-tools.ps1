$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

function Require-Contains([string]$text, [string]$needle, [string]$label) {
    if (-not $text.Contains($needle)) { throw "SOL dynamic-tools transform: missing expected $label" }
}

# This layer is deliberately last. It may add thread tools and server-request handling only;
# it must never alter the proven Realtime V3 media/auth transport.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'Refusing SOL dynamic-tools patch: Realtime V3 missing.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Refusing SOL dynamic-tools patch: gpt-live-1-codex missing.' }
if ($source -notmatch 'type = "webrtc"') { throw 'Refusing SOL dynamic-tools patch: WebRTC transport missing.' }
if ($source -match 'type = "existingCall"') { throw 'Refusing SOL dynamic-tools patch: legacy existingCall present.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'Refusing SOL dynamic-tools patch: direct realtime/calls path present.' }

$fieldNeedle = '    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();'
Require-Contains $source $fieldNeedle 'pending request map'
if (-not $source.Contains('readonly SolDynamicToolBridge? solDynamicTools')) {
    $source = $source.Replace(
        $fieldNeedle,
        $fieldNeedle + "`r`n    readonly SolDynamicToolBridge? solDynamicTools = SolDynamicToolBridge.TryCreate();"
    )
}

$startNeedle = '        threadId = await StartOrResumeThreadAsync(cwd, cancellationToken);'
Require-Contains $source $startNeedle 'StartOrResumeThreadAsync call'
$startBlock = @'
        var solToolSession = solDynamicTools is null
            ? null
            : await solDynamicTools.PrepareSessionAsync(cancellationToken);
        if (solToolSession?.CatalogChanged == true && !string.IsNullOrWhiteSpace(AppSettings.RealtimePersistentThreadId))
        {
            Console.WriteLine("SOL dynamic tool catalog changed · creating one fresh Codex thread so the new tools are visible");
            AppSettings.RequestNewRealtimeConversation();
        }
        threadId = await StartOrResumeThreadAsync(cwd, solToolSession, cancellationToken);
        if (solToolSession is not null) solDynamicTools?.MarkThreadReady(solToolSession.CatalogSignature);
'@
$source = $source.Replace($startNeedle, $startBlock.TrimEnd())

$signatureNeedle = '    async Task<string> StartOrResumeThreadAsync(string? cwd, CancellationToken cancellationToken)'
Require-Contains $source $signatureNeedle 'persistent thread helper signature'
$signatureReplacement = @'
    // Compatibility overload preserves the existing continuity contract and its CI invariant.
    async Task<string> StartOrResumeThreadAsync(string? cwd, CancellationToken cancellationToken)
        => await StartOrResumeThreadAsync(cwd, null, cancellationToken);

    async Task<string> StartOrResumeThreadAsync(string? cwd, SolDynamicToolSession? solToolSession, CancellationToken cancellationToken)
'@
$source = $source.Replace($signatureNeedle, $signatureReplacement.TrimEnd())

$newThreadNeedle = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);
        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
'@
Require-Contains $source $newThreadNeedle 'new persistent thread parameters'
$newThreadBlock = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);
        if (solToolSession is { ToolCount: > 0 })
            threadParams["dynamicTools"] = solToolSession.DynamicTools;
        if (solToolSession is not null && !string.IsNullOrWhiteSpace(solToolSession.DeveloperInstructions))
            threadParams["developerInstructions"] = solToolSession.DeveloperInstructions;
        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
'@
$source = $source.Replace($newThreadNeedle, $newThreadBlock)

$handleNeedle = @'
    async Task HandleMessageAsync(JsonElement root)
    {
'@
Require-Contains $source $handleNeedle 'app-server message handler'
if (-not $source.Contains('TryHandleSolDynamicToolRequestAsync')) {
    $source = $source.Replace(
        $handleNeedle,
        $handleNeedle + "        if (await TryHandleSolDynamicToolRequestAsync(root)) return;`r`n"
    )

    $toolHandler = @'
    async Task<bool> TryHandleSolDynamicToolRequestAsync(JsonElement root)
    {
        if (solDynamicTools is null) return false;
        if (!root.TryGetProperty("method", out var methodProp) || methodProp.GetString() != "item/tool/call") return false;
        if (!root.TryGetProperty("id", out var idProp)) return false;
        var id = idProp.Clone();
        var parameters = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var tool = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tool", out var toolProp)
            ? toolProp.GetString() ?? ""
            : "";
        var arguments = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
            ? args.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            if (string.IsNullOrWhiteSpace(tool)) throw new InvalidOperationException("Dynamic tool request did not contain a tool name.");
            var result = await solDynamicTools.InvokeAsync(tool, arguments, lifetime.Token);
            var response = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["result"] = new
                {
                    contentItems = new object[] { new { type = "inputText", text = JsonSerializer.Serialize(result) } },
                    success = true
                }
            };
            await SendJsonAsync(response, lifetime.Token);
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", $"Realtime SOL dynamic tool failed · tool={tool} · {ex.Message}");
            var response = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["result"] = new
                {
                    contentItems = new object[] { new { type = "inputText", text = "SOL tool failed: " + ex.Message } },
                    success = false
                }
            };
            await SendJsonAsync(response, lifetime.Token);
        }
        return true;
    }

'@
    $idx = $source.IndexOf($handleNeedle, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'Could not locate HandleMessageAsync insertion point.' }
    $source = $source.Substring(0, $idx) + $toolHandler + $source.Substring($idx)
}

$disposeNeedle = '        oauthWebRtcPeer.Dispose();'
Require-Contains $source $disposeNeedle 'Realtime bridge dispose'
if (-not $source.Contains('solDynamicTools?.Dispose();')) {
    $source = $source.Replace($disposeNeedle, '        solDynamicTools?.Dispose();' + "`r`n" + $disposeNeedle)
}

# Guard against changing the golden media/auth path while adding tool plumbing.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'V3 lost after SOL dynamic-tools patch.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Realtime model changed after SOL dynamic-tools patch.' }
if ($source -notmatch 'type = "webrtc"') { throw 'WebRTC transport lost after SOL dynamic-tools patch.' }
if ($source -match 'type = "existingCall"') { throw 'SOL dynamic-tools patch introduced existingCall.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'SOL dynamic-tools patch introduced direct realtime/calls.' }
if ($source -notmatch 'threadParams\["dynamicTools"\]') { throw 'Dynamic tools were not added to thread/start.' }
if ($source -notmatch 'developerInstructions') { throw 'SOL tool routing instructions were not added to thread/start.' }
if ($source -notmatch 'item/tool/call') { throw 'Dynamic tool app-server request handler missing.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'SOL dynamic tools layered onto golden Realtime V3 without changing media/auth transport.'
