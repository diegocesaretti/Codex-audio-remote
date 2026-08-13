using NAudio.CoreAudioApi;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

internal static class DownlinkDeviceSettings
{
    static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexAudioRemote");
    static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static string? SelectedDeviceId => Load().DownlinkDeviceId;

    public static bool IsUnsafe(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public static void ShowDialog()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => !IsUnsafe(d.FriendlyName))
            .OrderBy(d => d.FriendlyName)
            .ToList();
        var choices = devices.Select(d => new Choice(d.ID, d.FriendlyName)).ToList();

        using var form = new Form
        {
            Text = "Audio de respuesta / Downlink",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 170)
        };
        var label = new Label { Left = 12, Top = 14, Width = 530, Height = 34, Text = "Elegí dónde se escucha Codex. CABLE/VB-Audio no aparecen para evitar loops de eco." };
        var combo = new ComboBox { Left = 12, Top = 54, Width = 530, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.DisplayMember = nameof(Choice.Name);
        foreach (var choice in choices) combo.Items.Add(choice);
        var saved = SelectedDeviceId;
        var index = choices.FindIndex(c => string.Equals(c.Id, saved, StringComparison.Ordinal));
        if (index >= 0) combo.SelectedIndex = index;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        var save = new Button { Left = 372, Top = 116, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 462, Top = 116, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, combo, save, cancel });
        form.AcceptButton = save;
        form.CancelButton = cancel;

        if (form.ShowDialog() == DialogResult.OK && combo.SelectedItem is Choice selected)
        {
            var settings = Load();
            settings.DownlinkDeviceId = selected.Id;
            Save(settings);
            MessageBox.Show("Audio de respuesta: " + selected.Name + "\nSe aplicará en la próxima conversación.", "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    sealed class Settings { public string? DownlinkDeviceId { get; set; } }
    sealed record Choice(string Id, string Name);
}
