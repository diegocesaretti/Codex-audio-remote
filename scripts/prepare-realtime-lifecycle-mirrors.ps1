$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$bridgePath = Join-Path $root 'CodexRealtimeBridge.cs'
$peerPath = Join-Path $root 'CodexOAuthWebRtcPeer.cs'
$serverPath = Join-Path $root 'RealtimeSessionServer.cs'
$settingsPath = Join-Path $root 'SettingsForm.cs'

# WebRTC datachannel control: pause/resume microphone without closing the call.
$peer = Get-Content -LiteralPath $peerPath -Raw
if (-not $peer.Contains('public async Task SetInputPausedAsync')) {
    $jsMarker = '    async applyAnswer(id, sdp) {'
    $jsIndex = $peer.IndexOf($jsMarker, [StringComparison]::Ordinal)
    if ($jsIndex -lt 0) { throw 'WebRTC applyAnswer marker missing.' }
    $js = @'
    async pauseInput(id) {
      try {
        if (!dc || dc.readyState !== 'open') throw new Error('Realtime datachannel is not open.');
        dc.send(JSON.stringify({ type: 'input_audio.pause' }));
        event('input-audio-pause', 'sent');
        post({ id, ok: true, result: 'ok' });
      } catch (e) {
        post({ id, ok: false, error: String(e && e.stack ? e.stack : e) });
      }
    },

    async resumeInput(id) {
      try {
        if (!dc || dc.readyState !== 'open') throw new Error('Realtime datachannel is not open.');
        dc.send(JSON.stringify({ type: 'input_audio.resume' }));
        event('input-audio-resume', 'sent');
        post({ id, ok: true, result: 'ok' });
      } catch (e) {
        post({ id, ok: false, error: String(e && e.stack ? e.stack : e) });
      }
    },

'@
    $peer = $peer.Substring(0, $jsIndex) + $js + $peer.Substring($jsIndex)

    $closeMarker = '    public void Close(string reason = "normal")'
    $closeIndex = $peer.IndexOf($closeMarker, [StringComparison]::Ordinal)
    if ($closeIndex -lt 0) { throw 'WebRTC Close marker missing.' }
    $method = @'
    public async Task SetInputPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureHostAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeAsync(paused ? "pauseInput" : "resumeInput");
    }

'@
    $peer = $peer.Substring(0, $closeIndex) + $method + $peer.Substring($closeIndex)
    Set-Content -LiteralPath $peerPath -Value $peer -Encoding utf8 -NoNewline
}

$bridge = Get-Content -LiteralPath $bridgePath -Raw
if (-not $bridge.Contains('public async Task SetInputPausedAsync')) {
    $stopMarker = '    public async Task StopAsync(CancellationToken cancellationToken = default)'
    $stopIndex = $bridge.IndexOf($stopMarker, [StringComparison]::Ordinal)
    if ($stopIndex -lt 0) { throw 'Realtime bridge StopAsync marker missing.' }
    $method = @'
    public async Task SetInputPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        if (!realtimeStarted) return;
        await oauthWebRtcPeer.SetInputPausedAsync(paused, cancellationToken);
    }

'@
    $bridge = $bridge.Substring(0, $stopIndex) + $method + $bridge.Substring($stopIndex)
    Set-Content -LiteralPath $bridgePath -Value $bridge -Encoding utf8 -NoNewline
}

# Authoritative server: LISTENING -> PAUSED -> IDLE plus secondary mirrors.
$server = Get-Content -LiteralPath $serverPath -Raw
if (-not $server.Contains('RealtimeSecondaryAudioMirror secondaryMirror')) {
    $fieldMarker = '    readonly SemaphoreSlim activationGate = new(1, 1);'
    $fieldReplacement = @'
    readonly SemaphoreSlim activationGate = new(1, 1);
    readonly RealtimeSecondaryAudioMirror secondaryMirror = new();
    CancellationTokenSource? listenSilenceCts;
    CancellationTokenSource? conversationIdleCts;
'@
    if (-not $server.Contains($fieldMarker)) { throw 'Realtime server activation field marker missing.' }
    $server = $server.Replace($fieldMarker, $fieldReplacement)

    $eventOld = @'
                if (evt == "wake") await BeginSessionAsync();
                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));
'@
    $eventNew = @'
                if (evt == "wake")
                {
                    if (CurrentState() == "paused") await ResumeListeningAsync("wake_resume");
                    else await BeginSessionAsync();
                }
                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));
'@
    if (-not $server.Contains($eventOld)) { throw 'Realtime server wake event marker missing.' }
    $server = $server.Replace($eventOld, $eventNew)

    $startOld = @'
                await bridge.StartAsync(TrayController.RealtimeWorkingDirectory, token);
                if (token.IsCancellationRequested || !IsCurrentSession(id)) return;
                lock (sync) wakeSuppressedUntil = 0;
                await SetStateAsync("listening", "realtime_ready");
'@
    $startNew = @'
                await bridge.StartAsync(TrayController.RealtimeWorkingDirectory, token);
                if (token.IsCancellationRequested || !IsCurrentSession(id)) return;
                await secondaryMirror.StartAsync(id, token);
                lock (sync) wakeSuppressedUntil = 0;
                await SetStateAsync("listening", "realtime_ready");
                ScheduleListenTimeout(id);
'@
    if (-not $server.Contains($startOld)) { throw 'Realtime session start marker missing.' }
    $server = $server.Replace($startOld, $startNew)

    $speechMarker = '    public async Task<RealtimeSpeechRequestResult> SpeakExternalAsync('
    $speechIndex = $server.IndexOf($speechMarker, [StringComparison]::Ordinal)
    if ($speechIndex -lt 0) { throw 'SpeakExternalAsync marker missing after HA transform.' }
    $lifecycle = @'
    async Task PauseListeningAsync(string reason)
    {
        var id = CurrentSessionId();
        if (CurrentState() != "listening" || string.IsNullOrEmpty(id)) return;
        CancelListenTimer();
        await SetStateAsync("paused", reason);
        try { await bridge.SetInputPausedAsync(true); }
        catch (Exception ex) { Console.WriteLine("Realtime input pause warning: " + ex.Message); }
        ScheduleConversationIdleTimeout(id);
        Console.WriteLine($"Session {id}: PAUSED · microphone closed · reason={reason}");
    }

    async Task ResumeListeningAsync(string reason)
    {
        var id = CurrentSessionId();
        if (CurrentState() != "paused" || string.IsNullOrEmpty(id)) return;
        CancelConversationIdleTimer();
        try { await bridge.SetInputPausedAsync(false); }
        catch (Exception ex) { Console.WriteLine("Realtime input resume warning: " + ex.Message); }
        await SetStateAsync("listening", reason);
        ScheduleListenTimeout(id);
        Console.WriteLine($"Session {id}: LISTENING resumed · reason={reason}");
    }

    void NoteRealtimeActivity(string role, bool done)
    {
        var id = CurrentSessionId();
        if (string.IsNullOrEmpty(id)) return;
        var current = CurrentState();
        if (current == "listening") ScheduleListenTimeout(id);
        else if (current == "paused") ScheduleConversationIdleTimeout(id);
    }

    void ScheduleListenTimeout(string id)
    {
        CancelListenTimer();
        var seconds = RealtimeMirrorSettings.ListenSilenceTimeoutSeconds;
        if (seconds <= 0 || !IsCurrentSession(id) || CurrentState() != "listening") return;
        var local = new CancellationTokenSource();
        listenSilenceCts = local;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), local.Token);
                if (!local.IsCancellationRequested && IsCurrentSession(id) && CurrentState() == "listening")
                    await PauseListeningAsync("listen_silence_timeout");
            }
            catch (OperationCanceledException) { }
        });
    }

    void ScheduleConversationIdleTimeout(string id)
    {
        CancelConversationIdleTimer();
        var seconds = RealtimeMirrorSettings.ConversationIdleTimeoutSeconds;
        if (seconds <= 0 || !IsCurrentSession(id) || CurrentState() != "paused") return;
        var local = new CancellationTokenSource();
        conversationIdleCts = local;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), local.Token);
                if (!local.IsCancellationRequested && IsCurrentSession(id) && CurrentState() == "paused")
                    await EndSessionAsync("conversation_idle_timeout");
            }
            catch (OperationCanceledException) { }
        });
    }

    void CancelListenTimer()
    {
        var old = Interlocked.Exchange(ref listenSilenceCts, null);
        if (old is null) return;
        try { old.Cancel(); } catch { }
        old.Dispose();
    }

    void CancelConversationIdleTimer()
    {
        var old = Interlocked.Exchange(ref conversationIdleCts, null);
        if (old is null) return;
        try { old.Cancel(); } catch { }
        old.Dispose();
    }

    void CancelSessionTimers()
    {
        CancelListenTimer();
        CancelConversationIdleTimer();
    }

'@
    $server = $server.Substring(0, $speechIndex) + $lifecycle + $server.Substring($speechIndex)

    $busyOld = @'
        if (CurrentState() != "listening")
            return new RealtimeSpeechRequestResult(false, "busy", CurrentSessionId());
'@
    $busyNew = @'
        if (CurrentState() != "listening" && CurrentState() != "paused")
            return new RealtimeSpeechRequestResult(false, "busy", CurrentSessionId());
'@
    if (-not $server.Contains($busyOld)) { throw 'External speech state marker missing.' }
    $server = $server.Replace($busyOld, $busyNew)
    $server = $server.Replace('if (IsCurrentSession(targetSession) && CurrentState() == "listening")', 'if (IsCurrentSession(targetSession) && (CurrentState() == "listening" || CurrentState() == "paused"))')

    $endOld = @'
        cancellation?.Cancel();
        cancellation?.Dispose();
        await SendStateToCurrentAsync();
        try { await bridge.StopAsync(); }
'@
    $endNew = @'
        CancelSessionTimers();
        cancellation?.Cancel();
        cancellation?.Dispose();
        await SendStateToCurrentAsync();
        try { await secondaryMirror.StopAsync(); } catch (Exception ex) { Console.WriteLine("Realtime mirror stop warning: " + ex.Message); }
        try { await bridge.StopAsync(); }
'@
    if (-not $server.Contains($endOld)) { throw 'Realtime EndSession marker missing.' }
    $server = $server.Replace($endOld, $endNew)

    $audioOld = @'
        if (CurrentState() != "listening" || pcm.Length == 0) return;
        var androidPcm = CodexRealtimeBridge.ToAndroid16k(pcm, sourceRate);
        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is null || socket.State != WebSocketState.Open) return;
        await SendBinaryAsync(socket, androidPcm);
'@
    $audioNew = @'
        var current = CurrentState();
        if ((current != "listening" && current != "paused") || pcm.Length == 0) return;
        var androidPcm = CodexRealtimeBridge.ToAndroid16k(pcm, sourceRate);
        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is null || socket.State != WebSocketState.Open) return;
        await SendBinaryAsync(socket, androidPcm); // Android is always the primary sink.
        secondaryMirror.PushPcm16k(androidPcm);   // Mirrors are non-blocking best effort.
'@
    if (-not $server.Contains($audioOld)) { throw 'Realtime OnRealtimeAudio marker missing.' }
    $server = $server.Replace($audioOld, $audioNew)

    $transcriptOld = @'
            sessionId = CurrentSessionId()
        });
    }

    async Task SetStateAsync
'@
    $transcriptNew = @'
            sessionId = CurrentSessionId()
        });
        NoteRealtimeActivity(role, done);
    }

    async Task SetStateAsync
'@
    if (-not $server.Contains($transcriptOld)) { throw 'Realtime transcript marker missing.' }
    $server = $server.Replace($transcriptOld, $transcriptNew)

    $disposeOld = @'
        bridge.Dispose();
        sendGate.Dispose();
'@
    $disposeNew = @'
        CancelSessionTimers();
        try { secondaryMirror.Dispose(); } catch { }
        bridge.Dispose();
        sendGate.Dispose();
'@
    if (-not $server.Contains($disposeOld)) { throw 'Realtime Dispose marker missing.' }
    $server = $server.Replace($disposeOld, $disposeNew)

    Set-Content -LiteralPath $serverPath -Value $server -Encoding utf8 -NoNewline
}

# Settings UI: lifecycle and secondary mirror dialogs.
$settings = Get-Content -LiteralPath $settingsPath -Raw
if (-not $settings.Contains('SecondaryOutputSettingsForm.ShowSettings')) {
    $audioOld = @'
        var downlink = new Button { Text = "Elegir dispositivo de audio de respuesta…", Width = 360, Height = 34 };
        downlink.SetBounds(26, y, 360, 34);
        downlink.Click += (_, _) => DownlinkDeviceSettings.ShowDialog();
        tab.Controls.Add(downlink); y += 58;
'@
    $audioNew = @'
        AddInfo(tab, "Android es siempre la salida principal de Realtime. Las salidas de Windows/Bluetooth y Home Assistant son mirrors opcionales en paralelo.", 26, y, 690, 44); y += 50;
        var downlink = new Button { Text = "Configurar salidas secundarias / mirrors…", Width = 360, Height = 34 };
        downlink.SetBounds(26, y, 360, 34);
        downlink.Click += (_, _) => SecondaryOutputSettingsForm.ShowSettings();
        tab.Controls.Add(downlink); y += 58;
'@
    if (-not $settings.Contains($audioOld)) { throw 'Settings audio button marker missing.' }
    $settings = $settings.Replace($audioOld, $audioNew)

    $realtimeInfo = '        AddInfo(tab, "La voz se aplica al crear la próxima sesión. ''sol'' usa la voz nativa Sol de Realtime; no interviene ningún TTS externo.", 26, y, 690);'
    $realtimeNew = @'
        var lifecycle = new Button { Text = "Configurar fin de escucha y fin de conversación…", Width = 380, Height = 34 };
        lifecycle.SetBounds(26, y, 380, 34);
        lifecycle.Click += (_, _) => RealtimeLifecycleSettingsForm.ShowSettings();
        tab.Controls.Add(lifecycle); y += 48;
        AddInfo(tab, "Fin de escucha cierra sólo el micrófono y conserva el contexto; la wake word reanuda la misma sesión. Fin de conversación recién cierra Realtime.", 26, y, 690, 44); y += 48;
        AddInfo(tab, "La voz se aplica al crear la próxima sesión. 'sol' usa la voz nativa Sol de Realtime; no interviene ningún TTS externo.", 26, y, 690);
'@
    if (-not $settings.Contains($realtimeInfo)) { throw 'Settings realtime info marker missing.' }
    $settings = $settings.Replace($realtimeInfo, $realtimeNew)
    Set-Content -LiteralPath $settingsPath -Value $settings -Encoding utf8 -NoNewline
}

$peerCheck = Get-Content -LiteralPath $peerPath -Raw
$bridgeCheck = Get-Content -LiteralPath $bridgePath -Raw
$serverCheck = Get-Content -LiteralPath $serverPath -Raw
$settingsCheck = Get-Content -LiteralPath $settingsPath -Raw
if ($peerCheck -notmatch 'input_audio\.pause') { throw 'input_audio.pause missing.' }
if ($peerCheck -notmatch 'SetInputPausedAsync') { throw 'WebRTC pause/resume method missing.' }
if ($bridgeCheck -notmatch 'SetInputPausedAsync') { throw 'Realtime bridge pause/resume method missing.' }
if ($serverCheck -notmatch '"paused"') { throw 'PAUSED server state missing.' }
if ($serverCheck -notmatch 'RealtimeSecondaryAudioMirror') { throw 'Secondary Realtime mirror missing.' }
if ($serverCheck -notmatch 'secondaryMirror\.PushPcm16k') { throw 'Realtime PCM mirror fanout missing.' }
if ($settingsCheck -notmatch 'SecondaryOutputSettingsForm') { throw 'Secondary output settings button missing.' }
if ($settingsCheck -notmatch 'RealtimeLifecycleSettingsForm') { throw 'Lifecycle timeout settings button missing.' }
Write-Host 'Prepared PAUSED lifecycle + independent timeouts + Android-primary secondary audio mirrors.'
