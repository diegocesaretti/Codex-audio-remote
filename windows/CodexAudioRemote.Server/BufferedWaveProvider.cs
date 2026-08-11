using NAudio.Wave;
using System.Buffers.Binary;

// Transparent wrapper around NAudio's BufferedWaveProvider.
// It records exactly the PCM received from Android for diagnostics, but does not
// interfere with NAudio reads. Playout prebuffering is handled by AudioCableSink.
sealed class BufferedWaveProvider : IWaveProvider
{
    readonly NAudio.Wave.BufferedWaveProvider inner;
    readonly DiagnosticWavRecorder recorder;

    public BufferedWaveProvider(WaveFormat waveFormat)
    {
        inner = new NAudio.Wave.BufferedWaveProvider(waveFormat);
        recorder = new DiagnosticWavRecorder(waveFormat);
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
        inner.AddSamples(buffer, offset, count);
        recorder.Write(buffer, offset, count);
    }

    public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
}

sealed class DiagnosticWavRecorder
{
    readonly object sync = new();
    readonly FileStream stream;
    readonly WaveFormat format;
    long dataBytes;
    bool failed;

    public DiagnosticWavRecorder(WaveFormat format)
    {
        this.format = format;
        var dir = Path.Combine(AppContext.BaseDirectory, "recordings");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(dir, $"uplink-android-{stamp}-{format.SampleRate}Hz.wav");
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        WriteHeader();
        Console.WriteLine($"DEBUG WAV recording: {path}");
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (failed || count <= 0) return;
        lock (sync)
        {
            try
            {
                stream.Position = 44 + dataBytes;
                stream.Write(buffer, offset, count);
                dataBytes += count;
                UpdateLengths();
                stream.Flush();
            }
            catch (Exception ex)
            {
                failed = true;
                Console.WriteLine($"DEBUG WAV recording stopped: {ex.Message}");
            }
        }
    }

    void WriteHeader()
    {
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h[0..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..8], 36);
        "WAVE"u8.CopyTo(h[8..12]);
        "fmt "u8.CopyTo(h[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..24], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..28], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..32], (uint)format.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..34], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..36], (ushort)format.BitsPerSample);
        "data"u8.CopyTo(h[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..44], 0);
        stream.Write(h);
        stream.Flush();
    }

    void UpdateLengths()
    {
        Span<byte> b = stackalloc byte[4];
        stream.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        stream.Write(b);
        stream.Position = 40;
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Min(uint.MaxValue, dataBytes));
        stream.Write(b);
        stream.Position = 44 + dataBytes;
    }
}
