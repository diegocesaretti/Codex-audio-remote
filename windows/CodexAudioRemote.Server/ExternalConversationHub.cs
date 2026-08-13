using System.Net.WebSockets;
using System.Text.Json;

internal sealed record ExternalConversationRequest(string AudioUrl, string Source = "home_assistant");

internal static class ExternalConversationHub
{
    static readonly object Sync = new();
    static Func<ExternalConversationRequest, Task>? handler;

    public static void Register(Func<ExternalConversationRequest, Task> value)
    {
        lock (Sync) handler = value;
    }

    public static void Clear(Func<ExternalConversationRequest, Task> value)
    {
        lock (Sync)
        {
            if (handler == value) handler = null;
        }
    }

    public static async Task<bool> TryStartAsync(ExternalConversationRequest request)
    {
        Func<ExternalConversationRequest, Task>? current;
        lock (Sync) current = handler;
        if (current is null) return false;
        await current(request);
        return true;
    }
}

internal sealed class ExternalSessionController : IDisposable
{
    readonly WebSocket socket;
    readonly SemaphoreSlim sendGate;
    readonly AudioDeviceSwitcher switcher;
    readonly string shortcut;
    readonly int activationTimeoutMs;
    readonly string cableDeviceName;
    readonly CancellationTokenSource cts = new();
    LoopbackDownlink? downlink;
    int running;

    public ExternalSessionController(WebSocket socket, SemaphoreSlim sendGate, AudioDeviceSwitcher switcher,
        string shortcut, int activationTimeoutMs, string cableDeviceName)
    {
        this.socket = socket;
        this.sendGate = sendGate;
        this.switcher = switcher;
        this.shortcut = shortcut;
        this.activationTimeoutMs = activationTimeoutMs;
        this.cableDeviceName = cableDeviceName;
    }

    public Task QueueAsync(ExternalConversationRequest request)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(request); }
            catch (Exception ex)
            {
                Console.WriteLine("External conversation error: " + ex.Message);
                try { await SendJson(new { type = "external_error", reason = ex.Message }); } catch { }
                switcher.RestoreNow();
            }
            finally { Interlocked.Exchange(ref running, 0); }
        });
        return Task.CompletedTask;
    }

    async Task RunAsync(ExternalConversationRequest request)
    {
        await SendJson(new { type = "external_prepare", source = request.Source });
        if (!switcher.ActivateRemoteMic()) throw new InvalidOperationException("virtual_mic_not_found");
        switcher.BeginActivation();
        await Task.Delay(75, cts.Token);
        ShortcutSender.Send(shortcut);
        if (!await WaitMicAsync(true, activationTimeoutMs)) throw new TimeoutException("codex_mic_timeout");

        await ContextAudioInjector.PlayIntoVirtualCableAsync(request.AudioUrl, cableDeviceName, cts.Token);

        downlink?.Dispose();
        downlink = new LoopbackDownlink(SendBinary);
        await SendJson(new { type = "downlink_start", sampleRate = 16000, channels = 1 });
        downlink.Start();
        await SendJson(new { type = "external_context_sent" });

        await WaitMicAsync(false, 15000);
        await WaitMicAsync(true, 45000);

        downlink?.Dispose();
        downlink = null;
        switcher.MarkListening();
        await SendJson(new { type = "external_conversation_ready" });
    }

    async Task<bool> WaitMicAsync(bool desired, int timeoutMs)
    {
        var started = Environment.TickCount64;
        while (!cts.IsCancellationRequested && Environment.TickCount64 - started < timeoutMs)
        {
            if (CodexMicDetector.IsActive() == desired) return true;
            await Task.Delay(80, cts.Token);
        }
        return CodexMicDetector.IsActive() == desired;
    }

    async Task SendJson(object payload)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await sendGate.WaitAsync(cts.Token);
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token); }
        finally { sendGate.Release(); }
    }

    async Task SendBinary(byte[] bytes)
    {
        if (socket.State != WebSocketState.Open || bytes.Length == 0) return;
        await sendGate.WaitAsync(cts.Token);
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cts.Token); }
        finally { sendGate.Release(); }
    }

    public void Dispose()
    {
        cts.Cancel();
        downlink?.Dispose();
        downlink = null;
        cts.Dispose();
    }
}
