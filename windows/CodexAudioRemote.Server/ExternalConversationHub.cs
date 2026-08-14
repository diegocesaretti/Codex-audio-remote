using System.Net.WebSockets;
using System.Text.Json;

internal sealed record ExternalConversationRequest(string AudioUrl, string Source = "home_assistant", int EstimatedTtsMs = 0);

internal static class ExternalConversationHub
{
    static readonly object Sync = new();
    static Func<ExternalConversationRequest, Task>? handler;
    static int suppressCodexEvents;

    public static bool SuppressCodexEvents => Volatile.Read(ref suppressCodexEvents) != 0;

    public static void SetSuppressCodexEvents(bool value)
        => Interlocked.Exchange(ref suppressCodexEvents, value ? 1 : 0);

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
            ExternalConversationHub.SetSuppressCodexEvents(true);
            try { await RunAsync(request); }
            catch (Exception ex)
            {
                Console.WriteLine("External conversation error: " + ex.Message);
                downlink?.Dispose();
                downlink = null;
                try { await SendJson(new { type = "activation_failed", reason = "external_context_error" }); } catch { }
                switcher.RestoreNow();
            }
            finally
            {
                ExternalConversationHub.SetSuppressCodexEvents(false);
                Interlocked.Exchange(ref running, 0);
            }
        });
        return Task.CompletedTask;
    }

    async Task RunAsync(ExternalConversationRequest request)
    {
        Console.WriteLine($"External conversation START · source={request.Source} · estimatedTtsMs={request.EstimatedTtsMs}");

        // Start Codex first. Its own activation delay is useful time in which we can
        // prepare the virtual microphone instead of blocking before sending the shortcut.
        ShortcutSender.Send(shortcut);
        await SendJson(new { type = "activating" });
        if (!switcher.ActivateRemoteMic()) throw new InvalidOperationException("virtual_mic_not_found");
        switcher.BeginActivation();
        if (!await WaitMicAsync(true, activationTimeoutMs)) throw new TimeoutException("codex_mic_timeout");
        switcher.MarkListening();

        Console.WriteLine("Injecting HA context to virtual microphone only; Android microphone remains off");
        await ContextAudioInjector.PlayIntoVirtualCableAsync(request.AudioUrl, cableDeviceName, cts.Token);
        Console.WriteLine("HA context injection complete; TTS/input playback itself is the utterance-end signal");

        downlink?.Dispose();
        downlink = new LoopbackDownlink(SendBinary);
        await SendJson(new { type = "downlink_start", sampleRate = 16000, channels = 1 });
        downlink.Start();

        // No STT is used to decide when this REST session ends. The known TTS phrase
        // duration only scales watchdogs; the normal completion signal remains Codex
        // returning to its ready/listening state after producing the answer.
        var ttsMs = Math.Max(0, request.EstimatedTtsMs);
        var busyTimeoutMs = ttsMs > 0 ? Math.Clamp(5000 + ttsMs / 2, 5000, 15000) : 15000;
        var responseTimeoutMs = ttsMs > 0 ? Math.Clamp(18000 + ttsMs * 2, 25000, 60000) : 45000;
        Console.WriteLine($"REST watchdogs from TTS estimate · busy={busyTimeoutMs}ms · response={responseTimeoutMs}ms");

        var becameBusy = await WaitMicAsync(false, busyTimeoutMs);
        Console.WriteLine(becameBusy ? "Codex processing external context" : "No inactive transition detected; using TTS-sized readiness watchdog");
        var ready = await WaitMicAsync(true, responseTimeoutMs);
        Console.WriteLine(ready
            ? "Codex response complete; closing REST one-shot conversation"
            : "TTS-sized readiness watchdog expired; closing REST one-shot conversation without enabling Android microphone");

        // Give the downlink a tiny tail window so the last PCM already captured is not cut.
        try { await Task.Delay(150, cts.Token); } catch (OperationCanceledException) { }
        downlink?.Dispose();
        downlink = null;

        await SendJson(new { type = "session_ending", reason = "external_one_shot" });
        if (CodexMicDetector.IsActive())
        {
            ShortcutSender.Send(shortcut);
            await WaitMicAsync(false, Math.Max(activationTimeoutMs, 3000));
        }
        switcher.RestoreNow();
        await SendJson(new { type = "codex_idle" });
        Console.WriteLine("External conversation END · Android microphone was never enabled · no STT end detector used");
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
        ExternalConversationHub.SetSuppressCodexEvents(false);
        cts.Dispose();
    }
}
