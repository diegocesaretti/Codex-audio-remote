var options = Options.Parse(args);

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var detail = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "unknown";
    try { Console.Error.WriteLine("FATAL unhandled exception: " + detail); } catch { }
    SolPluginHost.Log("error", "FATAL unhandled exception: " + detail);
    SolPluginHost.Health("unhealthy", "Codex Audio Remote terminated because of an unhandled exception.");
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    var detail = e.Exception.ToString();
    try { Console.Error.WriteLine("Unobserved task exception: " + detail); } catch { }
    SolPluginHost.Log("warn", "Unobserved task exception: " + detail);
    e.SetObserved();
};

using var realtimeServer = new RealtimeSessionServer(options);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    realtimeServer.Dispose();
    Environment.Exit(0);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => realtimeServer.Dispose();

Console.WriteLine("Codex Audio Remote · SOL native Realtime V3");
Console.WriteLine("Audio path: Android PCM ↔ Codex WebRTC · native Realtime-only runtime");
SolPluginHost.Ready("realtime-v3");
try
{
    await realtimeServer.RunAsync();
}
catch (OperationCanceledException)
{
    // Normal during controlled shutdown.
}
catch (Exception ex)
{
    Console.Error.WriteLine("Codex Audio Remote fatal runtime error: " + ex);
    SolPluginHost.Log("error", "Codex Audio Remote fatal runtime error: " + ex);
    SolPluginHost.Health("unhealthy", "Native Realtime server stopped unexpectedly: " + ex.Message);
    Environment.ExitCode = 1;
}

internal sealed record Options(int Port)
{
    public static Options Parse(string[] args)
    {
        var port = 8765;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[++i], out var parsed))
                port = Math.Clamp(parsed, 1024, 65535);
        }
        return new Options(port);
    }
}
