using NAudio.CoreAudioApi;
using NAudio.Wave;

// Records the capture endpoint that Codex sees (normally CABLE Output).
// This is intentionally independent from the Android uplink recorder so we can
// compare before-VB-CABLE vs after-VB-CABLE audio byte-for-byte/by ear.
sealed class CableOutputRecorder : IDisposable
{
    readonly MMDevice device;
    readonly WasapiCapture capture;
    readonly WaveFileWriter writer;
    bool disposed;

    CableOutputRecorder(MMDevice device)
    {
        this.device = device;
        capture = new WasapiCapture(device);

        var dir = Path.Combine(AppContext.BaseDirectory, "recordings");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(dir, $"codex-input-{stamp}-{capture.WaveFormat.SampleRate}Hz-{capture.WaveFormat.Channels}ch.wav");
        writer = new WaveFileWriter(path, capture.WaveFormat);

        capture.DataAvailable += (_, e) =>
        {
            if (!disposed && e.BytesRecorded > 0)
                writer.Write(e.Buffer, 0, e.BytesRecorded);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                Console.WriteLine($"Codex-input recorder stopped: {e.Exception.Message}");
        };

        capture.StartRecording();
        Console.WriteLine($"CODEX INPUT WAV: {path}");
        Console.WriteLine($"Codex capture endpoint: {device.FriendlyName} | {capture.WaveFormat}");
    }

    public static CableOutputRecorder? TryCreate(string namePart)
    {
        MMDevice? d = null;
        try
        {
            d = AudioDeviceManager.FindDevice(DataFlow.Capture, namePart);
            return d is null ? null : new CableOutputRecorder(d);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not record Codex capture endpoint '{namePart}': {ex.Message}");
            d?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
        writer.Dispose();
        device.Dispose();
    }
}
