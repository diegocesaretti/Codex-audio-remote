using System.Text.Json;

internal static class SolPluginHost
{
    public static bool Enabled
        => string.Equals(Environment.GetEnvironmentVariable("SOL_PLUGIN_MODE"), "1", StringComparison.OrdinalIgnoreCase)
           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOL_PLUGIN_ID"));

    public static void Ready(string backend)
    {
        if (!Enabled) return;
        Write(new
        {
            type = "sol.plugin.ready",
            health = "healthy",
            details = new
            {
                backend,
                transport = "official-codex-webrtc-v3",
                model = AppSettings.DefaultRealtimeModel,
                voice = AppSettings.RealtimeVoice,
                homeAssistant = AppSettings.HomeAssistantEnabled,
                pid = Environment.ProcessId,
            },
        });
        Log("info", $"Codex Audio Remote ready under SOL · backend={backend}");
    }

    public static void Health(string health, string message)
    {
        if (!Enabled) return;
        Write(new
        {
            type = "sol.plugin.health",
            health,
            details = new { message },
        });
    }

    public static void Log(string level, string message)
    {
        if (!Enabled) return;
        Write(new
        {
            type = "sol.plugin.log",
            level,
            message,
        });
    }

    static void Write(object value)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(value));
            Console.Out.Flush();
        }
        catch
        {
            // Plugin telemetry must never interfere with the audio runtime.
        }
    }
}
