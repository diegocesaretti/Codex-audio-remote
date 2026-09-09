$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$runtimePath = 'windows/CodexAudioRemote.Server/SolRuntimeBridge.cs'
$cachePath = 'windows/CodexAudioRemote.Server/HomeAssistantWebSocketCache.cs'
$server = Get-Content $serverPath -Raw
$runtime = Get-Content $runtimePath -Raw
$cache = Get-Content $cachePath -Raw

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

if ($runtime -notmatch 'codex_audio_') { throw 'SOL native finalize: Audio MCP tool anchors missing.' }
$runtime = $runtime.Replace('codex_audio_', 'codex_audio_remote_')

$readerOld = 'new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, true, leaveOpen: false)'
$readerNew = 'new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, true, 4096, false)'
if (-not $runtime.Contains($readerOld)) { throw 'SOL native finalize: StreamReader anchor missing.' }
$runtime = $runtime.Replace($readerOld, $readerNew)

$cache = $cache.Replace('    Timer? persistTimer;', '    System.Threading.Timer? persistTimer;')
$cache = $cache.Replace('            persistTimer ??= new Timer(_ =>', '            persistTimer ??= new System.Threading.Timer(_ =>')

$utcTicksOld = 'Interlocked.Exchange(ref lastUpdateTicks, parsed.UtcTicks);'
$utcTicksNew = 'Interlocked.Exchange(ref lastUpdateTicks, parsed.UtcDateTime.Ticks);'
if ($cache.Contains($utcTicksOld)) { $cache = $cache.Replace($utcTicksOld, $utcTicksNew) }

$persistAgeOld = 'updatedAt = new DateTimeOffset(Math.Max(Interlocked.Read(ref lastUpdateTicks), DateTimeOffset.UtcNow.Ticks), TimeSpan.Zero),'
$persistAgeNew = 'updatedAt = new DateTimeOffset(Interlocked.Read(ref lastUpdateTicks) > 0 ? Interlocked.Read(ref lastUpdateTicks) : DateTimeOffset.UtcNow.Ticks, TimeSpan.Zero),'
if ($cache.Contains($persistAgeOld)) { $cache = $cache.Replace($persistAgeOld, $persistAgeNew) }

Set-Content $serverPath $server -Encoding UTF8
Set-Content $runtimePath $runtime -Encoding UTF8
Set-Content $cachePath $cache -Encoding UTF8
Write-Host 'SOL native MCP callback routing finalized; Audio MCP prefix aligned to codex_audio_remote_.'

& (Join-Path $PSScriptRoot 'add-sol-dynamic-tools.ps1')
& (Join-Path $PSScriptRoot 'add-sol-audio-quality-stability.ps1')
