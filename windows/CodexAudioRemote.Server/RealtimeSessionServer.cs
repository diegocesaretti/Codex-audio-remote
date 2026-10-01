using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// Experimental protocol-v2 server that keeps the Android satellite unchanged while replacing
/// the virtual-cable/Codex Desktop path with Codex app-server thread/realtime V3.
/// </summary>
internal sealed class RealtimeSessionServer : IDisposable
{
    readonly Options options;
    readonly HttpListener listener = new();
    readonly object sync = new();
    readonly CodexRealtimeBridge bridge;

    WebSocket? client;
    long revision;
    string state = "idle";
    string stateReason = "startup";
    string sessionId = "";
    int inputSampleRate = 16000;
    CancellationTokenSource? sessionCts;
    bool disposed;

    public RealtimeSessionServer(Options options)
    {
        this.options = options;
        listener.Prefixes.Add($"http://+:{options.Port}/ws/");
        bridge = new CodexRealtimeBridge(OnRealtimeAudioAsync, OnRealtimeTranscriptAsync);
    }

    public async Task RunAsync()
    {
        listener.Start();
        Console.WriteLine($"Codex Audio Remote · EXPERIMENTAL Realtime V3 · ws://0.0.0.0:{options.Port}/ws/");
        Console.WriteLine("Backend: codex app-server + ChatGPT OAuth · no virtual audio cable.");
        Console.WriteLine("Working directory: " + TrayController.RealtimeWorkingDirectory);

        while (!disposed)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) when (disposed) { break; }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            _ = AcceptClientAsync(context);
        }
    }

    async Task AcceptClientAsync(HttpListenerContext context)
    {
        WebSocket socket;
        try { socket = (await context.AcceptWebSocketAsync(null)).WebSocket; }
        catch (Exception ex)
        {
            Console.WriteLine("Realtime Android WebSocket accept failed: " + ex.Message);
            return;
        }

        WebSocket? old;
        lock (sync) { old = client; client = socket; }
        if (old is not null) try { old.Abort(); } catch { }

        Console.WriteLine("Realtime Android client connected · " + context.Request.RemoteEndPoint);
        await SendJsonAsync(socket, new { type = "hello", protocol = 2, server = "CodexAudioRemote", voiceBackend = "realtime-v3" });
        await SendStateAsync(socket);

        try { await ReceiveLoopAsync(socket); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("Realtime Android client error: " + ex.Message); }
        finally
        {
            lock (sync) if (ReferenceEquals(client, socket)) client = null;
            try { socket.Dispose(); } catch { }
            if (CurrentState() != "idle") await EndSessionAsync("transport_lost");
        }
    }

    async Task ReceiveLoopAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        while (!disposed && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (CurrentState() == "listening")
                {
                    try { await bridge.AppendAudioAsync(buffer, result.Count); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Realtime uplink error: " + ex.Message);
                        await SendJsonToCurrentAsync(new { type = "audio_error", reason = "realtime_uplink", detail = ex.Message, sessionId = CurrentSessionId() });
                    }
                }
                continue;
            }

            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var doc = JsonDocument.Parse(text);
            await HandleControlAsync(doc.RootElement);
        }
    }

    async Task HandleControlAsync(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var p) ? p.GetString() ?? "" : "";
        switch (type)
        {
            case "hello":
            case "sync":
                await SendStateToCurrentAsync();
                return;
            case "ping":
                await SendJsonToCurrentAsync(new { type = "pong", revision });
                return;
            case "audio_config":
                inputSampleRate = GetInt(root, "sampleRate", 16000, 8000, 48000);
                bridge.SetInputSampleRate(inputSampleRate);
                Console.WriteLine("Realtime uplink configured · " + inputSampleRate + " Hz PCM16 mono");
                return;
            case "event":
            {
                var evt = root.TryGetProperty("event", out var ep) ? ep.GetString() ?? "" : "";
                if (evt == "wake") await BeginSessionAsync();
                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));
                return;
            }
            case "wake":
                await BeginSessionAsync();
                return;
            case "end_session":
                await EndSessionAsync(ReadString(root, "reason", "legacy_client"));
                return;
        }
    }

    async Task BeginSessionAsync()
    {
        CancellationToken token;
        string id;
        lock (sync)
        {
            if (state != "idle") return;
            sessionCts?.Cancel();
            sessionCts?.Dispose();
            sessionCts = new CancellationTokenSource();
            token = sessionCts.Token;
            sessionId = Guid.NewGuid().ToString("N");
            id = sessionId;
        }

        await SetStateAsync("activating", "realtime_start");
        Console.WriteLine("Session " + id + ": starting Codex Realtime V3");

        try
        {
            await bridge.StartAsync(TrayController.RealtimeWorkingDirectory, token);
            if (token.IsCancellationRequested || CurrentSessionId() != id) return;
            await SetStateAsync("listening", "realtime_ready");
            await SendJsonToCurrentAsync(new
            {
                type = "realtime_status",
                backend = "codex-app-server-v3",
                authMode = bridge.AuthMode,
                planType = bridge.PlanType,
                inputSampleRate,
                outputSampleRate = 16000,
                sessionId = id
            });
            Console.WriteLine($"Session {id}: LISTENING · OAuth={bridge.AuthMode} · plan={bridge.PlanType}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine("Realtime activation failed: " + ex);
            await SendJsonToCurrentAsync(new { type = "realtime_error", message = ex.Message, sessionId = id });
            await SetStateAsync("idle", "realtime_start_failed", clearSession: true);
        }
    }

    public async Task EndSessionAsync(string reason)
    {
        string endingId;
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            if (state == "idle" || state == "ending") return;
            endingId = sessionId;
            cancellation = sessionCts;
            sessionCts = null;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
        await SetStateAsync("ending", reason);
        try { await bridge.StopAsync(); }
        catch (Exception ex) { Console.WriteLine("Realtime stop warning: " + ex.Message); }
        await SetStateAsync("idle", reason, clearSession: true);
        Console.WriteLine("Session " + endingId + ": ended · " + reason);
    }

    async Task OnRealtimeAudioAsync(byte[] pcm, int sourceRate)
    {
        if (CurrentState() != "listening" || pcm.Length == 0) return;
        var androidPcm = CodexRealtimeBridge.ToAndroid16k(pcm, sourceRate);
        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is null || socket.State != WebSocketState.Open) return;
        await SendBinaryAsync(socket, androidPcm);
    }

    async Task OnRealtimeTranscriptAsync(string role, string text, bool done)
    {
        if (string.IsNullOrEmpty(text)) return;
        await SendJsonToCurrentAsync(new
        {
            type = "realtime_transcript",
            role,
            text,
            done,
            sessionId = CurrentSessionId()
        });
    }

    async Task SetStateAsync(string newState, string reason, bool clearSession = false)
    {
        lock (sync)
        {
            state = newState;
            stateReason = reason;
            revision++;
            if (clearSession) sessionId = "";
        }
        await SendStateToCurrentAsync();
    }

    async Task SendStateToCurrentAsync()
    {
        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is not null) await SendStateAsync(socket);
    }

    async Task SendStateAsync(WebSocket socket)
    {
        string snapshotState, snapshotSession, snapshotReason;
        long snapshotRevision;
        lock (sync)
        {
            snapshotState = state;
            snapshotSession = sessionId;
            snapshotRevision = revision;
            snapshotReason = stateReason;
        }
        await SendJsonAsync(socket, new
        {
            type = "state",
            protocol = 2,
            state = snapshotState,
            sessionId = snapshotSession,
            revision = snapshotRevision,
            reason = snapshotReason,
            voiceBackend = "realtime-v3"
        });
    }

    async Task SendJsonToCurrentAsync(object payload)
    {
        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is not null) await SendJsonAsync(socket, payload);
    }

    readonly SemaphoreSlim sendGate = new(1, 1);

    async Task SendJsonAsync(WebSocket socket, object payload)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await sendGate.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
        finally { sendGate.Release(); }
    }

    async Task SendBinaryAsync(WebSocket socket, byte[] bytes)
    {
        if (socket.State != WebSocketState.Open || bytes.Length == 0) return;
        await sendGate.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch { }
        finally { sendGate.Release(); }
    }

    string CurrentState() { lock (sync) return state; }
    string CurrentSessionId() { lock (sync) return sessionId; }

    static int GetInt(JsonElement root, string name, int fallback, int min, int max)
        => root.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? Math.Clamp(value, min, max) : fallback;

    static string ReadString(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        lock (sync)
        {
            sessionCts?.Cancel();
            try { client?.Abort(); } catch { }
        }
        bridge.Dispose();
        sendGate.Dispose();
    }
}
