using NAudio.Wave;

// Thin prebuffer wrapper around NAudio's BufferedWaveProvider.
// It stages the style bootstrap plus the first ~200 ms of live microphone audio,
// then releases both in order to the virtual microphone. After startup it is transparent.
// No diagnostic audio is written to disk.
sealed class BufferedWaveProvider : IWaveProvider
{
    readonly NAudio.Wave.BufferedWaveProvider inner;
    readonly MemoryStream startupBuffer = new();
    readonly object sync = new();
    readonly int startupBytes;
    readonly TimeSpan minimumBufferDuration;
    bool started;

    public BufferedWaveProvider(WaveFormat waveFormat)
    {
        inner = new NAudio.Wave.BufferedWaveProvider(waveFormat);

        var stylePrompt = VoiceStylePromptPcm.Create(waveFormat);
        if (stylePrompt.Length > 0)
            startupBuffer.Write(stylePrompt, 0, stylePrompt.Length);

        var livePrebufferBytes = Math.Max(waveFormat.BlockAlign, waveFormat.AverageBytesPerSecond / 5); // ~200 ms
        startupBytes = stylePrompt.Length + livePrebufferBytes;

        // The user's live audio arrives while the style prompt is being consumed in real time.
        // Keep enough queue capacity for that intentional lead without changing playback latency.
        var styleMs = stylePrompt.Length * 1000.0 / Math.Max(1, waveFormat.AverageBytesPerSecond);
        minimumBufferDuration = stylePrompt.Length > 0
            ? TimeSpan.FromMilliseconds(Math.Max(5000, styleMs + 2000))
            : TimeSpan.Zero;
    }

    public WaveFormat WaveFormat => inner.WaveFormat;

    public TimeSpan BufferDuration
    {
        get => inner.BufferDuration;
        set => inner.BufferDuration = minimumBufferDuration > value ? minimumBufferDuration : value;
    }

    public bool DiscardOnBufferOverflow
    {
        get => inner.DiscardOnBufferOverflow;
        set => inner.DiscardOnBufferOverflow = value;
    }

    public bool ReadFully
    {
        get => inner.ReadFully;
        set => inner.ReadFully = value;
    }

    public int BufferedBytes => inner.BufferedBytes;
    public TimeSpan BufferedDuration => inner.BufferedDuration;

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        lock (sync)
        {
            if (started)
            {
                inner.AddSamples(buffer, offset, count);
                return;
            }

            startupBuffer.Write(buffer, offset, count);
            if (startupBuffer.Length < startupBytes) return;

            var staged = startupBuffer.ToArray();
            startupBuffer.SetLength(0);
            inner.AddSamples(staged, 0, staged.Length);
            started = true;
            Console.WriteLine($"Audio startup prebuffer released: {staged.Length * 1000.0 / WaveFormat.AverageBytesPerSecond:F0} ms");
        }
    }

    public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
}
