$ErrorActionPreference = 'Stop'

$bridgePath = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $bridgePath -Raw

$voiceConst = '    const string RealtimeVoice = "sol";'
$voiceProperty = '    static string RealtimeVoice => AppSettings.RealtimeVoice;'
if (-not $source.Contains($voiceConst)) { throw 'Expected RealtimeVoice constant from official-flow transform was not found.' }
$source = $source.Replace($voiceConst, $voiceProperty)

$envResolver = '        var explicitPath = Environment.GetEnvironmentVariable("CODEX_EXE");'
$settingsResolver = '        var explicitPath = AppSettings.CodexExecutableOverride;'
if (-not $source.Contains($envResolver)) { throw 'Expected CODEX_EXE resolver marker was not found.' }
$source = $source.Replace($envResolver, $settingsResolver)

Set-Content -LiteralPath $bridgePath -Value $source -Encoding utf8 -NoNewline

$check = Get-Content -LiteralPath $bridgePath -Raw
if ($check -notmatch 'RealtimeVoice => AppSettings\.RealtimeVoice') { throw 'Settings-backed Realtime voice missing.' }
if ($check -notmatch 'explicitPath = AppSettings\.CodexExecutableOverride') { throw 'Settings-backed Codex executable override missing.' }
Write-Host 'Applied persisted runtime settings to official Realtime bridge.'
