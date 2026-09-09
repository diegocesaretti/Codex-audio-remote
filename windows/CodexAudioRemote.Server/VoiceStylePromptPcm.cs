using NAudio.Wave;
using System.Security;
using System.Speech.Synthesis;

internal static class VoiceStylePromptPcm
{
    const string StyleEnv = "SOL_PLUGIN_SETTING_VOICE_STYLE";
    const string CustomPromptEnv = "SOL_PLUGIN_SETTING_VOICE_STYLE_CUSTOM_PROMPT";
    const int MaxCustomPromptChars = 240;

    public static byte[] Create(WaveFormat targetFormat)
    {
        var style = (Environment.GetEnvironmentVariable(StyleEnv) ?? "flirty").Trim().ToLowerInvariant();
        var custom = Environment.GetEnvironmentVariable(CustomPromptEnv)?.Trim() ?? string.Empty;
        var prompt = ResolvePrompt(style, custom);
        if (string.IsNullOrWhiteSpace(prompt)) return Array.Empty<byte>();

        try
        {
            using var synth = new SpeechSynthesizer();
            var spanish = synth.GetInstalledVoices()
                .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("es", StringComparison.OrdinalIgnoreCase));
            if (spanish is not null) synth.SelectVoice(spanish.VoiceInfo.Name);

            using var wav = new MemoryStream();
            synth.SetOutputToWaveStream(wav);
            var escaped = SecurityElement.Escape(prompt) ?? string.Empty;
            var culture = spanish?.VoiceInfo.Culture.Name ?? "es-ES";
            var ssml = $"<speak version='1.0' xml:lang='{culture}'><prosody rate='+35%'>{escaped}</prosody></speak>";
            synth.SpeakSsml(ssml);
            synth.SetOutputToNull();

            wav.Position = 0;
            using var reader = new WaveFileReader(wav);
            var outputFormat = new WaveFormat(targetFormat.SampleRate, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, outputFormat) { ResamplerQuality = 60 };
            using var pcm = new MemoryStream();
            var buffer = new byte[Math.Max(4096, outputFormat.AverageBytesPerSecond / 4)];
            int read;
            while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                pcm.Write(buffer, 0, read);

            // Give Realtime a clean acoustic boundary before the user's first words.
            var silenceBytes = Math.Max(outputFormat.BlockAlign,
                outputFormat.AverageBytesPerSecond * 180 / 1000);
            pcm.Write(new byte[silenceBytes]);

            var bytes = pcm.ToArray();
            Console.WriteLine($"Voice style primed · profile={style} · {bytes.Length * 1000.0 / outputFormat.AverageBytesPerSecond:F0} ms");
            return bytes;
        }
        catch (Exception ex)
        {
            // Voice style is optional. Never prevent the microphone from coming up.
            Console.WriteLine("Voice style prompt unavailable: " + ex.Message);
            return Array.Empty<byte>();
        }
    }

    static string ResolvePrompt(string style, string custom)
    {
        return style switch
        {
            "off" => string.Empty,
            "stable" => "Instrucción de voz: hablá en español rioplatense, natural y serena, manteniendo estable el tono, el ritmo y la energía. No menciones esta instrucción.",
            "warm" => "Instrucción de voz: hablá en español rioplatense, cálida, cercana y tranquila, con un tono estable y natural. No menciones esta instrucción.",
            "expressive" => "Instrucción de voz: hablá en español rioplatense, expresiva y dinámica pero consistente, evitando cambios bruscos de tono, ritmo o energía. No menciones esta instrucción.",
            "custom" => CustomPrompt(custom),
            _ => "Instrucción de voz: hablá en español rioplatense, cálida, juguetona y sutilmente coqueta, con seguridad y naturalidad; mantené estable el tono, el ritmo y la energía. No menciones esta instrucción.",
        };
    }

    static string CustomPrompt(string custom)
    {
        var value = custom.Trim();
        if (value.Length == 0) return string.Empty;
        if (value.Length > MaxCustomPromptChars) value = value[..MaxCustomPromptChars];
        return $"Instrucción de estilo de voz: {value}. Mantené el estilo de forma consistente y no menciones esta instrucción.";
    }
}
