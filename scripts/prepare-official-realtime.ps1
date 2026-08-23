$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

$oldVersion = '    const string RealtimeVersion = "v1";'
$newVersion = @'
    const string RealtimeVersion = "v3";
    const string RealtimeModel = "gpt-live-1-codex";
'@.TrimEnd()
if (-not $source.Contains($oldVersion)) { throw 'Expected v1 realtime version marker was not found.' }
$source = $source.Replace($oldVersion, $newVersion)

$directField = '    readonly CodexDirectRealtimeCall directRealtimeCall = new();'
if (-not $source.Contains($directField)) { throw 'Expected direct realtime call field was not found.' }
$source = $source.Replace($directField + "`r`n", '')
$source = $source.Replace($directField + "`n", '')

$startMarker = '        Console.WriteLine($"Starting direct OAuth WebRTC compatibility session · version={RealtimeVersion}");'
$endMarker = '        var startedAt = Environment.TickCount64;'
$start = $source.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $source.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt 0 -or $end -le $start) {
    throw 'Could not locate the old direct-call handshake block.'
}

$newHandshake = @'
        Console.WriteLine($"Starting official Codex WebRTC session · version={RealtimeVersion} · model={RealtimeModel}");

        // Follow the captured working Codex Desktop flow. Codex app-server owns the
        // authenticated ChatGPT /backend-api/codex/realtime/calls request, OAuth/cookies,
        // AVAS/quicksilver headers, call-id parsing, SDP response and sideband creation.
        await RequestAsync("thread/realtime/start", new
        {
            threadId,
            model = RealtimeModel,
            outputModality = "audio",
            version = RealtimeVersion,
            clientManagedHandoffs = false,
            flushTranscriptTailOnSessionEnd = true,
            codexResponsesAsItems = false,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
            codexResponseHandoffMode = "commentary",
            transport = new
            {
                type = "webrtc",
                sdp = offerSdp
            }
        }, cancellationToken);

'@
$source = $source.Substring(0, $start) + $newHandshake + $source.Substring($end)

$source = $source.Replace(
    '            throw new TimeoutException($"Codex realtime {RealtimeVersion} existingCall did not emit thread/realtime/started within 12 seconds.");',
    '            throw new TimeoutException($"Codex realtime {RealtimeVersion} official WebRTC did not emit thread/realtime/started within 12 seconds.");')

$oldSdpComment = @'
                // Kept for compatibility with stock Codex WebRTC-created calls. The direct-call
                // path normally applies its SDP before attaching as existingCall.
'@
$newSdpComment = @'
                // In the official flow Codex app-server creates the realtime call and returns
                // the SDP answer through thread/realtime/sdp. Chromium applies that answer here.
'@
$source = $source.Replace($oldSdpComment, $newSdpComment)

if ($source.Contains('directRealtimeCall.CreateAsync')) { throw 'Direct realtime/calls creation is still present.' }
if ($source.Contains('type = "existingCall"')) { throw 'existingCall transport is still present.' }
if (-not $source.Contains('model = RealtimeModel')) { throw 'Official gpt-live model was not inserted.' }
if (-not $source.Contains('type = "webrtc"')) { throw 'Official WebRTC transport was not inserted.' }
if (-not $source.Contains('includeStartupContext = false')) { throw 'Captured startup-context setting was not inserted.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'Prepared official Codex realtime flow: v3 + gpt-live-1-codex + app-server-owned WebRTC call creation.'
