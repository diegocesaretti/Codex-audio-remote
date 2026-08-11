using NAudio.Wave;

// Thin prebuffer wrapper around NAudio's BufferedWaveProvider.
// It only stages the first ~200 ms before releasing audio to WASAPI.
// After that it is completely transparent. No diagnostic audio is written to disk.
sealed class BufferedWaveProvider : IWaveProvider
{
    readonly NAudio.Wave.BufferedWaveProvider inner;
    readonly MemoryStream startupBuffer = new();
    readonly object sync = new();
    readonly int startupBytes;
    bool started;

    public BufferedWaveProvider(WaveFormat waveFormat)
    {
        inner = new NAudio.Wave.BufferedWaveProvider(waveFormat);
        startupBytes = Math.Max(waveFormat.BlockAlign, waveFormat.AverageBytesPerSecond / 5); // ~200 ms
    }

    public WaveFormat WaveFormat => inner.WaveFormat;

    public TimeSpan BufferDuration
    {
        get => inner.BufferDuration;
        set => inner.BufferDuration = value;
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
