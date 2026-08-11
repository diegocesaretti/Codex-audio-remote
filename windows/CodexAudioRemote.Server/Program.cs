using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var options = Options.Parse(args);
if (options.ListDevices) { AudioDeviceManager.ListDevices(); return; }

var switcher = new AudioDeviceSwitcher(options.VirtualMicName, options.RestoreDelayMs);
await switcher.TryRecoverAsync();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; switcher.RestoreNow(); Environment.Exit(0); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => switcher.RestoreNow();

Console.WriteLine($"Codex Audio Remote server listening on ws://0.0.0.0:{options.Port}/ws/");
Console.WriteLine($"Virtual microphone: '{options.VirtualMicName}' | cable playback: '{options.VirtualCableInputName}'");
Console.WriteLine("Adaptive audio profile: quality/latency controlled by Android satellite");

using var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{options.Port}/ws/");
listener.Start();

while (true)
{
    var context = await listener.GetContextAsync();
    if (!context.Request.IsWebSocketRequest) { context.Response.StatusCode = 400; context.Response.Close(); continue; }

    _ = Task.Run(async () =>
    {
        AudioCableSink? audioSink = null;
        LoopbackDownlink? downlink = null;
        var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;
            Console.WriteLine($"Client connected: {context.Request.RemoteEndPoint}");
            await SendJson(socket, sendGate, new { type = "hello", server = "CodexAudioRemote" });

            using var cts = new CancellationTokenSource();
            var registryTask = WatchCodexMic(socket, sendGate, switcher, cts.Token);
            var buffer = new byte[32 * 1024];
            long audioBytes = 0;
            int uplinkBytesPerSecond = 32000;

            async Task StopAudioSession(bool notify = true)
            {
                audioSink?.Dispose(); audioSink = null;
                downlink?.Dispose(); downlink = null;
                if (notify) await SendJson(socket, sendGate, new { type = "downlink_stop" });
            }

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    audioBytes += result.Count;
                    audioSink?.Write(buffer, 0, result.Count);
                    if (audioBytes % uplinkBytesPerSecond < result.Count)
                        Console.WriteLine($"Audio uplink injected: {audioBytes / (double)uplinkBytesPerSecond:F1}s");
                    continue;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                Console.WriteLine($"<- {text}");

                switch (type)
                {
                    case "wake":
                        await SendJson(socket, sendGate, new { type = "activating" });
                        if (!switcher.ActivateRemoteMic())
                        {
                            await SendJson(socket, sendGate, new { type = "activation_failed", reason = "virtual_mic_not_found" });
                            break;
                        }
                        switcher.BeginActivation();
                        await Task.Delay(75);
                        ShortcutSender.Send(options.Shortcut);
                        _ = ConfirmActivation(socket, sendGate, switcher, options.ActivationTimeoutMs);
                        break;

                    case "audio_start":
                        audioBytes = 0;
                        await StopAudioSession(false);
                        var sampleRate = GetInt(doc.RootElement, "sampleRate", 16000, 8000, 48000);
                        var chunkMs = GetInt(doc.RootElement, "chunkMs", 50, 10, 120);
                        var quality = GetInt(doc.RootElement, "quality", 80, 0, 100);
                        var latency = GetInt(doc.RootElement, "latency", 55, 0, 100);
                        uplinkBytesPerSecond = sampleRate * 2;
                        audioSink = AudioCableSink.TryCreate(options.VirtualCableInputName, sampleRate, quality, latency);
                        if (audioSink is null)
                        {
                            await SendJson(socket, sendGate, new { type = "audio_error", reason = "cable_input_not_found" });
                            break;
                        }
                        downlink = new LoopbackDownlink(async pcm => await SendBinary(socket, sendGate, pcm));
                        await SendJson(socket, sendGate, new { type = "downlink_start", sampleRate = 16000, channels = 1 });
                        downlink.Start();
                        Console.WriteLine($"Bidirectional session: {sampleRate} Hz, chunk {chunkMs} ms, quality {quality}%, latency {latency}%");
                        break;

                    case "audio_stop":
                        await StopAudioSession();
                        Console.WriteLine("Bidirectional audio session stopped");
                        break;

                    case "end_session":
                        var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : "client";
                        Console.WriteLine($"Ending conversation ({reason})");
                        await SendJson(socket, sendGate, new { type = "session_ending", reason });
                        await StopAudioSession();
                        if (CodexMicDetector.IsActive())
                        {
                            ShortcutSender.Send(options.Shortcut);
                            _ = ForceRestoreAfterEnd(switcher, options.EndSessionRestoreTimeoutMs);
                        }
                        else switcher.ScheduleRestore(force: true);
                        break;
                }
            }

            await StopAudioSession(false);
            cts.Cancel();
            try { await registryTask; } catch (OperationCanceledException) { }
            switcher.ScheduleRestore(force: true);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            Console.WriteLine("Client disconnected");
        }
        catch (Exception ex)
        {
            audioSink?.Dispose(); downlink?.Dispose();
            Console.WriteLine($"Client error: {ex.Message}");
            switcher.ScheduleRestore(force: true);
        }
        finally { sendGate.Dispose(); }
    });
}

static int GetInt(JsonElement root, string name, int fallback, int min, int max)
{
    if (!root.TryGetProperty(name, out var p) || !p.TryGetInt32(out var value)) return fallback;
    return Math.Clamp(value, min, max);
}

async Task ForceRestoreAfterEnd(AudioDeviceSwitcher audioSwitcher, int timeoutMs)
{
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs)
    {
        if (!CodexMicDetector.IsActive()) { audioSwitcher.ScheduleRestore(force: true); return; }
        await Task.Delay(50);
    }
    audioSwitcher.ScheduleRestore(force: true);
}

async Task ConfirmActivation(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, int timeoutMs)
{
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs && socket.State == WebSocketState.Open)
    {
        if (CodexMicDetector.IsActive()) { audioSwitcher.MarkListening(); return; }
        await Task.Delay(50);
    }
    if (socket.State == WebSocketState.Open && !CodexMicDetector.IsActive())
    {
        audioSwitcher.ActivationFailed();
        audioSwitcher.ScheduleRestore(force: true);
        await SendJson(socket, gate, new { type = "activation_failed", reason = "codex_mic_timeout" });
    }
}

async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, CancellationToken token)
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
                await SendJson(socket, gate, new { type = "codex_listening" });
                Console.WriteLine("Codex microphone ACTIVE");
            }
            else
            {
                await SendJson(socket, gate, new { type = "codex_idle" });
                Console.WriteLine("Codex microphone idle");
                if (audioSwitcher.State == AudioSessionState.Listening) audioSwitcher.ScheduleRestore();
            }
        }
        await Task.Delay(100, token);
    }
}

static async Task SendJson(WebSocket socket, SemaphoreSlim gate, object payload)
{
    if (socket.State != WebSocketState.Open) return;
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    await gate.WaitAsync();
    try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
    catch { }
    finally { gate.Release(); }
}

static async Task SendBinary(WebSocket socket, SemaphoreSlim gate, byte[] bytes)
{
    if (socket.State != WebSocketState.Open || bytes.Length == 0) return;
    await gate.WaitAsync();
    try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None); }
    catch { }
    finally { gate.Release(); }
}

sealed record Options(int Port, string Shortcut, int ActivationTimeoutMs, string VirtualMicName, string VirtualCableInputName, int RestoreDelayMs, int EndSessionRestoreTimeoutMs, bool ListDevices)
{
    public static Options Parse(string[] args)
    {
        var port = 8765; var shortcut = "ctrl+q"; var timeout = 6000;
        var virtualMic = "CABLE Output"; var virtualCableInput = "CABLE Input"; var restoreDelay = 400; var endRestoreTimeout = 2500;
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
                case "--end-restore-timeout": int.TryParse(args[++i], out endRestoreTimeout); break;
            }
        }
        return new(port, shortcut, timeout, virtualMic, virtualCableInput, restoreDelay, endRestoreTimeout, listDevices);
    }
}

static class AudioDeviceManager
{
    public static void ListDevices()
    {
        using var e = new MMDeviceEnumerator();
        Console.WriteLine("Capture devices:");
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) Console.WriteLine($"- {d.FriendlyName}\n  {d.ID}");
        Console.WriteLine("\nPlayback devices:");
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) Console.WriteLine($"- {d.FriendlyName}\n  {d.ID}");
    }
    public static MMDevice? FindDevice(DataFlow flow, string namePart)
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
    }
    public static string? GetDefaultCaptureId(Role role)
    {
        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID; } catch { return null; }
    }
}

sealed class AudioCableSink : IDisposable
{
    readonly MMDevice device;
    readonly BufferedWaveProvider source;
    readonly MediaFoundationResampler resampler;
    readonly WasapiOut output;
    bool disposed;

    AudioCableSink(MMDevice device, int sampleRate, int quality, int latency)
    {
        this.device = device;
        quality = Math.Clamp(quality, 0, 100);
        latency = Math.Clamp(latency, 0, 100);
        var bufferMs = 300 + (quality * 3); // reservoir only; does not force this much playout delay
        var wasapiLatencyMs = Math.Clamp(60 - (latency * 40 / 100), 20, 60);
        var resamplerQuality = Math.Clamp(20 + (quality * 40 / 100), 20, 60);
        source = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        var target = device.AudioClient.MixFormat;
        resampler = new MediaFoundationResampler(source, target) { ResamplerQuality = resamplerQuality };
        output = new WasapiOut(device, AudioClientShareMode.Shared, true, wasapiLatencyMs);
        output.Init(resampler); output.Play();
        Console.WriteLine($"Injecting Android audio into: {device.FriendlyName} | {sampleRate} Hz PCM | WASAPI {wasapiLatencyMs} ms | resampler {resamplerQuality} | reservoir {bufferMs} ms");
    }

    public static AudioCableSink? TryCreate(string namePart, int sampleRate, int quality, int latency)
    {
        MMDevice? d = null;
        try { d = AudioDeviceManager.FindDevice(DataFlow.Render, namePart); return d is null ? null : new AudioCableSink(d, sampleRate, quality, latency); }
        catch (Exception ex) { Console.WriteLine($"Could not open cable playback endpoint: {ex.Message}"); d?.Dispose(); return null; }
    }

    public void Write(byte[] data, int offset, int count) { if (!disposed) source.AddSamples(data, offset, count); }
    public void Dispose() { if (disposed) return; disposed = true; try { output.Stop(); } catch { } output.Dispose(); resampler.Dispose(); device.Dispose(); }
}

enum AudioSessionState { Idle, Activating, Listening }

sealed class AudioDeviceSwitcher
{
    readonly string virtualMicName; readonly int restoreDelayMs;
    readonly string recoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-restore.json");
    readonly object sync = new(); CancellationTokenSource? restoreCts; SavedDefaults? saved;
    public bool RemoteMicIsActive { get; private set; }
    public AudioSessionState State { get; private set; } = AudioSessionState.Idle;
    public AudioDeviceSwitcher(string virtualMicName, int restoreDelayMs) { this.virtualMicName = virtualMicName; this.restoreDelayMs = restoreDelayMs; }
    public bool ActivateRemoteMic()
    {
        lock (sync)
        {
            CancelPendingRestore(); if (RemoteMicIsActive) return true;
            using var target = AudioDeviceManager.FindDevice(DataFlow.Capture, virtualMicName);
            if (target is null) { Console.WriteLine($"Virtual microphone '{virtualMicName}' not found. Use --list-devices."); return false; }
            saved = new SavedDefaults(AudioDeviceManager.GetDefaultCaptureId(Role.Console), AudioDeviceManager.GetDefaultCaptureId(Role.Multimedia), AudioDeviceManager.GetDefaultCaptureId(Role.Communications));
            File.WriteAllText(recoveryPath, JsonSerializer.Serialize(saved));
            try
            {
                PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Console); PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Multimedia); PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Communications);
                RemoteMicIsActive = true; Console.WriteLine($"Default capture temporarily switched to: {target.FriendlyName}"); return true;
            }
            catch (Exception ex) { Console.WriteLine($"Failed to switch default microphone: {ex.Message}"); RestoreNow(); return false; }
        }
    }
    public void BeginActivation() { lock (sync) { CancelPendingRestore(); State = AudioSessionState.Activating; } }
    public void MarkListening() { lock (sync) { CancelPendingRestore(); if (RemoteMicIsActive) State = AudioSessionState.Listening; } }
    public void ActivationFailed() { lock (sync) State = AudioSessionState.Idle; }
    public void ScheduleRestore(bool force = false)
    {
        lock (sync)
        {
            if (!RemoteMicIsActive || (State == AudioSessionState.Activating && !force)) return;
            restoreCts?.Cancel(); restoreCts = new CancellationTokenSource(); var token = restoreCts.Token;
            _ = Task.Run(async () => { try { await Task.Delay(restoreDelayMs, token); if (force || !CodexMicDetector.IsActive()) RestoreNow(); } catch (OperationCanceledException) { } });
        }
    }
    public void CancelPendingRestore() { lock (sync) { restoreCts?.Cancel(); restoreCts?.Dispose(); restoreCts = null; } }
    public void RestoreNow()
    {
        lock (sync)
        {
            CancelPendingRestore(); var s = saved ?? LoadRecovery();
            if (s is null) { RemoteMicIsActive = false; State = AudioSessionState.Idle; return; }
            try
            {
                if (!string.IsNullOrWhiteSpace(s.Console)) PolicyConfig.SetDefaultEndpoint(s.Console, PolicyRole.Console);
                if (!string.IsNullOrWhiteSpace(s.Multimedia)) PolicyConfig.SetDefaultEndpoint(s.Multimedia, PolicyRole.Multimedia);
                if (!string.IsNullOrWhiteSpace(s.Communications)) PolicyConfig.SetDefaultEndpoint(s.Communications, PolicyRole.Communications);
                Console.WriteLine("Original default microphone(s) restored"); if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
                saved = null; RemoteMicIsActive = false; State = AudioSessionState.Idle;
            }
            catch (Exception ex) { Console.WriteLine($"WARNING: could not restore original microphone: {ex.Message}"); }
        }
    }
    public async Task TryRecoverAsync() { if (!File.Exists(recoveryPath)) return; Console.WriteLine("Recovering previous audio defaults..."); await Task.Delay(50); RestoreNow(); }
    SavedDefaults? LoadRecovery() { try { return File.Exists(recoveryPath) ? JsonSerializer.Deserialize<SavedDefaults>(File.ReadAllText(recoveryPath)) : null; } catch { return null; } }
    sealed record SavedDefaults(string? Console, string? Multimedia, string? Communications);
}

enum PolicyRole { Console = 0, Multimedia = 1, Communications = 2 }
static class PolicyConfig
{
    public static void SetDefaultEndpoint(string deviceId, PolicyRole role)
    {
        var client = (IPolicyConfig)new PolicyConfigClient(); var hr = client.SetDefaultEndpoint(deviceId, role); Marshal.ReleaseComObject(client); if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(string deviceId, IntPtr format); int GetDeviceFormat(string deviceId, int defaultFormat, IntPtr format); int ResetDeviceFormat(string deviceId);
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat); int GetProcessingPeriod(string deviceId, int defaultPeriod, IntPtr defaultPeriodPtr, IntPtr minimumPeriodPtr);
        int SetProcessingPeriod(string deviceId, IntPtr period); int GetShareMode(string deviceId, IntPtr mode); int SetShareMode(string deviceId, IntPtr mode);
        int GetPropertyValue(string deviceId, IntPtr key, IntPtr value); int SetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PolicyRole role); int SetEndpointVisibility(string deviceId, int visible);
    }
}

static class CodexMicDetector
{
    const string BasePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    public static bool IsActive()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(BasePath); if (root is null) return false;
            var name = root.GetSubKeyNames().FirstOrDefault(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)); if (name is null) return false;
            using var key = root.OpenSubKey(name); if (key is null) return false;
            var start = ToInt64(key.GetValue("LastUsedTimeStart")); var stop = ToInt64(key.GetValue("LastUsedTimeStop")); return start > 0 && stop == 0;
        }
        catch { return false; }
    }
    static long ToInt64(object? v) => v switch { long l => l, int i => i, _ => 0 };
}

static class ShortcutSender
{
    const uint KEYEVENTF_KEYUP = 0x0002; const byte VK_CONTROL = 0x11;
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public static void Send(string shortcut)
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero); keybd_event((byte)'Q', 0, 0, UIntPtr.Zero); keybd_event((byte)'Q', 0, KEYEVENTF_KEYUP, UIntPtr.Zero); keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Console.WriteLine("Sent Ctrl+Q");
    }
}
