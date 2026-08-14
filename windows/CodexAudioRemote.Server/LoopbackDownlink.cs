using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;

sealed class LoopbackDownlink : IDisposable
{
    const int PacketBytes = 640;          // exactly 20 ms @ PCM16 mono 16 kHz
    const int PacketMs = 20;
    const int PrebufferPackets = 8;       // ~160 ms jitter cushion
    const int MaxQueuedPackets = 75;      // 1.5 s hard ceiling

    readonly WasapiLoopbackCapture capture;
    readonly MMDevice captureDevice;
    readonly Func<byte[], Task> onPcm;
    readonly ConcurrentQueue<byte[]> sendQueue = new();
    readonly SemaphoreSlim queueSignal = new(0);
    readonly CancellationTokenSource sendCts = new();
    readonly byte[] packetAssembly = new byte[PacketBytes];
    Task? senderTask;
    double sourcePosition;
    int packetAssemblyCount;
    bool disposed;
    long capturedPackets;
    long sentPackets;
    long underruns;
    long droppedPackets;
    long lastCaptureTicks;
    long lastSpeechTicks;
    int speechSeen;
    const double SpeechRmsThreshold = 420.0;

    public LoopbackDownlink(Func<byte[], Task> onPcm, string? preferredDeviceId = null)
    {
        this.onPcm = onPcm;
        captureDevice = ResolveSafeRenderDevice(preferredDeviceId);
        capture = new WasapiLoopbackCapture(captureDevice);
        capture.DataAvailable += CaptureOnDataAvailable;
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Console.WriteLine($"Downlink stopped: {e.Exception.Message}"); };
    }

    static MMDevice ResolveSafeRenderDevice(string? preferredDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var active = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();

        bool IsUnsafe(MMDevice d) =>
            d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var selected = active.FirstOrDefault(d => string.Equals(d.ID, preferredDeviceId, StringComparison.Ordinal));
            if (selected is not null && !IsUnsafe(selected))
            {
                Console.WriteLine($"Downlink device selected: {selected.FriendlyName}");
                return selected;
            }
            if (selected is not null)
                Console.WriteLine($"Downlink safety: refusing virtual cable device '{selected.FriendlyName}'");
            else
                Console.WriteLine("Downlink selected device is no longer available; choosing a safe output.");
        }

        try
        {
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var matchingDefault = active.FirstOrDefault(d => string.Equals(d.ID, def.ID, StringComparison.Ordinal));
            def.Dispose();
            if (matchingDefault is not null && !IsUnsafe(matchingDefault))
            {
                Console.WriteLine($"Downlink safe default: {matchingDefault.FriendlyName}");
                return matchingDefault;
            }
        }
        catch { }

        var fallback = active.FirstOrDefault(d => !IsUnsafe(d));
        if (fallback is null)
        {
            foreach (var d in active) d.Dispose();
            throw new InvalidOperationException("No safe render device available for downlink. CABLE/VB-Audio devices are blocked to prevent feedback loops.");
        }

        Console.WriteLine($"Downlink safe fallback: {fallback.FriendlyName}");
        return fallback;
    }

    public void Start()
    {
        Console.WriteLine($"PC audio downlink capture: '{captureDevice.FriendlyName}' · {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, {capture.WaveFormat.Encoding}");
        Console.WriteLine($"Downlink jitter buffer: {PrebufferPackets * PacketMs} ms prebuffer, fixed {PacketMs} ms pacing, complete packets only");
        senderTask = Task.Run(() => SenderLoop(sendCts.Token));
        capture.StartRecording();
    }

    void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (disposed || e.BytesRecorded <= 0) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (lastCaptureTicks != 0)
            {
                double gapMs = (now - lastCaptureTicks) * 1000.0 / Stopwatch.Frequency;
                if (gapMs > 120)
                    Console.WriteLine($"Downlink capture gap: {gapMs:F0} ms · queue={sendQueue.Count}");
            }
            lastCaptureTicks = now;

            var pcm = ConvertToMono16k(e.Buffer, e.BytesRecorded, capture.WaveFormat);
            if (pcm.Length == 0) return;
            AppendPcm(pcm);
        }
        catch (Exception ex) { Console.WriteLine($"Downlink conversion error: {ex.Message}"); }
    }

    void AppendPcm(byte[] pcm)
    {
        int offset = 0;
        while (offset < pcm.Length)
        {
            int copy = Math.Min(PacketBytes - packetAssemblyCount, pcm.Length - offset);
            Buffer.BlockCopy(pcm, offset, packetAssembly, packetAssemblyCount, copy);
            packetAssemblyCount += copy;
            offset += copy;

            if (packetAssemblyCount == PacketBytes)
            {
                var packet = new byte[PacketBytes];
                Buffer.BlockCopy(packetAssembly, 0, packet, 0, PacketBytes);
                packetAssemblyCount = 0;
                EnqueuePacket(packet);
            }
        }
    }

    void EnqueuePacket(byte[] packet)
    {
        while (sendQueue.Count >= MaxQueuedPackets && sendQueue.TryDequeue(out _))
        {
            droppedPackets++;
            if (droppedPackets == 1 || droppedPackets % 25 == 0)
                Console.WriteLine($"Downlink jitter overflow: dropped={droppedPackets} · queue={sendQueue.Count}");
        }
        TrackSpeechActivity(packet);
        sendQueue.Enqueue(packet);
        capturedPackets++;
        queueSignal.Release();
    }

    void TrackSpeechActivity(byte[] packet)
    {
        if (packet.Length < 2) return;
        double sumSquares = 0;
        int samples = packet.Length / 2;
        for (int i = 0; i + 1 < packet.Length; i += 2)
        {
            short sample = (short)(packet[i] | (packet[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }
        var rms = Math.Sqrt(sumSquares / Math.Max(1, samples));
        if (rms >= SpeechRmsThreshold)
        {
            Interlocked.Exchange(ref speechSeen, 1);
            Interlocked.Exchange(ref lastSpeechTicks, Stopwatch.GetTimestamp());
        }
    }

    public async Task<bool> WaitForSpeechThenSilenceAsync(int speechStartTimeoutMs, int silenceMs, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var timeoutTicks = speechStartTimeoutMs * (double)Stopwatch.Frequency / 1000.0;
        while (!token.IsCancellationRequested && Volatile.Read(ref speechSeen) == 0)
        {
            if (Stopwatch.GetTimestamp() - started >= timeoutTicks)
            {
                Console.WriteLine($"Response VAD: no speech detected within {speechStartTimeoutMs} ms");
                return false;
            }
            await Task.Delay(50, token);
        }

        Console.WriteLine("Response VAD: speech detected; waiting for end-of-response silence");
        while (!token.IsCancellationRequested)
        {
            var last = Volatile.Read(ref lastSpeechTicks);
            if (last != 0)
            {
                var quietMs = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
                if (quietMs >= silenceMs)
                {
                    Console.WriteLine($"Response VAD: end detected after {quietMs:F0} ms silence");
                    return true;
                }
            }
            await Task.Delay(50, token);
        }
        return false;
    }

    async Task SenderLoop(CancellationToken token)
    {
        bool primed = false;
        var clock = Stopwatch.StartNew();
        double nextSendMs = 0;
        long lastStatsMs = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!primed)
                {
                    while (sendQueue.Count < PrebufferPackets && !token.IsCancellationRequested)
                        await queueSignal.WaitAsync(250, token);
                    if (token.IsCancellationRequested) break;
                    primed = true;
                    nextSendMs = clock.Elapsed.TotalMilliseconds;
                    Console.WriteLine($"Downlink jitter primed: queue={sendQueue.Count} (~{sendQueue.Count * PacketMs} ms)");
                }

                if (!sendQueue.TryDequeue(out var packet))
                {
                    underruns++;
                    Console.WriteLine($"Downlink jitter UNDERRUN #{underruns} · captured={capturedPackets} sent={sentPackets}");
                    primed = false;
                    continue;
                }

                double waitMs = nextSendMs - clock.Elapsed.TotalMilliseconds;
                if (waitMs > 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), token);

                if (!disposed)
                {
                    await onPcm(packet);
                    sentPackets++;
                }
                nextSendMs += PacketMs;

                long elapsedMs = clock.ElapsedMilliseconds;
                if (elapsedMs - lastStatsMs >= 5000)
                {
                    lastStatsMs = elapsedMs;
                    Console.WriteLine($"Downlink jitter stats: queue={sendQueue.Count} (~{sendQueue.Count * PacketMs} ms) · partial={packetAssemblyCount} bytes · sent={sentPackets} · underruns={underruns} · dropped={droppedPackets}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"Downlink sender error: {ex.Message}"); }
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
                int sampleOffset = frame * frameBytes + ch * bytesPerSample;
                sum += ReadSample(data, sampleOffset, format);
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
        captureDevice.Dispose();
        sendCts.Cancel();
        try { senderTask?.Wait(300); } catch { }
        sendCts.Dispose();
        queueSignal.Dispose();
        Console.WriteLine($"Downlink jitter final: captured={capturedPackets} sent={sentPackets} underruns={underruns} dropped={droppedPackets} partial={packetAssemblyCount}");
    }
}
