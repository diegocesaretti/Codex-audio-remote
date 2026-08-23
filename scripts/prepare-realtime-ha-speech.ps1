$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$bridgePath = Join-Path $root 'CodexRealtimeBridge.cs'
$serverPath = Join-Path $root 'RealtimeSessionServer.cs'
$programPath = Join-Path $root 'Program.cs'

# 1) Add direct speakable-text injection to the already transformed Realtime bridge.
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$stopMarker = '    public async Task StopAsync(CancellationToken cancellationToken = default)'
$stopIndex = $bridge.IndexOf($stopMarker, [StringComparison]::Ordinal)
if ($stopIndex -lt 0) { throw 'Could not locate CodexRealtimeBridge.StopAsync.' }
if (-not $bridge.Contains('public async Task AppendSpeechAsync')) {
$appendSpeech = @'
    public async Task AppendSpeechAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!realtimeStarted || string.IsNullOrEmpty(threadId))
            throw new InvalidOperationException("Codex Realtime is not active.");
        if (string.IsNullOrWhiteSpace(text)) return;
        await RequestAsync("thread/realtime/appendSpeech", new
        {
            threadId,
            text = text.Trim()
        }, cancellationToken);
    }

'@
    $bridge = $bridge.Substring(0, $stopIndex) + $appendSpeech + $bridge.Substring($stopIndex)
}
Set-Content -LiteralPath $bridgePath -Value $bridge -Encoding utf8 -NoNewline

# 2) Let the authoritative Realtime server accept external speech without creating a second state owner.
$server = Get-Content -LiteralPath $serverPath -Raw
$server = $server.Replace('    const long WakeRetryCooldownMs = 3500;', '    static long WakeRetryCooldownMs => AppSettings.WakeRetryCooldownMs;')
$endMarker = '    public async Task EndSessionAsync(string reason)'
$endIndex = $server.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($endIndex -lt 0) { throw 'Could not locate RealtimeSessionServer.EndSessionAsync.' }
if (-not $server.Contains('SpeakExternalAsync(string text')) {
$speechMethod = @'
    public async Task<RealtimeSpeechRequestResult> SpeakExternalAsync(
        string text,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RealtimeSpeechRequestResult(false, "empty_text", CurrentSessionId());

        text = text.Trim();
        source = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim().ToLowerInvariant();

        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is null || socket.State != WebSocketState.Open)
            return new RealtimeSpeechRequestResult(false, "android_not_connected", CurrentSessionId());

        var startedForSpeech = false;
        if (CurrentState() == "idle")
        {
            if (!AppSettings.HomeAssistantAutoStartSpeechSession)
                return new RealtimeSpeechRequestResult(false, "no_active_session", "");

            await BeginSessionAsync();
            startedForSpeech = true;
        }

        if (CurrentState() != "listening")
            return new RealtimeSpeechRequestResult(false, "busy", CurrentSessionId());

        var targetSession = CurrentSessionId();
        try
        {
            await bridge.AppendSpeechAsync(text, cancellationToken);
            Console.WriteLine($"Realtime speech injected · source={source} · voice={AppSettings.RealtimeVoice} · chars={text.Length} · session={targetSession}");

            if (startedForSpeech && !AppSettings.HomeAssistantKeepSpeechSessionOpen)
            {
                var estimatedMs = Math.Clamp(1800 + text.Length * 55, 2500, 18000);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(estimatedMs);
                        if (IsCurrentSession(targetSession) && CurrentState() == "listening")
                            await EndSessionAsync(source + "_speech_done");
                    }
                    catch { }
                });
            }

            return new RealtimeSpeechRequestResult(true, startedForSpeech ? "session_started_and_spoken" : "spoken", targetSession);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Realtime external speech error: " + ex.Message);
            return new RealtimeSpeechRequestResult(false, "speech_failed", targetSession);
        }
    }

'@
    $server = $server.Substring(0, $endIndex) + $speechMethod + $server.Substring($endIndex)
}
Set-Content -LiteralPath $serverPath -Value $server -Encoding utf8 -NoNewline

# 3) Start the HA REST speech adapter in the Realtime backend too.
$program = Get-Content -LiteralPath $programPath -Raw
$oldBlock = @'
if (TrayController.VoiceBackend == TrayController.RealtimeV3Backend)
{
    using var realtimeServer = new RealtimeSessionServer(options);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        realtimeServer.Dispose();
        Environment.Exit(0);
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => realtimeServer.Dispose();

    Console.WriteLine("Codex Audio Remote · experimental Realtime V3 backend");
    Console.WriteLine("Auth: existing Codex ChatGPT OAuth login");
    await realtimeServer.RunAsync();
    return;
}
'@
$newBlock = @'
if (TrayController.VoiceBackend == TrayController.RealtimeV3Backend)
{
    using var realtimeServer = new RealtimeSessionServer(options);
    using var realtimeHaApi = new RealtimeHomeAssistantApiServer(realtimeServer.SpeakExternalAsync);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        realtimeHaApi.Dispose();
        realtimeServer.Dispose();
        Environment.Exit(0);
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        realtimeHaApi.Dispose();
        realtimeServer.Dispose();
    };

    Console.WriteLine("Codex Audio Remote · Realtime V3 backend");
    Console.WriteLine("Auth: existing Codex ChatGPT OAuth login");
    Console.WriteLine($"Realtime voice: {AppSettings.RealtimeVoice}");
    try { realtimeHaApi.Start(); }
    catch (Exception ex) { Console.WriteLine("Home Assistant Realtime API could not start: " + ex.Message); }
    await realtimeServer.RunAsync();
    return;
}
'@
if (-not $program.Contains($oldBlock)) { throw 'Realtime Program.cs block did not match expected source.' }
$program = $program.Replace($oldBlock, $newBlock)
Set-Content -LiteralPath $programPath -Value $program -Encoding utf8 -NoNewline

# Validation.
$bridgeCheck = Get-Content -LiteralPath $bridgePath -Raw
$serverCheck = Get-Content -LiteralPath $serverPath -Raw
$programCheck = Get-Content -LiteralPath $programPath -Raw
if ($bridgeCheck -notmatch 'thread/realtime/appendSpeech') { throw 'appendSpeech bridge route missing.' }
if ($serverCheck -notmatch 'SpeakExternalAsync') { throw 'Realtime speech coordinator missing.' }
if ($serverCheck -notmatch 'AppSettings\.WakeRetryCooldownMs') { throw 'Settings-backed wake cooldown missing.' }
if ($programCheck -notmatch 'RealtimeHomeAssistantApiServer') { throw 'Realtime HA API startup missing.' }
Write-Host 'Prepared HA -> appendSpeech -> authoritative Realtime session flow.'
