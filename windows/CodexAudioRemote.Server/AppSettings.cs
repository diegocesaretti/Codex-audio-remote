using Microsoft.Win32;

internal static class AppSettings
{
    const string AppKey = @"Software\CodexAudioRemote";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "CodexAudioRemote";

    public const string ClassicBackend = "classic";
    public const string RealtimeV3Backend = "realtime-v3";
    public const string DefaultRealtimeModel = "gpt-live-1-codex";
    public const string DefaultHomeAssistantUrl = "http://homeassistant.local:8123";

    public static readonly string[] SupportedRealtimeVoices =
    {
        "alloy", "arbor", "ash", "ballad", "breeze", "cedar", "coral", "cove", "echo",
        "ember", "juniper", "maple", "marin", "sage", "shimmer", "sol", "spruce", "vale", "verse"
    };

    public static string VoiceBackend
    {
        get => ReadString("VoiceBackend") == RealtimeV3Backend ? RealtimeV3Backend : ClassicBackend;
        set => WriteString("VoiceBackend", value == RealtimeV3Backend ? RealtimeV3Backend : ClassicBackend);
    }

    public static string RealtimeVoice
    {
        get
        {
            var value = (ReadString("RealtimeVoice") ?? "sol").Trim().ToLowerInvariant();
            return SupportedRealtimeVoices.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : "sol";
        }
        set
        {
            var normalized = (value ?? "sol").Trim().ToLowerInvariant();
            WriteString("RealtimeVoice", SupportedRealtimeVoices.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : "sol");
        }
    }

    public static string RealtimeWorkingDirectory
    {
        get
        {
            var configured = ReadString("RealtimeWorkingDirectory");
            return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)
                ? configured
                : Environment.CurrentDirectory;
        }
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                WriteString("RealtimeWorkingDirectory", Path.GetFullPath(value));
        }
    }

    public static string HomeAssistantBaseUrl
    {
        get => NormalizeBaseUrl(ReadString("HomeAssistantUrl")) ?? DefaultHomeAssistantUrl;
        set => WriteString("HomeAssistantUrl", NormalizeBaseUrl(value) ?? DefaultHomeAssistantUrl);
    }

    public static bool HomeAssistantEnabled
    {
        get => ReadBool("HomeAssistantEnabled", true);
        set => WriteBool("HomeAssistantEnabled", value);
    }

    public static int HomeAssistantApiPort
    {
        get => ReadInt("HomeAssistantApiPort", 8766, 1024, 65535);
        set => WriteInt("HomeAssistantApiPort", Math.Clamp(value, 1024, 65535));
    }

    public static bool HomeAssistantAutoStartSpeechSession
    {
        get => ReadBool("HomeAssistantAutoStartSpeechSession", true);
        set => WriteBool("HomeAssistantAutoStartSpeechSession", value);
    }

    public static bool HomeAssistantKeepSpeechSessionOpen
    {
        get => ReadBool("HomeAssistantKeepSpeechSessionOpen", true);
        set => WriteBool("HomeAssistantKeepSpeechSessionOpen", value);
    }

    public static bool HomeAssistantRequireSourceMatch
    {
        get => ReadBool("HomeAssistantRequireSourceMatch", true);
        set => WriteBool("HomeAssistantRequireSourceMatch", value);
    }

    public static int WakeRetryCooldownMs
    {
        get => ReadInt("WakeRetryCooldownMs", 3500, 0, 30000);
        set => WriteInt("WakeRetryCooldownMs", Math.Clamp(value, 0, 30000));
    }

    public static string CodexExecutableOverride
    {
        get
        {
            var configured = ReadString("CodexExecutableOverride");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            return Environment.GetEnvironmentVariable("CODEX_EXE") ?? "";
        }
        set
        {
            var normalized = (value ?? "").Trim().Trim('"');
            WriteString("CodexExecutableOverride", normalized);
            Environment.SetEnvironmentVariable("CODEX_EXE", string.IsNullOrWhiteSpace(normalized) ? null : normalized);
        }
    }

    public static bool StartupEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(RunName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (!value)
                {
                    key.DeleteValue(RunName, false);
                    return;
                }
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe)) key.SetValue(RunName, "\"" + exe + "\"");
            }
            catch { }
        }
    }

    public static void ResetDefaults()
    {
        VoiceBackend = RealtimeV3Backend;
        RealtimeVoice = "sol";
        HomeAssistantBaseUrl = DefaultHomeAssistantUrl;
        HomeAssistantEnabled = true;
        HomeAssistantApiPort = 8766;
        HomeAssistantAutoStartSpeechSession = true;
        HomeAssistantKeepSpeechSessionOpen = true;
        HomeAssistantRequireSourceMatch = true;
        WakeRetryCooldownMs = 3500;
        CodexExecutableOverride = "";
    }

    public static string Summary()
        => $"Backend={VoiceBackend}; Voice={RealtimeVoice}; Model={DefaultRealtimeModel}; CWD={RealtimeWorkingDirectory}; " +
           $"HA={HomeAssistantEnabled} {HomeAssistantBaseUrl} API:{HomeAssistantApiPort}; " +
           $"HA auto-start={HomeAssistantAutoStartSpeechSession}; keep-open={HomeAssistantKeepSpeechSessionOpen}; " +
           $"WakeCooldown={WakeRetryCooldownMs}ms; CODEX_EXE={CodexExecutableOverride}";

    static string? ReadString(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey, false);
            return key?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    static void WriteString(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey, true);
            key.SetValue(name, value ?? "", RegistryValueKind.String);
        }
        catch { }
    }

    static bool ReadBool(string name, bool fallback)
    {
        var text = ReadString(name);
        if (bool.TryParse(text, out var parsed)) return parsed;
        return fallback;
    }

    static void WriteBool(string name, bool value) => WriteString(name, value.ToString());

    static int ReadInt(string name, int fallback, int min, int max)
    {
        var text = ReadString(name);
        return int.TryParse(text, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
    }

    static void WriteInt(string name, int value) => WriteString(name, value.ToString());

    public static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
