using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;

/// <summary>
/// Best-effort secondary audio mirrors for Realtime. Android remains the primary output and is
/// never blocked by this class. Windows/Bluetooth and Home Assistant mirrors each use independent
/// bounded queues so a slow/offline secondary sink drops its own audio instead of delaying Android.
/// </summary>
internal sealed class RealtimeSecondaryAudioMirror : IDisposable
{
    static readonly ConcurrentDictionary<string, LiveMp3Stream> LiveStreams = new(StringComparer.Ordinal);

    readonly object sync = new();
    CancellationTokenSource? cts;
    Channel<byte[]>? windowsPcm;
    Channel<byte[]>? haPcm;
    Task? windowsTask;
    Task? haTask;
    LiveMp3Stream? liveStream;
    bool disposed;

    public async Task StartAsync(string newSessionId, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        if (disposed) return;

        var mirrorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts = mirrorCts;

        if (RealtimeMirrorSettings.WindowsMirrorEnabled && !string.IsNullOrWhiteSpace(DownlinkDeviceSettings.SelectedDeviceId))
        {
            windowsPcm = CreatePcmQueue();
            var reader = windowsPcm.Reader;
            windowsTask = Task.Run(() => RunWindowsMirrorAsync(reader, mirrorCts.Token), mirrorCts.Token);
        }

        if (RealtimeMirrorSettings.HomeAssistantMirrorEnabled &&
            RealtimeMirrorSettings.HasHomeAssistantAccessToken &&
            !string.IsNullOrWhiteSpace(RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity))
        {
            haPcm = CreatePcmQueue();
            liveStream = new LiveMp3Stream();
            LiveStreams[liveStream.Token] = liveStream;
            var reader = haPcm.Reader;
            var stream = liveStream;
            haTask = Task.Run(() => RunHomeAssistantEncoderAsync(reader, stream, mirrorCts.Token), mirrorCts.Token);

            var ip = ResolveLanIPv4();
            var streamUrl = $"http://{ip}:{AppSettings.HomeAssistantApiPort}/api/realtime-mirror.mp3?token={Uri.EscapeDataString(stream.Token)}";
            _ = Task.Run(async () =>
            {
                try
                {
                    await HomeAssistantMediaClient.StartLiveStreamAsync(streamUrl, mirrorCts.Token);
                    Console.WriteLine($"Realtime HA mirror started · {RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity} · {streamUrl}");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine("Realtime HA mirror start failed: " + ex.Message);
                }
            }, mirrorCts.Token);
        }

        Console.WriteLine($"Realtime secondary mirrors · Android=always · Windows/BT={windowsPcm is not null} · HomeAssistant={haPcm is not null} · session={newSessionId}");
    }

    public void PushPcm16k(byte[] pcm)
    {
        if (disposed || pcm is null || pcm.Length == 0) return;
        var win = windowsPcm;
        if (win is not null) win.Writer.TryWrite(pcm.ToArray());
        var ha = haPcm;
        if (ha is not null) ha.Writer.TryWrite(pcm.ToArray());
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Channel<byte[]>? win;
        Channel<byte[]>? ha;
        Task? winWorker;
        Task? haWorker;
        LiveMp3Stream? stream;

        lock (sync)
        {
            cancellation = cts;
            cts = null;
            win = windowsPcm;
            windowsPcm = null;
            ha = haPcm;
            haPcm = null;
            winWorker = windowsTask;
            windowsTask = null;
            haWorker = haTask;
            haTask = null;
            stream = liveStream;
            liveStream = null;
        }

        try { win?.Writer.TryComplete(); } catch { }
        try { ha?.Writer.TryComplete(); } catch { }
        try { cancellation?.CancelAfter(800); } catch { }

        try { if (winWorker is not null) await winWorker.WaitAsync(TimeSpan.FromMilliseconds(1000)); } catch { }
        try { if (haWorker is not null) await haWorker.WaitAsync(TimeSpan.FromMilliseconds(1200)); } catch { }

        if (stream is not null)
        {
            LiveStreams.TryRemove(stream.Token, out _);
            stream.Complete();
        }

        try { cancellation?.Cancel(); } catch { }
        cancellation?.Dispose();
        BtcomBluetoothReconnect.DisconnectIfConnectedByCompanion();
    }

    static Channel<byte[]> CreatePcmQueue()
        => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(60)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    static async Task RunWindowsMirrorAsync(ChannelReader<byte[]> reader, CancellationToken token)
    {
        MMDevice? device = null;
        WasapiOut? output = null;
        MediaFoundationResampler? resampler = null;
        try
        {
            await BtcomBluetoothReconnect.EnsureSelectedOutputActiveAsync(token);
            var selectedId = DownlinkDeviceSettings.SelectedDeviceId;
            if (string.IsNullOrWhiteSpace(selectedId)) return;

            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => string.Equals(d.ID, selectedId, StringComparison.Ordinal));
            if (device is null)
            {
                Console.WriteLine("Realtime Windows/BT mirror unavailable: selected output is not Active.");
                return;
            }

            var provider = new NAudio.Wave.BufferedWaveProvider(new WaveFormat(16000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            resampler = new MediaFoundationResampler(provider, device.AudioClient.MixFormat) { ResamplerQuality = 60 };
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 90);
            output.Init(resampler);
            output.Play();
            Console.WriteLine($"Realtime Windows/BT mirror active · {device.FriendlyName}");

            await foreach (var pcm in reader.ReadAllAsync(token))
                if (pcm.Length > 0) provider.AddSamples(pcm, 0, pcm.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("Realtime Windows/BT mirror failed: " + ex.Message); }
        finally
        {
            try { output?.Stop(); } catch { }
            output?.Dispose();
            resampler?.Dispose();
            device?.Dispose();
        }
    }

    static async Task RunHomeAssistantEncoderAsync(ChannelReader<byte[]> reader, LiveMp3Stream stream, CancellationToken token)
    {
        try
        {
            using var sink = new BroadcastWriteStream(stream);
            using var encoder = new LameMP3FileWriter(sink, new WaveFormat(16000, 16, 1), 64);
            await foreach (var pcm in reader.ReadAllAsync(token))
                if (pcm.Length > 0) encoder.Write(pcm, 0, pcm.Length);
            encoder.Flush();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("Realtime HA MP3 encoder failed: " + ex.Message); }
        finally { stream.Complete(); }
    }

    public static async Task<bool> TryServeHomeAssistantStreamAsync(HttpListenerContext context, CancellationToken token)
    {
        var supplied = context.Request.QueryString["token"] ?? "";
        if (string.IsNullOrWhiteSpace(supplied) || !LiveStreams.TryGetValue(supplied, out var stream)) return false;

        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "audio/mpeg";
        response.SendChunked = true;
        response.KeepAlive = false;
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        Console.WriteLine($"Realtime HA mirror client connected · {context.Request.RemoteEndPoint}");

        try
        {
            await foreach (var chunk in stream.Subscribe(token))
            {
                if (chunk.Length == 0) continue;
                await response.OutputStream.WriteAsync(chunk, token);
                await response.OutputStream.FlushAsync(token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("Realtime HA mirror HTTP ended: " + ex.Message); }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
        }
        return true;
    }

    static string ResolveLanIPv4()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(x => x.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .ToArray();
            var privateAddress = candidates.FirstOrDefault(IsPrivateIPv4);
            if (privateAddress is not null) return privateAddress.ToString();
            if (candidates.Length > 0) return candidates[0].ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    static bool IsPrivateIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }

    public void Dispose()
    {
        if (disposed) return;
        StopAsync().GetAwaiter().GetResult();
        disposed = true;
    }

    sealed class LiveMp3Stream
    {
        readonly object gate = new();
        readonly List<Channel<byte[]>> subscribers = new();
        readonly Queue<byte[]> prebuffer = new();
        int prebufferBytes;
        bool completed;
        public string Token { get; } = Guid.NewGuid().ToString("N");

        public void Publish(byte[] bytes)
        {
            if (bytes.Length == 0) return;
            var copy = bytes.ToArray();
            lock (gate)
            {
                if (completed) return;
                if (subscribers.Count == 0)
                {
                    prebuffer.Enqueue(copy);
                    prebufferBytes += copy.Length;
                    while (prebufferBytes > 128 * 1024 && prebuffer.TryDequeue(out var old)) prebufferBytes -= old.Length;
                    return;
                }
                foreach (var sub in subscribers.ToArray()) sub.Writer.TryWrite(copy);
            }
        }

        public async IAsyncEnumerable<byte[]> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            lock (gate)
            {
                foreach (var chunk in prebuffer) channel.Writer.TryWrite(chunk);
                subscribers.Add(channel);
                if (completed) channel.Writer.TryComplete();
            }
            try { await foreach (var chunk in channel.Reader.ReadAllAsync(token)) yield return chunk; }
            finally { lock (gate) subscribers.Remove(channel); }
        }

        public void Complete()
        {
            lock (gate)
            {
                if (completed) return;
                completed = true;
                foreach (var sub in subscribers.ToArray()) sub.Writer.TryComplete();
            }
        }
    }

    sealed class BroadcastWriteStream : Stream
    {
        readonly LiveMp3Stream target;
        bool disposed;
        public BroadcastWriteStream(LiveMp3Stream target) => this.target = target;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !disposed;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (disposed || count <= 0) return;
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            target.Publish(copy);
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
    }
}
