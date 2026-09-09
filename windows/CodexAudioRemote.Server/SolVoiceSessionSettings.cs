using System.Globalization;
using System.Text;

internal static class SolVoiceSessionSettings
{
    public const int DefaultListenTimeoutSeconds = 16;
    public const int DefaultSilenceTimeoutSeconds = 8;
    public const int DefaultWorkTimeoutSeconds = 30;
    public const int DefaultSessionTimeoutSeconds = 40;
    public const string DefaultEndPhrases = "chau, gracias";

    public static int ListenTimeoutSeconds
        => ReadInt("listen_timeout_seconds", DefaultListenTimeoutSeconds, 1, 600);

    public static int SilenceTimeoutSeconds
        => ReadInt("silence_timeout_seconds", DefaultSilenceTimeoutSeconds, 0, 600);

    public static int WorkTimeoutSeconds
        => ReadInt("work_timeout_seconds", DefaultWorkTimeoutSeconds, 1, 3600);

    public static int SessionTimeoutSeconds
        => ReadInt("session_timeout_seconds", DefaultSessionTimeoutSeconds, 1, 3600);

    public static IReadOnlyList<string> EndPhrases
    {
        get
        {
            var raw = SolPluginHost.Setting("end_phrases") ?? DefaultEndPhrases;
            return raw
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public static string? MatchEndPhrase(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return null;
        foreach (var phrase in EndPhrases)
        {
            if (normalized.Equals(phrase, StringComparison.Ordinal)
                || normalized.EndsWith(" " + phrase, StringComparison.Ordinal))
                return phrase;
        }
        return null;
    }

    public static string Summary()
        => $"listen={ListenTimeoutSeconds}s; silence={SilenceTimeoutSeconds}s; work={WorkTimeoutSeconds}s; session={SessionTimeoutSeconds}s; end-phrases=[{string.Join(", ", EndPhrases)}]";

    static int ReadInt(string key, int fallback, int min, int max)
    {
        var configured = SolPluginHost.IntSetting(key);
        return Math.Clamp(configured ?? fallback, min, max);
    }

    static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var formD = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        var pendingSpace = false;
        foreach (var c in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(c);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString().Trim();
    }
}
