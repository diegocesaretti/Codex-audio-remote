$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$server = Get-Content $serverPath -Raw

function Require-Contains([string]$text, [string]$needle, [string]$label) {
    if (-not $text.Contains($needle)) { throw "Audio quality/stability transform: missing expected $label" }
}

if ($server.Contains('ScheduleTransportLossGrace')) {
    Write-Host 'SOL audio quality/session stability already layered.'
    exit 0
}

$fieldNeedle = '    CancellationTokenSource? endPhraseGraceCts;'
Require-Contains $server $fieldNeedle 'voice lifecycle timer fields'
$server = $server.Replace($fieldNeedle, $fieldNeedle + "`r`n    CancellationTokenSource? transportLossCts;")

$connectNeedle = @'
        lock (sync)
        {
            old = client;
            client = socket;
            generation = ++clientGeneration;
        }

        Console.WriteLine($"Realtime Android client connected · generation={generation} · {context.Request.RemoteEndPoint}");
'@
Require-Contains $server $connectNeedle 'client connection block'
$connectReplacement = @'
        lock (sync)
        {
            old = client;
            client = socket;
            generation = ++clientGeneration;
        }

        CancelTransportLossTimer();
        Console.WriteLine($"Realtime Android client connected · generation={generation} · {context.Request.RemoteEndPoint}");
'@
$server = $server.Replace($connectNeedle, $connectReplacement.TrimEnd())

$disconnectNeedle = @'
            Console.WriteLine($"Realtime current client disconnected · generation={generation} · state={CurrentState()}");
            if (CurrentState() != "idle") await EndSessionAsync("transport_lost");
'@
Require-Contains $server $disconnectNeedle 'immediate transport-lost shutdown'
$disconnectReplacement = @'
            Console.WriteLine($"Realtime current client disconnected · generation={generation} · state={CurrentState()}");
            if (CurrentState() != "idle") ScheduleTransportLossGrace(generation);
'@
$server = $server.Replace($disconnectNeedle, $disconnectReplacement.TrimEnd())

$audioNeedle = @'
        var current = CurrentState();
        if ((current != "listening" && current != "paused") || pcm.Length == 0) return;
        var androidPcm = CodexRealtimeBridge.ToAndroid16k(pcm, sourceRate);
'@
Require-Contains $server $audioNeedle '16 kHz downlink conversion'
$audioReplacement = @'
        var current = CurrentState();
        if ((current != "listening" && current != "paused") || pcm.Length == 0) return;
        var androidPcm = SolAudioQualitySettings.ProcessDownlink(pcm, sourceRate);
'@
$server = $server.Replace($audioNeedle, $audioReplacement.TrimEnd())

$statusNeedle = '                    outputSampleRate = 16000,'
Require-Contains $server $statusNeedle 'Realtime status output sample rate'
$server = $server.Replace($statusNeedle, '                    outputSampleRate = SolAudioQualitySettings.OutputSampleRate,')

$stateNeedle = '            endPhraseGraceSeconds = SolVoiceSessionSettings.EndPhraseGraceSeconds'
Require-Contains $server $stateNeedle 'state lifecycle payload'
$stateReplacement = @'
            endPhraseGraceSeconds = SolVoiceSessionSettings.EndPhraseGraceSeconds,
            outputSampleRate = SolAudioQualitySettings.OutputSampleRate,
            downlinkGainPercent = SolAudioQualitySettings.GainPercent,
            downlinkQuality = SolAudioQualitySettings.Quality,
            transportReconnectGraceSeconds = SolAudioQualitySettings.ReconnectGraceSeconds
'@
$server = $server.Replace($stateNeedle, $stateReplacement.TrimEnd())

$endNeedle = @'
        CancelSessionTimers();
        cancellation?.Cancel();
'@
Require-Contains $server $endNeedle 'session shutdown timer cleanup'
$endReplacement = @'
        CancelSessionTimers();
        CancelTransportLossTimer();
        cancellation?.Cancel();
'@
$server = $server.Replace($endNeedle, $endReplacement.TrimEnd())

$methodMarker = '    public async Task EndSessionAsync(string reason)'
$methodIndex = $server.IndexOf($methodMarker, [StringComparison]::Ordinal)
if ($methodIndex -lt 0) { throw 'Audio quality/stability transform: EndSessionAsync marker missing.' }
$methods = @'
    void ScheduleTransportLossGrace(long generation)
    {
        var seconds = SolAudioQualitySettings.ReconnectGraceSeconds;
        if (seconds <= 0)
        {
            _ = EndSessionAsync("transport_lost_timeout");
            return;
        }

        var local = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref transportLossCts, local);
        if (old is not null) { try { old.Cancel(); } catch { } old.Dispose(); }
        Console.WriteLine($"Realtime transport lost · holding session for {seconds}s · generation={generation}");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), local.Token);
                if (local.IsCancellationRequested) return;
                WebSocket? currentClient;
                lock (sync) currentClient = client;
                if (currentClient is not null && currentClient.State == WebSocketState.Open) return;
                var currentState = CurrentState();
                if (currentState != "idle" && currentState != "ending")
                    await EndSessionAsync("transport_lost_timeout");
            }
            catch (OperationCanceledException) { }
        });
    }

    void CancelTransportLossTimer()
    {
        var old = Interlocked.Exchange(ref transportLossCts, null);
        if (old is null) return;
        try { old.Cancel(); } catch { }
        old.Dispose();
    }

'@
$server = $server.Substring(0, $methodIndex) + $methods + $server.Substring($methodIndex)

$disposeNeedle = @'
        CancelSessionTimers();
        bridge.Dispose();
'@
Require-Contains $server $disposeNeedle 'dispose timer cleanup'
$disposeReplacement = @'
        CancelSessionTimers();
        CancelTransportLossTimer();
        bridge.Dispose();
'@
$server = $server.Replace($disposeNeedle, $disposeReplacement.TrimEnd())

Set-Content $serverPath $server -Encoding UTF8

$check = Get-Content $serverPath -Raw
foreach ($needle in @('SolAudioQualitySettings.ProcessDownlink', 'SolAudioQualitySettings.OutputSampleRate', 'ScheduleTransportLossGrace', 'transport_lost_timeout', 'CancelTransportLossTimer')) {
    if (-not $check.Contains($needle)) { throw "Audio quality/stability transform verification failed: $needle" }
}
if ($check.Contains('CodexRealtimeBridge.ToAndroid16k(pcm, sourceRate)')) { throw 'Legacy forced 16 kHz downlink survived.' }
Write-Host 'Prepared configurable 16/24/48 kHz downlink + gain + transport reconnect grace.'
