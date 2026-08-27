from pathlib import Path

path = Path("windows/CodexAudioRemote.Server/CodexRealtimeBridge.cs")
text = path.read_text(encoding="utf-8")

# -----------------------------------------------------------------------------
# Home Assistant fast path: ephemeral voice thread + cached HA developer context.
# -----------------------------------------------------------------------------
old_thread = '''        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
'''

new_thread = '''        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        // Voice sessions are disposable control surfaces. Avoid durable thread persistence work
        // and preload the current HA state so simple device commands do not need a discovery pass.
        threadParams["ephemeral"] = true;
        var haContext = HomeAssistantWebSocketCache.Current?.GetCompactContext(80) ?? "";
        if (!string.IsNullOrWhiteSpace(haContext))
        {
            threadParams["developerInstructions"] =
                "HOME ASSISTANT FAST PATH: The following snapshot is already current. " +
                "For simple home-control requests, use these exact entity ids/states and call the existing Home Assistant tool directly. " +
                "Do not spend a turn listing or rediscovering HA states unless the requested entity is absent or the snapshot is clearly stale.\\n\\n" +
                haContext;
        }

        var threadStartAt = Stopwatch.GetTimestamp();
        JsonElement thread;
        try
        {
            thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Compatibility escape hatch for an older stock Codex install. Never make HA cache
            // support a hard dependency for starting voice.
            Console.WriteLine("HA fast-path thread/start fallback to stock params: " + ex.Message);
            threadParams.Remove("ephemeral");
            threadParams.Remove("developerInstructions");
            thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        }
        var threadStartMs = Stopwatch.GetElapsedTime(threadStartAt).TotalMilliseconds;
        Console.WriteLine($"Realtime thread/start · {threadStartMs:0} ms · ephemeral={threadParams.ContainsKey(\"ephemeral\")} · HA-context={!string.IsNullOrWhiteSpace(haContext)} · chars={haContext.Length}");
        threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
'''

if old_thread in text:
    text = text.replace(old_thread, new_thread, 1)
elif "Realtime thread/start ·" not in text:
    raise SystemExit("CodexRealtimeBridge thread/start anchor not found; refusing to patch an unexpected source version")

# -----------------------------------------------------------------------------
# Official Codex locator.
#
# This intentionally mirrors the strategy that fixed the same Windows-launcher
# problem in Whatsapp-Codex-Nexo: do not assume a GUI process inherited the same
# PATH as the user's terminal. Prefer the native official desktop bundle, but also
# support npm, WinGet, Scoop, ~/.local/bin and AppX/MS Store installations.
# No Codex binary is bundled or modified.
# -----------------------------------------------------------------------------
old_launcher = '''    void StartAppServerProcess()
    {
        if (appServerProcess is { HasExited: false }) return;
        var bundledCodex = Path.Combine(AppContext.BaseDirectory, "codex.exe");
        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(bundledCodex) ? bundledCodex : "codex",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--enable");
        psi.ArgumentList.Add("realtime_conversation");
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add(AppServerUrl);
        appServerProcess = Process.Start(psi) ?? throw new InvalidOperationException("Could not start codex app-server.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!appServerProcess.HasExited)
                {
                    var line = await appServerProcess.StandardError.ReadLineAsync();
                    if (line is null) break;
                    Console.WriteLine("[app-server] " + line);
                }
            }
            catch { }
        });
    }
'''

new_launcher = r'''    void StartAppServerProcess()
    {
        if (appServerProcess is { HasExited: false }) return;

        var codex = ResolveOfficialCodexCli();
        var extension = Path.GetExtension(codex).ToLowerInvariant();
        ProcessStartInfo psi;

        if (extension is ".cmd" or ".bat")
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec))
                comspec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

            psi = new ProcessStartInfo
            {
                FileName = comspec,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("call");
            psi.ArgumentList.Add(codex);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = codex,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        psi.ArgumentList.Add("--enable");
        psi.ArgumentList.Add("realtime_conversation");
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add(AppServerUrl);

        Console.WriteLine("Launching official Codex CLI · " + codex);
        appServerProcess = Process.Start(psi) ?? throw new InvalidOperationException("Could not start official Codex app-server.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!appServerProcess.HasExited)
                {
                    var line = await appServerProcess.StandardError.ReadLineAsync();
                    if (line is null) break;
                    Console.WriteLine("[app-server] " + line);
                }
            }
            catch { }
        });
    }

    static string ResolveOfficialCodexCli()
    {
        var checkedPaths = new List<string>();

        static string? Existing(string? candidate, List<string> checkedPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
                checkedPaths.Add(full);
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        // 1) Explicit override for diagnostics / unusual installations.
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_AUDIO_REMOTE_CODEX_PATH");
        var found = Existing(explicitPath, checkedPaths);
        if (found is not null) return found;

        // 2) Current process PATH. Check actual files instead of relying on Process.Start("codex"),
        // because GUI/tray processes often inherit a different PATH from interactive terminals.
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawDir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            found = Existing(Path.Combine(rawDir.Trim('"'), "codex.exe"), checkedPaths)
                    ?? Existing(Path.Combine(rawDir.Trim('"'), "codex.cmd"), checkedPaths)
                    ?? Existing(Path.Combine(rawDir.Trim('"'), "codex.bat"), checkedPaths);
            if (found is not null) return found;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 3) Official Codex desktop installation. Prefer newest native bundle.
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var desktopRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(desktopRoot))
                {
                    foreach (var dir in Directory.GetDirectories(desktopRoot)
                                 .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)))
                    {
                        found = Existing(Path.Combine(dir, "codex.exe"), checkedPaths);
                        if (found is not null) return found;
                    }
                }
                checkedPaths.Add(desktopRoot + "\\<version>\\codex.exe");
            }
            catch { }

            // 4) WinGet link.
            found = Existing(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "codex.exe"), checkedPaths);
            if (found is not null) return found;
        }

        // 5) npm global shim.
        if (!string.IsNullOrWhiteSpace(appData))
        {
            found = Existing(Path.Combine(appData, "npm", "codex.exe"), checkedPaths)
                    ?? Existing(Path.Combine(appData, "npm", "codex.cmd"), checkedPaths);
            if (found is not null) return found;
        }

        // 6) Scoop and 7) ~/.local/bin.
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            found = Existing(Path.Combine(userProfile, "scoop", "shims", "codex.exe"), checkedPaths)
                    ?? Existing(Path.Combine(userProfile, "scoop", "shims", "codex.cmd"), checkedPaths)
                    ?? Existing(Path.Combine(userProfile, ".local", "bin", "codex.exe"), checkedPaths)
                    ?? Existing(Path.Combine(userProfile, ".local", "bin", "codex.cmd"), checkedPaths);
            if (found is not null) return found;
        }

        // 8) Microsoft Store / AppX desktop package compatibility fallback.
        try
        {
            var ps = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ps.ArgumentList.Add("-NoLogo");
            ps.ArgumentList.Add("-NoProfile");
            ps.ArgumentList.Add("-NonInteractive");
            ps.ArgumentList.Add("-Command");
            ps.ArgumentList.Add("$p=Get-AppxPackage -Name 'OpenAI.Codex' | Sort-Object Version -Descending | Select-Object -First 1; if($p){$c=Join-Path ([string]$p.InstallLocation) 'app\\resources\\codex.exe'; if(Test-Path -LiteralPath $c){Write-Output $c}}");
            using var proc = Process.Start(ps);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                found = Existing(output, checkedPaths);
                if (found is not null) return found;
            }
        }
        catch { }

        throw new FileNotFoundException(
            "Official Codex CLI was not found. Codex Audio Remote does not bundle a patched Codex. " +
            "Install/login to the official Codex app/CLI, or set CODEX_AUDIO_REMOTE_CODEX_PATH to the official codex.exe/codex.cmd. " +
            "Checked: " + string.Join(" ; ", checkedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(25)));
    }
'''

if old_launcher in text:
    text = text.replace(old_launcher, new_launcher, 1)
elif "ResolveOfficialCodexCli()" not in text:
    raise SystemExit("CodexRealtimeBridge launcher anchor not found; refusing to patch an unexpected source version")

path.write_text(text, encoding="utf-8")
print("Prepared official Codex HA fast-path + Nexo-style Windows Codex discovery in", path)
