using Microsoft.Win32;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var options = Options.Parse(args);
Console.WriteLine($"Codex Audio Remote server listening on ws://0.0.0.0:{options.Port}/ws/");
Console.WriteLine($"Shortcut: {options.Shortcut}; activation timeout: {options.ActivationTimeoutMs} ms");

using var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{options.Port}/ws/");
listener.Start();

while (true)
{
    var context = await listener.GetContextAsync();
    if (!context.Request.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        context.Response.Close();
        continue;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            using var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;
            Console.WriteLine($"Client connected: {context.Request.RemoteEndPoint}");
            await SendJson(socket, new { type = "hello", server = "CodexAudioRemote" });

            using var cts = new CancellationTokenSource();
            var registryTask = WatchCodexMic(socket, cts.Token);
            var buffer = new byte[64 * 1024];
            long audioBytes = 0;

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    audioBytes += result.Count;
                    if (audioBytes % (16000 * 2) < result.Count)
                        Console.WriteLine($"Audio uplink received: {audioBytes / 32000.0:F1}s PCM16 mono 16kHz");
                    continue;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.GetProperty("type").GetString();
                Console.WriteLine($"<- {text}");

                switch (type)
                {
                    case "wake":
                        await SendJson(socket, new { type = "activating" });
                        ShortcutSender.Send(options.Shortcut);
                        _ = ConfirmActivation(socket, options.ActivationTimeoutMs);
                        break;
                    case "audio_start":
                        audioBytes = 0;
                        Console.WriteLine("Audio stream started");
                        break;
                    case "audio_stop":
                        Console.WriteLine("Audio stream stopped");
                        break;
                }
            }

            cts.Cancel();
            try { await registryTask; } catch (OperationCanceledException) { }
            if (socket.State != WebSocketState.Closed)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            Console.WriteLine("Client disconnected");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client error: {ex.Message}");
        }
    });
}

async Task ConfirmActivation(WebSocket socket, int timeoutMs)
{
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs && socket.State == WebSocketState.Open)
    {
        if (CodexMicDetector.IsActive()) return;
        await Task.Delay(100);
    }
    if (socket.State == WebSocketState.Open && !CodexMicDetector.IsActive())
        await SendJson(socket, new { type = "activation_failed" });
}

async Task WatchCodexMic(WebSocket socket, CancellationToken token)
{
    bool? last = null;
    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var active = CodexMicDetector.IsActive();
        if (active != last)
        {
            last = active;
            await SendJson(socket, new { type = active ? "codex_listening" : "codex_idle" });
            Console.WriteLine(active ? "Codex microphone ACTIVE" : "Codex microphone idle");
        }
        await Task.Delay(250, token);
    }
}

static async Task SendJson(WebSocket socket, object payload)
{
    if (socket.State != WebSocketState.Open) return;
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

sealed record Options(int Port, string Shortcut, int ActivationTimeoutMs)
{
    public static Options Parse(string[] args)
    {
        var port = 8765;
        var shortcut = "ctrl+q";
        var timeout = 6000;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--port": int.TryParse(args[++i], out port); break;
                case "--shortcut": shortcut = args[++i]; break;
                case "--activation-timeout": int.TryParse(args[++i], out timeout); break;
            }
        }
        return new(port, shortcut, timeout);
    }
}

static class CodexMicDetector
{
    const string BasePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static bool IsActive()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(BasePath);
            if (root is null) return false;
            var name = root.GetSubKeyNames().FirstOrDefault(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase));
            if (name is null) return false;
            using var key = root.OpenSubKey(name);
            if (key is null) return false;
            var start = ToInt64(key.GetValue("LastUsedTimeStart"));
            var stop = ToInt64(key.GetValue("LastUsedTimeStop"));
            return start > 0 && stop == 0;
        }
        catch { return false; }
    }

    static long ToInt64(object? value) => value switch
    {
        long l => l,
        int i => i,
        _ => 0
    };
}

static class ShortcutSender
{
    const uint KEYEVENTF_KEYUP = 0x0002;
    const byte VK_CONTROL = 0x11;

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void Send(string shortcut)
    {
        var normalized = shortcut.Trim().ToLowerInvariant();
        if (normalized != "ctrl+q")
            Console.WriteLine($"Unknown shortcut '{shortcut}', falling back to Ctrl+Q");
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event((byte)'Q', 0, 0, UIntPtr.Zero);
        keybd_event((byte)'Q', 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Console.WriteLine("Sent Ctrl+Q");
    }
}
