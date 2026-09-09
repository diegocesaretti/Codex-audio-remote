$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$mirrorPath = Join-Path $root 'RealtimeSecondaryAudioMirror.cs'
$apiPath = Join-Path $root 'RealtimeHomeAssistantApiServer.cs'
$clientPath = Join-Path $root 'HomeAssistantMediaClient.cs'

$mirror = Get-Content -LiteralPath $mirrorPath -Raw

# Replace the MP3 encoder with a high-bitrate uncompressed WAV/LPCM stream.
# Cast commonly buffers live compressed audio aggressively; 96 kHz stereo LPCM
# increases the byte rate so the receiver reaches its startup buffer much faster.
$start = $mirror.IndexOf('    static async Task RunHomeAssistantEncoderAsync', [StringComparison]::Ordinal)
$end = $mirror.IndexOf('    public static async Task<bool> TryServeHomeAssistantStreamAsync', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt 0) { throw 'HA encoder method markers missing.' }
$newEncoder = @'
    static async Task RunHomeAssistantEncoderAsync(ChannelReader<byte[]> reader, LiveMp3Stream stream, CancellationToken token)
    {
        try
        {
            using var sink = new BroadcastWriteStream(stream);
            var header = BuildStreamingWavHeader();
            sink.Write(header, 0, header.Length);
            Console.WriteLine("Realtime HA WAV stream active · 96kHz stereo PCM16 · low-latency mode");

            await foreach (var data in reader.ReadAllAsync(token))
            {
                if (data.Length == 0) continue;
                var expanded = Expand16kMonoTo96kStereo(data);
                sink.Write(expanded, 0, expanded.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("Realtime HA WAV stream failed: " + ex.Message); }
        finally { stream.Complete(); }
    }

    static byte[] BuildStreamingWavHeader()
    {
        const int sampleRate = 96000;
        const short channels = 2;
        const short bitsPerSample = 16;
        const uint dataSize = 0x7ffffff0;
        var h = new byte[44];
        h[0] = (byte)'R'; h[1] = (byte)'I'; h[2] = (byte)'F'; h[3] = (byte)'F';
        BitConverter.GetBytes(36u + dataSize).CopyTo(h, 4);
        h[8] = (byte)'W'; h[9] = (byte)'A'; h[10] = (byte)'V'; h[11] = (byte)'E';
        h[12] = (byte)'f'; h[13] = (byte)'m'; h[14] = (byte)'t'; h[15] = (byte)' ';
        BitConverter.GetBytes(16u).CopyTo(h, 16);
        BitConverter.GetBytes((short)1).CopyTo(h, 20);
        BitConverter.GetBytes(channels).CopyTo(h, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(h, 24);
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        BitConverter.GetBytes(byteRate).CopyTo(h, 28);
        BitConverter.GetBytes((short)(channels * bitsPerSample / 8)).CopyTo(h, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(h, 34);
        h[36] = (byte)'d'; h[37] = (byte)'a'; h[38] = (byte)'t'; h[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(h, 40);
        return h;
    }

    static byte[] Expand16kMonoTo96kStereo(byte[] pcm)
    {
        var samples = pcm.Length / 2;
        var output = new byte[samples * 6 * 4];
        var o = 0;
        for (var i = 0; i < samples; i++)
        {
            var lo = pcm[i * 2];
            var hi = pcm[i * 2 + 1];
            for (var r = 0; r < 6; r++)
            {
                output[o++] = lo; output[o++] = hi;
                output[o++] = lo; output[o++] = hi;
            }
        }
        return output;
    }

    static bool HasAudiblePcm16k(byte[] pcm)
    {
        const int threshold = 220;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            if (Math.Abs((int)sample) >= threshold) return true;
        }
        return false;
    }

'@
$mirror = $mirror.Substring(0, $start) + $newEncoder + $mirror.Substring($end)

# Voice gate: don't feed pre-response silence into Cast. Start the live receiver
# only when the first audible assistant PCM arrives; after that keep the stream open.
$pushStart = $mirror.IndexOf('    public void PushPcm16k(byte[] pcm)', [StringComparison]::Ordinal)
$pushEnd = $mirror.IndexOf('    void EnsureHomeAssistantPlaybackStarted()', $pushStart, [StringComparison]::Ordinal)
if ($pushStart -lt 0 -or $pushEnd -lt 0) { throw 'PushPcm16k method markers missing.' }
$newPush = @'
    public void PushPcm16k(byte[] pcm)
    {
        if (RealtimeMirrorSettings.HomeAssistantMirrorEnabled && haPcm is null) ArmHomeAssistantMirrorForCurrentSession(); // lazy runtime refresh
        else if (!RealtimeMirrorSettings.HomeAssistantMirrorEnabled && haPcm is not null) DisarmHomeAssistantMirrorForCurrentSession();

        if (disposed || pcm is null || pcm.Length == 0) return;

        var win = windowsPcm;
        if (win is not null) win.Writer.TryWrite(pcm.ToArray());

        var ha = haPcm;
        if (ha is not null)
        {
            var alreadyStarted = Volatile.Read(ref haPlaybackStarted) != 0;
            if (alreadyStarted || HasAudiblePcm16k(pcm))
            {
                ha.Writer.TryWrite(pcm.ToArray());
                if (!alreadyStarted) Console.WriteLine("Realtime HA mirror voice gate opened · first audible assistant PCM");
                EnsureHomeAssistantPlaybackStarted();
            }
        }
    }

'@
$mirror = $mirror.Substring(0, $pushStart) + $newPush + $mirror.Substring($pushEnd)

# Remove the MP3-specific prebuffer delay; WAV header + first PCM chunk are available immediately.
$mirror = $mirror.Replace('                // Let LAME create a small MP3 prebuffer before the Cast receiver connects.' + "`r`n" + '                await Task.Delay(100, localCts.Token);', '                await Task.Delay(20, localCts.Token);')
$mirror = $mirror.Replace('                // Let LAME create a small MP3 prebuffer before the Cast receiver connects.' + "`n" + '                await Task.Delay(100, localCts.Token);', '                await Task.Delay(20, localCts.Token);')

# Serve the stream as WAV and expose a matching URL.
$mirror = $mirror.Replace('response.ContentType = "audio/mpeg";', 'response.ContentType = "audio/wav";')
$mirror = $mirror.Replace('/api/realtime-mirror.mp3?token=', '/api/realtime-mirror.wav?token=')

# Test tone must use the same WAV path as real assistant audio.
$toneStart = $mirror.IndexOf('    static async Task PumpTestToneAsync', [StringComparison]::Ordinal)
$toneEnd = $mirror.IndexOf('    public static async Task<string> ResolveLanIPv4ForHomeAssistantAsync', $toneStart, [StringComparison]::Ordinal)
if ($toneStart -lt 0 -or $toneEnd -lt 0) { throw 'Test tone method markers missing.' }
$newTone = @'
    static async Task PumpTestToneAsync(LiveMp3Stream stream, CancellationToken token)
    {
        using var sink = new BroadcastWriteStream(stream);
        var header = BuildStreamingWavHeader();
        sink.Write(header, 0, header.Length);
        const int sampleRate = 16000;
        const int samplesPerChunk = 320;

        for (int chunk = 0; chunk < 120; chunk++)
        {
            token.ThrowIfCancellationRequested();
            var pcm = new byte[samplesPerChunk * 2];
            var hz = chunk < 60 ? 659.25 : 880.0;
            for (int i = 0; i < samplesPerChunk; i++)
            {
                var global = chunk * samplesPerChunk + i;
                var envelope = Math.Min(1.0, Math.Min(global / 800.0, (120 * samplesPerChunk - global) / 800.0));
                var sample = (short)(Math.Sin(2 * Math.PI * hz * global / sampleRate) * 7000 * Math.Max(0, envelope));
                pcm[i * 2] = (byte)(sample & 0xff);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }
            var expanded = Expand16kMonoTo96kStereo(pcm);
            sink.Write(expanded, 0, expanded.Length);
            await Task.Delay(20, token);
        }
    }

'@
$mirror = $mirror.Substring(0, $toneStart) + $newTone + $mirror.Substring($toneEnd)
Set-Content -LiteralPath $mirrorPath -Value $mirror -Encoding utf8 -NoNewline

$api = Get-Content -LiteralPath $apiPath -Raw
$api = $api.Replace('string.Equals(path, "/api/realtime-mirror.mp3", StringComparison.OrdinalIgnoreCase)', '(string.Equals(path, "/api/realtime-mirror.mp3", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "/api/realtime-mirror.wav", StringComparison.OrdinalIgnoreCase))')
$api = $api.Replace('Mirror endpoint: GET/HEAD /api/realtime-mirror.mp3?token=<ephemeral>', 'Mirror endpoint: GET/HEAD /api/realtime-mirror.wav?token=<ephemeral>')
Set-Content -LiteralPath $apiPath -Value $api -Encoding utf8 -NoNewline

$client = Get-Content -LiteralPath $clientPath -Raw
$client = $client.Replace('media_content_type = "audio/mp3"', 'media_content_type = "audio/wav"')
$client = $client.Replace('media_content_type = "music"', 'media_content_type = "audio/wav"')
$client = $client.Replace('type=audio/mp3', 'type=audio/wav')
$client = $client.Replace('type=music', 'type=audio/wav')
Set-Content -LiteralPath $clientPath -Value $client -Encoding utf8 -NoNewline

$mirrorCheck = Get-Content -LiteralPath $mirrorPath -Raw
$apiCheck = Get-Content -LiteralPath $apiPath -Raw
$clientCheck = Get-Content -LiteralPath $clientPath -Raw
if ($mirrorCheck -notmatch '96kHz stereo PCM16') { throw 'Low-latency WAV encoder missing.' }
if ($mirrorCheck -notmatch 'HasAudiblePcm16k') { throw 'Assistant voice gate missing.' }
if ($mirrorCheck -notmatch 'audio/wav') { throw 'WAV HTTP content type missing.' }
if ($mirrorCheck -notmatch 'realtime-mirror\.wav') { throw 'WAV mirror URL missing.' }
if ($apiCheck -notmatch 'realtime-mirror\.wav') { throw 'WAV mirror API route missing.' }
if ($clientCheck -notmatch 'media_content_type = "audio/wav"') { throw 'HA play_media WAV type missing.' }
Write-Host 'Optimized HA Cast mirror for low latency: audible-start gate + WAV/LPCM 96kHz stereo.'
