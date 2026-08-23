$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'
$source = Get-Content -LiteralPath $path -Raw

$oldVersion = '    const string RealtimeVersion = "v1";'
$newVersion = @'
    const string RealtimeVersion = "v3";
    const string RealtimeModel = "gpt-live-1-codex";
'@.TrimEnd()
if (-not $source.Contains($oldVersion)) { throw 'Expected v1 realtime version marker was not found.' }
$source = $source.Replace($oldVersion, $newVersion)

$directField = '    readonly CodexDirectRealtimeCall directRealtimeCall = new();'
if (-not $source.Contains($directField)) { throw 'Expected direct realtime call field was not found.' }
$source = $source.Replace($directField + "`r`n", '')
$source = $source.Replace($directField + "`n", '')

$startMarker = '        Console.WriteLine($"Starting direct OAuth WebRTC compatibility session · version={RealtimeVersion}");'
$endMarker = '        var startedAt = Environment.TickCount64;'
$start = $source.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $source.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt 0 -or $end -le $start) {
    throw 'Could not locate the old direct-call handshake block.'
}

$newHandshake = @'
        Console.WriteLine($"Starting official Codex WebRTC session · version={RealtimeVersion} · model={RealtimeModel}");

        // Follow the captured working Codex Desktop flow. Codex app-server owns the
        // authenticated ChatGPT /backend-api/codex/realtime/calls request, OAuth/cookies,
        // AVAS/quicksilver headers, call-id parsing, SDP response and sideband creation.
        await RequestAsync("thread/realtime/start", new
        {
            threadId,
            model = RealtimeModel,
            outputModality = "audio",
            version = RealtimeVersion,
            clientManagedHandoffs = false,
            flushTranscriptTailOnSessionEnd = true,
            codexResponsesAsItems = false,
            includeStartupContext = false,
            initialItems = Array.Empty<object>(),
            codexResponseHandoffMode = "commentary",
            transport = new
            {
                type = "webrtc",
                sdp = offerSdp
            }
        }, cancellationToken);

'@
$source = $source.Substring(0, $start) + $newHandshake + $source.Substring($end)

$source = $source.Replace(
    '            throw new TimeoutException($"Codex realtime {RealtimeVersion} existingCall did not emit thread/realtime/started within 12 seconds.");',
    '            throw new TimeoutException($"Codex realtime {RealtimeVersion} official WebRTC did not emit thread/realtime/started within 12 seconds.");')

$oldSdpComment = @'
                // Kept for compatibility with stock Codex WebRTC-created calls. The direct-call
                // path normally applies its SDP before attaching as existingCall.
'@
$newSdpComment = @'
                // In the official flow Codex app-server creates the realtime call and returns
                // the SDP answer through thread/realtime/sdp. Chromium applies that answer here.
'@
$source = $source.Replace($oldSdpComment, $newSdpComment)

# The companion must work even when Codex Desktop/CLI was installed after the Windows
# session started and its bin directory is therefore absent from this process' PATH.
$oldFileName = '            FileName = File.Exists(bundledCodex) ? bundledCodex : "codex",'
if (-not $source.Contains($oldFileName)) { throw 'Expected Codex process FileName marker was not found.' }
$source = $source.Replace($oldFileName, '            FileName = ResolveCodexExecutable(bundledCodex),')

$processMarker = '    void StartAppServerProcess()'
$processIndex = $source.IndexOf($processMarker, [StringComparison]::Ordinal)
if ($processIndex -lt 0) { throw 'Could not locate StartAppServerProcess.' }

$resolver = @'
    static string ResolveCodexExecutable(string bundledCodex)
    {
        if (File.Exists(bundledCodex))
        {
            Console.WriteLine("Codex executable resolved · bundled=" + bundledCodex);
            return bundledCodex;
        }

        var explicitPath = Environment.GetEnvironmentVariable("CODEX_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            Console.WriteLine("Codex executable resolved · CODEX_EXE=" + explicitPath);
            return explicitPath;
        }

        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            // Current official standalone installer.
            candidates.Add(Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));

            // Executable mirror maintained by Codex Desktop, e.g.
            // %LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe.
            var desktopBinRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(desktopBinRoot))
                {
                    var files = Directory.GetFiles(desktopBinRoot, "codex.exe", SearchOption.AllDirectories);
                    Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                    candidates.AddRange(files);
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // Standalone release cache used by recent Windows Codex installers.
            var releasesRoot = Path.Combine(userProfile, ".codex", "packages", "standalone", "releases");
            try
            {
                if (Directory.Exists(releasesRoot))
                {
                    var files = Directory.GetFiles(releasesRoot, "codex.exe", SearchOption.AllDirectories);
                    Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                    candidates.AddRange(files);
                }
            }
            catch { }
        }

        // Finally inspect the inherited PATH explicitly, so the log contains the exact
        // executable rather than relying on CreateProcess to resolve the bare command.
        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var dir = segment.Trim().Trim('"');
                candidates.Add(Path.Combine(dir, "codex.exe"));
            }
            catch { }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    Console.WriteLine("Codex executable resolved · " + candidate);
                    return candidate;
                }
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Official Codex executable was not found. Install the official Windows Codex CLI " +
            "or set CODEX_EXE to codex.exe. Expected locations include " +
            "%LOCALAPPDATA%\\Programs\\OpenAI\\Codex\\bin\\codex.exe and " +
            "%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\<hash>\\codex.exe.");
    }

'@
$source = $source.Substring(0, $processIndex) + $resolver + $source.Substring($processIndex)

if ($source.Contains('directRealtimeCall.CreateAsync')) { throw 'Direct realtime/calls creation is still present.' }
if ($source.Contains('type = "existingCall"')) { throw 'existingCall transport is still present.' }
if (-not $source.Contains('model = RealtimeModel')) { throw 'Official gpt-live model was not inserted.' }
if (-not $source.Contains('type = "webrtc"')) { throw 'Official WebRTC transport was not inserted.' }
if (-not $source.Contains('includeStartupContext = false')) { throw 'Captured startup-context setting was not inserted.' }
if (-not $source.Contains('ResolveCodexExecutable(bundledCodex)')) { throw 'Codex executable resolver was not inserted.' }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host 'Prepared official Codex realtime flow: v3 + gpt-live-1-codex + app-server-owned WebRTC call creation + Windows Codex auto-discovery.'
