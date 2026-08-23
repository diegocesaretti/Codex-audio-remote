$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..'
$appSettingsPath = Join-Path $root 'windows\CodexAudioRemote.Server\AppSettings.cs'
$settingsFormPath = Join-Path $root 'windows\CodexAudioRemote.Server\SettingsForm.cs'
$bridgePath = Join-Path $root 'windows\CodexAudioRemote.Server\CodexRealtimeBridge.cs'

$app = Get-Content -LiteralPath $appSettingsPath -Raw
$form = Get-Content -LiteralPath $settingsFormPath -Raw
$bridge = Get-Content -LiteralPath $bridgePath -Raw

# ---------------- AppSettings ----------------
if (-not $app.Contains('ThreadContinuityPersistent')) {
    $marker = '    public const string DefaultHomeAssistantUrl = "http://homeassistant.local:8123";'
    if (-not $app.Contains($marker)) { throw 'AppSettings constants marker not found.' }
    $insert = @'
    public const string ThreadContinuityPersistent = "persistent";
    public const string ThreadContinuityMaxAge = "max-age";
    public const string ThreadContinuityAlwaysNew = "always-new";
'@
    $app = $app.Replace($marker, $marker + "`r`n" + $insert.TrimEnd())
}

if (-not $app.Contains('public static string RealtimeThreadContinuityMode')) {
    $marker = '    public static string HomeAssistantBaseUrl'
    $idx = $app.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'AppSettings HomeAssistantBaseUrl marker not found.' }
    $block = @'
    public static string RealtimeThreadContinuityMode
    {
        get
        {
            var value = (ReadString("RealtimeThreadContinuityMode") ?? ThreadContinuityPersistent).Trim().ToLowerInvariant();
            return value == ThreadContinuityMaxAge || value == ThreadContinuityAlwaysNew ? value : ThreadContinuityPersistent;
        }
        set
        {
            var normalized = (value ?? ThreadContinuityPersistent).Trim().ToLowerInvariant();
            if (normalized != ThreadContinuityMaxAge && normalized != ThreadContinuityAlwaysNew)
                normalized = ThreadContinuityPersistent;
            WriteString("RealtimeThreadContinuityMode", normalized);
        }
    }

    public static int RealtimeThreadMaxAgeHours
    {
        get => ReadInt("RealtimeThreadMaxAgeHours", 24, 1, 720);
        set => WriteInt("RealtimeThreadMaxAgeHours", Math.Clamp(value, 1, 720));
    }

    public static string RealtimePersistentThreadId
    {
        get => (ReadString("RealtimePersistentThreadId") ?? "").Trim();
        set => WriteString("RealtimePersistentThreadId", (value ?? "").Trim());
    }

    public static long RealtimePersistentThreadLastUsedUtcTicks
    {
        get => ReadLong("RealtimePersistentThreadLastUsedUtcTicks", 0);
        set => WriteLong("RealtimePersistentThreadLastUsedUtcTicks", Math.Max(0, value));
    }

    public static bool RealtimeThreadForceNew
    {
        get => ReadBool("RealtimeThreadForceNew", false);
        set => WriteBool("RealtimeThreadForceNew", value);
    }

    public static void RequestNewRealtimeConversation()
    {
        RealtimePersistentThreadId = "";
        RealtimePersistentThreadLastUsedUtcTicks = 0;
        RealtimeThreadForceNew = true;
    }

    public static void RememberRealtimeThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        RealtimePersistentThreadId = threadId;
        RealtimePersistentThreadLastUsedUtcTicks = DateTime.UtcNow.Ticks;
        RealtimeThreadForceNew = false;
    }

'@
    $app = $app.Substring(0, $idx) + $block + $app.Substring($idx)
}

if (-not $app.Contains('static long ReadLong(')) {
    $marker = '    static void WriteInt(string name, int value) => WriteString(name, value.ToString());'
    if (-not $app.Contains($marker)) { throw 'AppSettings WriteInt marker not found.' }
    $block = @'

    static long ReadLong(string name, long fallback)
    {
        var text = ReadString(name);
        return long.TryParse(text, out var parsed) ? parsed : fallback;
    }

    static void WriteLong(string name, long value) => WriteString(name, value.ToString());
'@
    $app = $app.Replace($marker, $marker + $block)
}

$app = $app.Replace(
    '        WakeRetryCooldownMs = 3500;',
    '        WakeRetryCooldownMs = 3500;' + "`r`n" +
    '        RealtimeThreadContinuityMode = ThreadContinuityPersistent;' + "`r`n" +
    '        RealtimeThreadMaxAgeHours = 24;' + "`r`n" +
    '        RequestNewRealtimeConversation();')

$app = $app.Replace(
    '$"WakeCooldown={WakeRetryCooldownMs}ms; CODEX_EXE={CodexExecutableOverride}";',
    '$"WakeCooldown={WakeRetryCooldownMs}ms; ThreadMode={RealtimeThreadContinuityMode}; ThreadMaxAge={RealtimeThreadMaxAgeHours}h; Thread={RealtimePersistentThreadId}; CODEX_EXE={CodexExecutableOverride}";')

# ---------------- CodexRealtimeBridge ----------------
$oldThreadBlock = @'
        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(threadId))
            throw new InvalidOperationException("thread/start did not return a thread id.");
'@
if ($bridge.Contains($oldThreadBlock)) {
    $newThreadBlock = @'
        threadId = await StartOrResumeThreadAsync(cwd, cancellationToken);
'@
    $bridge = $bridge.Replace($oldThreadBlock, $newThreadBlock)
} elseif (-not $bridge.Contains('StartOrResumeThreadAsync(cwd, cancellationToken)')) {
    throw 'CodexRealtimeBridge thread/start block not found.'
}

if (-not $bridge.Contains('async Task<string> StartOrResumeThreadAsync')) {
    $marker = '    async Task EnsureConnectedAsync(CancellationToken cancellationToken)'
    $idx = $bridge.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'CodexRealtimeBridge EnsureConnectedAsync marker not found.' }
    $method = @'
    async Task<string> StartOrResumeThreadAsync(string? cwd, CancellationToken cancellationToken)
    {
        var mode = AppSettings.RealtimeThreadContinuityMode;
        var forceNew = AppSettings.RealtimeThreadForceNew;
        var savedThreadId = AppSettings.RealtimePersistentThreadId;
        var canResume = !forceNew && mode != AppSettings.ThreadContinuityAlwaysNew && !string.IsNullOrWhiteSpace(savedThreadId);

        if (canResume && mode == AppSettings.ThreadContinuityMaxAge)
        {
            var lastTicks = AppSettings.RealtimePersistentThreadLastUsedUtcTicks;
            if (lastTicks <= 0)
            {
                canResume = false;
            }
            else
            {
                var age = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
                if (age > TimeSpan.FromHours(AppSettings.RealtimeThreadMaxAgeHours))
                {
                    Console.WriteLine($"Codex thread continuity expired · thread={savedThreadId} · age={age.TotalHours:F1}h · limit={AppSettings.RealtimeThreadMaxAgeHours}h");
                    canResume = false;
                }
            }
        }

        if (canResume)
        {
            try
            {
                var resumeParams = new Dictionary<string, object?> { ["threadId"] = savedThreadId };
                if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
                    resumeParams["cwd"] = Path.GetFullPath(cwd);
                var resumed = await RequestAsync("thread/resume", resumeParams, cancellationToken);
                var resumedId = resumed.GetProperty("thread").GetProperty("id").GetString() ?? savedThreadId;
                if (string.IsNullOrWhiteSpace(resumedId)) throw new InvalidOperationException("thread/resume returned no thread id.");
                AppSettings.RememberRealtimeThread(resumedId);
                Console.WriteLine($"Codex thread resumed · thread={resumedId} · mode={mode}");
                return resumedId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"Codex thread resume failed · thread={savedThreadId} · {ex.Message} · creating a new thread");
                AppSettings.RequestNewRealtimeConversation();
            }
        }
        else if (!string.IsNullOrWhiteSpace(savedThreadId) || forceNew)
        {
            Console.WriteLine($"Codex thread continuity creating new thread · mode={mode} · forceNew={forceNew}");
        }

        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);
        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        var newThreadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(newThreadId))
            throw new InvalidOperationException("thread/start did not return a thread id.");

        if (mode != AppSettings.ThreadContinuityAlwaysNew)
            AppSettings.RememberRealtimeThread(newThreadId);
        else
        {
            AppSettings.RealtimePersistentThreadId = "";
            AppSettings.RealtimePersistentThreadLastUsedUtcTicks = 0;
            AppSettings.RealtimeThreadForceNew = false;
        }
        Console.WriteLine($"Codex thread started · thread={newThreadId} · mode={mode}");
        return newThreadId;
    }

'@
    $bridge = $bridge.Substring(0, $idx) + $method + $bridge.Substring($idx)
}

$oldStop = @'
        oauthWebRtcPeer.Close("session ended");
        realtimeStarted = false;
        threadId = "";
'@
if ($bridge.Contains($oldStop)) {
    $newStop = @'
        oauthWebRtcPeer.Close("session ended");
        realtimeStarted = false;
        if (!string.IsNullOrWhiteSpace(threadId) &&
            AppSettings.RealtimeThreadContinuityMode != AppSettings.ThreadContinuityAlwaysNew &&
            !AppSettings.RealtimeThreadForceNew)
        {
            AppSettings.RememberRealtimeThread(threadId);
            Console.WriteLine($"Codex thread preserved after Realtime stop · thread={threadId}");
        }
        else if (AppSettings.RealtimeThreadForceNew)
        {
            Console.WriteLine("Codex thread intentionally not preserved · new conversation requested");
        }
        threadId = "";
'@
    $bridge = $bridge.Replace($oldStop, $newStop)
} elseif (-not $bridge.Contains('Codex thread preserved after Realtime stop')) {
    throw 'CodexRealtimeBridge StopAsync marker not found.'
}

# ---------------- SettingsForm ----------------
if (-not $form.Contains('readonly ComboBox threadContinuity')) {
    $marker = '    readonly NumericUpDown wakeCooldown = new();'
    if (-not $form.Contains($marker)) { throw 'SettingsForm field marker not found.' }
    $fields = @'
    readonly ComboBox threadContinuity = new();
    readonly NumericUpDown threadMaxAgeHours = new();
    readonly Label currentThread = new();
'@
    $form = $form.Replace($marker, $marker + "`r`n" + $fields.TrimEnd())
}

if (-not $form.Contains('Continuidad de conversación')) {
    $marker = '        AddReadOnly(tab, "Autenticación", "Login ChatGPT OAuth de Codex", ref y);'
    if (-not $form.Contains($marker)) { throw 'SettingsForm realtime authentication marker not found.' }
    $ui = @'

        AddSection(tab, "Continuidad de conversación", ref y);
        AddLabel(tab, "Al volver a decir Hola Sol", 26, y);
        threadContinuity.DropDownStyle = ComboBoxStyle.DropDownList;
        threadContinuity.Items.AddRange(new object[]
        {
            "Continuar siempre el mismo hilo",
            "Crear hilo nuevo después de X horas",
            "Crear siempre una conversación nueva"
        });
        threadContinuity.SetBounds(235, y - 4, 360, 30); tab.Controls.Add(threadContinuity); y += 44;

        AddLabel(tab, "Nuevo hilo después de", 26, y);
        threadMaxAgeHours.Minimum = 1; threadMaxAgeHours.Maximum = 720; threadMaxAgeHours.Increment = 1;
        threadMaxAgeHours.SetBounds(235, y - 4, 100, 28); tab.Controls.Add(threadMaxAgeHours);
        var hoursLabel = new Label { Text = "horas", AutoSize = true }; hoursLabel.SetBounds(343, y + 1, 50, 24); tab.Controls.Add(hoursLabel); y += 42;

        currentThread.SetBounds(26, y, 700, 24); currentThread.ForeColor = SystemColors.GrayText; tab.Controls.Add(currentThread); y += 34;
        var newConversation = new Button { Text = "Nueva conversación en la próxima activación", Width = 330, Height = 34 };
        newConversation.SetBounds(26, y, 330, 34);
        newConversation.Click += (_, _) =>
        {
            if (MessageBox.Show("¿Descartar el hilo guardado y crear una conversación nueva en la próxima activación?", "Codex Audio Remote", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            AppSettings.RequestNewRealtimeConversation();
            RefreshThreadStatus();
        };
        tab.Controls.Add(newConversation); y += 46;
        AddInfo(tab, "IDLE cierra WebRTC pero no olvida el hilo. PAUSED sigue reutilizando la sesión abierta. Si resume falla, se crea un hilo nuevo automáticamente.", 26, y, 700, 44); y += 52;
        threadContinuity.SelectedIndexChanged += (_, _) => UpdateThreadContinuityUi();
'@
    $form = $form.Replace($marker, $marker + $ui)
}

if (-not $form.Contains('void RefreshThreadStatus()')) {
    $marker = '    void SaveAndClose()'
    $idx = $form.IndexOf($marker, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw 'SettingsForm SaveAndClose marker not found.' }
    $methods = @'
    void UpdateThreadContinuityUi()
    {
        threadMaxAgeHours.Enabled = threadContinuity.SelectedIndex == 1;
    }

    void RefreshThreadStatus()
    {
        var id = AppSettings.RealtimePersistentThreadId;
        if (AppSettings.RealtimeThreadForceNew)
            currentThread.Text = "Hilo actual: se creará una conversación nueva en la próxima activación";
        else if (string.IsNullOrWhiteSpace(id))
            currentThread.Text = "Hilo guardado: ninguno";
        else
            currentThread.Text = "Hilo guardado: " + id;
    }

'@
    $form = $form.Substring(0, $idx) + $methods + $form.Substring($idx)
}

$loadMarker = '        wakeCooldown.Value = AppSettings.WakeRetryCooldownMs;'
if (-not $form.Contains('threadMaxAgeHours.Value = AppSettings.RealtimeThreadMaxAgeHours;')) {
    if (-not $form.Contains($loadMarker)) { throw 'SettingsForm LoadValues marker not found.' }
    $load = @'
        var continuityMode = AppSettings.RealtimeThreadContinuityMode;
        threadContinuity.SelectedIndex = continuityMode == AppSettings.ThreadContinuityMaxAge ? 1 : continuityMode == AppSettings.ThreadContinuityAlwaysNew ? 2 : 0;
        threadMaxAgeHours.Value = AppSettings.RealtimeThreadMaxAgeHours;
        UpdateThreadContinuityUi();
        RefreshThreadStatus();
'@
    $form = $form.Replace($loadMarker, $loadMarker + "`r`n" + $load.TrimEnd())
}

$saveMarker = '        AppSettings.WakeRetryCooldownMs = (int)wakeCooldown.Value;'
if (-not $form.Contains('AppSettings.RealtimeThreadContinuityMode = threadContinuity.SelectedIndex')) {
    if (-not $form.Contains($saveMarker)) { throw 'SettingsForm Save marker not found.' }
    $save = @'
        AppSettings.RealtimeThreadContinuityMode = threadContinuity.SelectedIndex == 1
            ? AppSettings.ThreadContinuityMaxAge
            : threadContinuity.SelectedIndex == 2
                ? AppSettings.ThreadContinuityAlwaysNew
                : AppSettings.ThreadContinuityPersistent;
        AppSettings.RealtimeThreadMaxAgeHours = (int)threadMaxAgeHours.Value;
'@
    $form = $form.Replace($saveMarker, $saveMarker + "`r`n" + $save.TrimEnd())
}

# Validation
$requiredApp = @('RealtimeThreadContinuityMode', 'RealtimePersistentThreadId', 'RealtimeThreadForceNew', 'RequestNewRealtimeConversation', 'RememberRealtimeThread')
foreach ($needle in $requiredApp) { if (-not $app.Contains($needle)) { throw "AppSettings continuity marker missing: $needle" } }
$requiredBridge = @('thread/resume', 'StartOrResumeThreadAsync', 'Codex thread resumed', 'Codex thread preserved after Realtime stop')
foreach ($needle in $requiredBridge) { if (-not $bridge.Contains($needle)) { throw "Bridge continuity marker missing: $needle" } }
$requiredForm = @('Continuidad de conversación', 'Nueva conversación en la próxima activación', 'RefreshThreadStatus', 'threadMaxAgeHours')
foreach ($needle in $requiredForm) { if (-not $form.Contains($needle)) { throw "Settings continuity marker missing: $needle" } }

Set-Content -LiteralPath $appSettingsPath -Value $app -Encoding utf8 -NoNewline
Set-Content -LiteralPath $settingsFormPath -Value $form -Encoding utf8 -NoNewline
Set-Content -LiteralPath $bridgePath -Value $bridge -Encoding utf8 -NoNewline
Write-Host 'Prepared persistent Codex thread continuity: resume stored thread, max-age policy, always-new mode, safe fallback and manual new-conversation reset.'