using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class ContextAudioInjector
{
    public static async Task PlayIntoVirtualCableAsync(string audioUrl, string cableDeviceName, CancellationToken token)
    {
        var resolved = ResolveAudioUrl(audioUrl);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = await http.GetByteArrayAsync(resolved, token);
        var extension = Path.GetExtension(resolved.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        var temp = Path.Combine(Path.GetTempPath(), "codex-context-" + Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(temp, bytes, token);
        try
        {
            await PlayFileAsync(temp, cableDeviceName, token);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    static Uri ResolveAudioUrl(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute;
        var baseUri = new Uri(TrayController.HomeAssistantBaseUrl.TrimEnd('/') + "/");
        return new Uri(baseUri, value.TrimStart('/'));
    }

    static async Task PlayFileAsync(string file, string cableDeviceName, CancellationToken token)
    {
        using var device = AudioDeviceManager.FindDevice(DataFlow.Render, cableDeviceName)
            ?? throw new InvalidOperationException($"Virtual cable playback device '{cableDeviceName}' not found");
        using var reader = new AudioFileReader(file);
        using var resampler = new MediaFoundationResampler(reader, device.AudioClient.MixFormat)
        {
            ResamplerQuality = 60
        };
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        var stopped = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) stopped.TrySetException(e.Exception);
            else stopped.TrySetResult(null);
        };
        output.Init(resampler);
        output.Play();
        using var registration = token.Register(() =>
        {
            try { output.Stop(); } catch { }
            stopped.TrySetCanceled(token);
        });
        await stopped.Task;
    }
}
