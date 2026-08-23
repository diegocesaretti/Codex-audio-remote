using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

internal static class RealtimeMirrorSettings
{
    const string AppKey = @"Software\CodexAudioRemote";

    public static bool WindowsMirrorEnabled
    {
        get => ReadBool("RealtimeWindowsMirrorEnabled", false);
        set => WriteBool("RealtimeWindowsMirrorEnabled", value);
    }

    public static bool HomeAssistantMirrorEnabled
    {
        get => ReadBool("RealtimeHomeAssistantMirrorEnabled", false);
        set => WriteBool("RealtimeHomeAssistantMirrorEnabled", value);
    }

    public static bool HomeAssistantMirrorAnnounce
    {
        get => ReadBool("RealtimeHomeAssistantMirrorAnnounce", true);
        set => WriteBool("RealtimeHomeAssistantMirrorAnnounce", value);
    }

    public static string HomeAssistantMediaPlayerEntity
    {
        get => (ReadString("RealtimeHomeAssistantMediaPlayer") ?? "").Trim();
        set => WriteString("RealtimeHomeAssistantMediaPlayer", (value ?? "").Trim());
    }

    public static int ListenSilenceTimeoutSeconds
    {
        get => ReadInt("RealtimeListenSilenceTimeoutSeconds", 12, 0, 600);
        set => WriteInt("RealtimeListenSilenceTimeoutSeconds", Math.Clamp(value, 0, 600));
    }

    public static int ConversationIdleTimeoutSeconds
    {
        get => ReadInt("RealtimeConversationIdleTimeoutSeconds", 90, 0, 3600);
        set => WriteInt("RealtimeConversationIdleTimeoutSeconds", Math.Clamp(value, 0, 3600));
    }

    public static bool HasHomeAssistantAccessToken => !string.IsNullOrWhiteSpace(HomeAssistantAccessToken);

    public static string HomeAssistantAccessToken
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("HOME_ASSISTANT_TOKEN");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            var protectedText = ReadString("HomeAssistantTokenProtected");
            if (string.IsNullOrWhiteSpace(protectedText)) return "";
            try
            {
                var encrypted = Convert.FromBase64String(protectedText);
                var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch
            {
                return "";
            }
        }
        set
        {
            var normalized = (value ?? "").Trim();
            if (normalized.Length == 0)
            {
                DeleteValue("HomeAssistantTokenProtected");
                return;
            }

            try
            {
                var clear = Encoding.UTF8.GetBytes(normalized);
                var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
                WriteString("HomeAssistantTokenProtected", Convert.ToBase64String(encrypted));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("No se pudo proteger el token de Home Assistant con DPAPI.", ex);
            }
        }
    }

    public static void ClearHomeAssistantAccessToken() => DeleteValue("HomeAssistantTokenProtected");

    public static string Summary()
        => $"listen-timeout={ListenSilenceTimeoutSeconds}s; conversation-idle={ConversationIdleTimeoutSeconds}s; " +
           $"windows-mirror={WindowsMirrorEnabled} '{DownlinkDeviceSettings.SelectedDeviceName}'; " +
           $"ha-mirror={HomeAssistantMirrorEnabled} entity={HomeAssistantMediaPlayerEntity}; HA-token={(HasHomeAssistantAccessToken ? "configured" : "missing")}";

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
        using var key = Registry.CurrentUser.CreateSubKey(AppKey, true);
        key.SetValue(name, value ?? "", RegistryValueKind.String);
    }

    static void DeleteValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey, true);
            key.DeleteValue(name, false);
        }
        catch { }
    }

    static bool ReadBool(string name, bool fallback)
    {
        var text = ReadString(name);
        return bool.TryParse(text, out var parsed) ? parsed : fallback;
    }

    static void WriteBool(string name, bool value) => WriteString(name, value.ToString());

    static int ReadInt(string name, int fallback, int min, int max)
    {
        var text = ReadString(name);
        return int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
    }

    static void WriteInt(string name, int value) => WriteString(name, value.ToString());
}
