var options = Options.Parse(args);
if (options.ListDevices)
{
    AudioDeviceManager.ListDevices();
    return;
}

var switcher = new AudioDeviceSwitcher(options.VirtualMicName);
await switcher.TryRecoverAsync();

using var server = new SessionServerV2(options, switcher);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    server.Dispose();
    Environment.Exit(0);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Dispose();

Console.WriteLine("Codex Audio Remote · state-machine v2");
Console.WriteLine($"Virtual mic: {options.VirtualMicName} · cable input: {options.VirtualCableInputName}");
await server.RunAsync();
