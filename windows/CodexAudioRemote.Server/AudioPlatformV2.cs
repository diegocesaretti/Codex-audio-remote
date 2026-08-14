using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed record Options(
    int Port,
    string Shortcut,
    int ActivationTimeoutMs,
    string VirtualMicName,
    string VirtualCableInputName,
    int EndSessionRestoreTimeoutMs,
    bool ListDevices)
{
    public static Options Parse(string[] args)
    {
        var port = 8765;
        var shortcut = "ctrl+q";
        var activationTimeout = 6000;
        var virtualMic = "CABLE Output";
        var virtualCableInput = "CABLE Input";
        var endRestoreTimeout = 2500;
        var listDevices = args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) break;
            switch (args[i])
            {
                case "--port": int.TryParse(args[++i], out port); break;
                case "--shortcut": shortcut = args[++i]; break;
                case "--activation-timeout": int.TryParse(args[++i], out activationTimeout); break;
                case "--virtual-mic": virtualMic = args[++i]; break;
                case "--cable-input": virtualCableInput = args[++i]; break;
                case "--end-restore-timeout": int.TryParse(args[++i], out endRestoreTimeout); break;
            }
        }

        return new Options(port, shortcut, activationTimeout, virtualMic, virtualCableInput, endRestoreTimeout, listDevices);
    }
}

internal static class AudioDeviceManager
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
            .FirstOrDefault(device => device.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
    }

    public static MMDevice? FindActiveById(DataFlow flow, string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(device => string.Equals(device.ID, id, StringComparison.Ordinal));
    }

    public static string? GetDefaultCaptureId(Role role)
    {
        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID; }
        catch { return null; }
    }

    public static string? GetDefaultRenderId(Role role)
    {
        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Render, role).ID; }
        catch { return null; }
    }
}

internal sealed class AudioCableSink : IDisposable
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

        var bufferMs = 300 + quality * 3;
        var wasapiLatencyMs = Math.Clamp(75 - latency * 40 / 100, 35, 75);
        source = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        output = new WasapiOut(device, AudioClientShareMode.Shared, true, wasapiLatencyMs);
        output.Init(source);
        output.Play();
        Console.WriteLine($"Virtual mic injection ready · {device.FriendlyName} · {sampleRate} Hz · WASAPI {wasapiLatencyMs} ms");
    }

    public static AudioCableSink? TryCreate(string namePart, int sampleRate, int quality, int latency)
    {
        MMDevice? device = null;
        try
        {
            device = AudioDeviceManager.FindDevice(DataFlow.Render, namePart);
            return device is null ? null : new AudioCableSink(device, sampleRate, quality, latency);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Virtual cable open failed: " + ex.Message);
            device?.Dispose();
            return null;
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (!disposed && count > 0) source.AddSamples(data, offset, count);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { output.Stop(); } catch { }
        output.Dispose();
        device.Dispose();
    }
}

internal sealed class AudioDeviceSwitcher
{
    readonly string virtualMicName;
    readonly object sync = new();
    readonly string captureRecoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-restore.json");
    readonly string renderRecoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-render-restore.json");

    SavedDefaults? savedCapture;
    SavedDefaults? savedRender;
    bool remoteCaptureActive;
    CancellationTokenSource? renderWatchCts;

    public AudioDeviceSwitcher(string virtualMicName)
    {
        this.virtualMicName = virtualMicName;
    }

    public bool ActivateRemoteMic()
    {
        lock (sync)
        {
            if (!remoteCaptureActive)
            {
                using var target = AudioDeviceManager.FindDevice(DataFlow.Capture, virtualMicName);
                if (target is null)
                {
                    Console.WriteLine($"Virtual microphone '{virtualMicName}' not found. Use --list-devices.");
                    return false;
                }

                savedCapture = CaptureDefaults(DataFlow.Capture);
                File.WriteAllText(captureRecoveryPath, JsonSerializer.Serialize(savedCapture));
                try
                {
                    SetAllRoles(target.ID);
                    remoteCaptureActive = true;
                    Console.WriteLine("Default capture -> " + target.FriendlyName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Capture switch failed: " + ex.Message);
                    RestoreNow();
                    return false;
                }
            }

            if (!TryActivateSelectedRenderLocked()) StartSelectedRenderWatchLocked();
            return true;
        }
    }

    public bool TryActivateSelectedRender()
    {
        lock (sync) return TryActivateSelectedRenderLocked();
    }

    bool TryActivateSelectedRenderLocked()
    {
        var id = DownlinkDeviceSettings.SelectedDeviceId;
        if (string.IsNullOrWhiteSpace(id)) return false;

        using var target = AudioDeviceManager.FindActiveById(DataFlow.Render, id);
        if (target is null) return false;
        if (DownlinkDeviceSettings.IsUnsafe(target.FriendlyName)) return false;

        try
        {
            if (savedRender is null)
            {
                savedRender = CaptureDefaults(DataFlow.Render);
                File.WriteAllText(renderRecoveryPath, JsonSerializer.Serialize(savedRender));
            }
            SetAllRoles(target.ID);
            CancelRenderWatchLocked();
            Console.WriteLine("Default playback -> " + target.FriendlyName);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Playback switch warning: " + ex.Message);
            return false;
        }
    }

    void StartSelectedRenderWatchLocked()
    {
        var selectedId = DownlinkDeviceSettings.SelectedDeviceId;
        if (string.IsNullOrWhiteSpace(selectedId)) return;

        CancelRenderWatchLocked();
        renderWatchCts = new CancellationTokenSource();
        var token = renderWatchCts.Token;
        Console.WriteLine("Selected playback is not active yet; handoff watcher armed.");

        _ = Task.Run(async () =>
        {
            var started = Environment.TickCount64;
            try
            {
                while (!token.IsCancellationRequested && Environment.TickCount64 - started < 12000)
                {
                    lock (sync)
                    {
                        if (!remoteCaptureActive) return;
                        if (TryActivateSelectedRenderLocked())
                        {
                            Console.WriteLine($"Selected playback became active after {Environment.TickCount64 - started} ms.");
                            return;
                        }
                    }
                    await Task.Delay(200, token);
                }
                if (!token.IsCancellationRequested)
                    Console.WriteLine("Selected playback handoff watcher expired; keeping current render device.");
            }
            catch (OperationCanceledException) { }
        });
    }

    void CancelRenderWatchLocked()
    {
        try { renderWatchCts?.Cancel(); } catch { }
        try { renderWatchCts?.Dispose(); } catch { }
        renderWatchCts = null;
    }

    // Compatibility no-ops. Session ownership lives exclusively in SessionServerV2.
    public void BeginActivation() { }
    public void MarkListening() { }
    public void ActivationFailed() { }

    public async Task TryRecoverAsync()
    {
        if (!File.Exists(captureRecoveryPath) && !File.Exists(renderRecoveryPath)) return;
        Console.WriteLine("Recovering Windows audio defaults from previous run...");
        RestoreNow();
        await Task.Delay(80);
    }

    public void RestoreNow()
    {
        lock (sync)
        {
            CancelRenderWatchLocked();
            RestoreDefaults(captureRecoveryPath, ref savedCapture, "capture");

            try { BtcomBluetoothReconnect.DisconnectIfConnectedByCompanion(); }
            catch (Exception ex) { Console.WriteLine("Bluetooth cleanup warning: " + ex.Message); }

            RestoreDefaults(renderRecoveryPath, ref savedRender, "playback");
            remoteCaptureActive = false;
        }
    }

    static SavedDefaults CaptureDefaults(DataFlow flow)
        => flow == DataFlow.Capture
            ? new SavedDefaults(
                AudioDeviceManager.GetDefaultCaptureId(Role.Console),
                AudioDeviceManager.GetDefaultCaptureId(Role.Multimedia),
                AudioDeviceManager.GetDefaultCaptureId(Role.Communications))
            : new SavedDefaults(
                AudioDeviceManager.GetDefaultRenderId(Role.Console),
                AudioDeviceManager.GetDefaultRenderId(Role.Multimedia),
                AudioDeviceManager.GetDefaultRenderId(Role.Communications));

    static void RestoreDefaults(string path, ref SavedDefaults? saved, string label)
    {
        try
        {
            if (saved is null && File.Exists(path))
                saved = JsonSerializer.Deserialize<SavedDefaults>(File.ReadAllText(path));
            if (saved is not null)
            {
                if (!string.IsNullOrWhiteSpace(saved.Console)) PolicyConfig.SetDefaultEndpoint(saved.Console, PolicyRole.Console);
                if (!string.IsNullOrWhiteSpace(saved.Multimedia)) PolicyConfig.SetDefaultEndpoint(saved.Multimedia, PolicyRole.Multimedia);
                if (!string.IsNullOrWhiteSpace(saved.Communications)) PolicyConfig.SetDefaultEndpoint(saved.Communications, PolicyRole.Communications);
                Console.WriteLine("Restored previous default " + label + " devices");
            }
        }
        catch (Exception ex) { Console.WriteLine($"{label} restore warning: {ex.Message}"); }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            saved = null;
        }
    }

    static void SetAllRoles(string deviceId)
    {
        PolicyConfig.SetDefaultEndpoint(deviceId, PolicyRole.Console);
        PolicyConfig.SetDefaultEndpoint(deviceId, PolicyRole.Multimedia);
        PolicyConfig.SetDefaultEndpoint(deviceId, PolicyRole.Communications);
    }
}

internal sealed record SavedDefaults(string? Console, string? Multimedia, string? Communications);

internal static class CodexMicDetector
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
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                var start = ToLong(key.GetValue("LastUsedTimeStart"));
                var stop = ToLong(key.GetValue("LastUsedTimeStop"));
                if (start > 0 && stop == 0) return true;
            }
        }
        catch { }
        return false;
    }

    static long ToLong(object? value) => value switch { long l => l, int i => i, _ => 0 };
}

internal static class ShortcutSender
{
    public static void Send(string shortcut)
    {
        if (!shortcut.Equals("ctrl+q", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only ctrl+q is implemented");

        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x51, 0, 0, UIntPtr.Zero);
        keybd_event(0x51, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

internal static class PolicyConfig
{
    public static void SetDefaultEndpoint(string deviceId, PolicyRole role)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role)); }
        finally { Marshal.ReleaseComObject(policy); }
    }
}

internal enum PolicyRole { Console = 0, Multimedia = 1, Communications = 2 }

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, PolicyRole role);
    [PreserveSig] int SetEndpointVisibility();
}
