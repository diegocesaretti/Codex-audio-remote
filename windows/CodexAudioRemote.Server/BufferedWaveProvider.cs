using NAudio.Wave;
using System.Buffers.Binary;
using System.IO;

// Transparent wrapper around NAudio's BufferedWaveProvider.
// It records exactly the PCM received from Android and only stages the first ~200 ms
// before releasing it to NAudio. Reads remain completely untouched.
sealed class BufferedWaveProvider : IWaveProvider
{
    readonly NAudio.Wave.BufferedWaveProvider inner;
    readonly DiagnosticWavRecorder recorder;
    readonly object writeGate = new();
    readonly MemoryStream startupBuffer = new();
    readonly int prebufferBytes;
    bool released;

    public BufferedWaveProvider(WaveFormat waveFormat)
    {
        inner = new NAudio.Wave.BufferedWaveProvider(waveFormat);
        recorder = new DiagnosticWavRecorder(waveFormat);
        prebufferBytes = Math.Max(waveFormat.BlockAlign, waveFormat.AverageBytesPerSecond * 200 / 1000);
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
        recorder.Write(buffer, offset, count);
        lock (writeGate)
        {
            if (released)
            {
                inner.AddSamples(buffer, offset, count);
                return;
            }

            startupBuffer.Write(buffer, offset, count);
            if (startupBuffer.Length < prebufferBytes) return;

            var staged = startupBuffer.ToArray();
            startupBuffer.SetLength(0);
            released = true;
            inner.AddSamples(staged, 0, staged.Length);
            Console.WriteLine($"Audio startup prebuffer RELEASED · {staged.Length * 1000.0 / WaveFormat.AverageBytesPerSecond:F0} ms queued");
        }
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
