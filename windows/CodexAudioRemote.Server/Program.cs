var options = Options.Parse(args);
if (options.ListDevices)
{
    AudioDeviceManager.ListDevices();
    return;
}

if (TrayController.VoiceBackend == TrayController.RealtimeV3Backend)
{
    using var realtimeServer = new RealtimeSessionServer(options);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        realtimeServer.Dispose();
        Environment.Exit(0);
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => realtimeServer.Dispose();

    Console.WriteLine("Codex Audio Remote · experimental Realtime V3 backend");
    Console.WriteLine("Auth: existing Codex ChatGPT OAuth login");
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
    homeAssistantApi.Dispose();
    server.Dispose();
    Environment.Exit(0);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    homeAssistantApi.Dispose();
    server.Dispose();
};

Console.WriteLine("Codex Audio Remote · state-machine v2");
Console.WriteLine($"Virtual mic: {options.VirtualMicName} · cable input: {options.VirtualCableInputName}");

try { homeAssistantApi.Start(); }
catch (Exception ex) { Console.WriteLine("Home Assistant REST adapter could not start: " + ex.Message); }

await server.RunAsync();
