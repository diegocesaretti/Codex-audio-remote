using System.Drawing;
using System.Windows.Forms;

internal sealed class HomeAssistantSettingsPanel : UserControl
{
    readonly CheckBox enabled = new();
    readonly TextBox baseUrl = new();
    readonly NumericUpDown apiPort = new();
    readonly CheckBox autoStart = new();
    readonly CheckBox keepOpen = new();
    readonly CheckBox requireSource = new();

    readonly TextBox token = new();
    readonly Label tokenStatus = new();
    readonly Button testHa = new();
    readonly ComboBox player = new();
    readonly Button refreshPlayers = new();
    readonly CheckBox mirrorEnabled = new();
    readonly CheckBox announce = new();
    readonly Button testMirror = new();
    readonly Label mirrorStatus = new();

    readonly TextBox speechText = new();
    readonly Button testSpeech = new();
    readonly Label speechStatus = new();
    readonly int activeApiPort;

    public HomeAssistantSettingsPanel()
    {
        activeApiPort = AppSettings.HomeAssistantApiPort;
        AutoScroll = false;
        BackColor = SystemColors.Control;
        Height = 790;

        var y = 18;
        AddSection("Conexión y API", ref y);
        enabled.Text = "Habilitar adaptador REST de Home Assistant";
        enabled.SetBounds(18, y, 560, 26); Controls.Add(enabled); y += 38;

        AddLabel("URL base de Home Assistant", 18, y);
        baseUrl.SetBounds(235, y - 4, 430, 28); Controls.Add(baseUrl); y += 42;

        AddLabel("Puerto local de Codex API", 18, y);
        apiPort.Minimum = 1024; apiPort.Maximum = 65535;
        apiPort.SetBounds(235, y - 4, 135, 28); Controls.Add(apiPort);
        var portHint = new Label { Text = $"API activa ahora: {activeApiPort}", AutoSize = true, ForeColor = SystemColors.GrayText };
        portHint.SetBounds(382, y, 230, 24); Controls.Add(portHint); y += 42;

        autoStart.Text = "Si no hay conversación, abrir una sesión Realtime para hablar";
        autoStart.SetBounds(18, y, 650, 26); Controls.Add(autoStart); y += 31;
        keepOpen.Text = "Dejar la sesión abierta después del anuncio para poder responder";
        keepOpen.SetBounds(18, y, 650, 26); Controls.Add(keepOpen); y += 31;
        requireSource.Text = "Restringir solicitudes remotas a la IP de Home Assistant (localhost siempre permitido)";
        requireSource.SetBounds(18, y, 690, 26); Controls.Add(requireSource); y += 42;

        AddSection("Autenticación de Home Assistant", ref y);
        AddLabel("Long-Lived Access Token", 18, y);
        token.SetBounds(235, y - 4, 330, 28); token.UseSystemPasswordChar = true; Controls.Add(token);
        var clearToken = new Button { Text = "Borrar", Width = 78, Height = 28 };
        clearToken.SetBounds(575, y - 4, 78, 28);
        clearToken.Click += (_, _) => { RealtimeMirrorSettings.ClearHomeAssistantAccessToken(); token.Clear(); UpdateTokenStatus(); };
        Controls.Add(clearToken); y += 31;
        tokenStatus.SetBounds(235, y, 430, 24); tokenStatus.ForeColor = SystemColors.GrayText; Controls.Add(tokenStatus); y += 31;

        testHa.Text = "Probar conexión HA"; testHa.SetBounds(235, y, 160, 30); Controls.Add(testHa);
        var haResult = new Label { AutoSize = false, ForeColor = SystemColors.GrayText };
        haResult.SetBounds(405, y + 5, 280, 42); Controls.Add(haResult);
        testHa.Click += async (_, _) => await RunBusyAsync(testHa, "Probando…", async () =>
        {
            SaveConnectionForTests();
            var result = await HomeAssistantMediaClient.TestConnectionAsync();
            haResult.Text = "OK · " + result;
        }, ex => haResult.Text = "Error · " + ex.Message);
        y += 52;

        AddSection("Mirror de audio por Home Assistant", ref y);
        mirrorEnabled.Text = "Reproducir también la voz de Sol en un media_player de Home Assistant";
        mirrorEnabled.SetBounds(18, y, 670, 26); Controls.Add(mirrorEnabled); y += 31;
        announce.Text = "Usar modo anuncio cuando el media_player lo soporte";
        announce.SetBounds(18, y, 560, 26); Controls.Add(announce); y += 36;

        AddLabel("media_player", 18, y);
        player.SetBounds(235, y - 4, 330, 28); player.DropDownStyle = ComboBoxStyle.DropDown; Controls.Add(player);
        refreshPlayers.Text = "Actualizar"; refreshPlayers.SetBounds(575, y - 4, 90, 28); Controls.Add(refreshPlayers);
        refreshPlayers.Click += async (_, _) => await RefreshPlayersAsync(); y += 40;

        testMirror.Text = "Probar stream LIVE"; testMirror.SetBounds(235, y, 160, 30); Controls.Add(testMirror);
        mirrorStatus.SetBounds(405, y + 4, 290, 48); mirrorStatus.ForeColor = SystemColors.GrayText; Controls.Add(mirrorStatus);
        testMirror.Click += async (_, _) => await RunBusyAsync(testMirror, "Probando…", async () =>
        {
            SaveConnectionForTests();
            var result = await RealtimeSecondaryAudioMirror.TestHomeAssistantMirrorAsync();
            mirrorStatus.Text = "OK · " + result;
        }, ex => mirrorStatus.Text = "Error · " + ex.Message);
        y += 58;

        AddInfo("El test LIVE usa exactamente el camino real: esta PC codifica MP3 → /api/realtime-mirror.mp3 → Home Assistant play_media → el media_player vuelve a buscar el stream en esta PC. Si falla, el log muestra la URL LAN y si el reproductor llegó con HEAD/GET.", ref y, 60);

        AddSection("Probar API de voz", ref y);
        speechText.Multiline = true;
        speechText.Text = "Prueba de Home Assistant. Si escuchás esto, la API de Sol funciona correctamente.";
        speechText.SetBounds(18, y, 540, 58); Controls.Add(speechText);
        testSpeech.Text = "Enviar /api/speak"; testSpeech.SetBounds(570, y, 125, 30); Controls.Add(testSpeech); y += 64;
        speechStatus.SetBounds(18, y, 675, 42); speechStatus.ForeColor = SystemColors.GrayText; Controls.Add(speechStatus);
        testSpeech.Click += async (_, _) => await RunBusyAsync(testSpeech, "Enviando…", async () =>
        {
            var result = await HomeAssistantMediaClient.PostLocalSpeechTestAsync(speechText.Text, activeApiPort);
            speechStatus.Text = "OK · " + result;
        }, ex => speechStatus.Text = "Error · " + ex.Message);
        y += 48;

        AddInfo($"POST /api/speak y /api/tts aceptan {{ \"text\": \"...\" }}. El botón prueba el endpoint local realmente activo en el puerto {activeApiPort}. Si cambiás el puerto arriba, reiniciá el companion antes de probar el nuevo puerto.", ref y, 54);

        LoadFromSettings();
    }

    public void LoadFromSettings()
    {
        enabled.Checked = AppSettings.HomeAssistantEnabled;
        baseUrl.Text = AppSettings.HomeAssistantBaseUrl;
        apiPort.Value = AppSettings.HomeAssistantApiPort;
        autoStart.Checked = AppSettings.HomeAssistantAutoStartSpeechSession;
        keepOpen.Checked = AppSettings.HomeAssistantKeepSpeechSessionOpen;
        requireSource.Checked = AppSettings.HomeAssistantRequireSourceMatch;
        token.Clear();
        mirrorEnabled.Checked = RealtimeMirrorSettings.HomeAssistantMirrorEnabled;
        announce.Checked = RealtimeMirrorSettings.HomeAssistantMirrorAnnounce;
        player.Text = RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity;
        UpdateTokenStatus();
    }

    public bool SaveToSettings(out string error)
    {
        error = "";
        var normalized = AppSettings.NormalizeBaseUrl(baseUrl.Text);
        if (normalized is null)
        {
            error = "La URL de Home Assistant no es válida.";
            return false;
        }

        var entity = SelectedEntity();
        if (mirrorEnabled.Checked && string.IsNullOrWhiteSpace(entity))
        {
            error = "Seleccioná un media_player para habilitar el mirror de Home Assistant.";
            return false;
        }

        AppSettings.HomeAssistantEnabled = enabled.Checked;
        AppSettings.HomeAssistantBaseUrl = normalized;
        AppSettings.HomeAssistantApiPort = (int)apiPort.Value;
        AppSettings.HomeAssistantAutoStartSpeechSession = autoStart.Checked;
        AppSettings.HomeAssistantKeepSpeechSessionOpen = keepOpen.Checked;
        AppSettings.HomeAssistantRequireSourceMatch = requireSource.Checked;
        RealtimeMirrorSettings.HomeAssistantMirrorEnabled = mirrorEnabled.Checked;
        RealtimeMirrorSettings.HomeAssistantMirrorAnnounce = announce.Checked;
        RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity = entity;
        if (!string.IsNullOrWhiteSpace(token.Text)) RealtimeMirrorSettings.HomeAssistantAccessToken = token.Text;
        UpdateTokenStatus();
        return true;
    }

    async Task RefreshPlayersAsync()
    {
        await RunBusyAsync(refreshPlayers, "…", async () =>
        {
            SaveConnectionForTests();
            var players = await HomeAssistantMediaClient.GetMediaPlayersAsync();
            var selected = SelectedEntity();
            player.Items.Clear();
            foreach (var item in players) player.Items.Add(item);
            var match = players.FirstOrDefault(p => string.Equals(p.EntityId, selected, StringComparison.OrdinalIgnoreCase));
            if (match is not null) player.SelectedItem = match;
            else if (!string.IsNullOrWhiteSpace(selected)) player.Text = selected;
            else if (players.Count > 0) player.SelectedIndex = 0;
            mirrorStatus.Text = $"{players.Count} media_player encontrados.";
            UpdateTokenStatus();
        }, ex => mirrorStatus.Text = "Error · " + ex.Message);
    }

    void SaveConnectionForTests()
    {
        var normalized = AppSettings.NormalizeBaseUrl(baseUrl.Text)
            ?? throw new InvalidOperationException("La URL de Home Assistant no es válida.");
        AppSettings.HomeAssistantBaseUrl = normalized;
        if (!string.IsNullOrWhiteSpace(token.Text)) RealtimeMirrorSettings.HomeAssistantAccessToken = token.Text;
        var entity = SelectedEntity();
        if (!string.IsNullOrWhiteSpace(entity)) RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity = entity;
        RealtimeMirrorSettings.HomeAssistantMirrorAnnounce = announce.Checked;
    }

    string SelectedEntity()
    {
        if (player.SelectedItem is HomeAssistantMediaPlayerChoice choice) return choice.EntityId;
        return (player.Text ?? "").Trim();
    }

    void UpdateTokenStatus()
    {
        var env = Environment.GetEnvironmentVariable("HOME_ASSISTANT_TOKEN");
        tokenStatus.Text = !string.IsNullOrWhiteSpace(env)
            ? "HOME_ASSISTANT_TOKEN detectado (tiene prioridad)."
            : RealtimeMirrorSettings.HasHomeAssistantAccessToken
                ? "Token guardado cifrado con DPAPI para este usuario."
                : "Sin token configurado.";
    }

    static async Task RunBusyAsync(Button button, string busyText, Func<Task> action, Action<Exception> error)
    {
        var oldText = button.Text;
        button.Enabled = false;
        button.Text = busyText;
        try { await action(); }
        catch (Exception ex) { error(ex); }
        finally { button.Text = oldText; button.Enabled = true; }
    }

    void AddSection(string text, ref int y)
    {
        var label = new Label { Text = text, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AutoSize = true };
        label.SetBounds(12, y, 690, 24); Controls.Add(label); y += 34;
    }

    void AddLabel(string text, int x, int y)
    {
        var label = new Label { Text = text, AutoSize = true };
        label.SetBounds(x, y, 205, 24); Controls.Add(label);
    }

    void AddInfo(string text, ref int y, int height)
    {
        var label = new Label { Text = text, ForeColor = SystemColors.GrayText, AutoSize = false };
        label.SetBounds(18, y, 680, height); Controls.Add(label); y += height + 8;
    }
}
