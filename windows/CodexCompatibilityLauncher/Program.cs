using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    static readonly byte[] Needle = Encoding.ASCII.GetBytes("quicksilver=v1");
    static readonly byte[] Replacement = Encoding.ASCII.GetBytes("quicksilver=v2");

    static async Task<int> Main(string[] args)
    {
        try
        {
            var official = FindOfficialCodex();
            var patched = CreateCompatibilityCopy(official);
            Console.Error.WriteLine("[compat] official Codex: " + official);
            Console.Error.WriteLine("[compat] realtime header shim active: quicksilver=v1 -> quicksilver=v2");

            var psi = new ProcessStartInfo
            {
                FileName = patched,
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var child = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start the compatibility Codex process.");

            var stdout = PumpAsync(child.StandardOutput, Console.Out);
            var stderr = PumpAsync(child.StandardError, Console.Error);
            await child.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[compat] ERROR: " + ex.Message);
            return 1;
        }
    }

    static async Task PumpAsync(StreamReader source, TextWriter destination)
    {
        while (await source.ReadLineAsync() is { } line)
            await destination.WriteLineAsync(line);
    }

    static string FindOfficialCodex()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_REAL_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var self = Path.GetFullPath(Environment.ProcessPath ?? "");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "codex.exe");
                if (File.Exists(candidate) && !SamePath(candidate, self)) return Path.GetFullPath(candidate);
            }
            catch { }
        }

        var roots = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            roots.Add(Path.Combine(appData, "npm", "node_modules", "@openai", "codex"));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var clean = dir.Trim('"');
                if (File.Exists(Path.Combine(clean, "codex.cmd")))
                    roots.Add(Path.Combine(clean, "node_modules", "@openai", "codex"));
            }
            catch { }
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var candidates = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .Where(path => !SamePath(path, self))
                    .OrderByDescending(path => path.Contains("x86_64-pc-windows-msvc", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => path.Length);
                var match = candidates.FirstOrDefault();
                if (match is not null) return Path.GetFullPath(match);
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not locate the official Codex native executable. Run 'codex --version' first, or set CODEX_REAL_EXECUTABLE to the real codex.exe path.");
    }

    static string CreateCompatibilityCopy(string source)
    {
        var info = new FileInfo(source);
        var identity = source + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).Substring(0, 16);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexAudioRemote", "compat");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "codex-quicksilver-v2-" + hash + ".exe");

        if (File.Exists(output) && new FileInfo(output).Length == info.Length)
            return output;

        var bytes = File.ReadAllBytes(source);
        var replacements = ReplaceAll(bytes, Needle, Replacement);
        if (replacements == 0)
            throw new InvalidOperationException(
                "The installed Codex binary does not contain the expected quicksilver=v1 marker. Update the compatibility shim for this Codex version.");

        var temp = output + ".tmp-" + Environment.ProcessId;
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, output, true);
        Console.Error.WriteLine("[compat] patched " + replacements + " quicksilver marker(s) in a temporary copy; the installed Codex binary was not modified.");
        return output;
    }

    static int ReplaceAll(byte[] data, byte[] needle, byte[] replacement)
    {
        if (needle.Length != replacement.Length)
            throw new InvalidOperationException("Compatibility replacement must preserve binary length.");

        var count = 0;
        for (var i = 0; i <= data.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (data[i + j] == needle[j]) continue;
                match = false;
                break;
            }
            if (!match) continue;
            Buffer.BlockCopy(replacement, 0, data, i, replacement.Length);
            count++;
            i += needle.Length - 1;
        }
        return count;
    }

    static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
