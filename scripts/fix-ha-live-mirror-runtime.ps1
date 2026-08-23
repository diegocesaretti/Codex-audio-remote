$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$settingsPath = Join-Path $root 'RealtimeMirrorSettings.cs'
$mirrorPath = Join-Path $root 'RealtimeSecondaryAudioMirror.cs'

# Notify the active Realtime mirror when the HA mirror preference changes.
$settings = Get-Content -LiteralPath $settingsPath -Raw
if (-not $settings.Contains('HomeAssistantMirrorEnabledChanged')) {
    $marker = '    const string AppKey = @"Software\CodexAudioRemote";'
    if (-not $settings.Contains($marker)) { throw 'Mirror settings class marker missing.' }
    $settings = $settings.Replace($marker, $marker + "`r`n`r`n    public static event Action<bool>? HomeAssistantMirrorEnabledChanged;")

    $old = @'
    public static bool HomeAssistantMirrorEnabled
    {
        get => ReadBool("RealtimeHomeAssistantMirrorEnabled", false);
        set => WriteBool("RealtimeHomeAssistantMirrorEnabled", value);
    }
'@
    $new = @'
    public static bool HomeAssistantMirrorEnabled
    {
        get => ReadBool("RealtimeHomeAssistantMirrorEnabled", false);
        set
        {
            var previous = ReadBool("RealtimeHomeAssistantMirrorEnabled", false);
            WriteBool("RealtimeHomeAssistantMirrorEnabled", value);
            if (previous != value) HomeAssistantMirrorEnabledChanged?.Invoke(value);
        }
    }
'@
    if (-not $settings.Contains($old)) { throw 'HomeAssistantMirrorEnabled property marker missing.' }
    $settings = $settings.Replace($old, $new)
}
Set-Content -LiteralPath $settingsPath -Value $settings -Encoding utf8 -NoNewline

$mirror = Get-Content -LiteralPath $mirrorPath -Raw

if (-not $mirror.Contains('OnHomeAssistantMirrorEnabledChanged')) {
    $marker = '    public async Task StartAsync(string newSessionId, CancellationToken cancellationToken = default)'
    $idx = $mirror.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'Realtime mirror StartAsync marker missing.' }
    $methods = @'
    public RealtimeSecondaryAudioMirror()
    {
        RealtimeMirrorSettings.HomeAssistantMirrorEnabledChanged += OnHomeAssistantMirrorEnabledChanged;
    }

    void OnHomeAssistantMirrorEnabledChanged(bool enabled)
    {
        if (disposed) return;
        if (enabled) ArmHomeAssistantMirrorForCurrentSession();
        else DisarmHomeAssistantMirrorForCurrentSession();
    }

    void ArmHomeAssistantMirrorForCurrentSession()
    {
        if (!RealtimeMirrorSettings.HomeAssistantMirrorEnabled ||
            !RealtimeMirrorSettings.HasHomeAssistantAccessToken ||
            string.IsNullOrWhiteSpace(RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity)) return;

        string? entity = null;
        lock (sync)
        {
            var localCts = cts;
            if (disposed || localCts is null || localCts.IsCancellationRequested || haPcm is not null) return;

            var queue = CreatePcmQueue();
            var stream = new LiveMp3Stream();
            haPcm = queue;
            liveStream = stream;
            Interlocked.Exchange(ref haPlaybackStarted, 0);
            LiveStreams[stream.Token] = stream;
            var reader = queue.Reader;
            haTask = Task.Run(() => RunHomeAssistantEncoderAsync(reader, stream, localCts.Token), localCts.Token);
            entity = RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity;
        }

        Console.WriteLine($"Realtime HA mirror armed live · {entity} · starts on next assistant audio");
    }

    void DisarmHomeAssistantMirrorForCurrentSession()
    {
        Channel<byte[]>? queue;
        LiveMp3Stream? stream;
        lock (sync)
        {
            queue = haPcm;
            haPcm = null;
            stream = liveStream;
            liveStream = null;
            haTask = null;
            Interlocked.Exchange(ref haPlaybackStarted, 0);
        }

        if (queue is null && stream is null) return;
        try { queue?.Writer.TryComplete(); } catch { }
        try { stream?.Complete(); } catch { }
        if (stream is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                LiveStreams.TryRemove(stream.Token, out _);
            });
        }
        Console.WriteLine("Realtime HA mirror disarmed live");
    }

'@
    $mirror = $mirror.Substring(0, $idx) + $methods + $mirror.Substring($idx)
}

# At session start, use the same live-arm path and emit a complete settings snapshot.
$oldBlock = @'
        if (RealtimeMirrorSettings.HomeAssistantMirrorEnabled &&
            RealtimeMirrorSettings.HasHomeAssistantAccessToken &&
            !string.IsNullOrWhiteSpace(RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity))
        {
            haPcm = CreatePcmQueue();
            liveStream = new LiveMp3Stream();
            LiveStreams[liveStream.Token] = liveStream;
            var reader = haPcm.Reader;
            var stream = liveStream;
            haTask = Task.Run(() => RunHomeAssistantEncoderAsync(reader, stream, mirrorCts.Token), mirrorCts.Token);
            Console.WriteLine($"Realtime HA mirror armed · {RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity} · starts on first assistant audio");
        }
'@
$newBlock = @'
        Console.WriteLine($"Realtime HA mirror config · enabled={RealtimeMirrorSettings.HomeAssistantMirrorEnabled} · token={RealtimeMirrorSettings.HasHomeAssistantAccessToken} · entity={RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity}");
        ArmHomeAssistantMirrorForCurrentSession();
'@
if ($mirror.Contains($oldBlock)) {
    $mirror = $mirror.Replace($oldBlock, $newBlock)
} elseif (-not $mirror.Contains('Realtime HA mirror config · enabled=')) {
    throw 'Home Assistant mirror session-start block marker missing.'
}

# If Settings was changed without restarting the session, make sure the next assistant PCM sees it.
if (-not $mirror.Contains('ArmHomeAssistantMirrorForCurrentSession(); // lazy runtime refresh')) {
    $marker = '    public void PushPcm16k(byte[] pcm)'
    $idx = $mirror.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'PushPcm16k marker missing.' }
    $brace = $mirror.IndexOf('{', $idx)
    if ($brace -lt 0) { throw 'PushPcm16k opening brace missing.' }
    $insert = "`r`n        if (RealtimeMirrorSettings.HomeAssistantMirrorEnabled && haPcm is null) ArmHomeAssistantMirrorForCurrentSession(); // lazy runtime refresh`r`n        else if (!RealtimeMirrorSettings.HomeAssistantMirrorEnabled && haPcm is not null) DisarmHomeAssistantMirrorForCurrentSession();"
    $mirror = $mirror.Substring(0, $brace + 1) + $insert + $mirror.Substring($brace + 1)
}

# Prevent event subscription from outliving the mirror instance.
if (-not $mirror.Contains('HomeAssistantMirrorEnabledChanged -= OnHomeAssistantMirrorEnabledChanged')) {
    $marker = '        disposed = true;'
    $idx = $mirror.LastIndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'Dispose marker missing.' }
    $replacement = $marker + "`r`n        RealtimeMirrorSettings.HomeAssistantMirrorEnabledChanged -= OnHomeAssistantMirrorEnabledChanged;"
    $mirror = $mirror.Remove($idx, $marker.Length).Insert($idx, $replacement)
}

Set-Content -LiteralPath $mirrorPath -Value $mirror -Encoding utf8 -NoNewline

$settingsCheck = Get-Content -LiteralPath $settingsPath -Raw
$mirrorCheck = Get-Content -LiteralPath $mirrorPath -Raw
if ($settingsCheck -notmatch 'HomeAssistantMirrorEnabledChanged') { throw 'HA mirror change event missing.' }
if ($mirrorCheck -notmatch 'ArmHomeAssistantMirrorForCurrentSession') { throw 'Live HA mirror arm method missing.' }
if ($mirrorCheck -notmatch 'DisarmHomeAssistantMirrorForCurrentSession') { throw 'Live HA mirror disarm method missing.' }
if ($mirrorCheck -notmatch 'lazy runtime refresh') { throw 'HA mirror lazy runtime refresh missing.' }
if ($mirrorCheck -notmatch 'Realtime HA mirror config') { throw 'HA mirror config diagnostics missing.' }
Write-Host 'Made Home Assistant mirror live-toggleable during active Realtime sessions.'

# First keep the MP3 streaming path correct, then replace it with the lower-latency WAV path.
& (Join-Path $PSScriptRoot 'fix-ha-lame-stream-flush.ps1')
& (Join-Path $PSScriptRoot 'optimize-ha-cast-low-latency.ps1')
