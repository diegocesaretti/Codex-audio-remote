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
    public static string? BtcomPath => Load().BtcomPath;
    public static int BtcomWaitSeconds => Math.Clamp(Load().BtcomWaitSeconds, 1, 15);

    public static bool IsUnsafe(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public static void ShowDialog()
    {
        var choices = new List<Choice>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                try
                {
                    var id = device.ID;
                    var name = device.FriendlyName;
                    var active = device.State == DeviceState.Active;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || IsUnsafe(name)) continue;
                    choices.Add(new Choice(id, active ? name : name + " (desconectado)", name, active));
                }
                catch (Exception ex)
                {
                    // Windows can retain stale Bluetooth/audio endpoints whose property store is
                    // already invalid (for example COM 0xE000020B). One zombie endpoint must not
                    // make the whole output selector crash.
                    Console.WriteLine($"Skipping unreadable audio endpoint in output selector: {ex.GetType().Name} · {ex.Message}");
                }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }

        choices = choices
            .OrderByDescending(c => c.Active)
            .ThenBy(c => c.BaseName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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
            Text = "Salida de Codex / Downlink",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 270)
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

        var btcomLabel = new Label { Left = 12, Top = 112, Width = 530, Text = "btcom.exe (opcional; vacío = detectar automáticamente):" };
        var btcomBox = new TextBox { Left = 12, Top = 136, Width = 530, Text = settings.BtcomPath ?? "" };
        var waitLabel = new Label { Left = 12, Top = 174, Width = 300, Text = "Esperar endpoint Active (segundos):" };
        var wait = new NumericUpDown { Left = 312, Top = 170, Width = 65, Minimum = 1, Maximum = 15, Value = Math.Clamp(settings.BtcomWaitSeconds, 1, 15) };
        var save = new Button { Left = 372, Top = 222, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 462, Top = 222, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, combo, btcomLabel, btcomBox, waitLabel, wait, save, cancel });
        form.AcceptButton = save;
        form.CancelButton = cancel;

        if (form.ShowDialog() == DialogResult.OK && combo.SelectedItem is Choice selected)
        {
            settings.DownlinkDeviceId = selected.Id;
            settings.DownlinkDeviceName = selected.BaseName;
            settings.BtcomPath = string.IsNullOrWhiteSpace(btcomBox.Text) ? null : btcomBox.Text.Trim().Trim('"');
            settings.BtcomWaitSeconds = (int)wait.Value;
            Save(settings);
            var suffix = selected.Active
                ? "\nSe aplicará en la próxima conversación."
                : "\nLa selección queda guardada y se usará automáticamente cuando el dispositivo vuelva a conectarse.";
            MessageBox.Show("Audio de respuesta: " + selected.Name + suffix, "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
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
        public string? BtcomPath { get; set; }
        public int BtcomWaitSeconds { get; set; } = 6;
    }

    sealed record Choice(string Id, string Name, string BaseName, bool Active);
}
