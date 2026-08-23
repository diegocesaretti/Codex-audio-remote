using System.Net;
using System.Text.Json;

internal sealed record RealtimeSpeechRequestResult(bool Accepted, string Status, string SessionId);

/// <summary>
/// Home Assistant adapter for the official Realtime backend. It never owns conversation state;
/// it delegates speech requests to RealtimeSessionServer, which remains the single authority.
/// The same listener also serves the short-lived authenticated live MP3 mirror consumed by HA/Cast.
/// </summary>
internal sealed class RealtimeHomeAssistantApiServer : IDisposable
{
    readonly Func<string, string, CancellationToken, Task<RealtimeSpeechRequestResult>> speak;
    readonly HttpListener listener = new();
    readonly CancellationTokenSource cts = new();
    Task? loopTask;
    bool disposed;

    public RealtimeHomeAssistantApiServer(
        Func<string, string, CancellationToken, Task<RealtimeSpeechRequestResult>> speak)
    {
        this.speak = speak;
        listener.Prefixes.Add($"http://+:{AppSettings.HomeAssistantApiPort}/api/");
    }

    public void Start()
    {
        if (!AppSettings.HomeAssistantEnabled)
        {
            Console.WriteLine("Home Assistant Realtime API disabled in settings");
            return;
        }
        listener.Start();
        loopTask = Task.Run(() => LoopAsync(cts.Token));
        Console.WriteLine($"Home Assistant Realtime API · http://0.0.0.0:{AppSettings.HomeAssistantApiPort}/api/");
        Console.WriteLine("Home Assistant base URL: " + AppSettings.HomeAssistantBaseUrl);
        Console.WriteLine("Speech endpoints: POST /api/speak · POST /api/tts");
        Console.WriteLine("Mirror endpoint: GET /api/realtime-mirror.mp3?token=<ephemeral>");
    }

    async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch when (token.IsCancellationRequested || disposed) { break; }
            catch (Exception ex)
            {
                Console.WriteLine("HA Realtime API listener error: " + ex.Message);
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
                string.Equals(path, "/api/realtime-mirror.mp3", StringComparison.OrdinalIgnoreCase))
            {
                if (await RealtimeSecondaryAudioMirror.TryServeHomeAssistantStreamAsync(context, token)) return;
                await WriteJson(context.Response, 404, new { ok = false, error = "mirror_stream_not_found" });
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(context.Response, 200, new
                {
                    ok = true,
                    protocol = 2,
                    backend = "realtime-v3",
                    service = "home_assistant_speech",
                    voice = AppSettings.RealtimeVoice,
                    model = AppSettings.DefaultRealtimeModel,
                    mirrors = new
                    {
                        android = "always",
                        windows = RealtimeMirrorSettings.WindowsMirrorEnabled,
                        homeAssistant = RealtimeMirrorSettings.HomeAssistantMirrorEnabled,
                        mediaPlayer = RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity
                    }
                });
                return;
            }

            var isSpeak = string.Equals(path, "/api/speak", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(path, "/api/tts", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) || !isSpeak)
            {
                await WriteJson(context.Response, 404, new
                {
                    ok = false,
                    error = "not_found",
                    hint = "Use POST /api/speak with { text: ... }"
                });
                return;
            }

            if (AppSettings.HomeAssistantRequireSourceMatch &&
                !await IsConfiguredHomeAssistantAsync(context.Request.RemoteEndPoint?.Address))
            {
                await WriteJson(context.Response, 403, new { ok = false, error = "source_not_home_assistant" });
                return;
            }

            using var doc = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: token);
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                await WriteJson(context.Response, 400, new { ok = false, error = "text_required" });
                return;
            }

            text = text.Trim();
            if (text.Length > 8000)
            {
                await WriteJson(context.Response, 413, new { ok = false, error = "text_too_long", max = 8000 });
                return;
            }

            Console.WriteLine($"HA Realtime speech request · chars={text.Length} · remote={context.Request.RemoteEndPoint}");
            var result = await speak(text, "home_assistant", token);
            if (!result.Accepted)
            {
                await WriteJson(context.Response, result.Status == "busy" ? 409 : 503,
                    new { ok = false, error = result.Status, sessionId = result.SessionId });
                return;
            }

            await WriteJson(context.Response, 202, new
            {
                ok = true,
                status = result.Status,
                sessionId = result.SessionId,
                voice = AppSettings.RealtimeVoice,
                keepSessionOpen = AppSettings.HomeAssistantKeepSpeechSessionOpen
            });
        }
        catch (JsonException ex)
        {
            Console.WriteLine("HA Realtime API invalid JSON: " + ex.Message);
            try { await WriteJson(context.Response, 400, new { ok = false, error = "invalid_json" }); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine("HA Realtime API request error: " + ex.Message);
            try { await WriteJson(context.Response, 500, new { ok = false, error = "server_error", detail = ex.Message }); } catch { }
        }
    }

    static async Task<bool> IsConfiguredHomeAssistantAsync(IPAddress? remote)
    {
        if (remote is null) return false;
        try
        {
            var uri = new Uri(AppSettings.HomeAssistantBaseUrl);
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
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        try { loopTask?.Wait(500); } catch { }
        cts.Dispose();
    }
}
