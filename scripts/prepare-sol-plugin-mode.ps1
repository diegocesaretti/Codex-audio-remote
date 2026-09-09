$ErrorActionPreference = 'Stop'

$appSettingsPath = 'windows/CodexAudioRemote.Server/AppSettings.cs'
$realtimeServerPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'

$appSettings = Get-Content $appSettingsPath -Raw
$realtimeServer = Get-Content $realtimeServerPath -Raw

function Require-Contains([string]$text, [string]$needle, [string]$label) {
    if (-not $text.Contains($needle)) { throw "SOL plugin transform: missing expected $label" }
}

# SOL settings overlay only the Realtime values the native plugin actually uses.
$voiceNeedle = '            var value = (ReadString("RealtimeVoice") ?? "sol").Trim().ToLowerInvariant();'
Require-Contains $appSettings $voiceNeedle 'RealtimeVoice getter'
$appSettings = $appSettings.Replace(
    $voiceNeedle,
    '            var value = ((SolPluginHost.Enabled ? SolPluginHost.Setting("realtime_voice") : null) ?? ReadString("RealtimeVoice") ?? "sol").Trim().ToLowerInvariant();'
)

$cwdNeedle = '            var configured = ReadString("RealtimeWorkingDirectory");'
Require-Contains $appSettings $cwdNeedle 'RealtimeWorkingDirectory getter'
$appSettings = $appSettings.Replace(
    $cwdNeedle,
    '            var configured = (SolPluginHost.Enabled ? SolPluginHost.Setting("realtime_working_directory") : null) ?? ReadString("RealtimeWorkingDirectory");'
)

# The native plugin never enters TrayController/classic mode. Replace the two remaining
# Realtime working-directory reads so the minimal plugin project does not need TrayController.
$realtimeServer = $realtimeServer.Replace('TrayController.RealtimeWorkingDirectory', 'AppSettings.RealtimeWorkingDirectory')
if ($realtimeServer -match 'TrayController') { throw 'SOL native runtime still depends on TrayController.' }

# Wire the generic SOL runtime bridge only after every known-good V3/context transform has
# completed. This keeps WebRTC ownership unchanged and makes SOL integration additive.
$solFieldNeedle = '    readonly CodexRealtimeBridge bridge;'
Require-Contains $realtimeServer $solFieldNeedle 'Realtime bridge field'
$realtimeServer = $realtimeServer.Replace(
    $solFieldNeedle,
    $solFieldNeedle + "`r`n    readonly SolRuntimeBridge? solRuntime;`r`n    string solDeviceKey = `"unknown`";"
)

$solCtorNeedle = '        bridge = new CodexRealtimeBridge(OnRealtimeAudioAsync, OnRealtimeTranscriptAsync);'
Require-Contains $realtimeServer $solCtorNeedle 'Realtime bridge constructor'
$realtimeServer = $realtimeServer.Replace(
    $solCtorNeedle,
    $solCtorNeedle + "`r`n        solRuntime = SolRuntimeBridge.TryCreate(GetSolSnapshot, reason => EndSessionAsync(reason));"
)

$solRunNeedle = '        listener.Start();'
Require-Contains $realtimeServer $solRunNeedle 'Realtime listener start'
$realtimeServer = $realtimeServer.Replace(
    $solRunNeedle,
    $solRunNeedle + "`r`n        if (solRuntime is not null) await solRuntime.StartAsync();"
)

$solConnectNeedle = '        Console.WriteLine($"Realtime Android client connected · generation={generation} · {context.Request.RemoteEndPoint}");'
Require-Contains $realtimeServer $solConnectNeedle 'Android client connected log'
$solConnectBlock = @'
        Console.WriteLine($"Realtime Android client connected · generation={generation} · {context.Request.RemoteEndPoint}");
        solRuntime?.ObserveRemoteClient(context.Request.RemoteEndPoint);
        solDeviceKey = context.Request.RemoteEndPoint is IPEndPoint ip
            ? "remote:" + ip.Address
            : "remote:" + (context.Request.RemoteEndPoint?.ToString() ?? "unknown");
'@
$realtimeServer = $realtimeServer.Replace($solConnectNeedle, $solConnectBlock.TrimEnd())

$solHelloNeedle = @'
            case "hello":
            case "sync":
                await SendStateToCurrentAsync();
                return;
'@
Require-Contains $realtimeServer $solHelloNeedle 'hello/sync control block'
$solHelloBlock = @'
            case "hello":
            {
                solRuntime?.ObserveClientHello(root);
                var explicitKey = ReadString(root, "speakerKey", "");
                if (string.IsNullOrWhiteSpace(explicitKey)) explicitKey = ReadString(root, "deviceId", "");
                if (string.IsNullOrWhiteSpace(explicitKey)) explicitKey = ReadString(root, "clientId", "");
                if (!string.IsNullOrWhiteSpace(explicitKey)) solDeviceKey = "device:" + explicitKey.Trim();
                await SendStateToCurrentAsync();
                return;
            }
            case "sync":
                await SendStateToCurrentAsync();
                return;
'@
$realtimeServer = $realtimeServer.Replace($solHelloNeedle, $solHelloBlock)

$solSessionStartNeedle = @'
            await SendStateToCurrentAsync();
            Console.WriteLine("Session " + id + ": starting Codex Realtime WebRTC");
'@
Require-Contains $realtimeServer $solSessionStartNeedle 'session activation announcement'
$solSessionStartBlock = @'
            solRuntime?.OnSessionStarted(id);
            await SendStateToCurrentAsync();
            Console.WriteLine("Session " + id + ": starting Codex Realtime WebRTC");
'@
$realtimeServer = $realtimeServer.Replace($solSessionStartNeedle, $solSessionStartBlock)

$solSessionEndNeedle = '        Console.WriteLine("Session " + endingId + ": ended · " + reason);'
Require-Contains $realtimeServer $solSessionEndNeedle 'session end log'
$realtimeServer = $realtimeServer.Replace(
    $solSessionEndNeedle,
    '        solRuntime?.OnSessionEnded(endingId);' + "`r`n" + $solSessionEndNeedle
)

$solTranscriptNeedle = '        NoteRealtimeActivity(role, done);'
Require-Contains $realtimeServer $solTranscriptNeedle 'Realtime transcript activity hook'
$realtimeServer = $realtimeServer.Replace(
    $solTranscriptNeedle,
    $solTranscriptNeedle + "`r`n        if (done && solRuntime is not null)`r`n            _ = solRuntime.IngestTranscriptAsync(role, text, true, CurrentSessionId());"
)

$solSnapshotNeedle = '    string CurrentSessionId() { lock (sync) return sessionId; }'
Require-Contains $realtimeServer $solSnapshotNeedle 'current session getter'
$solSnapshotBlock = @'
    string CurrentSessionId() { lock (sync) return sessionId; }

    SolAudioRuntimeSnapshot GetSolSnapshot()
    {
        lock (sync)
            return new SolAudioRuntimeSnapshot(state, sessionId, revision, stateReason, clientGeneration, solDeviceKey);
    }
'@
$realtimeServer = $realtimeServer.Replace($solSnapshotNeedle, $solSnapshotBlock.TrimEnd())

$solDisposeNeedle = '        bridge.Dispose();'
Require-Contains $realtimeServer $solDisposeNeedle 'Realtime bridge dispose'
$realtimeServer = $realtimeServer.Replace(
    $solDisposeNeedle,
    '        solRuntime?.Dispose();' + "`r`n" + $solDisposeNeedle
)

Set-Content $appSettingsPath $appSettings -Encoding UTF8
Set-Content $realtimeServerPath $realtimeServer -Encoding UTF8

Write-Host 'SOL native Realtime mode layered without tray, cable, loopback, downlink-device or Bluetooth/btcom dependencies.'
