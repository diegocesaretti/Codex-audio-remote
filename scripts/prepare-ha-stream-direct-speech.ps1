$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$peerPath = Join-Path $root 'CodexOAuthWebRtcPeer.cs'
$bridgePath = Join-Path $root 'CodexRealtimeBridge.cs'
$mirrorPath = Join-Path $root 'RealtimeSecondaryAudioMirror.cs'
$haClientPath = Join-Path $root 'HomeAssistantMediaClient.cs'
$haApiPath = Join-Path $root 'RealtimeHomeAssistantApiServer.cs'

# -----------------------------------------------------------------------------
# 1) Bypass older app-server appendSpeech behavior for V3/WebRTC.
# Current Codex V3 implements standalone speech as:
# session.context.append { channel:"speakable", content:[{type:"input_text",text:...}] }
# Send that exact Frameless event over the already-open oai-events data channel.
# -----------------------------------------------------------------------------
$peer = Get-Content -LiteralPath $peerPath -Raw

if (-not $peer.Contains('function utf8Chunks(text')) {
    $marker = @'
  function base64FromBytes(bytes) {
'@
    $idx = $peer.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'Peer base64 helper marker missing.' }
    $helper = @'
  function utf8Chunks(text, maxBytes = 480) {
    const encoder = new TextEncoder();
    const chunks = [];
    let current = '';
    let bytes = 0;
    for (const ch of String(text || '')) {
      const n = encoder.encode(ch).byteLength;
      if (current && bytes + n > maxBytes) {
        chunks.push(current);
        current = ch;
        bytes = n;
      } else {
        current += ch;
        bytes += n;
      }
    }
    if (current) chunks.push(current);
    return chunks;
  }

'@
    $peer = $peer.Substring(0, $idx) + $helper + $peer.Substring($idx)
}

if (-not $peer.Contains('async speakText(id, text)')) {
    $marker = @'
    async closePeer(id, reason) {
'@
    $idx = $peer.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'Peer closePeer JS marker missing.' }
    $method = @'
    async speakText(id, text) {
      try {
        if (!dc || dc.readyState !== 'open') throw new Error('Realtime datachannel is not open.');
        const chunks = utf8Chunks(String(text || '').trim());
        if (!chunks.length) {
          post({ id, ok: true, result: 'empty' });
          return;
        }
        for (const chunk of chunks) {
          dc.send(JSON.stringify({
            type: 'session.context.append',
            channel: 'speakable',
            content: [{ type: 'input_text', text: chunk }]
          }));
        }
        event('speakable-text', `chars=${String(text || '').length} chunks=${chunks.length}`);
        post({ id, ok: true, result: 'ok' });
      } catch (e) {
        post({ id, ok: false, error: String(e && e.stack ? e.stack : e) });
      }
    },

'@
    $peer = $peer.Substring(0, $idx) + $method + $peer.Substring($idx)
}

if (-not $peer.Contains('public async Task PushSpeakableTextAsync')) {
    $marker = '    public void Close(string reason = "normal")'
    $idx = $peer.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'Peer Close C# marker missing.' }
    $method = @'
    public async Task PushSpeakableTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        await EnsureHostAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeAsync("speakText", text).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

'@
    $peer = $peer.Substring(0, $idx) + $method + $peer.Substring($idx)
}
Set-Content -LiteralPath $peerPath -Value $peer -Encoding utf8 -NoNewline

# Replace the app-server appendSpeech request inserted by prepare-realtime-ha-speech.ps1.
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$old = @'
        await RequestAsync("thread/realtime/appendSpeech", new
        {
            threadId,
            text = text.Trim()
        }, cancellationToken);
'@
$new = @'
        // Direct V3 speakable context append. This matches current Codex Frameless semantics and
        // avoids older app-server builds that accidentally treat appendSpeech as user input.
        await oauthWebRtcPeer.PushSpeakableTextAsync(text.Trim(), cancellationToken);
'@
if (-not $bridge.Contains($old)) { throw 'Generated AppendSpeechAsync app-server route marker missing.' }
$bridge = $bridge.Replace($old, $new)
Set-Content -LiteralPath $bridgePath -Value $bridge -Encoding utf8 -NoNewline

# -----------------------------------------------------------------------------
# 2) Home Assistant / Cast stream robustness.
# -----------------------------------------------------------------------------
$haClient = Get-Content -LiteralPath $haClientPath -Raw
$haClient = $haClient.Replace('media_content_type = "music",', 'media_content_type = "audio/mp3",')
$haClient = $haClient.Replace('type=music · stream=', 'type=audio/mp3 · stream=')
Set-Content -LiteralPath $haClientPath -Value $haClient -Encoding utf8 -NoNewline

$haApi = Get-Content -LiteralPath $haApiPath -Raw
$ctorMarker = @'
        this.speak = speak;
        listener.Prefixes.Add($"http://+:{AppSettings.HomeAssistantApiPort}/api/");
'@
$ctorNew = @'
        this.speak = speak;
        // Cast receivers routinely probe/abort streaming HTTP connections. Do not surface those
        // client disconnects as listener failures.
        listener.IgnoreWriteExceptions = true;
        listener.Prefixes.Add($"http://+:{AppSettings.HomeAssistantApiPort}/api/");
'@
if (-not $haApi.Contains('listener.IgnoreWriteExceptions = true;')) {
    if (-not $haApi.Contains($ctorMarker)) { throw 'HA API constructor marker missing.' }
    $haApi = $haApi.Replace($ctorMarker, $ctorNew)
}
Set-Content -LiteralPath $haApiPath -Value $haApi -Encoding utf8 -NoNewline

$mirror = Get-Content -LiteralPath $mirrorPath -Raw

# Track actual receiver probes, GETs and delivered bytes so the Settings test reports useful facts.
if (-not $mirror.Contains('public long BytesServed =>')) {
    $fields = @'
        int prebufferBytes;
        bool completed;
        public string Token { get; } = Guid.NewGuid().ToString("N");
'@
    $fieldsNew = @'
        int prebufferBytes;
        bool completed;
        int headRequests;
        int getRequests;
        long bytesServed;
        string lastDisconnect = "";
        readonly TaskCompletionSource<bool> firstGet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public int HeadRequests => Volatile.Read(ref headRequests);
        public int GetRequests => Volatile.Read(ref getRequests);
        public long BytesServed => Interlocked.Read(ref bytesServed);
        public string LastDisconnect { get { lock (gate) return lastDisconnect; } }
        public void NoteHead() => Interlocked.Increment(ref headRequests);
        public void NoteGet() { Interlocked.Increment(ref getRequests); firstGet.TrySetResult(true); }
        public void NoteBytes(int count) { if (count > 0) Interlocked.Add(ref bytesServed, count); }
        public void NoteDisconnect(string value) { lock (gate) lastDisconnect = value ?? ""; }
        public async Task<bool> WaitForGetAsync(TimeSpan timeout, CancellationToken token)
        {
            try { await firstGet.Task.WaitAsync(timeout, token); return true; }
            catch (TimeoutException) { return false; }
        }
'@
    if (-not $mirror.Contains($fields)) { throw 'LiveMp3Stream field marker missing.' }
    $mirror = $mirror.Replace($fields, $fieldsNew)
}

$headOld = @'
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            try { response.Close(); } catch { }
            return true;
        }
'@
$headNew = @'
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            stream.NoteHead();
            try { response.Close(); } catch { }
            return true;
        }
'@
if ($mirror.Contains($headOld)) { $mirror = $mirror.Replace($headOld, $headNew) }

$getMarker = @'
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 405;
            try { response.Close(); } catch { }
            return true;
        }

        try
'@
$getNew = @'
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 405;
            try { response.Close(); } catch { }
            return true;
        }

        stream.NoteGet();
        try
'@
if ($mirror.Contains($getMarker)) { $mirror = $mirror.Replace($getMarker, $getNew) }

$writeOld = @'
                await response.OutputStream.WriteAsync(chunk, token);
                await response.OutputStream.FlushAsync(token);
'@
$writeNew = @'
                await response.OutputStream.WriteAsync(chunk, token);
                stream.NoteBytes(chunk.Length);
                await response.OutputStream.FlushAsync(token);
'@
if ($mirror.Contains($writeOld)) { $mirror = $mirror.Replace($writeOld, $writeNew) }

$catchOld = '        catch (Exception ex) { Console.WriteLine("Realtime HA mirror HTTP ended: " + ex.Message); }'
$catchNew = @'
        catch (Exception ex)
        {
            // A Cast/Google Home client closing a live stream early is normal (probe, buffering
            // strategy, media replacement, etc.). Record it for diagnostics but never fail the
            // producer or the Settings test solely because the HTTP consumer went away.
            stream.NoteDisconnect(ex.Message);
            Console.WriteLine("Realtime HA mirror HTTP client disconnected: " + ex.Message);
        }
'@
if ($mirror.Contains($catchOld)) { $mirror = $mirror.Replace($catchOld, $catchNew) }

# Replace the test body so receiver disconnects are diagnostic, and success requires an actual GET
# with bytes delivered rather than merely a 200 from Home Assistant's service call.
$testPattern = '(?s)    public static async Task<string> TestHomeAssistantMirrorAsync\(CancellationToken cancellationToken = default\)\s*\{.*?\n    \}\n\n    static async Task PumpTestToneAsync'
$testReplacement = @'
    public static async Task<string> TestHomeAssistantMirrorAsync(CancellationToken cancellationToken = default)
    {
        if (!RealtimeMirrorSettings.HasHomeAssistantAccessToken)
            throw new InvalidOperationException("Falta el token de Home Assistant.");
        if (string.IsNullOrWhiteSpace(RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity))
            throw new InvalidOperationException("Seleccioná un media_player de Home Assistant.");

        var stream = new LiveMp3Stream();
        LiveStreams[stream.Token] = stream;
        var ip = await ResolveLanIPv4ForHomeAssistantAsync(cancellationToken);
        var url = BuildStreamUrl(ip, stream.Token);
        Console.WriteLine($"HA mirror test armed · {url}");

        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        testCts.CancelAfter(TimeSpan.FromSeconds(15));
        var pump = Task.Run(() => PumpTestToneAsync(stream, testCts.Token), testCts.Token);

        try
        {
            await Task.Delay(180, testCts.Token);
            try
            {
                await HomeAssistantMediaClient.StartLiveStreamAsync(url, testCts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("play_media falló: " + ex.Message, ex);
            }

            var gotGet = await stream.WaitForGetAsync(TimeSpan.FromSeconds(6), testCts.Token);
            try { await pump; }
            catch (OperationCanceledException) when (testCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine("HA mirror test tone producer ended: " + ex.Message);
            }

            await Task.Delay(250, CancellationToken.None);
            var status = $"HEAD={stream.HeadRequests} · GET={stream.GetRequests} · bytes={stream.BytesServed}";
            if (!string.IsNullOrWhiteSpace(stream.LastDisconnect)) status += $" · receiverClose={stream.LastDisconnect}";

            if (!gotGet || stream.GetRequests == 0)
                throw new InvalidOperationException($"play_media fue aceptado pero el reproductor nunca hizo GET al stream. {status} · URL={url}");
            if (stream.BytesServed <= 0)
                throw new InvalidOperationException($"El reproductor abrió el stream pero no recibió audio. {status} · URL={url}");

            return $"stream OK · {status} · {url}";
        }
        finally
        {
            stream.Complete();
            await Task.Delay(600, CancellationToken.None);
            LiveStreams.TryRemove(stream.Token, out _);
        }
    }

    static async Task PumpTestToneAsync'@
$updated = [regex]::Replace($mirror, $testPattern, $testReplacement, 1)
if ($updated -eq $mirror) { throw 'HA mirror test method regex did not match.' }
$mirror = $updated
Set-Content -LiteralPath $mirrorPath -Value $mirror -Encoding utf8 -NoNewline

# Validation.
$peerCheck = Get-Content -LiteralPath $peerPath -Raw
$bridgeCheck = Get-Content -LiteralPath $bridgePath -Raw
$mirrorCheck = Get-Content -LiteralPath $mirrorPath -Raw
$clientCheck = Get-Content -LiteralPath $haClientPath -Raw
$apiCheck = Get-Content -LiteralPath $haApiPath -Raw
if ($peerCheck -notmatch 'session\.context\.append') { throw 'Direct V3 session.context.append missing.' }
if ($peerCheck -notmatch "channel: 'speakable'") { throw 'Direct V3 speakable channel missing.' }
if ($bridgeCheck -match 'thread/realtime/appendSpeech') { throw 'Legacy app-server appendSpeech still used by bridge.' }
if ($bridgeCheck -notmatch 'PushSpeakableTextAsync') { throw 'Bridge direct speakable call missing.' }
if ($mirrorCheck -notmatch 'BytesServed') { throw 'HA stream diagnostics missing.' }
if ($mirrorCheck -notmatch 'WaitForGetAsync') { throw 'HA GET verification missing.' }
if ($clientCheck -notmatch 'media_content_type = "audio/mp3"') { throw 'Cast media_content_type audio/mp3 missing.' }
if ($apiCheck -notmatch 'IgnoreWriteExceptions = true') { throw 'HttpListener disconnect tolerance missing.' }
Write-Host 'Prepared direct V3 speakable text + resilient HA/Cast live stream diagnostics.'
