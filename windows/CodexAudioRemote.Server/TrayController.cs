using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

static class TrayController
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "CodexAudioRemote";
    const string AppKey = @"Software\CodexAudioRemote";
    const string HomeAssistantUrlName = "HomeAssistantUrl";
    const string DefaultHomeAssistantUrl = "http://homeassistant.local:8123";
    static NotifyIcon? icon;

    public static string HomeAssistantBaseUrl => GetHomeAssistantUrl();

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
        var startup = new ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true, Checked = IsStartupEnabled() };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        var haUrl = new ToolStripMenuItem("Home Assistant URL…");
        haUrl.Click += (_, _) => ShowHomeAssistantUrlDialog();
        var status = new ToolStripMenuItem("Codex Audio Remote activo") { Enabled = false };
        var exit = new ToolStripMenuItem("Salir");
        exit.Click += (_, _) =>
        {
            try { icon?.Dispose(); } catch { }
            Environment.Exit(0);
        };

        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startup);
        menu.Items.Add(haUrl);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Codex Audio Remote",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => icon.ShowBalloonTip(1200, "Codex Audio Remote", "Servidor activo en segundo plano", ToolTipIcon.Info);

        Application.Run();
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
        var box = new TextBox { Left = 12, Top = 38, Width = 490, Text = GetHomeAssistantUrl() };
        var save = new Button { Left = 332, Top = 78, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 422, Top = 78, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, box, save, cancel });
        form.AcceptButton = save;
        form.CancelButton = cancel;
        if (form.ShowDialog() != DialogResult.OK) return;
        var normalized = NormalizeBaseUrl(box.Text);
        if (normalized is null)
        {
            MessageBox.Show("Ingresá una URL válida, por ejemplo http://homeassistant.local:8123", "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SetHomeAssistantUrl(normalized);
        icon?.ShowBalloonTip(1200, "Codex Audio Remote", "Home Assistant: " + normalized, ToolTipIcon.Info);
    }

    static string GetHomeAssistantUrl()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey, false);
            var value = key?.GetValue(HomeAssistantUrlName) as string;
            return NormalizeBaseUrl(value) ?? DefaultHomeAssistantUrl;
        }
        catch { return DefaultHomeAssistantUrl; }
    }

    static void SetHomeAssistantUrl(string url)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey, true);
            key.SetValue(HomeAssistantUrlName, url);
        }
        catch { }
    }

    static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(RunName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    static void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (!enabled) { key.DeleteValue(RunName, false); return; }
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe)) key.SetValue(RunName, "\"" + exe + "\"");
        }
        catch { }
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
