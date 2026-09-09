$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

# Guardrail: this script MUST run after the known-good official V3 transforms.
if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'Refusing context patch: working V3 transform is missing.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Refusing context patch: gpt-live-1-codex is missing.' }
if ($source -notmatch 'type = "webrtc"') { throw 'Refusing context patch: official WebRTC transport is missing.' }
if ($source -match 'type = "existingCall"') { throw 'Refusing context patch: existingCall unexpectedly present.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'Refusing context patch: direct ChatGPT call unexpectedly present.' }
if ($source -notmatch 'StartOrResumeThreadAsync\(cwd, cancellationToken\)') { throw 'Refusing context patch: working thread continuity flow is missing.' }

# This transform is for the SOL-native package only. Home Assistant state is owned by
# the Home Assistant SOL plugin and consumed through the read-only plugin bus. There
# is intentionally no direct HA token/WebSocket/cache fallback in this package.
$sessionMarker = '        Console.WriteLine($"Starting official Codex WebRTC session · version={RealtimeVersion} · model={RealtimeModel} · voice={RealtimeVoice}");'
if (-not $source.Contains($sessionMarker)) { throw 'Official V3 session marker not found after transforms.' }
$contextBlock = @'
        var haContext = SolPluginHost.Enabled
            ? await SolHomeAssistantContext.GetContextAsync(80, cancellationToken)
            : string.Empty;
        var voiceStyleInstructions = SolPluginHost.Enabled ? SolVoiceStyle.Instructions : string.Empty;
        var haInstructions = string.IsNullOrWhiteSpace(haContext)
            ? string.Empty
            : "HOME ASSISTANT FAST PATH. The following state snapshot is already current. " +
              "For simple home-control requests, use these exact entity ids/states and call the existing Home Assistant tool directly. " +
              "Do not rediscover/list HA state unless the requested entity is absent or this snapshot is stale.\n\n" + haContext;
        var realtimeInstructions = string.Join("\n\n", new[] { voiceStyleInstructions, haInstructions }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        Console.WriteLine($"Realtime context · HA={!string.IsNullOrWhiteSpace(haContext)} · voiceStyle={!string.IsNullOrWhiteSpace(voiceStyleInstructions)} · chars={realtimeInstructions.Length}");

'@
$source = $source.Replace($sessionMarker, $contextBlock + $sessionMarker)

$oldRealtime = @'
            codexResponsesAsItems = false,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
'@
$newRealtime = @'
            codexResponsesAsItems = false,
            realtimeStartInstructions = string.IsNullOrWhiteSpace(realtimeInstructions) ? null : realtimeInstructions,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
'@
if (-not $source.Contains($oldRealtime)) { throw 'V3 realtime/start anchor not found after official transforms.' }
$source = $source.Replace($oldRealtime, $newRealtime)

if ($source -notmatch 'RealtimeVersion = "v3"') { throw 'V3 lost after context patch.' }
if ($source -notmatch 'RealtimeModel = "gpt-live-1-codex"') { throw 'Realtime model changed after context patch.' }
if ($source -notmatch 'type = "webrtc"') { throw 'WebRTC transport lost after context patch.' }
if ($source -match 'type = "existingCall"') { throw 'Context patch introduced existingCall.' }
if ($source -match 'directRealtimeCall\.CreateAsync') { throw 'Context patch introduced direct realtime call.' }
if ($source -notmatch 'StartOrResumeThreadAsync\(cwd, cancellationToken\)') { throw 'Context patch changed thread continuity.' }
if ($source -notmatch 'realtimeStartInstructions') { throw 'Realtime instructions missing.' }
if ($source -notmatch 'SolVoiceStyle\.Instructions') { throw 'Native voice style instructions missing.' }
if ($source -match 'HomeAssistantWebSocketCache') { throw 'Direct HA cache leaked into native bridge.' }
if ($source -match 'threadParams\["ephemeral"\]') { throw 'Context build must not alter thread lifecycle with ephemeral.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'SOL HA context + native voice style layered onto V3; no direct HA cache or legacy audio path.'
