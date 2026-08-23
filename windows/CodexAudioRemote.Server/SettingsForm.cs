using System.Drawing;
using System.Windows.Forms;

internal sealed class SettingsForm : Form
{
    readonly CheckBox startup = new();
    readonly ComboBox backend = new();
    readonly TextBox realtimeCwd = new();
    readonly ComboBox voice = new();
    readonly NumericUpDown wakeCooldown = new();
    readonly HomeAssistantSettingsPanel haPanel = new();
    readonly TextBox codexExe = new();
    readonly Label restartHint = new();

    public static void ShowSettings()
    {
        using var form = new SettingsForm();
        form.ShowDialog();
    }

    SettingsForm()
    {
        Text = "Codex Audio Remote · Configuración";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 640);
        ClientSize = new Size(850, 690);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 7) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildRealtimeTab());
        tabs.TabPages.Add(BuildHomeAssistantTab());
        tabs.TabPages.Add(BuildAudioTab());
        tabs.TabPages.Add(BuildAdvancedTab());

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58 };
        restartHint.SetBounds(14, 18, 515, 28);
        restartHint.Text = "Algunos cambios se aplican en la próxima sesión; puerto/backend requieren reiniciar.";
        restartHint.ForeColor = SystemColors.GrayText;
        var save = new Button { Text = "Guardar", Width = 100, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        var cancel = new Button { Text = "Cancelar", Width = 100, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top, DialogResult = DialogResult.Cancel };
        save.SetBounds(630, 14, 100, 30);
        cancel.SetBounds(740, 14, 100, 30);
        save.Click += (_, _) => SaveAndClose();
        bottom.Controls.AddRange(new Control[] { restartHint, save, cancel });

        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = save;
        CancelButton = cancel;
        LoadValues();
    }

    TabPage BuildGeneralTab()
    {
        var tab = NewTab("General");
        var y = 24;
        AddSection(tab, "Inicio y backend", ref y);
        startup.Text = "Iniciar Codex Audio Remote con Windows";
        startup.SetBounds(26, y, 430, 26); tab.Controls.Add(startup); y += 42;

        AddLabel(tab, "Backend de voz", 26, y);
        backend.DropDownStyle = ComboBoxStyle.DropDownList;
        backend.Items.AddRange(new object[] { "Realtime V3 · Codex oficial + OAuth", "Clásico · Codex Desktop + cable virtual" });
        backend.SetBounds(235, y - 4, 430, 30); tab.Controls.Add(backend); y += 48;

        AddLabel(tab, "Carpeta de trabajo Realtime", 26, y);
        realtimeCwd.SetBounds(235, y - 4, 430, 28); tab.Controls.Add(realtimeCwd);
        var browse = new Button { Text = "…", Width = 42, Height = 28 };
        browse.SetBounds(675, y - 4, 42, 28);
        browse.Click += (_, _) => BrowseFolder(realtimeCwd);
        tab.Controls.Add(browse); y += 52;

        AddInfo(tab, "La carpeta elegida se usa como cwd de los threads Realtime nuevos. Cambiar backend requiere reiniciar el companion.", 26, y, 700);
        return tab;
    }

    TabPage BuildRealtimeTab()
    {
        var tab = NewTab("Realtime / Voz");
        var y = 24;
        AddSection(tab, "Sesión oficial Codex Realtime", ref y);
        AddLabel(tab, "Voz", 26, y);
        voice.DropDownStyle = ComboBoxStyle.DropDownList;
        voice.Items.AddRange(AppSettings.SupportedRealtimeVoices.Cast<object>().ToArray());
        voice.SetBounds(235, y - 4, 230, 30); tab.Controls.Add(voice); y += 46;

        AddReadOnly(tab, "Modelo", AppSettings.DefaultRealtimeModel, ref y);
        AddReadOnly(tab, "Protocolo", "V3 / Frameless · WebRTC", ref y);
        AddReadOnly(tab, "Autenticación", "Login ChatGPT OAuth de Codex", ref y);

        AddLabel(tab, "Cooldown de reintento wake", 26, y);
        wakeCooldown.Minimum = 0; wakeCooldown.Maximum = 30000; wakeCooldown.Increment = 250;
        wakeCooldown.SetBounds(235, y - 4, 140, 28); tab.Controls.Add(wakeCooldown);
        var ms = new Label { Text = "ms", AutoSize = true }; ms.SetBounds(383, y + 1, 40, 24); tab.Controls.Add(ms); y += 48;

        var lifecycle = new Button { Text = "Configurar fin de escucha y fin de conversación…", Width = 380, Height = 34 };
        lifecycle.SetBounds(26, y, 380, 34);
        lifecycle.Click += (_, _) => RealtimeLifecycleSettingsForm.ShowSettings();
        tab.Controls.Add(lifecycle); y += 48;
        AddInfo(tab, "Fin de escucha cierra sólo el micrófono y conserva el contexto; la wake word reanuda la misma sesión. Fin de conversación recién cierra Realtime.", 26, y, 700, 44); y += 48;
        AddInfo(tab, "La voz se aplica al crear la próxima sesión. 'sol' usa la voz nativa Sol de Realtime; no interviene ningún TTS externo.", 26, y, 700);
        return tab;
    }

    TabPage BuildHomeAssistantTab()
    {
        var tab = NewTab("Home Assistant");
        haPanel.Location = new Point(8, 8);
        haPanel.Width = 735;
        haPanel.Height = 800;
        haPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        tab.Controls.Add(haPanel);
        tab.AutoScrollMinSize = new Size(0, 830);
        return tab;
    }

    TabPage BuildAudioTab()
    {
        var tab = NewTab("Audio");
        var y = 24;
        AddSection(tab, "Salida principal", ref y);
        AddInfo(tab, "Android SIEMPRE reproduce la respuesta Realtime. Ninguna salida secundaria puede reemplazar ni bloquear al satélite Android.", 26, y, 700, 44); y += 52;

        AddSection(tab, "Mirror Windows / Bluetooth", ref y);
        var secondary = new Button { Text = "Configurar dispositivo secundario Windows / Bluetooth…", Width = 410, Height = 34 };
        secondary.SetBounds(26, y, 410, 34);
        secondary.Click += (_, _) => SecondaryOutputSettingsForm.ShowSettings();
        tab.Controls.Add(secondary); y += 54;
        AddInfo(tab, "La configuración del mirror Home Assistant ahora está unificada en la pestaña Home Assistant, junto al token, media_player y pruebas de API/stream.", 26, y, 700, 44); y += 52;

        AddSection(tab, "Pipeline", ref y);
        AddReadOnly(tab, "Mic Android → Realtime", "PCM16 mono · sample rate informado por Android", ref y);
        AddReadOnly(tab, "Realtime → Android", "WebRTC audio · PCM16 16 kHz al satélite", ref y);
        AddReadOnly(tab, "Data channel", "oai-events · control / transcripts", ref y);
        AddInfo(tab, "Los controles de fuente, ganancia, calidad, chunk, latencia y sonidos de estado siguen perteneciendo al satélite Android.", 26, y + 8, 700);
        return tab;
    }

    TabPage BuildAdvancedTab()
    {
        var tab = NewTab("Avanzado");
        var y = 24;
        AddSection(tab, "Codex oficial", ref y);
        AddLabel(tab, "Override codex.exe", 26, y);
        codexExe.SetBounds(235, y - 4, 430, 28); tab.Controls.Add(codexExe);
        var browse = new Button { Text = "…", Width = 42, Height = 28 };
        browse.SetBounds(675, y - 4, 42, 28);
        browse.Click += (_, _) => BrowseCodexExe();
        tab.Controls.Add(browse); y += 50;
        AddInfo(tab, "Vacío = autodetección. Se buscan instalaciones oficiales de Codex y luego PATH.", 26, y, 700); y += 54;

        AddSection(tab, "Herramientas", ref y);
        var copy = new Button { Text = "Copiar resumen de configuración", Width = 260, Height = 32 };
        copy.SetBounds(26, y, 260, 32);
        copy.Click += (_, _) => { try { Clipboard.SetText(AppSettings.Summary() + "; " + RealtimeMirrorSettings.Summary()); } catch { } };
        tab.Controls.Add(copy);
        var reset = new Button { Text = "Restaurar valores recomendados", Width = 260, Height = 32 };
        reset.SetBounds(300, y, 260, 32);
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show("¿Restaurar la configuración recomendada?", "Codex Audio Remote", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            AppSettings.ResetDefaults();
            RealtimeMirrorSettings.WindowsMirrorEnabled = false;
            RealtimeMirrorSettings.HomeAssistantMirrorEnabled = false;
            LoadValues();
        };
        tab.Controls.Add(reset); y += 54;
        AddInfo(tab, "Los secretos OAuth no se muestran ni se almacenan aquí. El login y la renovación de tokens siguen siendo propiedad de Codex. El token HA se protege con DPAPI salvo que uses HOME_ASSISTANT_TOKEN.", 26, y, 700, 54);
        return tab;
    }

    void LoadValues()
    {
        startup.Checked = AppSettings.StartupEnabled;
        backend.SelectedIndex = AppSettings.VoiceBackend == AppSettings.RealtimeV3Backend ? 0 : 1;
        realtimeCwd.Text = AppSettings.RealtimeWorkingDirectory;
        voice.SelectedItem = AppSettings.RealtimeVoice;
        if (voice.SelectedIndex < 0) voice.SelectedItem = "sol";
        wakeCooldown.Value = AppSettings.WakeRetryCooldownMs;
        haPanel.LoadFromSettings();
        codexExe.Text = AppSettings.CodexExecutableOverride;
    }

    void SaveAndClose()
    {
        if (!string.IsNullOrWhiteSpace(realtimeCwd.Text) && !Directory.Exists(realtimeCwd.Text))
        {
            MessageBox.Show("La carpeta de trabajo Realtime no existe.", "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!string.IsNullOrWhiteSpace(codexExe.Text) && !File.Exists(codexExe.Text.Trim().Trim('"')))
        {
            MessageBox.Show("El override de codex.exe no existe. Dejalo vacío para usar autodetección.", "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!haPanel.SaveToSettings(out var haError))
        {
            MessageBox.Show(haError, "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AppSettings.StartupEnabled = startup.Checked;
        AppSettings.VoiceBackend = backend.SelectedIndex == 0 ? AppSettings.RealtimeV3Backend : AppSettings.ClassicBackend;
        AppSettings.RealtimeWorkingDirectory = realtimeCwd.Text;
        AppSettings.RealtimeVoice = voice.SelectedItem?.ToString() ?? "sol";
        AppSettings.WakeRetryCooldownMs = (int)wakeCooldown.Value;
        AppSettings.CodexExecutableOverride = codexExe.Text;

        DialogResult = DialogResult.OK;
        Close();
    }

    static TabPage NewTab(string text) => new(text) { AutoScroll = true, Padding = new Padding(8) };

    static void AddSection(Control parent, string text, ref int y)
    {
        var label = new Label { Text = text, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AutoSize = true };
        label.SetBounds(20, y, 710, 24); parent.Controls.Add(label); y += 38;
    }

    static void AddLabel(Control parent, string text, int x, int y)
    {
        var label = new Label { Text = text, AutoSize = true };
        label.SetBounds(x, y, 200, 24); parent.Controls.Add(label);
    }

    static void AddReadOnly(Control parent, string label, string value, ref int y)
    {
        AddLabel(parent, label, 26, y);
        var box = new TextBox { ReadOnly = true, Text = value, BackColor = SystemColors.Control };
        box.SetBounds(235, y - 4, 430, 28); parent.Controls.Add(box); y += 44;
    }

    static void AddInfo(Control parent, string text, int x, int y, int width, int height = 42)
    {
        var label = new Label { Text = text, ForeColor = SystemColors.GrayText, AutoSize = false };
        label.SetBounds(x, y, width, height); parent.Controls.Add(label);
    }

    static void BrowseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { Description = "Carpeta de trabajo para Codex Realtime", ShowNewFolderButton = true };
        if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
        if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    void BrowseCodexExe()
    {
        using var dialog = new OpenFileDialog { Filter = "Codex executable|codex.exe|Executable|*.exe|Todos|*.*", CheckFileExists = true };
        if (File.Exists(codexExe.Text)) dialog.FileName = codexExe.Text;
        if (dialog.ShowDialog() == DialogResult.OK) codexExe.Text = dialog.FileName;
    }
}
