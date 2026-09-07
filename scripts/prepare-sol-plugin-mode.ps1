$ErrorActionPreference = 'Stop'

$programPath = 'windows/CodexAudioRemote.Server/Program.cs'
$trayPath = 'windows/CodexAudioRemote.Server/TrayController.cs'
$appSettingsPath = 'windows/CodexAudioRemote.Server/AppSettings.cs'
$downlinkPath = 'windows/CodexAudioRemote.Server/DownlinkDeviceSettings.cs'

$program = Get-Content $programPath -Raw
$tray = Get-Content $trayPath -Raw
$appSettings = Get-Content $appSettingsPath -Raw
$downlink = Get-Content $downlinkPath -Raw

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

Set-Content $programPath $program -Encoding UTF8
Set-Content $trayPath $tray -Encoding UTF8
Set-Content $appSettingsPath $appSettings -Encoding UTF8
Set-Content $downlinkPath $downlink -Encoding UTF8

Write-Host 'SOL plugin mode layered after the known-good V3 transforms; settings remain standalone-compatible fallbacks.'
