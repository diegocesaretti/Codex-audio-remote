using System.Drawing;
using System.Windows.Forms;

internal sealed class SecondaryOutputSettingsForm : Form
{
    readonly CheckBox windowsEnabled = new();
    readonly Label windowsDevice = new();
    readonly CheckBox haEnabled = new();
    readonly CheckBox haAnnounce = new();
    readonly TextBox haToken = new();
    readonly Label tokenStatus = new();
    readonly ComboBox haPlayer = new();
    readonly Button refreshPlayers = new();

    public static void ShowSettings()
    {
        using var form = new SecondaryOutputSettingsForm();
        form.ShowDialog();
    }

    SecondaryOutputSettingsForm()
    {
        Text = "Realtime · Salidas secundarias";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 510);

        var y = 20;
        AddTitle("Salida principal", ref y);
        AddInfo("Android SIEMPRE reproduce la respuesta. Las opciones de abajo sólo agregan copias en paralelo; nunca reemplazan ni bloquean al satélite Android.", ref y, 58);

        AddTitle("Mirror Windows / Bluetooth", ref y);
        windowsEnabled.Text = "Reproducir también en un dispositivo de audio de Windows";
        windowsEnabled.SetBounds(24, y, 600, 26); Controls.Add(windowsEnabled); y += 34;
        var choose = new Button { Text = "Elegir dispositivo Windows / Bluetooth…", Width = 300, Height = 30 };
        choose.SetBounds(24, y, 300, 30);
        choose.Click += (_, _) => { DownlinkDeviceSettings.ShowDialog(); UpdateWindowsLabel(); };
        Controls.Add(choose);
        windowsDevice.SetBounds(336, y + 5, 350, 26); Controls.Add(windowsDevice); y += 52;

        AddTitle("Mirror Home Assistant", ref y);
        haEnabled.Text = "Reproducir también en un media_player de Home Assistant";
        haEnabled.SetBounds(24, y, 610, 26); Controls.Add(haEnabled); y += 34;
        haAnnounce.Text = "Usar como anuncio cuando el media_player lo soporte";
        haAnnounce.SetBounds(24, y, 520, 26); Controls.Add(haAnnounce); y += 38;

        var tokenLabel = new Label { Text = "Long-Lived Access Token", AutoSize = true };
        tokenLabel.SetBounds(24, y + 5, 180, 24); Controls.Add(tokenLabel);
        haToken.SetBounds(205, y, 360, 28); haToken.UseSystemPasswordChar = true; Controls.Add(haToken);
        var clear = new Button { Text = "Borrar", Width = 80, Height = 28 };
        clear.SetBounds(575, y, 80, 28);
        clear.Click += (_, _) => { RealtimeMirrorSettings.ClearHomeAssistantAccessToken(); haToken.Clear(); UpdateTokenStatus(); };
        Controls.Add(clear); y += 34;
        tokenStatus.SetBounds(205, y, 450, 22); tokenStatus.ForeColor = SystemColors.GrayText; Controls.Add(tokenStatus); y += 34;

        var playerLabel = new Label { Text = "media_player", AutoSize = true };
        playerLabel.SetBounds(24, y + 5, 170, 24); Controls.Add(playerLabel);
        haPlayer.SetBounds(205, y, 360, 28); haPlayer.DropDownStyle = ComboBoxStyle.DropDown; Controls.Add(haPlayer);
        refreshPlayers.Text = "Actualizar"; refreshPlayers.SetBounds(575, y, 80, 28); Controls.Add(refreshPlayers);
        refreshPlayers.Click += async (_, _) => await RefreshPlayersAsync(); y += 44;

        AddInfo("El mirror HA usa un stream MP3 LIVE servido por esta PC. En Google Cast puede existir más buffer que en Android/BT; esa demora nunca se propaga al Android.", ref y, 52);

        var save = new Button { Text = "Guardar", Width = 100, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        save.SetBounds(486, 460, 100, 32); cancel.SetBounds(596, 460, 100, 32);
        save.Click += (_, _) => SaveValues();
        Controls.Add(save); Controls.Add(cancel);
        AcceptButton = save; CancelButton = cancel;

        LoadValues();
    }

    void LoadValues()
    {
        windowsEnabled.Checked = RealtimeMirrorSettings.WindowsMirrorEnabled;
        haEnabled.Checked = RealtimeMirrorSettings.HomeAssistantMirrorEnabled;
        haAnnounce.Checked = RealtimeMirrorSettings.HomeAssistantMirrorAnnounce;
        haPlayer.Text = RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity;
        UpdateWindowsLabel();
        UpdateTokenStatus();
    }

    void SaveValues()
    {
        RealtimeMirrorSettings.WindowsMirrorEnabled = windowsEnabled.Checked;
        RealtimeMirrorSettings.HomeAssistantMirrorEnabled = haEnabled.Checked;
        RealtimeMirrorSettings.HomeAssistantMirrorAnnounce = haAnnounce.Checked;
        RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity = SelectedEntity();
        if (!string.IsNullOrWhiteSpace(haToken.Text)) RealtimeMirrorSettings.HomeAssistantAccessToken = haToken.Text;
    }

    async Task RefreshPlayersAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(haToken.Text))
                RealtimeMirrorSettings.HomeAssistantAccessToken = haToken.Text;
            refreshPlayers.Enabled = false;
            refreshPlayers.Text = "…";
            var players = await HomeAssistantMediaClient.GetMediaPlayersAsync();
            var selected = SelectedEntity();
            haPlayer.Items.Clear();
            foreach (var player in players) haPlayer.Items.Add(player);
            var match = players.FirstOrDefault(p => string.Equals(p.EntityId, selected, StringComparison.OrdinalIgnoreCase));
            if (match is not null) haPlayer.SelectedItem = match;
            else if (!string.IsNullOrWhiteSpace(selected)) haPlayer.Text = selected;
            else if (players.Count > 0) haPlayer.SelectedIndex = 0;
            UpdateTokenStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Home Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            refreshPlayers.Enabled = true;
            refreshPlayers.Text = "Actualizar";
        }
    }

    string SelectedEntity()
    {
        if (haPlayer.SelectedItem is HomeAssistantMediaPlayerChoice choice) return choice.EntityId;
        return (haPlayer.Text ?? "").Trim();
    }

    void UpdateWindowsLabel()
    {
        windowsDevice.Text = string.IsNullOrWhiteSpace(DownlinkDeviceSettings.SelectedDeviceName)
            ? "Sin dispositivo secundario seleccionado"
            : DownlinkDeviceSettings.SelectedDeviceName;
    }

    void UpdateTokenStatus()
    {
        var env = Environment.GetEnvironmentVariable("HOME_ASSISTANT_TOKEN");
        tokenStatus.Text = !string.IsNullOrWhiteSpace(env)
            ? "Token detectado desde HOME_ASSISTANT_TOKEN (tiene prioridad)."
            : RealtimeMirrorSettings.HasHomeAssistantAccessToken
                ? "Token guardado cifrado con DPAPI para este usuario de Windows."
                : "No hay token configurado; el mirror HA no se iniciará.";
    }

    void AddTitle(string text, ref int y)
    {
        var label = new Label { Text = text, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AutoSize = true };
        label.SetBounds(18, y, 680, 24); Controls.Add(label); y += 32;
    }

    void AddInfo(string text, ref int y, int height)
    {
        var label = new Label { Text = text, ForeColor = SystemColors.GrayText, AutoSize = false };
        label.SetBounds(24, y, 660, height); Controls.Add(label); y += height + 8;
    }
}
