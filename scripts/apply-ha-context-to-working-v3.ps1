$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

# Guardrail: this script MUST run after the known-good official V3 transforms.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'Refusing HA patch: working V3 transform is missing.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Refusing HA patch: gpt-live-1-codex is missing.' }
if ($source -notmatch 'type = "webrtc"') { throw 'Refusing HA patch: official WebRTC transport is missing.' }
if ($source -match 'type = "existingCall"') { throw 'Refusing HA patch: existingCall unexpectedly present.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'Refusing HA patch: direct ChatGPT call unexpectedly present.' }

$oldThread = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
'@

$newThread = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        // Fast-path change #1 only: voice threads are disposable. This does not alter
        // the Realtime V3/WebRTC handshake below.
        threadParams["ephemeral"] = true;

        // Snapshot is prepared by the independent HA WebSocket cache. Empty context is valid
        // and must never block or fail voice startup.
        var haContext = HomeAssistantWebSocketCache.GetGlobalContext(80);
        var threadStartAt = Stopwatch.GetTimestamp();
        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        Console.WriteLine($"Realtime thread/start · {Stopwatch.GetElapsedTime(threadStartAt).TotalMilliseconds:0} ms · ephemeral=True · HA-context={!string.IsNullOrWhiteSpace(haContext)} · chars={haContext.Length}");
'@

if (-not $source.Contains($oldThread)) { throw 'thread/start anchor not found after official transforms.' }
$source = $source.Replace($oldThread, $newThread)

$oldRealtime = @'
            codexResponsesAsItems = false,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
'@

$newRealtime = @'
            codexResponsesAsItems = false,

            // Fast-path change #2 only: feed the already-current HA snapshot as V3 realtime
            // developer instructions. Transport, model, voice, SDP and protocol version remain
            // byte-for-byte the known-good official flow.
            realtimeStartInstructions = string.IsNullOrWhiteSpace(haContext)
                ? null
                : "HOME ASSISTANT FAST PATH. The following state snapshot is already current. " +
                  "For simple home-control requests, use these exact entity ids/states and call the existing Home Assistant tool directly. " +
                  "Do not rediscover/list HA state unless the requested entity is absent or this snapshot is stale.\n\n" + haContext,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
'@

if (-not $source.Contains($oldRealtime)) { throw 'V3 realtime/start anchor not found after official transforms.' }
$source = $source.Replace($oldRealtime, $newRealtime)

# Final invariants: HA may change context only; it may not change the proven media/auth path.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'V3 lost after HA patch.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Realtime model changed after HA patch.' }
if ($source -notmatch 'type = "webrtc"') { throw 'WebRTC transport lost after HA patch.' }
if ($source -match 'type = "existingCall"') { throw 'HA patch introduced existingCall.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'HA patch introduced direct realtime call.' }
if ($source -notmatch 'realtimeStartInstructions') { throw 'HA realtime context instruction missing.' }
if ($source -notmatch 'threadParams\["ephemeral"\] = true') { throw 'Ephemeral thread missing.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline

# Start the independent HA cache only AFTER all legacy Program.cs transforms have finished.
# This avoids changing any source block that those known-good scripts expect to match exactly.
$programPath = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\Program.cs'
$program = Get-Content -LiteralPath $programPath -Raw
$programAnchor = 'var options = Options.Parse(args);'
if (-not $program.Contains($programAnchor)) { throw 'Program.cs options anchor missing after official transforms.' }
if ($program -notmatch 'HomeAssistantWebSocketCache\.StartGlobal') {
    $programInsert = @'
var options = Options.Parse(args);

// Independent HA state cache. It does not own or alter Codex Realtime/WebRTC.
HomeAssistantWebSocketCache.StartGlobal();
AppDomain.CurrentDomain.ProcessExit += (_, _) => HomeAssistantWebSocketCache.DisposeGlobal();
'@
    $program = $program.Replace($programAnchor, $programInsert.TrimEnd())
}
Set-Content -LiteralPath $programPath -Value $program -Encoding utf8 -NoNewline

Write-Host 'HA context layered onto known-good official V3 flow without changing Realtime transport.'