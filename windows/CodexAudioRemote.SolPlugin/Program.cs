var options = Options.Parse(args);

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
await realtimeServer.RunAsync();

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
