$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

# Deliver decoded remote WebRTC PCM through the bridge's existing audio callback.
$ctorMarker = '        this.onTranscript = onTranscript;'
if (-not $source.Contains($ctorMarker)) { throw 'CodexRealtimeBridge constructor marker not found.' }
$ctorReplacement = @'
        this.onTranscript = onTranscript;
        oauthWebRtcPeer.AudioReceived = (pcm, rate) => _ = this.onAudio(pcm, rate);
'@.TrimEnd()
$source = $source.Replace($ctorMarker, $ctorReplacement)

# V3/Frameless does not accept sideband input_audio.append. The microphone PCM must
# ride the negotiated WebRTC audio media track instead.
$appendStartMarker = '    public async Task AppendAudioAsync(byte[] pcm, int count, CancellationToken cancellationToken = default)'
$stopMarker = '    public async Task StopAsync(CancellationToken cancellationToken = default)'
$appendStart = $source.IndexOf($appendStartMarker, [StringComparison]::Ordinal)
$stopStart = $source.IndexOf($stopMarker, [StringComparison]::Ordinal)
if ($appendStart -lt 0 -or $stopStart -lt 0 -or $stopStart -le $appendStart) {
    throw 'Could not locate CodexRealtimeBridge.AppendAudioAsync.'
}

$newAppend = @'
    public async Task AppendAudioAsync(byte[] pcm, int count, CancellationToken cancellationToken = default)
    {
        if (!realtimeStarted || count <= 0) return;
        await oauthWebRtcPeer.PushPcmAsync(pcm, count, inputSampleRate, cancellationToken);
    }

'@
$source = $source.Substring(0, $appendStart) + $newAppend + $source.Substring($stopStart)

if ($source.Contains('thread/realtime/appendAudio')) { throw 'Legacy sideband appendAudio is still present.' }
if (-not $source.Contains('oauthWebRtcPeer.PushPcmAsync')) { throw 'WebRTC PCM uplink was not inserted.' }
if (-not $source.Contains('oauthWebRtcPeer.AudioReceived')) { throw 'WebRTC PCM downlink callback was not inserted.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'Prepared V3 WebRTC media audio: Android PCM -> WebRTC track -> remote PCM callback.'
