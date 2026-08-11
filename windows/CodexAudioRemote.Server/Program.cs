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
        CableOutputRecorder? codexInputRecorder = null;
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
                codexInputRecorder?.Dispose(); codexInputRecorder = null;
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
                        codexInputRecorder = CableOutputRecorder.TryCreate(options.VirtualMicName);
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
            codexInputRecorder?.Dispose(); audioSink?.Dispose(); downlink?.Dispose();
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
    readonly WasapiOut output;
    bool disposed;

    AudioCableSink(MMDevice device, int sampleRate, int quality, int latency)
    {
        this.device = device;
        quality = Math.Clamp(quality, 0, 100);
        latency = Math.Clamp(latency, 0, 100);
        var bufferMs = 400 + (quality * 3);
        var wasapiLatencyMs = Math.Clamp(80 - (latency * 45 / 100), 35, 80);
        source = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        var target = device.AudioClient.MixFormat;
        output = new WasapiOut(device, AudioClientShareMode.Shared, true, wasapiLatencyMs);
        output.Init(source);
        output.Play();
        Console.WriteLine($"Injecting Android audio into: {device.FriendlyName} | source {source.WaveFormat} | cable mix {target} | WASAPI shared conversion | {wasapiLatencyMs} ms | reservoir {bufferMs} ms");
    }

    public static AudioCableSink? TryCreate(string namePart, int sampleRate, int quality, int latency)
    {
        MMDevice? d = null;
        try { d = AudioDeviceManager.FindDevice(DataFlow.Render, namePart); return d is null ? null : new AudioCableSink(d, sampleRate, quality, latency); }
        catch (Exception ex) { Console.WriteLine($"Could not open cable playback endpoint: {ex.Message}"); d?.Dispose(); return null; }
    }

    public void Write(byte[] data, int offset, int count) { if (!disposed) source.AddSamples(data, offset, count); }
    public void Dispose() { if (disposed) return; disposed = true; try { output.Stop(); } catch { } output.Dispose(); device.Dispose(); }
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
            if (!RemoteMicIsActive) { State = AudioSessionState.Idle; return; }
            if (!force && State == AudioSessionState.Activating) return;
            CancelPendingRestore(); restoreCts = new CancellationTokenSource(); var token = restoreCts.Token;
            _ = Task.Run(async () => { try { await Task.Delay(restoreDelayMs, token); if (!token.IsCancellationRequested) RestoreNow(); } catch (OperationCanceledException) { } });
        }
    }
    void CancelPendingRestore() { restoreCts?.Cancel(); restoreCts?.Dispose(); restoreCts = null; }
    public void RestoreNow()
    {
        lock (sync)
        {
            CancelPendingRestore();
            try
            {
                if (saved is null && File.Exists(recoveryPath)) saved = JsonSerializer.Deserialize<SavedDefaults>(File.ReadAllText(recoveryPath));
                if (saved is not null)
                {
                    if (!string.IsNullOrWhiteSpace(saved.Console)) PolicyConfig.SetDefaultEndpoint(saved.Console, PolicyRole.Console);
                    if (!string.IsNullOrWhiteSpace(saved.Multimedia)) PolicyConfig.SetDefaultEndpoint(saved.Multimedia, PolicyRole.Multimedia);
                    if (!string.IsNullOrWhiteSpace(saved.Communications)) PolicyConfig.SetDefaultEndpoint(saved.Communications, PolicyRole.Communications);
                }
            }
            catch (Exception ex) { Console.WriteLine($"Restore warning: {ex.Message}"); }
            try { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch { }
            saved = null; RemoteMicIsActive = false; State = AudioSessionState.Idle;
        }
    }
    public async Task TryRecoverAsync() { if (!File.Exists(recoveryPath)) return; Console.WriteLine("Recovering audio defaults from previous run..."); RestoreNow(); await Task.Delay(100); }
}

sealed record SavedDefaults(string? Console, string? Multimedia, string? Communications);

static class CodexMicDetector
{
    const string Base = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    public static bool IsActive()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Base);
            if (root is null) return false;
            foreach (var name in root.GetSubKeyNames().Where(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)))
            {
                using var key = root.OpenSubKey(name); if (key is null) continue;
                var start = ToLong(key.GetValue("LastUsedTimeStart")); var stop = ToLong(key.GetValue("LastUsedTimeStop"));
                if (start > 0 && stop == 0) return true;
            }
        } catch { }
        return false;
    }
    static long ToLong(object? v) => v switch { long l => l, int i => i, _ => 0 };
}

static class ShortcutSender
{
    public static void Send(string shortcut)
    {
        if (!shortcut.Equals("ctrl+q", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Only ctrl+q is implemented");
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x51, 0, 0, UIntPtr.Zero); keybd_event(0x51, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

static class PolicyConfig
{
    public static void SetDefaultEndpoint(string deviceId, PolicyRole role)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role)); }
        finally { Marshal.ReleaseComObject(policy); }
    }
}

enum PolicyRole { Console = 0, Multimedia = 1, Communications = 2 }

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
class PolicyConfigClient { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat(); [PreserveSig] int SetDeviceFormat(); [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod(); [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode(); [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, PolicyRole role);
    [PreserveSig] int SetEndpointVisibility();
}
