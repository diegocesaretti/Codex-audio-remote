internal static class SolAudioQualitySettings
{
    public const string DefaultQuality = "high";
    public const int DefaultGainPercent = 100;
    public const int DefaultReconnectGraceSeconds = 4;

    public static string Quality
    {
        get
        {
            var value = (SolPluginHost.Setting("downlink_quality") ?? DefaultQuality).Trim().ToLowerInvariant();
            return value is "compatibility" or "balanced" or "high" ? value : DefaultQuality;
        }
    }

    public static int OutputSampleRate => Quality switch
    {
        "compatibility" => 16000,
        "balanced" => 24000,
        _ => 48000,
    };

    public static int GainPercent
        => Math.Clamp(SolPluginHost.IntSetting("downlink_gain_percent") ?? DefaultGainPercent, 50, 125);

    public static int ReconnectGraceSeconds
        => Math.Clamp(SolPluginHost.IntSetting("transport_reconnect_grace_seconds") ?? DefaultReconnectGraceSeconds, 0, 15);

    public static byte[] ProcessDownlink(byte[] pcm, int sourceRate)
    {
        if (pcm.Length == 0) return pcm;
        var safeSourceRate = Math.Clamp(sourceRate, 8000, 48000);
        var targetRate = OutputSampleRate;
        var output = ResamplePcm16Mono(pcm, safeSourceRate, targetRate);
        ApplyGainPcm16InPlace(output, GainPercent);
        return output;
    }

    public static string Summary()
        => $"quality={Quality}; output={OutputSampleRate}Hz; gain={GainPercent}%; reconnect-grace={ReconnectGraceSeconds}s";

    static byte[] ResamplePcm16Mono(byte[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || input.Length < 4) return input.ToArray();
        var sourceSamples = input.Length / 2;
        var targetSamples = Math.Max(1, (int)Math.Round(sourceSamples * (double)targetRate / sourceRate));
        var output = new byte[targetSamples * 2];

        for (var i = 0; i < targetSamples; i++)
        {
            var src = i * (double)sourceRate / targetRate;
            var left = Math.Min(sourceSamples - 1, (int)src);
            var right = Math.Min(sourceSamples - 1, left + 1);
            var frac = src - left;
            var a = (short)(input[left * 2] | input[left * 2 + 1] << 8);
            var b = (short)(input[right * 2] | input[right * 2 + 1] << 8);
            var sample = (short)Math.Clamp((int)Math.Round(a + (b - a) * frac), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(sample & 0xff);
            output[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return output;
    }

    static void ApplyGainPcm16InPlace(byte[] pcm, int gainPercent)
    {
        if (gainPercent == 100) return;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | pcm[i + 1] << 8);
            var scaled = Math.Clamp(sample * gainPercent / 100, short.MinValue, short.MaxValue);
            pcm[i] = (byte)(scaled & 0xff);
            pcm[i + 1] = (byte)((scaled >> 8) & 0xff);
        }
    }
}
