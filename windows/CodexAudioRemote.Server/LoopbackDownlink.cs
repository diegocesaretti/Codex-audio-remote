using NAudio.CoreAudioApi;
using NAudio.Wave;

sealed class LoopbackDownlink : IDisposable
{
    const int PacketBytes = 640; // 20 ms @ PCM16 mono 16 kHz
    readonly WasapiLoopbackCapture capture;
    readonly Func<byte[], Task> onPcm;
    readonly object sendSync = new();
    Task sendTail = Task.CompletedTask;
    double sourcePosition;
    bool disposed;

    public LoopbackDownlink(Func<byte[], Task> onPcm)
    {
        this.onPcm = onPcm;
        capture = new WasapiLoopbackCapture();
        capture.DataAvailable += CaptureOnDataAvailable;
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Console.WriteLine($"Downlink stopped: {e.Exception.Message}"); };
    }

    public void Start()
    {
        Console.WriteLine($"PC audio downlink capture: {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, {capture.WaveFormat.Encoding}");
        capture.StartRecording();
    }

    void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (disposed || e.BytesRecorded <= 0) return;
        try
        {
            var pcm = ConvertToMono16k(e.Buffer, e.BytesRecorded, capture.WaveFormat);
            if (pcm.Length == 0) return;
            for (int offset = 0; offset < pcm.Length; offset += PacketBytes)
            {
                int count = Math.Min(PacketBytes, pcm.Length - offset);
                var packet = new byte[count];
                Buffer.BlockCopy(pcm, offset, packet, 0, count);
                QueueSend(packet);
            }
        }
        catch (Exception ex) { Console.WriteLine($"Downlink conversion error: {ex.Message}"); }
    }

    void QueueSend(byte[] packet)
    {
        lock (sendSync)
        {
            sendTail = sendTail.ContinueWith(async _ =>
            {
                if (!disposed) await onPcm(packet);
            }, TaskScheduler.Default).Unwrap();
        }
    }

    byte[] ConvertToMono16k(byte[] data, int count, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int bits = format.BitsPerSample;
        int bytesPerSample = Math.Max(1, bits / 8);
        int frameBytes = bytesPerSample * channels;
        if (frameBytes <= 0) return Array.Empty<byte>();
        int frames = count / frameBytes;
        if (frames <= 0) return Array.Empty<byte>();

        double step = format.SampleRate / 16000.0;
        var output = new List<byte>((int)(frames / step) * 2 + 4);
        double pos = sourcePosition;
        while (pos < frames)
        {
            int frame = (int)pos;
            double sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int offset = frame * frameBytes + ch * bytesPerSample;
                sum += ReadSample(data, offset, format);
            }
            double mono = Math.Clamp(sum / channels, -1.0, 1.0);
            short s = (short)Math.Round(mono * 32767.0);
            output.Add((byte)(s & 0xff)); output.Add((byte)((s >> 8) & 0xff));
            pos += step;
        }
        sourcePosition = pos - frames;
        return output.ToArray();
    }

    static double ReadSample(byte[] data, int offset, WaveFormat format)
    {
        if (format.BitsPerSample == 32) return BitConverter.ToSingle(data, offset);
        if (format.BitsPerSample == 16) return BitConverter.ToInt16(data, offset) / 32768.0;
        if (format.BitsPerSample == 24)
        {
            int v = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xff000000);
            return v / 8388608.0;
        }
        return 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
    }
}
