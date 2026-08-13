using NAudio.CoreAudioApi;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

internal static class DownlinkDeviceSettings
{
    static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexAudioRemote");
    static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static string? SelectedDeviceId => Load().DownlinkDeviceId;
    public static string? SelectedDeviceName => Load().DownlinkDeviceName;

    public static bool IsUnsafe(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public static void ShowDialog()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All)
            .Where(d => !IsUnsafe(d.FriendlyName))
            .OrderByDescending(d => d.State == DeviceState.Active)
            .ThenBy(d => d.FriendlyName)
            .ToList();

        var choices = devices.Select(d => new Choice(
            d.ID,
            d.State == DeviceState.Active ? d.FriendlyName : d.FriendlyName + " (desconectado)",
            d.FriendlyName,
            d.State == DeviceState.Active)).ToList();

        var settings = Load();
        var saved = settings.DownlinkDeviceId;
        if (!string.IsNullOrWhiteSpace(saved) && !choices.Any(c => string.Equals(c.Id, saved, StringComparison.Ordinal)))
        {
            var rememberedName = string.IsNullOrWhiteSpace(settings.DownlinkDeviceName)
                ? "Dispositivo seleccionado"
                : settings.DownlinkDeviceName;
            choices.Insert(0, new Choice(saved, rememberedName + " (desconectado)", rememberedName, false));
        }

        using var form = new Form
        {
            Text = "Audio de respuesta / Downlink",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 190)
        };
        var label = new Label
        {
            Left = 12,
            Top = 14,
            Width = 530,
            Height = 50,
            Text = "Elegí dónde se escucha Codex. Los Bluetooth emparejados se conservan aunque estén desconectados; al reconectarse vuelven a usarse automáticamente. CABLE/VB-Audio no aparecen para evitar loops de eco."
        };
        var combo = new ComboBox { Left = 12, Top = 72, Width = 530, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.DisplayMember = nameof(Choice.Name);
        foreach (var choice in choices) combo.Items.Add(choice);

        var index = choices.FindIndex(c => string.Equals(c.Id, saved, StringComparison.Ordinal));
        if (index >= 0) combo.SelectedIndex = index;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;

        var save = new Button { Left = 372, Top = 136, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 462, Top = 136, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, combo, save, cancel });
        form.AcceptButton = save;
        form.CancelButton = cancel;

        if (form.ShowDialog() == DialogResult.OK && combo.SelectedItem is Choice selected)
        {
            settings.DownlinkDeviceId = selected.Id;
            settings.DownlinkDeviceName = selected.BaseName;
            Save(settings);
            var suffix = selected.Active
                ? "\nSe aplicará en la próxima conversación."
                : "\nLa selección queda guardada y se usará automáticamente cuando el dispositivo vuelva a conectarse.";
            MessageBox.Show("Audio de respuesta: " + selected.Name + suffix, "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        foreach (var d in devices) d.Dispose();
    }

    static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new Settings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch { return new Settings(); }
    }

    static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    sealed class Settings
    {
        public string? DownlinkDeviceId { get; set; }
        public string? DownlinkDeviceName { get; set; }
    }

    sealed record Choice(string Id, string Name, string BaseName, bool Active);
}
