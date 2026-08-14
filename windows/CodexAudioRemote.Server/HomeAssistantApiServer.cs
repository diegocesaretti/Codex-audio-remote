using System.Net;
using System.Text.Json;

internal sealed class HomeAssistantApiServer : IDisposable
{
    readonly SessionServerV2 sessions;
    readonly HttpListener listener = new();
    readonly CancellationTokenSource cts = new();
    readonly int port;
    Task? loopTask;

    public HomeAssistantApiServer(SessionServerV2 sessions, int port = 8766)
    {
        this.sessions = sessions;
        this.port = port;
        listener.Prefixes.Add($"http://+:{port}/api/");
    }

    public void Start()
    {
        listener.Start();
        loopTask = Task.Run(() => LoopAsync(cts.Token));
        Console.WriteLine($"Home Assistant API v2 listening on http://0.0.0.0:{port}/api/");
        Console.WriteLine("Home Assistant base URL: " + TrayController.HomeAssistantBaseUrl);
    }

    async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine("HA API listener error: " + ex.Message);
                continue;
            }
            _ = Task.Run(() => HandleAsync(context, token), token);
        }
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(context.Response, 200, new { ok = true, protocol = 2, service = "home_assistant_rest" });
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(path, "/api/conversation/start", StringComparison.OrdinalIgnoreCase))
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
            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;

            string contextSource;
            string inputType;
            if (!string.IsNullOrWhiteSpace(text))
            {
                contextSource = ContextAudioInjector.PackText(text.Trim());
                inputType = "text";
            }
            else if (!string.IsNullOrWhiteSpace(audioUrl))
            {
                contextSource = audioUrl.Trim();
                inputType = "audio_url";
            }
            else
            {
                await WriteJson(context.Response, 400, new { ok = false, error = "text_or_audio_url_required" });
                return;
            }

            Console.WriteLine($"HA conversation request · input={inputType} · remote={context.Request.RemoteEndPoint}");
            var result = await sessions.StartExternalConversationAsync(contextSource, "home_assistant");
            if (!result.Accepted)
            {
                await WriteJson(context.Response, 409, new { ok = false, error = result.Status });
                return;
            }

            await WriteJson(context.Response, 202, new
            {
                ok = true,
                status = "accepted",
                input = inputType,
                sessionId = result.SessionId,
                protocol = 2
            });
        }
        catch (JsonException ex)
        {
            Console.WriteLine("HA API invalid JSON: " + ex.Message);
            try { await WriteJson(context.Response, 400, new { ok = false, error = "invalid_json" }); } catch { }
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
