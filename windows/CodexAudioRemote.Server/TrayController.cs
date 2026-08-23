using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

static class TrayController
{
    public const string ClassicBackend = AppSettings.ClassicBackend;
    public const string RealtimeV3Backend = AppSettings.RealtimeV3Backend;
    static NotifyIcon? icon;

    public static string HomeAssistantBaseUrl => AppSettings.HomeAssistantBaseUrl;
    public static string VoiceBackend => AppSettings.VoiceBackend;
    public static string RealtimeWorkingDirectory => AppSettings.RealtimeWorkingDirectory;
    public static string RealtimeVoice => AppSettings.RealtimeVoice;

    [ModuleInitializer]
    public static void Initialize()
    {
        HideConsoleWindow();
        var thread = new Thread(RunTray) { IsBackground = true, Name = "TrayUI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static void RunTray()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var menu = new ContextMenuStrip();
        var settings = new ToolStripMenuItem("Configuración…");
        settings.Font = new Font(settings.Font, FontStyle.Bold);
        settings.Click += (_, _) => SettingsForm.ShowSettings();

        var startup = new ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true, Checked = AppSettings.StartupEnabled };
        startup.CheckedChanged += (_, _) => AppSettings.StartupEnabled = startup.Checked;

        var backendMenu = new ToolStripMenuItem("Backend de voz");
        var classic = new ToolStripMenuItem("Clásico · Codex Desktop + cable virtual") { CheckOnClick = true };
        var realtime = new ToolStripMenuItem("Codex Realtime V3 · OAuth") { CheckOnClick = true };
        void RefreshBackendChecks()
        {
            classic.Checked = VoiceBackend == ClassicBackend;
            realtime.Checked = VoiceBackend == RealtimeV3Backend;
        }
        classic.Click += (_, _) =>
        {
            AppSettings.VoiceBackend = ClassicBackend;
            RefreshBackendChecks();
            icon?.ShowBalloonTip(1500, "Codex Audio Remote", "Backend clásico seleccionado. Reiniciá el companion para aplicarlo.", ToolTipIcon.Info);
        };
        realtime.Click += (_, _) =>
        {
            AppSettings.VoiceBackend = RealtimeV3Backend;
            RefreshBackendChecks();
            icon?.ShowBalloonTip(2000, "Codex Audio Remote", "Codex Realtime V3 seleccionado. Reiniciá el companion para aplicarlo.", ToolTipIcon.Info);
        };
        RefreshBackendChecks();
        backendMenu.DropDownItems.Add(classic);
        backendMenu.DropDownItems.Add(realtime);

        var voiceMenu = new ToolStripMenuItem("Voz Realtime · " + AppSettings.RealtimeVoice);
        voiceMenu.Click += (_, _) => SettingsForm.ShowSettings();

        var realtimeFolder = new ToolStripMenuItem("Carpeta de trabajo Realtime…");
        realtimeFolder.Click += (_, _) => ShowRealtimeWorkingDirectoryDialog();
        var haUrl = new ToolStripMenuItem("Home Assistant URL…");
        haUrl.Click += (_, _) => ShowHomeAssistantUrlDialog();
        var downlink = new ToolStripMenuItem("Audio de respuesta / Downlink…");
        downlink.Click += (_, _) => DownlinkDeviceSettings.ShowDialog();

        var status = new ToolStripMenuItem("Codex Audio Remote activo") { Enabled = false };
        var exit = new ToolStripMenuItem("Salir");
        exit.Click += (_, _) =>
        {
            try { icon?.Dispose(); } catch { }
            Environment.Exit(0);
        };

        menu.Items.Add(status);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startup);
        menu.Items.Add(backendMenu);
        menu.Items.Add(voiceMenu);
        menu.Items.Add(realtimeFolder);
        menu.Items.Add(haUrl);
        menu.Items.Add(downlink);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Codex Audio Remote",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => SettingsForm.ShowSettings();

        Application.Run();
    }

    static void ShowRealtimeWorkingDirectoryDialog()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Elegí la carpeta/proyecto que recibirá las nuevas conversaciones Codex Realtime.",
            UseDescriptionForTitle = true,
            SelectedPath = RealtimeWorkingDirectory,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        AppSettings.RealtimeWorkingDirectory = dialog.SelectedPath;
        icon?.ShowBalloonTip(1800, "Codex Audio Remote", "Carpeta Realtime: " + dialog.SelectedPath, ToolTipIcon.Info);
    }

    static void ShowHomeAssistantUrlDialog()
    {
        using var form = new Form
        {
            Text = "Home Assistant",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(520, 125)
        };
        var label = new Label { Left = 12, Top = 14, Width = 490, Text = "URL base de Home Assistant:" };
        var box = new TextBox { Left = 12, Top = 38, Width = 490, Text = AppSettings.HomeAssistantBaseUrl };
        var save = new Button { Left = 332, Top = 78, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 422, Top = 78, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, box, save, cancel });
        form.AcceptButton = save;
        form.CancelButton = cancel;
        if (form.ShowDialog() != DialogResult.OK) return;
        var normalized = AppSettings.NormalizeBaseUrl(box.Text);
        if (normalized is null)
        {
            MessageBox.Show("Ingresá una URL válida, por ejemplo http://homeassistant.local:8123", "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AppSettings.HomeAssistantBaseUrl = normalized;
        icon?.ShowBalloonTip(1200, "Codex Audio Remote", "Home Assistant: " + normalized, ToolTipIcon.Info);
    }

    static void HideConsoleWindow()
    {
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0);
        }
        catch { }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
