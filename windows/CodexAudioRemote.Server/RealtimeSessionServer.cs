using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// Experimental protocol-v2 server that keeps the Android satellite unchanged while replacing
/// the virtual-cable/Codex Desktop path with Codex app-server realtime over WebRTC.
/// </summary>
internal sealed class RealtimeSessionServer : IDisposable
{
    const long WakeRetryCooldownMs = 3500;

    readonly Options options;
    readonly HttpListener listener = new();
    readonly object sync = new();
    readonly CodexRealtimeBridge bridge;
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly SemaphoreSlim activationGate = new(1, 1);

    WebSocket? client;
    long clientGeneration;
    long revision;
    long wakeSuppressedUntil;
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
        Console.WriteLine($"Codex Audio Remote · EXPERIMENTAL Realtime WebRTC · ws://0.0.0.0:{options.Port}/ws/");
        Console.WriteLine("Backend: official Codex app-server + ChatGPT OAuth + Chromium WebRTC.");
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
        long generation;
        lock (sync)
        {
            old = client;
            client = socket;
            generation = ++clientGeneration;
        }

        Console.WriteLine($"Realtime Android client connected · generation={generation} · {context.Request.RemoteEndPoint}");

        await SendJsonAsync(socket, new { type = "hello", protocol = 2, server = "CodexAudioRemote", voiceBackend = "realtime-webrtc" });
        await SendStateAsync(socket);

        if (old is not null && !ReferenceEquals(old, socket))
        {
            Console.WriteLine($"Realtime Android client superseded · new generation={generation}");
            try { old.Abort(); } catch { }
        }

        bool wasCurrent = false;
        try { await ReceiveLoopAsync(socket, generation); }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            if (IsCurrent(socket, generation))
                Console.WriteLine($"Realtime Android client socket error · generation={generation}: {ex.Message}");
            else
                Console.WriteLine($"Realtime stale Android socket ended · generation={generation}");
        }
        catch (Exception ex)
        {
            if (IsCurrent(socket, generation))
                Console.WriteLine($"Realtime Android client error · generation={generation}: {ex.Message}");
            else
                Console.WriteLine($"Realtime stale Android client ended · generation={generation}");
        }
        finally
        {
            lock (sync)
            {
                wasCurrent = ReferenceEquals(client, socket) && clientGeneration == generation;
                if (wasCurrent) client = null;
            }
            try { socket.Dispose(); } catch { }
        }

        if (!wasCurrent)
        {
            Console.WriteLine($"Realtime stale client cleanup ignored · generation={generation}");
        }
        else
        {
            Console.WriteLine($"Realtime current client disconnected · generation={generation} · state={CurrentState()}");
            if (CurrentState() != "idle") await EndSessionAsync("transport_lost");
        }
    }

    async Task ReceiveLoopAsync(WebSocket socket, long generation)
    {
        var buffer = new byte[64 * 1024];
        while (!disposed && IsCurrent(socket, generation) && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (!IsCurrent(socket, generation)) return;
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
            if (IsCurrent(socket, generation)) await HandleControlAsync(doc.RootElement);
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
        await activationGate.WaitAsync();
        try
        {
            CancellationToken token;
            string id;
            lock (sync)
            {
                if (state != "idle")
                {
                    Console.WriteLine($"Realtime wake ignored · state={state} · session={sessionId}");
                    return;
                }

                var now = Environment.TickCount64;
                if (now < wakeSuppressedUntil)
                {
                    Console.WriteLine($"Realtime wake ignored · retry cooldown={wakeSuppressedUntil - now}ms");
                    return;
                }

                sessionCts?.Cancel();
                sessionCts?.Dispose();
                sessionCts = new CancellationTokenSource();
                token = sessionCts.Token;
                sessionId = Guid.NewGuid().ToString("N");
                id = sessionId;

                // Claim the activation atomically before any await. This closes the small window
                // where simultaneous Android wake events could all observe state=idle.
                state = "activating";
                stateReason = "realtime_start";
                revision++;
            }

            await SendStateToCurrentAsync();
            Console.WriteLine("Session " + id + ": starting Codex Realtime WebRTC");

            try
            {
                await bridge.StartAsync(TrayController.RealtimeWorkingDirectory, token);
                if (token.IsCancellationRequested || !IsCurrentSession(id)) return;
                lock (sync) wakeSuppressedUntil = 0;
                await SetStateAsync("listening", "realtime_ready");
                await SendJsonToCurrentAsync(new
                {
                    type = "realtime_status",
                    backend = "codex-app-server-webrtc",
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
                if (IsCurrentSession(id))
                {
                    lock (sync)
                        wakeSuppressedUntil = Environment.TickCount64 + WakeRetryCooldownMs;
                    await SetStateAsync("idle", "realtime_start_failed", clearSession: true);
                    Console.WriteLine($"Realtime wake retry suppressed for {WakeRetryCooldownMs}ms after activation failure");
                }
            }
        }
        finally { activationGate.Release(); }
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
            state = "ending";
            stateReason = reason;
            revision++;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
        await SendStateToCurrentAsync();
        try { await bridge.StopAsync(); }
        catch (Exception ex) { Console.WriteLine("Realtime stop warning: " + ex.Message); }
        if (IsCurrentSession(endingId))
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
            voiceBackend = "realtime-webrtc"
        });
    }

    async Task SendJsonToCurrentAsync(object payload)
    {
        WebSocket? socket;
        lock (sync) socket = client;
        if (socket is not null) await SendJsonAsync(socket, payload);
    }

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

    bool IsCurrent(WebSocket socket, long generation)
    {
        lock (sync) return ReferenceEquals(client, socket) && clientGeneration == generation;
    }

    bool IsCurrentSession(string id)
    {
        lock (sync) return !string.IsNullOrEmpty(id) && sessionId == id;
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
            clientGeneration++;
            try { client?.Abort(); } catch { }
            client = null;
        }
        bridge.Dispose();
        sendGate.Dispose();
        activationGate.Dispose();
    }
}
