$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$server = Get-Content $serverPath -Raw

function Require-Contains([string]$text, [string]$needle, [string]$label) {
    if (-not $text.Contains($needle)) { throw "Voice lifecycle transform: missing expected $label" }
}

if ($server.Contains('ScheduleMaxListenTimeout')) {
    Write-Host 'SOL voice session lifecycle already layered.'
    exit 0
}

$fieldNeedle = '    readonly SemaphoreSlim activationGate = new(1, 1);'
Require-Contains $server $fieldNeedle 'activation gate field'
$fieldReplacement = @'
    readonly SemaphoreSlim activationGate = new(1, 1);
    readonly SemaphoreSlim lifecycleGate = new(1, 1);
    CancellationTokenSource? maxListenCts;
    CancellationTokenSource? silenceCts;
    CancellationTokenSource? workCts;
'@
$server = $server.Replace($fieldNeedle, $fieldReplacement.TrimEnd())

$eventNeedle = @'
                if (evt == "wake") await BeginSessionAsync();
                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));
                return;
'@
Require-Contains $server $eventNeedle 'event control block'
$eventReplacement = @'
                if (evt == "wake")
                {
                    var current = CurrentState();
                    if (current == "paused" || current == "listening")
                        await ResumeListeningAsync(current == "paused" ? "wake_resume" : "wake_reset");
                    else
                        await BeginSessionAsync();
                }
                else if (evt == "pause")
                {
                    await PauseListeningAsync(ReadString(root, "reason", "client_pause"));
                }
                else if (evt == "end")
                {
                    var reason = ReadString(root, "reason", "client");
                    if (string.Equals(reason, "phrase", StringComparison.OrdinalIgnoreCase))
                        await PauseListeningAsync("phrase");
                    else
                        await EndSessionAsync(reason);
                }
                return;
'@
$server = $server.Replace($eventNeedle, $eventReplacement.TrimEnd())

$legacyWakeNeedle = @'
            case "wake":
                await BeginSessionAsync();
                return;
'@
Require-Contains $server $legacyWakeNeedle 'legacy wake block'
$legacyWakeReplacement = @'
            case "wake":
            {
                var current = CurrentState();
                if (current == "paused" || current == "listening")
                    await ResumeListeningAsync(current == "paused" ? "wake_resume" : "wake_reset");
                else
                    await BeginSessionAsync();
                return;
            }
'@
$server = $server.Replace($legacyWakeNeedle, $legacyWakeReplacement.TrimEnd())

$startNeedle = '                await SetStateAsync("listening", "realtime_ready");'
Require-Contains $server $startNeedle 'LISTENING transition'
$server = $server.Replace(
    $startNeedle,
    $startNeedle + "`r`n                ArmListeningTimers(id);`r`n                Console.WriteLine(`$`"Session {id}: lifecycle · {SolVoiceSessionSettings.Summary()}`");"
)

$endMarker = '    public async Task EndSessionAsync(string reason)'
$endIndex = $server.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($endIndex -lt 0) { throw 'Voice lifecycle transform: EndSessionAsync marker missing.' }
$lifecycleMethods = @'
    async Task PauseListeningAsync(string reason)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            string id;
            lock (sync)
            {
                if (state != "listening" || string.IsNullOrEmpty(sessionId)) return;
                id = sessionId;
            }
            CancelListeningTimers();
            await SetStateAsync("paused", reason);
            ScheduleWorkTimeout(id);
            Console.WriteLine($"Session {id}: PAUSED · audio input closed · Codex keeps working · reason={reason}");
        }
        finally { lifecycleGate.Release(); }
    }

    async Task ResumeListeningAsync(string reason)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            string id;
            string current;
            lock (sync)
            {
                current = state;
                id = sessionId;
                if ((current != "paused" && current != "listening") || string.IsNullOrEmpty(id)) return;
            }

            CancelSessionTimers();
            await SetStateAsync("listening", reason);
            ArmListeningTimers(id);
            Console.WriteLine($"Session {id}: LISTENING · lifecycle reset · reason={reason}");
        }
        finally { lifecycleGate.Release(); }
    }

    void NoteRealtimeActivity(string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var id = CurrentSessionId();
        if (string.IsNullOrEmpty(id) || CurrentState() != "listening") return;
        ScheduleSilenceTimeout(id);
    }

    void ArmListeningTimers(string id)
    {
        CancelListeningTimers();
        ScheduleMaxListenTimeout(id);
        ScheduleSilenceTimeout(id);
    }

    void ScheduleMaxListenTimeout(string id)
    {
        var seconds = SolVoiceSessionSettings.ListenTimeoutSeconds;
        if (seconds <= 0 || !IsCurrentSession(id) || CurrentState() != "listening") return;
        var local = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref maxListenCts, local);
        if (old is not null) { try { old.Cancel(); } catch { } old.Dispose(); }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), local.Token);
                if (!local.IsCancellationRequested && IsCurrentSession(id) && CurrentState() == "listening")
                    await PauseListeningAsync("listen_timeout");
            }
            catch (OperationCanceledException) { }
        });
    }

    void ScheduleSilenceTimeout(string id)
    {
        var seconds = SolVoiceSessionSettings.SilenceTimeoutSeconds;
        var local = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref silenceCts, local);
        if (old is not null) { try { old.Cancel(); } catch { } old.Dispose(); }
        if (seconds <= 0 || !IsCurrentSession(id) || CurrentState() != "listening")
        {
            local.Dispose();
            Interlocked.CompareExchange(ref silenceCts, null, local);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), local.Token);
                if (!local.IsCancellationRequested && IsCurrentSession(id) && CurrentState() == "listening")
                    await PauseListeningAsync("silence_timeout");
            }
            catch (OperationCanceledException) { }
        });
    }

    void ScheduleWorkTimeout(string id)
    {
        var seconds = SolVoiceSessionSettings.WorkTimeoutSeconds;
        var local = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref workCts, local);
        if (old is not null) { try { old.Cancel(); } catch { } old.Dispose(); }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), local.Token);
                if (!local.IsCancellationRequested && IsCurrentSession(id) && CurrentState() == "paused")
                    await EndSessionAsync("work_timeout");
            }
            catch (OperationCanceledException) { }
        });
    }

    void CancelListeningTimers()
    {
        CancelTimer(ref maxListenCts);
        CancelTimer(ref silenceCts);
    }

    void CancelSessionTimers()
    {
        CancelListeningTimers();
        CancelTimer(ref workCts);
    }

    static void CancelTimer(ref CancellationTokenSource? source)
    {
        var old = Interlocked.Exchange(ref source, null);
        if (old is null) return;
        try { old.Cancel(); } catch { }
        old.Dispose();
    }

'@
$server = $server.Substring(0, $endIndex) + $lifecycleMethods + $server.Substring($endIndex)

$endNeedle = @'
        cancellation?.Cancel();
        cancellation?.Dispose();
        await SendStateToCurrentAsync();
'@
Require-Contains $server $endNeedle 'EndSession cancellation block'
$endReplacement = @'
        CancelSessionTimers();
        cancellation?.Cancel();
        cancellation?.Dispose();
        await SendStateToCurrentAsync();
'@
$server = $server.Replace($endNeedle, $endReplacement.TrimEnd())

$audioNeedle = '        if (CurrentState() != "listening" || pcm.Length == 0) return;'
Require-Contains $server $audioNeedle 'Realtime downlink guard'
$audioReplacement = @'
        var current = CurrentState();
        if ((current != "listening" && current != "paused") || pcm.Length == 0) return;
'@
$server = $server.Replace($audioNeedle, $audioReplacement.TrimEnd())

$transcriptNeedle = @'
            sessionId = CurrentSessionId()
        });
    }

    async Task SetStateAsync
'@
Require-Contains $server $transcriptNeedle 'Realtime transcript callback tail'
$transcriptReplacement = @'
            sessionId = CurrentSessionId()
        });

        NoteRealtimeActivity(role, text);
        if (done && role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            var matched = SolVoiceSessionSettings.MatchEndPhrase(text);
            if (matched is not null && CurrentState() == "listening")
            {
                Console.WriteLine($"Session {CurrentSessionId()}: end phrase matched · {matched}");
                await PauseListeningAsync("end_phrase:" + matched);
            }
        }
    }

    async Task SetStateAsync
'@
$server = $server.Replace($transcriptNeedle, $transcriptReplacement.TrimEnd())

$statePayloadNeedle = @'
            reason = snapshotReason,
            voiceBackend = "realtime-webrtc"
'@
Require-Contains $server $statePayloadNeedle 'state payload'
$statePayloadReplacement = @'
            reason = snapshotReason,
            voiceBackend = "realtime-webrtc",
            listenTimeoutSeconds = SolVoiceSessionSettings.ListenTimeoutSeconds,
            silenceTimeoutSeconds = SolVoiceSessionSettings.SilenceTimeoutSeconds,
            workTimeoutSeconds = SolVoiceSessionSettings.WorkTimeoutSeconds
'@
$server = $server.Replace($statePayloadNeedle, $statePayloadReplacement.TrimEnd())

$disposeNeedle = @'
        bridge.Dispose();
        sendGate.Dispose();
        activationGate.Dispose();
'@
Require-Contains $server $disposeNeedle 'Dispose block'
$disposeReplacement = @'
        CancelSessionTimers();
        bridge.Dispose();
        sendGate.Dispose();
        activationGate.Dispose();
        lifecycleGate.Dispose();
'@
$server = $server.Replace($disposeNeedle, $disposeReplacement.TrimEnd())

Set-Content $serverPath $server -Encoding UTF8

$check = Get-Content $serverPath -Raw
foreach ($needle in @('"paused"', 'ScheduleMaxListenTimeout', 'ScheduleSilenceTimeout', 'ScheduleWorkTimeout', 'SolVoiceSessionSettings.MatchEndPhrase', 'wake_reset', 'wake_resume')) {
    if (-not $check.Contains($needle)) { throw "Voice lifecycle transform verification failed: $needle" }
}
Write-Host 'Prepared transcript-driven LISTENING -> PAUSED -> ENDING lifecycle with wake reset.'
