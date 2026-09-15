$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$server = Get-Content $serverPath -Raw

# The lifecycle and reconnect layers intentionally use fire-and-forget timer tasks.
# Cancellation is normal, but any other exception must be observed and logged so it
# cannot silently destabilize a later session or disappear until finalizer time.
$pattern = 'catch \(OperationCanceledException\) \{ \}'
$matches = [regex]::Matches($server, $pattern)
if ($matches.Count -lt 6) {
    throw "Background-task hardening expected at least 6 cancellation handlers after lifecycle/reconnect transforms; found $($matches.Count)."
}
$replacement = @'
catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SolPluginHost.Log("warn", "SOL Realtime background task failed: " + ex);
                SolPluginHost.Health("degraded", "A Realtime background task failed; the process remains available.");
            }
'@
$server = [regex]::Replace($server, $pattern, $replacement.TrimEnd())

# Timer tasks are cancelled during Dispose but are not awaited. Disposing their
# synchronization gates immediately creates a small shutdown race where an already
# unwinding async method can Release() a disposed SemaphoreSlim. These gates live for
# the lifetime of the process, so leaving them for process teardown is safer and does
# not create a persistent resource leak.
$disposeBlock = @'
        bridge.Dispose();
        sendGate.Dispose();
        activationGate.Dispose();
        lifecycleGate.Dispose();
'@
if (-not $server.Contains($disposeBlock.TrimEnd())) {
    throw 'Background-task hardening could not find the expected synchronization-gate dispose block.'
}
$disposeReplacement = @'
        bridge.Dispose();
        // Process-lifetime synchronization gates intentionally remain undisposed.
        // Outstanding async callbacks may still be unwinding after timer cancellation.
'@
$server = $server.Replace($disposeBlock.TrimEnd(), $disposeReplacement.TrimEnd())

Set-Content $serverPath $server -Encoding UTF8

$check = Get-Content $serverPath -Raw
if ($check -notmatch 'SOL Realtime background task failed') { throw 'Background task exception telemetry missing.' }
if ($check -match 'lifecycleGate\.Dispose\(\)') { throw 'Lifecycle gate dispose race survived.' }
if ($check -match 'activationGate\.Dispose\(\)') { throw 'Activation gate dispose race survived.' }
if ($check -match 'sendGate\.Dispose\(\)') { throw 'Send gate dispose race survived.' }
Write-Host "Hardened $($matches.Count) Realtime cancellation handlers and removed shutdown gate-dispose races."
