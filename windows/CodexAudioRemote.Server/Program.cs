using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var options = Options.Parse(args);

if (options.ListDevices)
{
    AudioDeviceManager.ListDevices();
    return;
}

var switcher = new AudioDeviceSwitcher(options.VirtualMicName, options.RestoreDelayMs);
await switcher.TryRecoverAsync();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    switcher.RestoreNow();
    Environment.Exit(0);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => switcher.RestoreNow();

Console.WriteLine($"Codex Audio Remote server listening on ws://0.0.0.0:{options.Port}/ws/");
Console.WriteLine($"Shortcut: {options.Shortcut}; activation timeout: {options.ActivationTimeoutMs} ms");
Console.WriteLine($"Virtual microphone match: '{options.VirtualMicName}'");
Console.WriteLine($"Virtual cable playback match: '{options.VirtualCableInputName}'");

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
        AudioCableSink? audioSink = null;
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;
            Console.WriteLine($"Client connected: {context.Request.RemoteEndPoint}");
            await SendJson(socket, new { type = "hello", server = "CodexAudioRemote" });

            using var cts = new CancellationTokenSource();
            var registryTask = WatchCodexMic(socket, switcher, cts.Token);
            var buffer = new byte[64 * 1024];
            long audioBytes = 0;

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    audioBytes += result.Count;
                    audioSink?.Write(buffer, 0, result.Count);
                    if (audioBytes % (16000 * 2) < result.Count)
                        Console.WriteLine($"Audio uplink injected: {audioBytes / 32000.0:F1}s PCM16 mono 16kHz");
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
                        if (!switcher.ActivateRemoteMic())
                        {
                            await SendJson(socket, new { type = "activation_failed", reason = "virtual_mic_not_found" });
                            break;
                        }
                        switcher.BeginActivation();
                        await Task.Delay(150);
                        ShortcutSender.Send(options.Shortcut);
                        _ = ConfirmActivation(socket, switcher, options.ActivationTimeoutMs);
                        break;

                    case "audio_start":
                        audioBytes = 0;
                        audioSink?.Dispose();
                        audioSink = AudioCableSink.TryCreate(options.VirtualCableInputName);
                        if (audioSink is null)
                        {
                            Console.WriteLine($"Virtual cable playback '{options.VirtualCableInputName}' not found or could not be opened.");
                            await SendJson(socket, new { type = "audio_error", reason = "cable_input_not_found" });
                        }
                        else
                        {
                            Console.WriteLine("Audio stream started -> virtual cable");
                        }
                        break;

                    case "audio_stop":
                        audioSink?.Dispose();
                        audioSink = null;
                        Console.WriteLine("Audio stream stopped");
                        break;
                }
            }

            audioSink?.Dispose();
            cts.Cancel();
            try { await registryTask; } catch (OperationCanceledException) { }
            switcher.ScheduleRestore(force: true);
            if (socket.State != WebSocketState.Closed)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            Console.WriteLine("Client disconnected");
        }
        catch (Exception ex)
        {
            audioSink?.Dispose();
            Console.WriteLine($"Client error: {ex.Message}");
            switcher.ScheduleRestore(force: true);
        }
    });
}

async Task ConfirmActivation(WebSocket socket, AudioDeviceSwitcher audioSwitcher, int timeoutMs)
{
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs && socket.State == WebSocketState.Open)
    {
        if (CodexMicDetector.IsActive())
        {
            audioSwitcher.MarkListening();
            return;
        }
        await Task.Delay(100);
    }

    if (socket.State == WebSocketState.Open && !CodexMicDetector.IsActive())
    {
        audioSwitcher.ActivationFailed();
        audioSwitcher.ScheduleRestore(force: true);
        await SendJson(socket, new { type = "activation_failed", reason = "codex_mic_timeout" });
    }
}

async Task WatchCodexMic(WebSocket socket, AudioDeviceSwitcher audioSwitcher, CancellationToken token)
{
    bool? last = null;
    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var active = CodexMicDetector.IsActive();
        if (active != last)
        {
            last = active;
            if (active)
            {
                audioSwitcher.MarkListening();
                await SendJson(socket, new { type = "codex_listening" });
                Console.WriteLine("Codex microphone ACTIVE");
            }
            else
            {
                await SendJson(socket, new { type = "codex_idle" });
                Console.WriteLine("Codex microphone idle");
                // Never restore while ACTIVATING. Initial idle is expected while Codex opens Voice.
                if (audioSwitcher.State == AudioSessionState.Listening)
                    audioSwitcher.ScheduleRestore();
            }
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

sealed record Options(
    int Port,
    string Shortcut,
    int ActivationTimeoutMs,
    string VirtualMicName,
    string VirtualCableInputName,
    int RestoreDelayMs,
    bool ListDevices)
{
    public static Options Parse(string[] args)
    {
        var port = 8765;
        var shortcut = "ctrl+q";
        var timeout = 6000;
        var virtualMic = "CABLE Output";
        var virtualCableInput = "CABLE Input";
        var restoreDelay = 800;
        var listDevices = args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) break;
            switch (args[i])
            {
                case "--port": int.TryParse(args[++i], out port); break;
                case "--shortcut": shortcut = args[++i]; break;
                case "--activation-timeout": int.TryParse(args[++i], out timeout); break;
                case "--virtual-mic": virtualMic = args[++i]; break;
                case "--cable-input": virtualCableInput = args[++i]; break;
                case "--restore-delay": int.TryParse(args[++i], out restoreDelay); break;
            }
        }
        return new(port, shortcut, timeout, virtualMic, virtualCableInput, restoreDelay, listDevices);
    }
}

static class AudioDeviceManager
{
    public static void ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        Console.WriteLine("Capture devices:");
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            Console.WriteLine($"- {device.FriendlyName}\n  {device.ID}");

        Console.WriteLine("\nPlayback devices:");
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            Console.WriteLine($"- {device.FriendlyName}\n  {device.ID}");
    }

    public static MMDevice? FindDevice(DataFlow flow, string namePart)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
    }

    public static string? GetDefaultCaptureId(Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID;
        }
        catch { return null; }
    }
}

sealed class AudioCableSink : IDisposable
{
    readonly MMDevice device;
    readonly BufferedWaveProvider source;
    readonly MediaFoundationResampler resampler;
    readonly WasapiOut output;
    bool disposed;

    AudioCableSink(MMDevice device)
    {
        this.device = device;
        source = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        var targetFormat = device.AudioClient.MixFormat;
        resampler = new MediaFoundationResampler(source, targetFormat)
        {
            ResamplerQuality = 60
        };

        output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        output.Init(resampler);
        output.Play();
        Console.WriteLine($"Injecting Android audio into: {device.FriendlyName} ({targetFormat.SampleRate} Hz, {targetFormat.Channels} ch)");
    }

    public static AudioCableSink? TryCreate(string namePart)
    {
        MMDevice? device = null;
        try
        {
            device = AudioDeviceManager.FindDevice(DataFlow.Render, namePart);
            return device is null ? null : new AudioCableSink(device);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open cable playback endpoint: {ex.Message}");
            device?.Dispose();
            return null;
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (!disposed) source.AddSamples(data, offset, count);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { output.Stop(); } catch { }
        output.Dispose();
        resampler.Dispose();
        device.Dispose();
    }
}

enum AudioSessionState
{
    Idle,
    Activating,
    Listening
}

sealed class AudioDeviceSwitcher
{
    readonly string virtualMicName;
    readonly int restoreDelayMs;
    readonly string recoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-restore.json");
    readonly object sync = new();
    CancellationTokenSource? restoreCts;
    SavedDefaults? saved;

    public bool RemoteMicIsActive { get; private set; }
    public AudioSessionState State { get; private set; } = AudioSessionState.Idle;

    public AudioDeviceSwitcher(string virtualMicName, int restoreDelayMs)
    {
        this.virtualMicName = virtualMicName;
        this.restoreDelayMs = restoreDelayMs;
    }

    public bool ActivateRemoteMic()
    {
        lock (sync)
        {
            CancelPendingRestore();
            if (RemoteMicIsActive) return true;

            using var target = AudioDeviceManager.FindDevice(DataFlow.Capture, virtualMicName);
            if (target is null)
            {
                Console.WriteLine($"Virtual microphone '{virtualMicName}' not found. Use --list-devices.");
                return false;
            }

            saved = new SavedDefaults(
                AudioDeviceManager.GetDefaultCaptureId(Role.Console),
                AudioDeviceManager.GetDefaultCaptureId(Role.Multimedia),
                AudioDeviceManager.GetDefaultCaptureId(Role.Communications));

            File.WriteAllText(recoveryPath, JsonSerializer.Serialize(saved));

            try
            {
                PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Console);
                PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Multimedia);
                PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Communications);
                RemoteMicIsActive = true;
                Console.WriteLine($"Default capture temporarily switched to: {target.FriendlyName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to switch default microphone: {ex.Message}");
                RestoreNow();
                return false;
            }
        }
    }

    public void BeginActivation()
    {
        lock (sync)
        {
            CancelPendingRestore();
            State = AudioSessionState.Activating;
        }
    }

    public void MarkListening()
    {
        lock (sync)
        {
            CancelPendingRestore();
            if (RemoteMicIsActive) State = AudioSessionState.Listening;
        }
    }

    public void ActivationFailed()
    {
        lock (sync) State = AudioSessionState.Idle;
    }

    public void ScheduleRestore(bool force = false)
    {
        lock (sync)
        {
            if (!RemoteMicIsActive) return;
            if (State == AudioSessionState.Activating && !force) return;

            restoreCts?.Cancel();
            restoreCts = new CancellationTokenSource();
            var token = restoreCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(restoreDelayMs, token);
                    if (force || !CodexMicDetector.IsActive()) RestoreNow();
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    public void CancelPendingRestore()
    {
        lock (sync)
        {
            restoreCts?.Cancel();
            restoreCts?.Dispose();
            restoreCts = null;
        }
    }

    public void RestoreNow()
    {
        lock (sync)
        {
            CancelPendingRestore();
            var state = saved ?? LoadRecovery();
            if (state is null)
            {
                RemoteMicIsActive = false;
                State = AudioSessionState.Idle;
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(state.Console)) PolicyConfig.SetDefaultEndpoint(state.Console, PolicyRole.Console);
                if (!string.IsNullOrWhiteSpace(state.Multimedia)) PolicyConfig.SetDefaultEndpoint(state.Multimedia, PolicyRole.Multimedia);
                if (!string.IsNullOrWhiteSpace(state.Communications)) PolicyConfig.SetDefaultEndpoint(state.Communications, PolicyRole.Communications);
                Console.WriteLine("Original default microphone(s) restored");
                if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
                saved = null;
                RemoteMicIsActive = false;
                State = AudioSessionState.Idle;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: could not restore original microphone: {ex.Message}");
            }
        }
    }

    public async Task TryRecoverAsync()
    {
        if (!File.Exists(recoveryPath)) return;
        Console.WriteLine("Previous session left an audio restore state; recovering defaults...");
        await Task.Delay(50);
        RestoreNow();
    }

    SavedDefaults? LoadRecovery()
    {
        try
        {
            return File.Exists(recoveryPath)
                ? JsonSerializer.Deserialize<SavedDefaults>(File.ReadAllText(recoveryPath))
                : null;
        }
        catch { return null; }
    }

    sealed record SavedDefaults(string? Console, string? Multimedia, string? Communications);
}

enum PolicyRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

static class PolicyConfig
{
    public static void SetDefaultEndpoint(string deviceId, PolicyRole role)
    {
        var client = (IPolicyConfig)new PolicyConfigClient();
        var hr = client.SetDefaultEndpoint(deviceId, role);
        Marshal.ReleaseComObject(client);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(string deviceId, IntPtr format);
        int GetDeviceFormat(string deviceId, int defaultFormat, IntPtr format);
        int ResetDeviceFormat(string deviceId);
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string deviceId, int defaultPeriod, IntPtr defaultPeriodPtr, IntPtr minimumPeriodPtr);
        int SetProcessingPeriod(string deviceId, IntPtr period);
        int GetShareMode(string deviceId, IntPtr mode);
        int SetShareMode(string deviceId, IntPtr mode);
        int GetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        int SetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PolicyRole role);
        int SetEndpointVisibility(string deviceId, int visible);
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
