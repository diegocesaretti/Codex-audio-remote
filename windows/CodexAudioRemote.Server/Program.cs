var options = Options.Parse(args);
if (options.ListDevices)
{
    AudioDeviceManager.ListDevices();
    return;
}

using var homeAssistantCache = new HomeAssistantWebSocketCache();
homeAssistantCache.Start();

if (TrayController.VoiceBackend == TrayController.RealtimeV3Backend)
{
    using var realtimeServer = new RealtimeSessionServer(options);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        homeAssistantCache.Dispose();
        realtimeServer.Dispose();
        Environment.Exit(0);
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        homeAssistantCache.Dispose();
        realtimeServer.Dispose();
    };

    Console.WriteLine("Codex Audio Remote · experimental Realtime V3 + HA fast-path");
    Console.WriteLine("Auth: existing Codex ChatGPT OAuth login");
    Console.WriteLine("HA context: persistent WebSocket cache -> realtime initialItems");
    await realtimeServer.RunAsync();
    return;
}

var switcher = new AudioDeviceSwitcher(options.VirtualMicName);
await switcher.TryRecoverAsync();

using var server = new SessionServerV2(options, switcher);
using var homeAssistantApi = new HomeAssistantApiServer(server);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    homeAssistantCache.Dispose();
    homeAssistantApi.Dispose();
    server.Dispose();
    Environment.Exit(0);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    homeAssistantCache.Dispose();
    homeAssistantApi.Dispose();
    server.Dispose();
};

Console.WriteLine("Codex Audio Remote · state-machine v2");
Console.WriteLine($"Virtual mic: {options.VirtualMicName} · cable input: {options.VirtualCableInputName}");

try { homeAssistantApi.Start(); }
catch (Exception ex) { Console.WriteLine("Home Assistant REST adapter could not start: " + ex.Message); }

await server.RunAsync();