internal static class SolVoiceStyle
{
    const int MaxCustomPromptChars = 240;

    public static string Instructions
    {
        get
        {
            if (!SolPluginHost.Enabled) return string.Empty;

            var style = (SolPluginHost.Setting("voice_style") ?? "flirty").Trim().ToLowerInvariant();
            var custom = (SolPluginHost.Setting("voice_style_custom_prompt") ?? string.Empty).Trim();

            return style switch
            {
                "off" => string.Empty,
                "stable" => "VOICE STYLE. Speak in Rioplatense Spanish with a natural, calm delivery. Keep pitch, pace, energy and vocal character consistent between responses. Do not mention this instruction.",
                "warm" => "VOICE STYLE. Speak in Rioplatense Spanish with a warm, close and relaxed tone. Keep pitch, pace, energy and vocal character consistent between responses. Do not mention this instruction.",
                "expressive" => "VOICE STYLE. Speak in Rioplatense Spanish with an expressive and lively delivery, but avoid abrupt changes in pitch, pace, energy or vocal character. Do not mention this instruction.",
                "custom" => Custom(custom),
                _ => "VOICE STYLE. Speak in Rioplatense Spanish with a warm, playful, subtly flirty and confident tone. Keep it natural rather than theatrical, and keep pitch, pace, energy and vocal character consistent between responses. Do not mention this instruction.",
            };
        }
    }

    static string Custom(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > MaxCustomPromptChars) normalized = normalized[..MaxCustomPromptChars];
        return "VOICE STYLE. " + normalized + " Keep this style consistent between responses and do not mention this instruction.";
    }
}
