$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexOAuthWebRtcPeer.cs'
$source = Get-Content -LiteralPath $path -Raw

$oldVars = @'
  let uplinkChain = Promise.resolve();
  let downlinkGeneration = 0;
'@
$newVars = @'
  let uplinkChain = Promise.resolve();
  let downlinkGeneration = 0;
  let uplinkChunks = 0;
  let uplinkBytes = 0;
  let downlinkFrames = 0;
  let statsTimer = null;
'@
if (-not $source.Contains($oldVars)) { throw 'Audio diagnostic variable marker not found.' }
$source = $source.Replace($oldVars, $newVars)

$oldReset = @'
    uplinkWriter = null;
    uplinkTrack = null;
    uplinkTimestampUs = 0;
    uplinkChain = Promise.resolve();
'@
$newReset = @'
    uplinkWriter = null;
    uplinkTrack = null;
    uplinkTimestampUs = 0;
    uplinkChain = Promise.resolve();
    uplinkChunks = 0;
    uplinkBytes = 0;
    downlinkFrames = 0;
    if (statsTimer) clearInterval(statsTimer);
    statsTimer = null;
'@
if (-not $source.Contains($oldReset)) { throw 'Audio diagnostic reset marker not found.' }
$source = $source.Replace($oldReset, $newReset)

$oldEven = @'
      const frames = evenLength / 2;
      const data = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + evenLength);
'@
$newEven = @'
      const frames = evenLength / 2;
      uplinkChunks++;
      uplinkBytes += evenLength;
      if (uplinkChunks === 1 || uplinkChunks % 50 === 0)
        event('uplink-media', `chunks=${uplinkChunks} bytes=${uplinkBytes} rate=${sampleRate}`);
      const data = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + evenLength);
'@
if (-not $source.Contains($oldEven)) { throw 'Audio diagnostic uplink marker not found.' }
$source = $source.Replace($oldEven, $newEven)

$oldDown = @'
          const frames = frame.numberOfFrames;
          const channels = Math.max(1, frame.numberOfChannels);
          const mix = new Float32Array(frames);
'@
$newDown = @'
          const frames = frame.numberOfFrames;
          const channels = Math.max(1, frame.numberOfChannels);
          downlinkFrames++;
          if (downlinkFrames === 1 || downlinkFrames % 50 === 0)
            event('downlink-media', `frames=${downlinkFrames} rate=${frame.sampleRate} channels=${channels}`);
          const mix = new Float32Array(frames);
'@
if (-not $source.Contains($oldDown)) { throw 'Audio diagnostic downlink marker not found.' }
$source = $source.Replace($oldDown, $newDown)

$oldDc = @'
        dc.onopen = () => event('datachannel', 'open');
        dc.onclose = () => event('datachannel', 'closed');
'@
$newDc = @'
        dc.onopen = () => {
          event('datachannel', 'open');
          try {
            dc.send(JSON.stringify({ type: 'input_audio.resume' }));
            event('input-audio-resume', 'sent');
          } catch (e) {
            event('input-audio-resume-error', e && e.stack ? e.stack : e);
          }

          if (statsTimer) clearInterval(statsTimer);
          statsTimer = setInterval(async () => {
            try {
              if (!pc) return;
              const stats = await pc.getStats();
              let outPackets = 0, outBytes = 0, inPackets = 0, inBytes = 0;
              stats.forEach(report => {
                const kind = report.kind || report.mediaType;
                if (kind !== 'audio') return;
                if (report.type === 'outbound-rtp' && !report.isRemote) {
                  outPackets += Number(report.packetsSent || 0);
                  outBytes += Number(report.bytesSent || 0);
                }
                if (report.type === 'inbound-rtp' && !report.isRemote) {
                  inPackets += Number(report.packetsReceived || 0);
                  inBytes += Number(report.bytesReceived || 0);
                }
              });
              event('rtp-stats', `outPackets=${outPackets} outBytes=${outBytes} inPackets=${inPackets} inBytes=${inBytes}`);
            } catch (e) {
              event('rtp-stats-error', e && e.message ? e.message : e);
            }
          }, 2000);
        };
        dc.onclose = () => event('datachannel', 'closed');
        dc.onmessage = e => {
          try {
            const payload = JSON.parse(String(e.data || '{}'));
            event('datachannel-message', payload.type || 'unknown');
          } catch (_) {
            event('datachannel-message', 'non-json');
          }
        };
'@
if (-not $source.Contains($oldDc)) { throw 'Audio diagnostic datachannel marker not found.' }
$source = $source.Replace($oldDc, $newDc)

if (-not $source.Contains("input_audio.resume")) { throw 'input_audio.resume was not inserted.' }
if (-not $source.Contains("rtp-stats")) { throw 'RTP diagnostics were not inserted.' }
if (-not $source.Contains("uplink-media")) { throw 'Uplink media diagnostics were not inserted.' }
if (-not $source.Contains("downlink-media")) { throw 'Downlink media diagnostics were not inserted.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'Instrumented WebRTC media: resume + RTP counters + uplink/downlink counters.'
