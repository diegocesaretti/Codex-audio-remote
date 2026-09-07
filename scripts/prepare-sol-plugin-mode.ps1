$ErrorActionPreference = 'Stop'

$programPath = 'windows/CodexAudioRemote.Server/Program.cs'
$trayPath = 'windows/CodexAudioRemote.Server/TrayController.cs'

$program = Get-Content $programPath -Raw
$tray = Get-Content $trayPath -Raw

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
    $serverNeedle + "`r`n    SolPluginHost.Ready(\"realtime-v3\");"
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
    $trayNeedle + "`r`n        if (SolPluginHost.Enabled)`r`n        {`r`n            SolPluginHost.Log(\"info\", \"SOL plugin mode active · standalone tray suppressed\");`r`n            return;`r`n        }"
)

Set-Content $programPath $program -Encoding UTF8
Set-Content $trayPath $tray -Encoding UTF8

Write-Host 'SOL plugin mode layered after the known-good V3 transforms.'
