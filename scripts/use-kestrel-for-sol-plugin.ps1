$ErrorActionPreference = 'Stop'

$serverPath = 'windows/CodexAudioRemote.Server/RealtimeSessionServer.cs'
$runtimePath = 'windows/CodexAudioRemote.Server/SolRuntimeBridge.cs'
$server = Get-Content $serverPath -Raw
$runtime = Get-Content $runtimePath -Raw

function Require-Contains([string]$text, [string]$needle, [string]$label) {
    if (-not $text.Contains($needle)) { throw "Kestrel transform: missing expected $label" }
}

# The native SOL plugin must not depend on Windows HTTP.sys URLACL reservations.
# Kestrel listens directly on the TCP socket, so a normal unelevated SOL process can
# accept Android WebSocket traffic from the LAN without netsh/admin setup.
Require-Contains $server 'readonly HttpListener listener = new();' 'HttpListener field'
$server = $server.Replace(
    '    readonly HttpListener listener = new();',
    '    Microsoft.AspNetCore.Builder.WebApplication? webApp;')

$prefix = '        listener.Prefixes.Add($"http://+:{options.Port}/ws/");'
Require-Contains $server $prefix 'HttpListener prefix registration'
$server = $server.Replace($prefix + "`r`n", '')
$server = $server.Replace($prefix + "`n", '')

$runPattern = '(?s)    public async Task RunAsync\(\)\s*\{.*?\r?\n    \}\r?\n\r?\n    async Task AcceptClientAsync\(HttpListenerContext context\)'
if ($server -notmatch $runPattern) { throw 'Kestrel transform: RunAsync/AcceptClientAsync block not found.' }
$runReplacement = @'
    public async Task RunAsync()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(options.Port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (solRuntime is not null && await solRuntime.TryHandleCallbackAsync(context)) return;

            if (!string.Equals(context.Request.Path.Value, "/ws/", StringComparison.Ordinal) ||
                !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await AcceptClientAsync(context);
        });

        webApp = app;
        await app.StartAsync();
        if (solRuntime is not null) await solRuntime.StartAsync();

        Console.WriteLine($"Codex Audio Remote · SOL native Kestrel/WebRTC · ws://0.0.0.0:{options.Port}/ws/");
        Console.WriteLine("Backend: official Codex app-server + ChatGPT OAuth + Chromium WebRTC.");
        Console.WriteLine("Working directory: " + AppSettings.RealtimeWorkingDirectory);
        await app.WaitForShutdownAsync();
    }

    async Task AcceptClientAsync(HttpContext context)
'@
$server = [regex]::Replace($server, $runPattern, $runReplacement.TrimEnd(), 1)

$oldAccept = '(await context.AcceptWebSocketAsync(null)).WebSocket'
Require-Contains $server $oldAccept 'HttpListener WebSocket accept result'
$server = $server.Replace($oldAccept, 'await context.WebSockets.AcceptWebSocketAsync()')

# Preserve the existing speaker/device identity semantics using Kestrel connection metadata.
$acceptMarker = '        WebSocket? old;'
Require-Contains $server $acceptMarker 'accepted socket marker'
$remoteBlock = @'
        var remoteEndpoint = context.Connection.RemoteIpAddress is { } remoteIp
            ? new IPEndPoint(remoteIp, context.Connection.RemotePort)
            : null;

        WebSocket? old;
'@
$server = $server.Replace($acceptMarker, $remoteBlock.TrimEnd())
$server = $server.Replace('context.Request.RemoteEndPoint', 'remoteEndpoint')

$disposeOld = @'
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
'@
Require-Contains $server $disposeOld.TrimEnd() 'HttpListener dispose block'
$disposeNew = @'
        try { webApp?.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { webApp?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
'@
$server = $server.Replace($disposeOld.TrimEnd(), $disposeNew.TrimEnd())

# Kestrel types are referenced by the transformed source only in the native plugin build.
$server = $server.Replace(
    'using System.Text.Json;',
    "using System.Text.Json;`r`nusing Microsoft.AspNetCore.Builder;`r`nusing Microsoft.AspNetCore.Http;`r`nusing Microsoft.AspNetCore.Hosting;`r`nusing Microsoft.Extensions.Hosting;")

if ($server -match 'HttpListener') { throw 'Kestrel transform: HttpListener survived in RealtimeSessionServer.' }
if ($server -notmatch 'ListenAnyIP\(options.Port\)') { throw 'Kestrel transform: ListenAnyIP binding missing.' }
if ($server -notmatch 'app\.StartAsync\(\)') { throw 'Kestrel transform: server is not started before SOL MCP registration.' }

# Convert the SOL MCP callback handler from HttpListenerContext to ASP.NET Core HttpContext.
$runtime = $runtime.Replace(
    'using System.Text.Json;',
    "using System.Text.Json;`r`nusing Microsoft.AspNetCore.Http;")
$runtime = $runtime.Replace('TryHandleCallbackAsync(HttpListenerContext context)', 'TryHandleCallbackAsync(HttpContext context)')
$runtime = $runtime.Replace('context.Request.Url?.AbsolutePath', 'context.Request.Path.Value')
$runtime = $runtime.Replace('(context.Request.RemoteEndPoint as IPEndPoint)?.Address', 'context.Connection.RemoteIpAddress')
$runtime = $runtime.Replace('HandleCallbackAsync(HttpListenerContext context)', 'HandleCallbackAsync(HttpContext context)')
$runtime = $runtime.Replace('context.Request.HttpMethod', 'context.Request.Method')

$readPattern = '(?s)    static async Task<JsonElement> ReadJsonAsync\(HttpListenerRequest request\)\s*\{.*?\r?\n    \}'
if ($runtime -notmatch $readPattern) { throw 'Kestrel transform: HttpListener ReadJsonAsync block not found.' }
$readReplacement = @'
    static async Task<JsonElement> ReadJsonAsync(HttpRequest request)
    {
        if (request.ContentLength is > 1024 * 1024) throw new InvalidOperationException("request_too_large");
        using var reader = new StreamReader(request.Body, Encoding.UTF8, true, 4096, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new InvalidOperationException("request_too_large");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return document.RootElement.Clone();
    }
'@
$runtime = [regex]::Replace($runtime, $readPattern, $readReplacement.TrimEnd(), 1)

$writePattern = '(?s)    static async Task WriteJsonAsync\(HttpListenerResponse response, int status, object payload\)\s*\{.*?\r?\n    \}'
if ($runtime -notmatch $writePattern) { throw 'Kestrel transform: HttpListener WriteJsonAsync block not found.' }
$writeReplacement = @'
    static async Task WriteJsonAsync(HttpResponse response, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes);
    }
'@
$runtime = [regex]::Replace($runtime, $writePattern, $writeReplacement.TrimEnd(), 1)

# Do not launch async status work from Dispose and then immediately destroy its gate/token.
# SOL already knows the plugin process exited, so this status update is redundant and racy.
$disposeStatus = '        _ = SetInputStatusAsync("disconnected");'
Require-Contains $runtime $disposeStatus 'racy disconnected status update'
$runtime = $runtime.Replace($disposeStatus + "`r`n", '')
$runtime = $runtime.Replace($disposeStatus + "`n", '')

if ($runtime -match 'HttpListener(Context|Request|Response)') { throw 'Kestrel transform: HttpListener callback types survived.' }
if ($runtime -match '_ = SetInputStatusAsync\("disconnected"\)') { throw 'Kestrel transform: disposed SemaphoreSlim race survived.' }

Set-Content $serverPath $server -Encoding UTF8
Set-Content $runtimePath $runtime -Encoding UTF8
Write-Host 'SOL plugin transport switched from HTTP.sys/HttpListener to unelevated Kestrel; dispose race removed.'
