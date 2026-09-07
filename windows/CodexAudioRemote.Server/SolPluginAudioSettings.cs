using NAudio.CoreAudioApi;

internal static class SolPluginAudioSettings
{
    public static string? SelectedDeviceId => ResolveDevice()?.Id;
    public static string? SelectedDeviceName => ResolveDevice()?.Name;
    public static string? BtcomPath => SolPluginHost.Setting("btcom_path");
    public static int? BtcomWaitSeconds
    {
        get
        {
            var value = SolPluginHost.IntSetting("btcom_wait_seconds");
            return value is null ? null : Math.Clamp(value.Value, 1, 15);
        }
    }

    static DeviceChoice? ResolveDevice()
    {
        if (!SolPluginHost.Enabled) return null;
        var selector = SolPluginHost.Setting("downlink_device");
        if (string.IsNullOrWhiteSpace(selector)) return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            DeviceChoice? partial = null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                try
                {
                    var id = device.ID;
                    var name = device.FriendlyName;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || DownlinkDeviceSettings.IsUnsafe(name)) continue;
                    if (string.Equals(id, selector, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, selector, StringComparison.CurrentCultureIgnoreCase))
                        return new DeviceChoice(id, name);
                    if (partial is null && name.Contains(selector, StringComparison.CurrentCultureIgnoreCase))
                        partial = new DeviceChoice(id, name);
                }
                catch { }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
            return partial;
        }
        catch (Exception ex)
        {
            SolPluginHost.Log("warn", "Could not resolve SOL audio output setting: " + ex.Message);
            return null;
        }
    }

    sealed record DeviceChoice(string Id, string Name);
}
