using Microsoft.Win32;
using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

internal static class BtcomBluetoothReconnect
{
    const string A2dpSinkService = "110b";
    static readonly object StateSync = new();
    static ConnectedTarget? connectedByCompanion;

    public static async Task<bool> EnsureSelectedOutputActiveAsync(CancellationToken cancellationToken = default)
    {
        var selectedId = DownlinkDeviceSettings.SelectedDeviceId;
        if (string.IsNullOrWhiteSpace(selectedId)) return true;

        using (var enumerator = new MMDeviceEnumerator())
        {
            var selected = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All)
                .FirstOrDefault(d => string.Equals(d.ID, selectedId, StringComparison.Ordinal));
            if (selected?.State == DeviceState.Active)
            {
                selected.Dispose();
                return true;
            }
            selected?.Dispose();
        }

        var selectedName = DownlinkDeviceSettings.SelectedDeviceName;
        if (string.IsNullOrWhiteSpace(selectedName))
        {
            Console.WriteLine("Bluetooth reconnect failure: selected output is offline and has no remembered name; using fallback.");
            return false;
        }

        var btcomPath = FindBtcom();
        if (btcomPath is null)
        {
            Console.WriteLine("Bluetooth reconnect failure: btcom.exe was not found (configured path, CODEX_AUDIO_REMOTE_BTCOM, PATH, or standard install folders); using fallback without clearing selection.");
            return false;
        }

        var address = FindBluetoothAddress(selectedName);
        if (address is null && !selectedName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Selected output '{selectedName}' is offline but was not identified as Bluetooth; skipping btcom and using fallback without clearing selection.");
            return false;
        }
        var target = address is null ? $"name='{selectedName}'" : $"address={FormatAddress(address)}";
        Console.WriteLine($"Bluetooth reconnect attempt: '{selectedName}' · A2DP service {A2dpSinkService} · {target} · btcom='{btcomPath}'");

        try
        {
            var result = await RunBtcomAsync(btcomPath, address, selectedName, "-c", cancellationToken);
            if (result.ExitCode != 0 && Regex.IsMatch(result.Message, @"Code:\s*87\b"))
            {
                Console.WriteLine("Bluetooth reconnect: A2DP is already registered but inactive (btcom code 87); refreshing only service 110b.");
                var remove = await RunBtcomAsync(btcomPath, address, selectedName, "-r", cancellationToken);
                if (remove.ExitCode != 0)
                {
                    Console.WriteLine($"Bluetooth reconnect failure: could not refresh A2DP association, btcom remove exit={remove.ExitCode}{FormatToolMessage(remove.Message, "")}; using fallback without clearing selection.");
                    return false;
                }
                await Task.Delay(300, cancellationToken);
                result = await RunBtcomAsync(btcomPath, address, selectedName, "-c", cancellationToken);
            }

            if (result.ExitCode != 0)
            {
                Console.WriteLine($"Bluetooth reconnect failure: btcom exit={result.ExitCode}{FormatToolMessage(result.Message, "")}; using fallback without clearing selection.");
                return false;
            }

            lock (StateSync) connectedByCompanion = new ConnectedTarget(btcomPath, address, selectedName);

            var waitSeconds = DownlinkDeviceSettings.BtcomWaitSeconds;
            var started = Stopwatch.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(waitSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsActive(selectedId))
                {
                    Console.WriteLine($"Bluetooth reconnect success: '{selectedName}' became Active after {started.Elapsed.TotalSeconds:F1}s.");
                    return true;
                }
                await Task.Delay(200, cancellationToken);
            }

            Console.WriteLine($"Bluetooth reconnect failure: btcom completed but endpoint did not become Active within {waitSeconds}s; using fallback without clearing selection.");
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Bluetooth reconnect failure: btcom timed out; using fallback without clearing selection.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bluetooth reconnect failure: {ex.Message}; using fallback without clearing selection.");
            return false;
        }
    }

    public static void DisconnectIfConnectedByCompanion()
    {
        ConnectedTarget? target;
        lock (StateSync)
        {
            target = connectedByCompanion;
            connectedByCompanion = null;
        }
        if (target is null) return;

        Console.WriteLine($"Bluetooth conversation cleanup: disconnecting companion-connected A2DP output '{target.Name}' before restoring previous playback device.");
        try
        {
            var result = RunBtcomAsync(target.Path, target.Address, target.Name, "-r", CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode == 0)
                Console.WriteLine($"Bluetooth conversation cleanup success: A2DP service {A2dpSinkService} disconnected for '{target.Name}'.");
            else
                Console.WriteLine($"Bluetooth conversation cleanup failure: btcom exit={result.ExitCode}{FormatToolMessage(result.Message, "")}; continuing with previous playback restore.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bluetooth conversation cleanup failure: {ex.Message}; continuing with previous playback restore.");
        }
    }

    static bool IsActive(string selectedId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var selected = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => string.Equals(d.ID, selectedId, StringComparison.Ordinal));
        return selected is not null;
    }

    static async Task<BtcomResult> RunBtcomAsync(string path, string? address, string name, string action, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        process.StartInfo.ArgumentList.Add(address is null ? "-n" + name : "-b" + FormatAddress(address));
        process.StartInfo.ArgumentList.Add(action);
        process.StartInfo.ArgumentList.Add("-s" + A2dpSinkService);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        return new BtcomResult(process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    static string? FindBtcom()
    {
        var configured = DownlinkDeviceSettings.BtcomPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var environment = Environment.GetEnvironmentVariable("CODEX_AUDIO_REMOTE_BTCOM");
        if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment)) return Path.GetFullPath(environment);

        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim(), "btcom.exe")));
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            candidates.Add(Path.Combine(root, "Bluetooth Command Line Tools", "bin", "btcom.exe"));
            candidates.Add(Path.Combine(root, "Bluetooth Command Line Tools", "btcom.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    static string? FindBluetoothAddress(string endpointName)
    {
        var wanted = NormalizeName(endpointName);
        try
        {
            using var devices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            if (devices is null) return null;
            string? best = null;
            var bestScore = 0;
            foreach (var address in devices.GetSubKeyNames())
            {
                if (!Regex.IsMatch(address, "^[0-9a-fA-F]{12}$")) continue;
                using var device = devices.OpenSubKey(address);
                var name = ReadRegistryName(device?.GetValue("Name"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                var normalized = NormalizeName(name);
                var score = wanted == normalized ? 3 : wanted.Contains(normalized) || normalized.Contains(wanted) ? 2 : 0;
                if (score > bestScore) { best = address; bestScore = score; }
            }
            return best;
        }
        catch { return null; }
    }

    static string? ReadRegistryName(object? value) => value switch
    {
        string text => text.TrimEnd('\0'),
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
        _ => null
    };

    static string NormalizeName(string value)
    {
        var text = Regex.Replace(value, @"\s*\((?:desconectado|stereo|estéreo|hands[- ]?free|headset)[^)]*\)\s*", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^(speakers?|headphones?|headset|auriculares?|altavoces?)\s*\((.*)\)$", "$2", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    }

    static string FormatAddress(string raw) => string.Join(":", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2).ToUpperInvariant()));
    static string FormatToolMessage(string primary, string secondary)
    {
        var message = string.IsNullOrWhiteSpace(primary) ? secondary : primary;
        return string.IsNullOrWhiteSpace(message) ? "" : " · " + Regex.Replace(message, @"\s+", " ");
    }

    sealed record BtcomResult(int ExitCode, string Message);
    sealed record ConnectedTarget(string Path, string? Address, string Name);
}
