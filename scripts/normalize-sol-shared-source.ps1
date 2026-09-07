$ErrorActionPreference = 'Stop'

$runtimePath = 'windows/CodexAudioRemote.Server/SolRuntimeBridge.cs'
$cachePath = 'windows/CodexAudioRemote.Server/HomeAssistantWebSocketCache.cs'
$runtime = Get-Content $runtimePath -Raw
$cache = Get-Content $cachePath -Raw

# These files are compiled into the standalone assembly but remain dormant unless
# SOL_PLUGIN_ID is present. Normalize only framework/name ambiguities; do not enable SOL mode.
$runtime = $runtime.Replace(
    'new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, true, leaveOpen: false)',
    'new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, true, 4096, false)'
)

$cache = $cache.Replace('    Timer? persistTimer;', '    System.Threading.Timer? persistTimer;')
$cache = $cache.Replace('            persistTimer ??= new Timer(_ =>', '            persistTimer ??= new System.Threading.Timer(_ =>')
$cache = $cache.Replace(
    'Interlocked.Exchange(ref lastUpdateTicks, parsed.UtcTicks);',
    'Interlocked.Exchange(ref lastUpdateTicks, parsed.UtcDateTime.Ticks);'
)
$cache = $cache.Replace(
    'updatedAt = new DateTimeOffset(Math.Max(Interlocked.Read(ref lastUpdateTicks), DateTimeOffset.UtcNow.Ticks), TimeSpan.Zero),',
    'updatedAt = new DateTimeOffset(Interlocked.Read(ref lastUpdateTicks) > 0 ? Interlocked.Read(ref lastUpdateTicks) : DateTimeOffset.UtcNow.Ticks, TimeSpan.Zero),'
)

Set-Content $runtimePath $runtime -Encoding UTF8
Set-Content $cachePath $cache -Encoding UTF8
Write-Host 'Normalized shared SOL source for standalone compilation; runtime behavior remains disabled outside SOL.'
