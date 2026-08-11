using NAudio.Wave;

// Local replacement for NAudio's BufferedWaveProvider. Because this type lives in the
// application's namespace, Program.cs resolves this implementation first while keeping
// the same small API surface it already uses.
sealed class BufferedWaveProvider : IWaveProvider
{
    readonly object sync = new();
    readonly Queue<byte[]> chunks = new();
    readonly int prebufferBytes;
    int headOffset;
    int bufferedBytes;
    bool playoutStarted;
    long packets;
    long underflows;
    long overflows;
    long lastDiagnosticsTick;
    TimeSpan bufferDuration = TimeSpan.FromMilliseconds(600);

    public BufferedWaveProvider(WaveFormat waveFormat)
    {
        WaveFormat = waveFormat;
        prebufferBytes = Math.Max(waveFormat.BlockAlign, waveFormat.AverageBytesPerSecond * 135 / 1000);
    }

    public WaveFormat WaveFormat { get; }
    public bool DiscardOnBufferOverflow { get; set; }
    public bool ReadFully { get; set; }

    public TimeSpan BufferDuration
    {
        get { lock (sync) return bufferDuration; }
        set { lock (sync) bufferDuration = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(600) : value; }
    }

    int MaxBufferBytes
    {
        get
        {
            var bytes = (long)(WaveFormat.AverageBytesPerSecond * BufferDuration.TotalSeconds);
            return (int)Math.Clamp(bytes, prebufferBytes * 2L, int.MaxValue);
        }
    }

    public int BufferedBytes { get { lock (sync) return bufferedBytes; } }
    public TimeSpan BufferedDuration => TimeSpan.FromSeconds(BufferedBytes / (double)WaveFormat.AverageBytesPerSecond);

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);

        lock (sync)
        {
            packets++;
            int max = MaxBufferBytes;
            if (bufferedBytes + count > max)
            {
                overflows++;
                if (!DiscardOnBufferOverflow)
                    throw new InvalidOperationException("Audio jitter buffer overflow");

                // Prefer dropping the oldest queued audio rather than blocking the WebSocket
                // receive thread. This keeps latency bounded if the producer temporarily runs ahead.
                while (bufferedBytes + count > max && chunks.Count > 0)
                    DropOldestChunk();
            }

            chunks.Enqueue(copy);
            bufferedBytes += copy.Length;
            MaybeLogDiagnostics(false);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        lock (sync)
        {
            // Initial prebuffer and rebuffer after an underrun. WASAPI keeps receiving silence,
            // while incoming network packets accumulate until there is enough reserve to resume
            // continuous playout.
            if (!playoutStarted)
            {
                if (bufferedBytes >= prebufferBytes)
                {
                    playoutStarted = true;
                    Console.WriteLine($"Jitter buffer playout started: {BufferedMsUnsafe():F0} ms buffered");
                }
                else
                {
                    Array.Clear(buffer, offset, count);
                    return ReadFully ? count : 0;
                }
            }

            if (bufferedBytes < count)
            {
                underflows++;
                playoutStarted = false;
                Array.Clear(buffer, offset, count);
                MaybeLogDiagnostics(true);
                return ReadFully ? count : 0;
            }

            int remaining = count;
            int dst = offset;
            while (remaining > 0 && chunks.Count > 0)
            {
                var head = chunks.Peek();
                int available = head.Length - headOffset;
                int take = Math.Min(available, remaining);
                Buffer.BlockCopy(head, headOffset, buffer, dst, take);
                headOffset += take;
                dst += take;
                remaining -= take;
                bufferedBytes -= take;

                if (headOffset >= head.Length)
                {
                    chunks.Dequeue();
                    headOffset = 0;
                }
            }

            if (remaining > 0)
            {
                Array.Clear(buffer, dst, remaining);
                underflows++;
                playoutStarted = false;
                MaybeLogDiagnostics(true);
                return ReadFully ? count : count - remaining;
            }

            MaybeLogDiagnostics(false);
            return count;
        }
    }

    void DropOldestChunk()
    {
        if (chunks.Count == 0) return;
        var head = chunks.Dequeue();
        int remaining = head.Length - headOffset;
        bufferedBytes = Math.Max(0, bufferedBytes - remaining);
        headOffset = 0;
    }

    double BufferedMsUnsafe() => bufferedBytes * 1000.0 / WaveFormat.AverageBytesPerSecond;

    void MaybeLogDiagnostics(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - lastDiagnosticsTick < 2000) return;
        lastDiagnosticsTick = now;
        Console.WriteLine($"Jitter buffer: {BufferedMsUnsafe():F0} ms | packets {packets} | underflows {underflows} | overflows {overflows} | {(playoutStarted ? "playing" : "buffering")}");
    }
}
