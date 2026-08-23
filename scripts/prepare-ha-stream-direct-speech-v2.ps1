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

# Cast wants an audio media type; the HTTP response itself remains audio/mpeg.
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

# Per-stream diagnostics.
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

# HEAD probe accounting.
$mirror = [regex]::Replace(
    $mirror,
    '(if \(string\.Equals\(method, "HEAD", StringComparison\.OrdinalIgnoreCase\)\)\s*\{)(\s*try \{ response\.Close\(\); \} catch \{ \})',
    '$1' + "`r`n            stream.NoteHead();" + '$2',
    1)

# GET accounting.
$guard = '(if \(!string\.Equals\(method, "GET", StringComparison\.OrdinalIgnoreCase\)\)\s*\{.*?return true;\s*\})\s*(try)'
$mirror2 = [regex]::Replace($mirror, $guard, '$1' + "`r`n`r`n        stream.NoteGet();`r`n        " + '$2', 1, [Text.RegularExpressions.RegexOptions]::Singleline)
if ($mirror2 -eq $mirror -and -not $mirror.Contains('stream.NoteGet();')) { throw 'Could not add HA GET accounting.' }
$mirror = $mirror2

# Count bytes actually delivered to the receiver.
if (-not $mirror.Contains('stream.NoteBytes(chunk.Length);')) {
    $needle = '                await response.OutputStream.WriteAsync(chunk, token);'
    if (-not $mirror.Contains($needle)) { throw 'HA stream write marker missing.' }
    $mirror = $mirror.Replace($needle, $needle + "`r`n                stream.NoteBytes(chunk.Length);")
}

# Record receiver-close reason without treating it as producer failure.
$oldCatch = '        catch (Exception ex) { Console.WriteLine("Realtime HA mirror HTTP ended: " + ex.Message); }'
if ($mirror.Contains($oldCatch)) {
    $newCatch = @'
        catch (Exception ex)
        {
            stream.NoteDisconnect(ex.Message);
            Console.WriteLine("Realtime HA mirror HTTP client disconnected: " + ex.Message);
        }
'@
    $mirror = $mirror.Replace($oldCatch, $newCatch.TrimEnd())
}

# Replace TestHomeAssistantMirrorAsync using stable textual markers (CRLF/LF agnostic).
$startMarker = '    public static async Task<string> TestHomeAssistantMirrorAsync(CancellationToken cancellationToken = default)'
$endMarker = '    static async Task PumpTestToneAsync'
$start = $mirror.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = if ($start -ge 0) { $mirror.IndexOf($endMarker, $start, [StringComparison]::Ordinal) } else { -1 }
if ($start -lt 0 -or $end -lt 0 -or $end -le $start) { throw 'HA mirror test method markers missing.' }
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
$mirror = $mirror.Substring(0, $start) + $testMethod + $mirror.Substring($end)
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
if ($clientCheck -notmatch 'media_content_type = "audio/mp3"') { throw 'Cast media_content_type audio/mp3 missing.' }
if ($apiCheck -notmatch 'IgnoreWriteExceptions = true') { throw 'HttpListener disconnect tolerance missing.' }
Write-Host 'Prepared direct V3 speakable text + resilient HA/Cast live stream diagnostics.'
