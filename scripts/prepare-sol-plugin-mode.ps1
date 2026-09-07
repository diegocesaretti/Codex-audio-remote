$ErrorActionPreference = 'Stop'

$programPath = 'windows/CodexAudioRemote.Server/Program.cs'
$trayPath = 'windows/CodexAudioRemote.Server/TrayController.cs'
$appSettingsPath = 'windows/CodexAudioRemote.Server/AppSettings.cs'
$downlinkPath = 'windows/CodexAudioRemote.Server/DownlinkDeviceSettings.cs'
$realtimeServerPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'

$program = Get-Content $programPath -Raw
$tray = Get-Content $trayPath -Raw
$appSettings = Get-Content $appSettingsPath -Raw
$downlink = Get-Content $downlinkPath -Raw
$realtimeServer = Get-Content $realtimeServerPath -Raw

function Require-Contains([string]$text, [string]$needle, [string]$label) {
    if (-not $text.Contains($needle)) { throw "SOL plugin transform: missing expected $label" }
}

# A SOL-hosted Codex Audio instance always uses the known-good Realtime V3 path,
# without changing the user's standalone registry setting.
$backendNeedle = 'if (TrayController.VoiceBackend == TrayController.RealtimeV3Backend)'
Require-Contains $program $backendNeedle 'Realtime backend selector'
$program = $program.Replace(
    $backendNeedle,
    'if (SolPluginHost.Enabled || TrayController.VoiceBackend == TrayController.RealtimeV3Backend)'
)

# Announce health to SOL only after the Realtime server object was constructed.
$serverNeedle = '    using var realtimeServer = new RealtimeSessionServer(options);'
Require-Contains $program $serverNeedle 'RealtimeSessionServer construction'
$program = $program.Replace(
    $serverNeedle,
    $serverNeedle + "`r`n    SolPluginHost.Ready(`"realtime-v3`");"
)

# Keep classic mode observable too if this source is ever reused independently.
$classicNeedle = 'await server.RunAsync();'
Require-Contains $program $classicNeedle 'classic server run'
$program = $program.Replace(
    $classicNeedle,
    'SolPluginHost.Ready("classic");' + "`r`n" + $classicNeedle
)

# The standalone keeps its tray exactly as-is. SOL plugin mode suppresses only the
# plugin process' duplicate tray/icon and settings UI.
$trayNeedle = '        HideConsoleWindow();'
Require-Contains $tray $trayNeedle 'tray initialization'
$tray = $tray.Replace(
    $trayNeedle,
    $trayNeedle + "`r`n        if (SolPluginHost.Enabled)`r`n        {`r`n            SolPluginHost.Log(`"info`", `"SOL plugin mode active · standalone tray suppressed`");`r`n            return;`r`n        }"
)

# SOL settings are overlays only. When SOL has no value, the existing standalone
# Registry/settings.json value remains authoritative.
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

$haUrlNeedle = '        get => NormalizeBaseUrl(ReadString("HomeAssistantUrl")) ?? DefaultHomeAssistantUrl;'
Require-Contains $appSettings $haUrlNeedle 'HomeAssistant URL getter'
$appSettings = $appSettings.Replace(
    $haUrlNeedle,
    '        get => NormalizeBaseUrl((SolPluginHost.Enabled ? SolPluginHost.Setting("home_assistant_url") : null) ?? ReadString("HomeAssistantUrl")) ?? DefaultHomeAssistantUrl;'
)

$haEnabledNeedle = '        get => ReadBool("HomeAssistantEnabled", true);'
Require-Contains $appSettings $haEnabledNeedle 'HomeAssistant enabled getter'
$appSettings = $appSettings.Replace(
    $haEnabledNeedle,
    '        get => (SolPluginHost.Enabled ? SolPluginHost.BoolSetting("home_assistant_enabled") : null) ?? ReadBool("HomeAssistantEnabled", true);'
)

$haPortNeedle = '        get => ReadInt("HomeAssistantApiPort", 8766, 1024, 65535);'
Require-Contains $appSettings $haPortNeedle 'HomeAssistant API port getter'
$appSettings = $appSettings.Replace(
    $haPortNeedle,
    '        get => Math.Clamp((SolPluginHost.Enabled ? SolPluginHost.IntSetting("home_assistant_api_port") : null) ?? ReadInt("HomeAssistantApiPort", 8766, 1024, 65535), 1024, 65535);'
)

$deviceIdNeedle = '    public static string? SelectedDeviceId => Load().DownlinkDeviceId;'
Require-Contains $downlink $deviceIdNeedle 'downlink device id getter'
$downlink = $downlink.Replace(
    $deviceIdNeedle,
    '    public static string? SelectedDeviceId => SolPluginAudioSettings.SelectedDeviceId ?? Load().DownlinkDeviceId;'
)

$deviceNameNeedle = '    public static string? SelectedDeviceName => Load().DownlinkDeviceName;'
Require-Contains $downlink $deviceNameNeedle 'downlink device name getter'
$downlink = $downlink.Replace(
    $deviceNameNeedle,
    '    public static string? SelectedDeviceName => SolPluginAudioSettings.SelectedDeviceName ?? Load().DownlinkDeviceName;'
)

$btcomNeedle = '    public static string? BtcomPath => Load().BtcomPath;'
Require-Contains $downlink $btcomNeedle 'btcom path getter'
$downlink = $downlink.Replace(
    $btcomNeedle,
    '    public static string? BtcomPath => SolPluginAudioSettings.BtcomPath ?? Load().BtcomPath;'
)

$waitNeedle = '    public static int BtcomWaitSeconds => Math.Clamp(Load().BtcomWaitSeconds, 1, 15);'
Require-Contains $downlink $waitNeedle 'btcom wait getter'
$downlink = $downlink.Replace(
    $waitNeedle,
    '    public static int BtcomWaitSeconds => SolPluginAudioSettings.BtcomWaitSeconds ?? Math.Clamp(Load().BtcomWaitSeconds, 1, 15);'
)

# Wire the generic SOL runtime bridge only after every known-good V3/HA transform has
# completed. This keeps audio/WebRTC ownership unchanged and makes SOL integration additive.
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

Set-Content $programPath $program -Encoding UTF8
Set-Content $trayPath $tray -Encoding UTF8
Set-Content $appSettingsPath $appSettings -Encoding UTF8
Set-Content $downlinkPath $downlink -Encoding UTF8
Set-Content $realtimeServerPath $realtimeServer -Encoding UTF8

Write-Host 'SOL plugin mode layered after golden V3 transforms; native MCP/input bridge and speaker identity contract enabled without changing standalone.'