using System.Net;
using System.Text.Json;

internal sealed class HomeAssistantApiServer : IDisposable
{
    readonly HttpListener listener = new();
    readonly CancellationTokenSource cts = new();
    Task? loopTask;

    public HomeAssistantApiServer(int port = 8766)
    {
        listener.Prefixes.Add($"http://+:{port}/api/");
    }

    public void Start()
    {
        listener.Start();
        loopTask = Task.Run(() => LoopAsync(cts.Token));
        Console.WriteLine("Home Assistant API listening on http://0.0.0.0:8766/api/");
        Console.WriteLine("Home Assistant base URL: " + TrayController.HomeAssistantBaseUrl);
    }

    async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine("HA API listener error: " + ex.Message); continue; }
            _ = Task.Run(() => HandleAsync(context, token), token);
        }
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(context.Request.Url?.AbsolutePath, "/api/conversation/start", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(context.Response, 404, new { ok = false, error = "not_found" });
                return;
            }

            if (!await IsConfiguredHomeAssistantAsync(context.Request.RemoteEndPoint?.Address))
            {
                await WriteJson(context.Response, 403, new { ok = false, error = "source_not_home_assistant" });
                return;
            }

            using var doc = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: token);
            var root = doc.RootElement;
            var audioUrl = root.TryGetProperty("audio_url", out var audioProp) ? audioProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                await WriteJson(context.Response, 400, new { ok = false, error = "audio_url_required" });
                return;
            }

            var started = await ExternalConversationHub.TryStartAsync(new ExternalConversationRequest(audioUrl));
            await WriteJson(context.Response, started ? 202 : 409,
                started ? new { ok = true, status = "accepted" } : new { ok = false, error = "android_not_connected" });
        }
        catch (Exception ex)
        {
            Console.WriteLine("HA API request error: " + ex.Message);
            try { await WriteJson(context.Response, 500, new { ok = false, error = "server_error" }); } catch { }
        }
    }

    static async Task<bool> IsConfiguredHomeAssistantAsync(IPAddress? remote)
    {
        if (remote is null) return false;
        try
        {
            var uri = new Uri(TrayController.HomeAssistantBaseUrl);
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            return addresses.Any(a => a.Equals(remote) || a.MapToIPv6().Equals(remote.MapToIPv6()));
        }
        catch { return false; }
    }

    static async Task WriteJson(HttpListenerResponse response, int status, object payload)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public void Dispose()
    {
        cts.Cancel();
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        try { loopTask?.Wait(500); } catch { }
        cts.Dispose();
    }
}
