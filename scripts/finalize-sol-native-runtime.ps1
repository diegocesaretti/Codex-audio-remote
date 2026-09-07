$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$server = Get-Content $serverPath -Raw

$ctorOld = '        solRuntime = SolRuntimeBridge.TryCreate(GetSolSnapshot, reason => EndSessionAsync(reason));'
$ctorNew = '        solRuntime = SolRuntimeBridge.TryCreate(GetSolSnapshot, reason => EndSessionAsync(reason), options.Port);'
if (-not $server.Contains($ctorOld)) { throw 'SOL native finalize: runtime constructor anchor missing.' }
$server = $server.Replace($ctorOld, $ctorNew)

$routeAnchor = '            if (!context.Request.IsWebSocketRequest)'
if (-not $server.Contains($routeAnchor)) { throw 'SOL native finalize: websocket dispatch anchor missing.' }
$routeBlock = @'
            // SOL MCP callbacks share the already-proven audio HttpListener/port. This avoids
            // a second Windows URLACL/listener while keeping the callback loopback-only.
            if (solRuntime is not null && await solRuntime.TryHandleCallbackAsync(context)) continue;

            if (!context.Request.IsWebSocketRequest)
'@
$server = $server.Replace($routeAnchor, $routeBlock.TrimEnd())

Set-Content $serverPath $server -Encoding UTF8
Write-Host 'SOL native MCP callback routing finalized on the existing Realtime listener.'
