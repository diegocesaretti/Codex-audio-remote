$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

# Guardrail: this script MUST run after the known-good official V3 transforms.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'Refusing HA patch: working V3 transform is missing.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Refusing HA patch: gpt-live-1-codex is missing.' }
if ($source -notmatch 'type = "webrtc"') { throw 'Refusing HA patch: official WebRTC transport is missing.' }
if ($source -match 'type = "existingCall"') { throw 'Refusing HA patch: existingCall unexpectedly present.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'Refusing HA patch: direct ChatGPT call unexpectedly present.' }
if ($source -notmatch 'StartOrResumeThreadAsync\(cwd, cancellationToken\)') { throw 'Refusing HA patch: working thread continuity flow is missing.' }

# IMPORTANT: do NOT touch thread/start/thread/resume. The known-good build has configurable
# persistent thread continuity. HA context is intentionally layered only onto realtime/start.
$sessionMarker = '        Console.WriteLine($"Starting official Codex WebRTC session · version={RealtimeVersion} · model={RealtimeModel} · voice={RealtimeVoice}");'
if (-not $source.Contains($sessionMarker)) { throw 'Official V3 session marker not found after transforms.' }
$contextBlock = @'
        // Independent live HA snapshot. Empty context is valid and never blocks voice startup.
        // This does not change thread continuity, model, voice, SDP, auth or WebRTC transport.
        var haContext = HomeAssistantWebSocketCache.GetGlobalContext(80);
        Console.WriteLine($"HA realtime context · available={!string.IsNullOrWhiteSpace(haContext)} · chars={haContext.Length}");

'@
$source = $source.Replace($sessionMarker, $contextBlock + $sessionMarker)

$oldRealtime = @'
            codexResponsesAsItems = false,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
'@

$newRealtime = @'
            codexResponsesAsItems = false,

            // HA fast-path: only add developer instructions to the already-working V3 session.
            // The proven media/auth path remains unchanged.
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

# Final invariants: HA may change context only; it may not change the proven media/auth/thread path.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'V3 lost after HA patch.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Realtime model changed after HA patch.' }
if ($source -notmatch 'type = "webrtc"') { throw 'WebRTC transport lost after HA patch.' }
if ($source -match 'type = "existingCall"') { throw 'HA patch introduced existingCall.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'HA patch introduced direct realtime call.' }
if ($source -notmatch 'StartOrResumeThreadAsync\(cwd, cancellationToken\)') { throw 'HA patch changed thread continuity.' }
if ($source -notmatch 'realtimeStartInstructions') { throw 'HA realtime context instruction missing.' }
if ($source -match 'threadParams\["ephemeral"\]') { throw 'HA recovery build must not alter thread lifecycle with ephemeral.' }

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

Write-Host 'HA context layered onto known-good official V3 flow; thread lifecycle and Realtime transport unchanged.'