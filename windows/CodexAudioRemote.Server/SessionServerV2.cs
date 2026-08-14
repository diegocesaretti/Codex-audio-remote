using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal enum ServerSessionState
{
    Idle,
    Activating,
    Listening,
    Ending
}

internal sealed class SessionServerV2 : IDisposable
{
    readonly Options options;
    readonly AudioDeviceSwitcher switcher;
    readonly HttpListener listener = new();
    readonly object sync = new();

    ClientPeer? currentPeer;
    long nextGeneration;
    long revision;
    string sessionId = "";
    ServerSessionState state = ServerSessionState.Idle;
    string stateReason = "startup";

    AudioCableSink? audioSink;
    LoopbackDownlink? downlink;
    CancellationTokenSource? sessionCts;
    CancellationTokenSource? disconnectGraceCts;
    bool disposed;

    public SessionServerV2(Options options, AudioDeviceSwitcher switcher)
    {
        this.options = options;
        this.switcher = switcher;
        listener.Prefixes.Add($"http://+:{options.Port}/ws/");
    }

    public async Task RunAsync()
    {
        listener.Start();
        Console.WriteLine($"Codex Audio Remote v2 listening on ws://0.0.0.0:{options.Port}/ws/");
        Console.WriteLine("Protocol v2: Windows owns session state; Android mirrors it.");

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
        try
        {
            var accepted = await context.AcceptWebSocketAsync(null);
            socket = accepted.WebSocket;
        }
        catch (Exception ex)
        {
            Console.WriteLine("WebSocket accept failed: " + ex.Message);
            return;
        }

        ClientPeer peer;
        ClientPeer? previous;
        lock (sync)
        {
            var generation = ++nextGeneration;
            peer = new ClientPeer(socket, generation);
            previous = currentPeer;
            currentPeer = peer;
            disconnectGraceCts?.Cancel();
            disconnectGraceCts?.Dispose();
            disconnectGraceCts = null;
        }

        if (previous is not null)
        {
            Console.WriteLine($"Client generation {previous.Generation} superseded by {peer.Generation}");
            try { previous.Socket.Abort(); } catch { }
        }

        Console.WriteLine($"Client connected · generation={peer.Generation} · {context.Request.RemoteEndPoint}");
        await peer.SendJsonAsync(new { type = "hello", protocol = 2, server = "CodexAudioRemote" });
        await SendStateToPeerAsync(peer);

        try { await ReceiveLoopAsync(peer); }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Console.WriteLine($"Client socket error · generation={peer.Generation} · {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"Client error · generation={peer.Generation} · {ex}"); }
        finally { await OnPeerEndedAsync(peer); }
    }

    async Task ReceiveLoopAsync(ClientPeer peer)
    {
        var buffer = new byte[64 * 1024];
        while (peer.Socket.State == WebSocketState.Open && IsCurrent(peer))
        {
            var result = await peer.Socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                AudioCableSink? sink;
                ServerSessionState snapshot;
                lock (sync) { sink = audioSink; snapshot = state; }
                if (snapshot == ServerSessionState.Listening && sink is not null)
                    sink.Write(buffer, 0, result.Count);
                continue;
            }

            if (!result.EndOfMessage)
            {
                Console.WriteLine("Ignoring fragmented control message");
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"<- g{peer.Generation} {text}");
            using var doc = JsonDocument.Parse(text);
            await HandleControlAsync(peer, doc.RootElement);
        }
    }

    async Task HandleControlAsync(ClientPeer peer, JsonElement root)
    {
        if (!IsCurrent(peer)) return;
        var type = root.TryGetProperty("type", out var p) ? p.GetString() ?? "" : "";

        switch (type)
        {
            case "hello":
            case "sync":
                await SendStateToPeerAsync(peer);
                return;

            case "event":
            {
                var evt = root.TryGetProperty("event", out var eventProp) ? eventProp.GetString() ?? "" : "";
                if (evt == "wake") await BeginWakeAsync();
                else if (evt == "end") await EndSessionAsync(ReadString(root, "reason", "client"));
                else Console.WriteLine("Ignoring unknown client event: " + evt);
                return;
            }

            case "audio_config":
                await ConfigureUplinkAsync(root);
                return;

            case "ping":
                await peer.SendJsonAsync(new { type = "pong", revision = CurrentRevision() });
                return;

            // v1 compatibility while old clients are being replaced.
            case "wake":
                await BeginWakeAsync();
                return;
            case "end_session":
                await EndSessionAsync(ReadString(root, "reason", "legacy_client"));
                return;
            case "audio_start":
                await ConfigureUplinkAsync(root);
                return;
            case "audio_stop":
                StopUplink();
                return;
        }
    }

    async Task BeginWakeAsync()
    {
        string newSession;
        CancellationToken token;
        lock (sync)
        {
            if (state != ServerSessionState.Idle)
            {
                Console.WriteLine($"Wake ignored: authoritative state is {state}");
                _ = BroadcastStateAsync();
                return;
            }

            sessionCts?.Cancel();
            sessionCts?.Dispose();
            sessionCts = new CancellationTokenSource();
            token = sessionCts.Token;
            newSession = Guid.NewGuid().ToString("N");
            sessionId = newSession;
        }

        await SetStateAsync(ServerSessionState.Activating, "wake");
        Console.WriteLine($"Session {newSession}: activation started");

        _ = Task.Run(async () =>
        {
            Task bluetoothTask = Task.CompletedTask;
            try
            {
                bluetoothTask = BtcomBluetoothReconnect.EnsureSelectedOutputActiveAsync(token);

                if (!switcher.ActivateRemoteMic())
                {
                    await FailActivationAsync(newSession, "virtual_mic_not_found");
                    return;
                }

                switcher.BeginActivation();
                await Task.Delay(60, token);
                ShortcutSender.Send(options.Shortcut);

                var started = Environment.TickCount64;
                while (!token.IsCancellationRequested && Environment.TickCount64 - started < options.ActivationTimeoutMs)
                {
                    if (!IsSession(newSession, ServerSessionState.Activating)) return;
                    if (CodexMicDetector.IsActive())
                    {
                        switcher.MarkListening();
                        await SetStateAsync(ServerSessionState.Listening, "codex_ready", newSession);
                        StartDownlink(newSession);
                        Console.WriteLine($"Session {newSession}: LISTENING confirmed in {Environment.TickCount64 - started} ms");

                        try
                        {
                            await bluetoothTask;
                            if (IsSession(newSession, ServerSessionState.Listening))
                            {
                                Console.WriteLine($"Session {newSession}: Bluetooth task completed; rebinding downlink once");
                                StartDownlink(newSession);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Console.WriteLine("Bluetooth handoff warning: " + ex.Message); }
                        return;
                    }
                    await Task.Delay(50, token);
                }

                if (!token.IsCancellationRequested)
                    await FailActivationAsync(newSession, "codex_mic_timeout");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Session {newSession}: activation exception: {ex.Message}");
                await FailActivationAsync(newSession, "activation_exception");
            }
        });
    }

    async Task FailActivationAsync(string expectedSession, string reason)
    {
        if (!IsSession(expectedSession, ServerSessionState.Activating)) return;
        Console.WriteLine($"Session {expectedSession}: activation failed · {reason}");
        StopAudio();
        switcher.ActivationFailed();
        switcher.RestoreNow();
        await SetStateAsync(ServerSessionState.Idle, reason, expectedSession, clearSession: true);
    }

    async Task ConfigureUplinkAsync(JsonElement root)
    {
        string? messageSession = ReadOptionalString(root, "sessionId");
        lock (sync)
        {
            if (state != ServerSessionState.Listening) return;
            if (!string.IsNullOrEmpty(messageSession) && messageSession != sessionId) return;
        }

        var sampleRate = GetInt(root, "sampleRate", 48000, 8000, 48000);
        var quality = GetInt(root, "quality", 80, 0, 100);
        var latency = GetInt(root, "latency", 55, 0, 100);

        var replacement = AudioCableSink.TryCreate(options.VirtualCableInputName, sampleRate, quality, latency);
        if (replacement is null)
        {
            Console.WriteLine("Could not create virtual cable sink; keeping authoritative LISTENING state");
            var peer = CurrentPeer();
            if (peer is not null) await peer.SendJsonAsync(new { type = "audio_error", reason = "cable_input_not_found", sessionId = CurrentSessionId() });
            return;
        }

        AudioCableSink? old;
        lock (sync) { old = audioSink; audioSink = replacement; }
        old?.Dispose();
        Console.WriteLine($"Uplink configured · {sampleRate} Hz · q={quality} · latency={latency}");
    }

    void StopUplink()
    {
        AudioCableSink? old;
        lock (sync) { old = audioSink; audioSink = null; }
        old?.Dispose();
    }

    void StartDownlink(string expectedSession)
    {
        if (!IsSession(expectedSession, ServerSessionState.Listening)) return;

        LoopbackDownlink? replacement = null;
        try
        {
            replacement = new LoopbackDownlink(SendBinaryToCurrentAsync, DownlinkDeviceSettings.SelectedDeviceId);
            replacement.Start();
        }
        catch (Exception ex)
        {
            replacement?.Dispose();
            Console.WriteLine("Downlink start warning: " + ex.Message);
            return;
        }

        LoopbackDownlink? old;
        lock (sync)
        {
            if (state != ServerSessionState.Listening || sessionId != expectedSession)
            {
                replacement.Dispose();
                return;
            }
            old = downlink;
            downlink = replacement;
        }
        old?.Dispose();
    }

    async Task SendBinaryToCurrentAsync(byte[] pcm)
    {
        ClientPeer? peer;
        ServerSessionState snapshot;
        lock (sync) { peer = currentPeer; snapshot = state; }
        if (snapshot != ServerSessionState.Listening || peer is null || pcm.Length == 0) return;
        await peer.SendBinaryAsync(pcm);
    }

    public async Task EndSessionAsync(string reason)
    {
        string endingSession;
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            if (state == ServerSessionState.Idle || state == ServerSessionState.Ending) return;
            endingSession = sessionId;
            cancellation = sessionCts;
            sessionCts = null;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();

        await SetStateAsync(ServerSessionState.Ending, reason, endingSession);
        StopAudio();

        try
        {
            if (CodexMicDetector.IsActive())
            {
                ShortcutSender.Send(options.Shortcut);
                var started = Environment.TickCount64;
                while (CodexMicDetector.IsActive() && Environment.TickCount64 - started < options.EndSessionRestoreTimeoutMs)
                    await Task.Delay(50);
            }
        }
        catch (Exception ex) { Console.WriteLine("Voice close warning: " + ex.Message); }
        finally { switcher.RestoreNow(); }

        await SetStateAsync(ServerSessionState.Idle, reason, endingSession, clearSession: true);
        Console.WriteLine($"Session {endingSession}: ended · {reason}");
    }

    void StopAudio()
    {
        AudioCableSink? sink;
        LoopbackDownlink? link;
        lock (sync)
        {
            sink = audioSink;
            audioSink = null;
            link = downlink;
            downlink = null;
        }
        sink?.Dispose();
        link?.Dispose();
    }

    async Task OnPeerEndedAsync(ClientPeer peer)
    {
        bool wasCurrent;
        lock (sync)
        {
            wasCurrent = currentPeer == peer;
            if (wasCurrent) currentPeer = null;
        }

        peer.Dispose();
        if (!wasCurrent)
        {
            Console.WriteLine($"Stale client ended · generation={peer.Generation}");
            return;
        }

        Console.WriteLine($"Current client disconnected · generation={peer.Generation} · state={CurrentState()}");
        ScheduleDisconnectGrace();
        await Task.CompletedTask;
    }

    void ScheduleDisconnectGrace()
    {
        CancellationTokenSource cts;
        lock (sync)
        {
            disconnectGraceCts?.Cancel();
            disconnectGraceCts?.Dispose();
            disconnectGraceCts = new CancellationTokenSource();
            cts = disconnectGraceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3500, cts.Token);
                bool shouldEnd;
                lock (sync) shouldEnd = currentPeer is null && state != ServerSessionState.Idle;
                if (shouldEnd) await EndSessionAsync("transport_lost");
            }
            catch (OperationCanceledException) { }
        });
    }

    async Task SetStateAsync(ServerSessionState newState, string reason, string? expectedSession = null, bool clearSession = false)
    {
        lock (sync)
        {
            if (expectedSession is not null && sessionId != expectedSession) return;
            state = newState;
            stateReason = reason;
            revision++;
            if (clearSession) sessionId = "";
        }
        await BroadcastStateAsync();
    }

    async Task BroadcastStateAsync()
    {
        var peer = CurrentPeer();
        if (peer is not null) await SendStateToPeerAsync(peer);
    }

    async Task SendStateToPeerAsync(ClientPeer peer)
    {
        StateSnapshot snapshot;
        lock (sync)
        {
            snapshot = new StateSnapshot(state, sessionId, revision, stateReason);
        }
        await peer.SendJsonAsync(new
        {
            type = "state",
            protocol = 2,
            state = snapshot.State.ToString().ToLowerInvariant(),
            sessionId = snapshot.SessionId,
            revision = snapshot.Revision,
            reason = snapshot.Reason
        });
    }

    bool IsCurrent(ClientPeer peer)
    {
        lock (sync) return currentPeer == peer;
    }

    bool IsSession(string id, ServerSessionState expected)
    {
        lock (sync) return sessionId == id && state == expected;
    }

    ClientPeer? CurrentPeer() { lock (sync) return currentPeer; }
    long CurrentRevision() { lock (sync) return revision; }
    string CurrentSessionId() { lock (sync) return sessionId; }
    ServerSessionState CurrentState() { lock (sync) return state; }

    static int GetInt(JsonElement root, string name, int fallback, int min, int max)
        => root.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? Math.Clamp(value, min, max) : fallback;

    static string ReadString(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;

    static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        lock (sync)
        {
            sessionCts?.Cancel();
            disconnectGraceCts?.Cancel();
            currentPeer?.Socket.Abort();
        }
        StopAudio();
        switcher.RestoreNow();
    }

    sealed record StateSnapshot(ServerSessionState State, string SessionId, long Revision, string Reason);

    sealed class ClientPeer : IDisposable
    {
        readonly SemaphoreSlim sendGate = new(1, 1);
        public WebSocket Socket { get; }
        public long Generation { get; }

        public ClientPeer(WebSocket socket, long generation)
        {
            Socket = socket;
            Generation = generation;
        }

        public async Task SendJsonAsync(object payload)
        {
            if (Socket.State != WebSocketState.Open) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await sendGate.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
            finally { sendGate.Release(); }
        }

        public async Task SendBinaryAsync(byte[] bytes)
        {
            if (Socket.State != WebSocketState.Open || bytes.Length == 0) return;
            await sendGate.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch { }
            finally { sendGate.Release(); }
        }

        public void Dispose()
        {
            try
            {
                if (Socket.State == WebSocketState.Open || Socket.State == WebSocketState.CloseReceived)
                    Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { }
            try { Socket.Dispose(); } catch { }
            sendGate.Dispose();
        }
    }
}
