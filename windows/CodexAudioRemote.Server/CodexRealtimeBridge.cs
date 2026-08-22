using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal sealed class CodexRealtimeBridge : IAsyncDisposable, IDisposable
{
    const string AppServerUrl = "ws://127.0.0.1:4282";
    readonly Func<byte[], int, Task> onAudio;
    readonly Func<string, string, bool, Task> onTranscript;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();

    ClientWebSocket? socket;
    Process? appServerProcess;
    Task? receiveTask;
    long requestId;
    string threadId = "";
    int inputSampleRate = 16000;
    bool realtimeStarted;
    bool disposed;

    public string AuthMode { get; private set; } = "unknown";
    public string PlanType { get; private set; } = "unknown";
    public string LastError { get; private set; } = "";
    public bool IsRealtimeActive => realtimeStarted;

    public CodexRealtimeBridge(
        Func<byte[], int, Task> onAudio,
        Func<string, string, bool, Task> onTranscript)
    {
        this.onAudio = onAudio;
        this.onTranscript = onTranscript;
    }

    public void SetInputSampleRate(int sampleRate)
        => inputSampleRate = Math.Clamp(sampleRate, 8000, 48000);

    public async Task StartAsync(string? cwd, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        LastError = "";
        await EnsureConnectedAsync(cancellationToken);
        await InitializeAsync(cancellationToken);
        await ReadAccountAsync(cancellationToken);

        if (!string.Equals(AuthMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex App Server is not logged in with ChatGPT OAuth. Run 'codex login' first.");

        realtimeStarted = false;

        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(threadId))
            throw new InvalidOperationException("thread/start did not return a thread id.");

        // Omit transport intentionally. Codex app-server then uses its official
        // Realtime WebSocket path while preserving the user's ChatGPT OAuth session.
        await RequestAsync("thread/realtime/start", new
        {
            threadId,
            outputModality = "audio",
            version = "v3",
            includeStartupContext = true
        }, cancellationToken);

        var startedAt = Environment.TickCount64;
        while (!realtimeStarted && Environment.TickCount64 - startedAt < 12000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(LastError)) throw new InvalidOperationException(LastError);
            await Task.Delay(50, cancellationToken);
        }

        if (!realtimeStarted)
            throw new TimeoutException("Codex realtime V3 WebSocket did not emit thread/realtime/started within 12 seconds.");
    }

    public async Task AppendAudioAsync(byte[] pcm, int count, CancellationToken cancellationToken = default)
    {
        if (!realtimeStarted || string.IsNullOrEmpty(threadId) || count <= 0) return;
        var data = Convert.ToBase64String(pcm, 0, count);
        var samples = count / 2;
        await SendRequestNoWaitAsync("thread/realtime/appendAudio", new
        {
            threadId,
            audio = new
            {
                data,
                sampleRate = inputSampleRate,
                numChannels = 1,
                samplesPerChannel = samples
            }
        }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(threadId) && socket?.State == WebSocketState.Open)
        {
            try { await RequestAsync("thread/realtime/stop", new { threadId }, cancellationToken); }
            catch { }
        }
        realtimeStarted = false;
        threadId = "";
    }

    async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (socket?.State == WebSocketState.Open) return;

        if (!await TryConnectAsync(cancellationToken))
        {
            StartAppServerProcess();
            Exception? last = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(200, cancellationToken);
                try
                {
                    if (await TryConnectAsync(cancellationToken)) return;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("Could not connect to codex app-server at " + AppServerUrl, last);
        }
    }

    async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(AppServerUrl), cancellationToken);
            socket?.Dispose();
            socket = ws;
            receiveTask = Task.Run(() => ReceiveLoopAsync(lifetime.Token));
            return true;
        }
        catch
        {
            ws.Dispose();
            return false;
        }
    }

    void StartAppServerProcess()
    {
        if (appServerProcess is { HasExited: false }) return;
        var psi = new ProcessStartInfo
        {
            FileName = "codex",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--enable");
        psi.ArgumentList.Add("realtime_conversation");
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add(AppServerUrl);
        appServerProcess = Process.Start(psi) ?? throw new InvalidOperationException("Could not start official codex app-server. Ensure Codex CLI is installed and available in PATH.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!appServerProcess.HasExited)
                {
                    var line = await appServerProcess.StandardError.ReadLineAsync();
                    if (line is null) break;
                    Console.WriteLine("[app-server] " + line);
                }
            }
            catch { }
        });
    }

    async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex-audio-remote", title = "Codex Audio Remote", version = "0.2.0-official-ws" },
            capabilities = new { experimentalApi = true }
        }, cancellationToken);
        await SendNotificationAsync("initialized", new { }, cancellationToken);
    }

    async Task ReadAccountAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
        AuthMode = result.TryGetProperty("authMode", out var auth) ? auth.GetString() ?? "unknown" : "unknown";
        PlanType = result.TryGetProperty("planType", out var plan) ? plan.GetString() ?? "unknown" : "unknown";

        if (AuthMode == "unknown" && result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
        {
            if (account.TryGetProperty("type", out var type)) AuthMode = type.GetString() ?? "unknown";
            if (account.TryGetProperty("planType", out var nestedPlan)) PlanType = nestedPlan.GetString() ?? PlanType;
        }
    }

    async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref requestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            await SendJsonAsync(new { id, method, @params = parameters }, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    async Task SendRequestNoWaitAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref requestId);
        await SendJsonAsync(new { id, method, @params = parameters }, cancellationToken);
    }

    Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
        => SendJsonAsync(new { method, @params = parameters }, cancellationToken);

    async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        var ws = socket ?? throw new InvalidOperationException("App Server socket is not connected.");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await sendGate.WaitAsync(cancellationToken);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendGate.Release(); }
    }

    async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket?.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                using var doc = JsonDocument.Parse(stream.ToArray());
                await HandleMessageAsync(doc.RootElement.Clone());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = "Codex app-server transport error: " + ex.Message;
            Console.WriteLine(LastError);
        }
    }

    async Task HandleMessageAsync(JsonElement root)
    {
        if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var id))
        {
            if (!pending.TryGetValue(id, out var waiter)) return;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                waiter.TrySetException(new InvalidOperationException(message ?? "App Server request failed."));
            }
            else if (root.TryGetProperty("result", out var result)) waiter.TrySetResult(result.Clone());
            else waiter.TrySetResult(default);
            return;
        }

        if (!root.TryGetProperty("method", out var methodProp)) return;
        var method = methodProp.GetString() ?? "";
        var parameters = root.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "thread/realtime/started":
                realtimeStarted = true;
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("threadId", out var startedThread))
                    threadId = startedThread.GetString() ?? threadId;
                Console.WriteLine($"Codex Realtime V3 WebSocket started · thread={threadId} · auth={AuthMode} · plan={PlanType}");
                break;

            case "thread/realtime/sdp":
                Console.WriteLine("Ignoring unexpected SDP notification in official WebSocket mode");
                break;

            case "thread/realtime/outputAudio/delta":
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("audio", out var audio))
                {
                    var data = audio.TryGetProperty("data", out var d) ? d.GetString() : null;
                    var rate = audio.TryGetProperty("sampleRate", out var sr) && sr.TryGetInt32(out var parsed) ? parsed : 24000;
                    if (!string.IsNullOrEmpty(data)) await onAudio(Convert.FromBase64String(data), rate);
                }
                break;

            case "thread/realtime/transcript/delta":
            case "thread/realtime/transcript/done":
                if (parameters.ValueKind == JsonValueKind.Object)
                {
                    var role = parameters.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                    var text = method.EndsWith("/done", StringComparison.Ordinal)
                        ? (parameters.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")
                        : (parameters.TryGetProperty("delta", out var delta) ? delta.GetString() ?? "" : "");
                    await onTranscript(role, text, method.EndsWith("/done", StringComparison.Ordinal));
                }
                break;

            case "thread/realtime/error":
                LastError = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Realtime error"
                    : "Realtime error";
                Console.WriteLine("Codex Realtime V3 WebSocket error: " + LastError);
                break;

            case "thread/realtime/closed":
                realtimeStarted = false;
                Console.WriteLine("Codex Realtime V3 WebSocket closed");
                break;
        }
    }

    static byte[] ResamplePcm16Mono(byte[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || input.Length < 4) return input;
        var sourceSamples = input.Length / 2;
        var targetSamples = Math.Max(1, (int)Math.Round(sourceSamples * (double)targetRate / sourceRate));
        var output = new byte[targetSamples * 2];

        for (var i = 0; i < targetSamples; i++)
        {
            var src = i * (double)sourceRate / targetRate;
            var left = Math.Min(sourceSamples - 1, (int)src);
            var right = Math.Min(sourceSamples - 1, left + 1);
            var frac = src - left;
            var a = BitConverter.ToInt16(input, left * 2);
            var b = BitConverter.ToInt16(input, right * 2);
            var sample = (short)Math.Clamp((int)Math.Round(a + (b - a) * frac), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(sample & 0xff);
            output[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return output;
    }

    public static byte[] ToAndroid16k(byte[] pcm, int sourceRate) => ResamplePcm16Mono(pcm, sourceRate, 16000);

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CodexRealtimeBridge));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { socket?.Abort(); } catch { }
        try { socket?.Dispose(); } catch { }
        try
        {
            if (appServerProcess is { HasExited: false }) appServerProcess.Kill(entireProcessTree: true);
        }
        catch { }
        appServerProcess?.Dispose();
        lifetime.Dispose();
        sendGate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
