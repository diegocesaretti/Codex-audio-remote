$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$peerPath = Join-Path $root 'CodexOAuthWebRtcPeer.cs'
$bridgePath = Join-Path $root 'CodexRealtimeBridge.cs'
$mirrorPath = Join-Path $root 'RealtimeSecondaryAudioMirror.cs'
$haClientPath = Join-Path $root 'HomeAssistantMediaClient.cs'
$haApiPath = Join-Path $root 'RealtimeHomeAssistantApiServer.cs'

# Direct V3 speakable text over the already-open oai-events data channel.
$peer = Get-Content -LiteralPath $peerPath -Raw
if (-not $peer.Contains('function utf8Chunks(text')) {
    $idx = $peer.IndexOf('  function base64FromBytes(bytes) {', [StringComparison]::Ordinal)
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
    $idx = $peer.IndexOf('    async closePeer(id, reason) {', [StringComparison]::Ordinal)
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
    $idx = $peer.IndexOf('    public void Close(string reason = "normal")', [StringComparison]::Ordinal)
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

# Replace older app-server appendSpeech with direct speakable Frameless event.
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$pattern = '(?s)\s*await RequestAsync\("thread/realtime/appendSpeech",\s*new\s*\{\s*threadId,\s*text\s*=\s*text\.Trim\(\)\s*\},\s*cancellationToken\);'
$replacement = @'
        // Send exactly what current Codex V3 uses for standalone speech; do not route through
        // older app-server appendSpeech implementations that can behave like user input.
        await oauthWebRtcPeer.PushSpeakableTextAsync(text.Trim(), cancellationToken);
'@
$bridge2 = [regex]::Replace($bridge, $pattern, "`r`n" + $replacement.TrimEnd(), 1)
if ($bridge2 -eq $bridge) { throw 'Could not replace generated appendSpeech route.' }
Set-Content -LiteralPath $bridgePath -Value $bridge2 -Encoding utf8 -NoNewline

# Cast-specific media type; the HTTP response itself remains audio/mpeg.
$haClient = Get-Content -LiteralPath $haClientPath -Raw
$haClient = $haClient.Replace('media_content_type = "music",', 'media_content_type = "audio/mp3",')
$haClient = $haClient.Replace('type=music · stream=', 'type=audio/mp3 · stream=')
Set-Content -LiteralPath $haClientPath -Value $haClient -Encoding utf8 -NoNewline

# Ignore normal HTTP write failures caused by Cast probes/disconnects.
$haApi = Get-Content -LiteralPath $haApiPath -Raw
if (-not $haApi.Contains('listener.IgnoreWriteExceptions = true;')) {
    $needle = '        listener.Prefixes.Add($"http://+:{AppSettings.HomeAssistantApiPort}/api/");'
    if (-not $haApi.Contains($needle)) { throw 'HA API listener prefix marker missing.' }
    $haApi = $haApi.Replace($needle, "        listener.IgnoreWriteExceptions = true;`r`n" + $needle)
}
Set-Content -LiteralPath $haApiPath -Value $haApi -Encoding utf8 -NoNewline

$mirror = Get-Content -LiteralPath $mirrorPath -Raw

# Per-stream diagnostics used by the Settings test.
if (-not $mirror.Contains('public long BytesServed =>')) {
    $needle = '        public string Token { get; } = Guid.NewGuid().ToString("N");'
    if (-not $mirror.Contains($needle)) { throw 'LiveMp3Stream token marker missing.' }
    $insert = @'
        int headRequests;
        int getRequests;
        long bytesServed;
        string lastDisconnect = "";
        readonly TaskCompletionSource<bool> firstGet = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    $mirror = $mirror.Replace($needle, $insert + "`r`n" + $needle)
}

# Replace the entire HTTP serving method. This deliberately avoids regex over generated C# so the
# transform is stable on both CRLF and LF worktrees.
$serveStartMarker = '    public static async Task<bool> TryServeHomeAssistantStreamAsync(HttpListenerContext context, CancellationToken token)'
$serveEndMarker = '    /// <summary>'
$serveStart = $mirror.IndexOf($serveStartMarker, [StringComparison]::Ordinal)
$serveEnd = if ($serveStart -ge 0) { $mirror.IndexOf($serveEndMarker, $serveStart, [StringComparison]::Ordinal) } else { -1 }
if ($serveStart -lt 0 -or $serveEnd -lt 0 -or $serveEnd -le $serveStart) { throw 'HA stream serving method markers missing.' }
$serveMethod = @'
    public static async Task<bool> TryServeHomeAssistantStreamAsync(HttpListenerContext context, CancellationToken token)
    {
        var supplied = context.Request.QueryString["token"] ?? "";
        if (string.IsNullOrWhiteSpace(supplied) || !LiveStreams.TryGetValue(supplied, out var stream)) return false;

        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "audio/mpeg";
        response.SendChunked = true;
        response.KeepAlive = false;
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Accept-Ranges"] = "none";

        var method = context.Request.HttpMethod;
        var ua = context.Request.UserAgent ?? "unknown";
        Console.WriteLine($"Realtime HA mirror HTTP {method} · remote={context.Request.RemoteEndPoint} · ua={ua}");

        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            stream.NoteHead();
            try { response.Close(); } catch { }
            return true;
        }

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 405;
            try { response.Close(); } catch { }
            return true;
        }

        stream.NoteGet();
        try
        {
            await foreach (var chunk in stream.Subscribe(token))
            {
                if (chunk.Length == 0) continue;
                await response.OutputStream.WriteAsync(chunk, token);
                stream.NoteBytes(chunk.Length);
                await response.OutputStream.FlushAsync(token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Cast can intentionally close/re-open a LIVE stream while probing or replacing media.
            // Record the reason for diagnostics but do not fail the producer because of it.
            stream.NoteDisconnect(ex.Message);
            Console.WriteLine("Realtime HA mirror HTTP client disconnected: " + ex.Message);
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
        }
        return true;
    }

'@
$mirror = $mirror.Substring(0, $serveStart) + $serveMethod + $mirror.Substring($serveEnd)

# Replace TestHomeAssistantMirrorAsync using stable textual markers. Success requires the receiver
# to perform GET and actually receive MP3 bytes; an early receiver close is only diagnostic.
$testStartMarker = '    public static async Task<string> TestHomeAssistantMirrorAsync(CancellationToken cancellationToken = default)'
$testEndMarker = '    static async Task PumpTestToneAsync'
$testStart = $mirror.IndexOf($testStartMarker, [StringComparison]::Ordinal)
$testEnd = if ($testStart -ge 0) { $mirror.IndexOf($testEndMarker, $testStart, [StringComparison]::Ordinal) } else { -1 }
if ($testStart -lt 0 -or $testEnd -lt 0 -or $testEnd -le $testStart) { throw 'HA mirror test method markers missing.' }
$testMethod = @'
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
            try { await HomeAssistantMediaClient.StartLiveStreamAsync(url, testCts.Token); }
            catch (Exception ex) { throw new InvalidOperationException("play_media falló: " + ex.Message, ex); }

            var gotGet = await stream.WaitForGetAsync(TimeSpan.FromSeconds(6), testCts.Token);
            try { await pump; }
            catch (OperationCanceledException) when (testCts.IsCancellationRequested) { }
            catch (Exception ex) { Console.WriteLine("HA mirror test tone producer ended: " + ex.Message); }

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

'@
$mirror = $mirror.Substring(0, $testStart) + $testMethod + $mirror.Substring($testEnd)
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
if ($mirrorCheck -notmatch 'BytesServed') { throw 'HA stream byte diagnostics missing.' }
if ($mirrorCheck -notmatch 'WaitForGetAsync') { throw 'HA GET verification missing.' }
if ($mirrorCheck -notmatch 'stream\.NoteGet\(\)') { throw 'HA GET accounting missing.' }
if ($mirrorCheck -notmatch 'stream\.NoteBytes') { throw 'HA byte accounting missing.' }
if ($clientCheck -notmatch 'media_content_type = "audio/mp3"') { throw 'Cast media_content_type audio/mp3 missing.' }
if ($apiCheck -notmatch 'IgnoreWriteExceptions = true') { throw 'HttpListener disconnect tolerance missing.' }
Write-Host 'Prepared direct V3 speakable text + resilient HA/Cast live stream diagnostics.'
