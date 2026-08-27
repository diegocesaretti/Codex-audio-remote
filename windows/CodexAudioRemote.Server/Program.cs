var options = Options.Parse(args);
if (options.ListDevices)
{
    AudioDeviceManager.ListDevices();
    return;
}

var switcher = new AudioDeviceSwitcher(options.VirtualMicName);
await switcher.TryRecoverAsync();

using var homeAssistantCache = new HomeAssistantWebSocketCache();
using var server = new SessionServerV2(options, switcher);
using var homeAssistantApi = new HomeAssistantApiServer(server);
homeAssistantCache.Start();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    homeAssistantApi.Dispose();
    homeAssistantCache.Dispose();
    server.Dispose();
    Environment.Exit(0);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    homeAssistantApi.Dispose();
    homeAssistantCache.Dispose();
    server.Dispose();
};

Console.WriteLine("Codex Audio Remote · state-machine v2");
Console.WriteLine($"Virtual mic: {options.VirtualMicName} · cable input: {options.VirtualCableInputName}");
Console.WriteLine("Home Assistant WebSocket cache: persistent /api/websocket + state_changed");

try { homeAssistantApi.Start(); }
catch (Exception ex) { Console.WriteLine("Home Assistant REST adapter could not start: " + ex.Message); }

await server.RunAsync();
