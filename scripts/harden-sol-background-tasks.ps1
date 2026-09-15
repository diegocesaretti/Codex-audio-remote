$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$server = Get-Content $serverPath -Raw

# Timer and socket tasks can still be unwinding after cancellation during process
# shutdown. Disposing these process-lifetime gates immediately creates a race where an
# async finally block can Release() a disposed SemaphoreSlim. They are reclaimed with
# the process, so deliberately leaving them undisposed is safer than racing teardown.
$disposeBlock = @'
        bridge.Dispose();
        sendGate.Dispose();
        activationGate.Dispose();
        lifecycleGate.Dispose();
'@
if (-not $server.Contains($disposeBlock.TrimEnd())) {
    throw 'Shutdown hardening could not find the expected synchronization-gate dispose block.'
}
$disposeReplacement = @'
        bridge.Dispose();
        // Process-lifetime synchronization gates intentionally remain undisposed.
        // Outstanding async callbacks may still be unwinding after timer cancellation.
'@
$server = $server.Replace($disposeBlock.TrimEnd(), $disposeReplacement.TrimEnd())

Set-Content $serverPath $server -Encoding UTF8

$check = Get-Content $serverPath -Raw
if ($check -match 'lifecycleGate\.Dispose\(\)') { throw 'Lifecycle gate dispose race survived.' }
if ($check -match 'activationGate\.Dispose\(\)') { throw 'Activation gate dispose race survived.' }
if ($check -match 'sendGate\.Dispose\(\)') { throw 'Send gate dispose race survived.' }
Write-Host 'Removed Realtime shutdown gate-dispose races; background task failures remain observable through Program.cs UnobservedTaskException telemetry.'
