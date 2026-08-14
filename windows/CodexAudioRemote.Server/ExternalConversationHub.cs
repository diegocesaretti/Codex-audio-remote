using System.Net.WebSockets;
using System.Text.Json;

internal sealed record ExternalConversationRequest(string AudioUrl, string Source = "home_assistant");

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
        Console.WriteLine($"External conversation START · source={request.Source}");
        await SendJson(new { type = "activating" });
        await BtcomBluetoothReconnect.EnsureSelectedOutputActiveAsync(cts.Token);
        if (!switcher.ActivateRemoteMic()) throw new InvalidOperationException("virtual_mic_not_found");
        switcher.BeginActivation();
        await Task.Delay(75, cts.Token);
        ShortcutSender.Send(shortcut);
        if (!await WaitMicAsync(true, activationTimeoutMs)) throw new TimeoutException("codex_mic_timeout");

        Console.WriteLine("Injecting HA context to virtual microphone only; Android downlink remains off");
        await ContextAudioInjector.PlayIntoVirtualCableAsync(request.AudioUrl, cableDeviceName, cts.Token);
        Console.WriteLine("HA context injection complete");

        downlink?.Dispose();
        downlink = new LoopbackDownlink(SendBinary, DownlinkDeviceSettings.SelectedDeviceId);
        await SendJson(new { type = "downlink_start", sampleRate = 16000, channels = 1 });
        downlink.Start();

        var becameBusy = await WaitForBusyAsync(12000);
        Console.WriteLine(becameBusy ? "Codex processing external context" : "No explicit busy transition detected; response VAD remains primary");

        // Primary signal: listen to the exact audio being sent to Android. As soon as Codex has
        // spoken and stays quiet for ~0.9 s, its response is over. This is substantially faster
        // and more reliable than waiting for accessibility/microphone state transitions.
        var responseEnded = downlink != null && await downlink.WaitForSpeechThenSilenceAsync(45000, 900, cts.Token);
        bool ready;
        if (responseEnded)
        {
            ready = true;
            Console.WriteLine("HA handoff: response audio ended; opening Android mic");
        }
        else
        {
            Console.WriteLine("HA handoff: response VAD unavailable; using short 4 s readiness fallback");
            ready = await WaitForStableReadyAsync(4000);
        }

        // Allow the Android jitter buffer to drain the final packets before its microphone opens.
        await Task.Delay(450, cts.Token);
        downlink?.Dispose();
        downlink = null;
        switcher.MarkListening();
        ExternalConversationHub.SetSuppressCodexEvents(false);
        await SendJson(new { type = "codex_listening", source = "external_context", readyConfirmed = ready, handoff = responseEnded ? "audio_end" : "4s_fallback" });
        Console.WriteLine("External conversation READY · Android microphone enabled");
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

    async Task<bool> WaitForBusyAsync(int timeoutMs)
    {
        var started = Environment.TickCount64;
        while (!cts.IsCancellationRequested && Environment.TickCount64 - started < timeoutMs)
        {
            var ui = CodexUiStateDetector.Detect();
            if (!CodexMicDetector.IsActive() || ui.Busy) return true;
            await Task.Delay(100, cts.Token);
        }
        return false;
    }

    async Task<bool> WaitForStableReadyAsync(int timeoutMs)
    {
        var started = Environment.TickCount64;
        long readySince = 0;
        const int StableMs = 700;
        while (!cts.IsCancellationRequested && Environment.TickCount64 - started < timeoutMs)
        {
            var mic = CodexMicDetector.IsActive();
            var ui = CodexUiStateDetector.Detect();
            var candidate = mic && !ui.Busy;
            var now = Environment.TickCount64;
            if (candidate)
            {
                if (readySince == 0) readySince = now;
                if (now - readySince >= StableMs) return true;
            }
            else readySince = 0;
            await Task.Delay(100, cts.Token);
        }
        return false;
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
