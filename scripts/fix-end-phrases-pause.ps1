$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\RealtimeSessionServer.cs'
$source = Get-Content -LiteralPath $path -Raw

if ($source -notmatch 'evt == "pause"') {
    $old = '                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));'
    $new = @'
                else if (evt == "pause") await PauseListeningAsync(ReadString(root, "reason", "client_pause"));
                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));
'@
    if (-not $source.Contains($old)) { throw 'Realtime event end marker missing after lifecycle transform.' }
    $source = $source.Replace($old, $new.TrimEnd("`r","`n"))
}

if ($source -notmatch 'evt == "pause"') { throw 'Realtime pause event handler missing.' }
if ($source -notmatch 'PauseListeningAsync\(ReadString\(root, "reason"') { throw 'Realtime pause event is not routed to PauseListeningAsync.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'End-phrase protocol ready: Android pause event -> PAUSED lifecycle, conversation remains alive.'
