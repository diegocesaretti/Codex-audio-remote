using System.Drawing;
using System.Windows.Forms;

internal sealed class SecondaryOutputSettingsForm : Form
{
    readonly CheckBox windowsEnabled = new();
    readonly Label windowsDevice = new();

    public static void ShowSettings()
    {
        using var form = new SecondaryOutputSettingsForm();
        form.ShowDialog();
    }

    SecondaryOutputSettingsForm()
    {
        Text = "Realtime · Mirror Windows / Bluetooth";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(700, 285);

        var y = 20;
        AddTitle("Salida principal", ref y);
        AddInfo("Android SIEMPRE reproduce la respuesta. Este diálogo sólo agrega una copia en paralelo a un dispositivo de audio de Windows/Bluetooth.", ref y, 50);

        AddTitle("Mirror Windows / Bluetooth", ref y);
        windowsEnabled.Text = "Reproducir también en un dispositivo de audio de Windows";
        windowsEnabled.SetBounds(24, y, 620, 26); Controls.Add(windowsEnabled); y += 36;

        var choose = new Button { Text = "Elegir dispositivo Windows / Bluetooth…", Width = 300, Height = 30 };
        choose.SetBounds(24, y, 300, 30);
        choose.Click += (_, _) => { DownlinkDeviceSettings.ShowDialog(); UpdateWindowsLabel(); };
        Controls.Add(choose);
        windowsDevice.SetBounds(336, y + 5, 335, 26); Controls.Add(windowsDevice); y += 54;

        AddInfo("La configuración de Home Assistant, token, media_player y pruebas de stream están ahora en Configuración → Home Assistant.", ref y, 44);

        var save = new Button { Text = "Guardar", Width = 100, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        save.SetBounds(466, 238, 100, 32); cancel.SetBounds(576, 238, 100, 32);
        save.Click += (_, _) => RealtimeMirrorSettings.WindowsMirrorEnabled = windowsEnabled.Checked;
        Controls.Add(save); Controls.Add(cancel);
        AcceptButton = save; CancelButton = cancel;

        windowsEnabled.Checked = RealtimeMirrorSettings.WindowsMirrorEnabled;
        UpdateWindowsLabel();
    }

    void UpdateWindowsLabel()
    {
        windowsDevice.Text = string.IsNullOrWhiteSpace(DownlinkDeviceSettings.SelectedDeviceName)
            ? "Sin dispositivo secundario seleccionado"
            : DownlinkDeviceSettings.SelectedDeviceName;
    }

    void AddTitle(string text, ref int y)
    {
        var label = new Label { Text = text, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AutoSize = true };
        label.SetBounds(18, y, 650, 24); Controls.Add(label); y += 32;
    }

    void AddInfo(string text, ref int y, int height)
    {
        var label = new Label { Text = text, ForeColor = SystemColors.GrayText, AutoSize = false };
        label.SetBounds(24, y, 650, height); Controls.Add(label); y += height + 8;
    }
}
