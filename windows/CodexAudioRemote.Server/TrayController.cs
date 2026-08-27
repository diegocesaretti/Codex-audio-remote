using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Drawing;

static class TrayController
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "CodexAudioRemote";
    const string AppKey = @"Software\CodexAudioRemote";
    const string HomeAssistantUrlName = "HomeAssistantUrl";
    const string HomeAssistantTokenName = "HomeAssistantTokenDpapi";
    const string DefaultHomeAssistantUrl = "http://homeassistant.local:8123";
    static NotifyIcon? icon;

    public static string HomeAssistantBaseUrl => GetHomeAssistantUrl();
    public static string HomeAssistantAccessToken => GetHomeAssistantToken();

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
        var ha = new ToolStripMenuItem("Home Assistant WebSocket…");
        ha.Click += (_, _) => ShowHomeAssistantDialog();
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startup);
        menu.Items.Add(ha);
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
        icon.DoubleClick += (_, _) => icon.ShowBalloonTip(1200, "Codex Audio Remote", "Servidor activo en segundo plano", ToolTipIcon.Info);

        Application.Run();
    }

    static void ShowHomeAssistantDialog()
    {
        using var form = new Form
        {
            Text = "Home Assistant · WebSocket cache",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(620, 390)
        };
        var urlLabel = new Label { Left = 12, Top = 14, Width = 590, Text = "URL base de Home Assistant:" };
        var urlBox = new TextBox { Left = 12, Top = 36, Width = 590, Text = GetHomeAssistantUrl() };
        var tokenLabel = new Label { Left = 12, Top = 70, Width = 590, Text = "Long-Lived Access Token (cifrado con DPAPI para este usuario de Windows):" };
        var tokenBox = new TextBox { Left = 12, Top = 92, Width = 590, Text = GetHomeAssistantToken(), UseSystemPasswordChar = true };
        var statusLabel = new Label { Left = 12, Top = 130, Width = 590, Height = 42, Text = "Cache: sin datos" };
        var preview = new TextBox
        {
            Left = 12,
            Top = 176,
            Width = 590,
            Height = 145,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.5f)
        };
        var reconnect = new Button { Left = 12, Top = 338, Width = 150, Text = "Guardar y reconectar" };
        var close = new Button { Left = 522, Top = 338, Width = 80, Text = "Cerrar", DialogResult = DialogResult.Cancel };

        void RefreshStatus()
        {
            var snapshot = HomeAssistantWebSocketCache.Current?.GetSnapshot(8);
            if (snapshot is null)
            {
                statusLabel.Text = "Cache: cliente todavía no iniciado";
                preview.Text = "";
                return;
            }
            var age = snapshot.LastUpdateUtc is null
                ? "sin actualización"
                : Math.Max(0, (DateTimeOffset.UtcNow - snapshot.LastUpdateUtc.Value).TotalSeconds).ToString("0.0") + " s";
            statusLabel.Text = "Cache: " + snapshot.Status + " · entidades " + snapshot.EntityCount + " · última actualización " + age;
            preview.Text = string.Join(Environment.NewLine, snapshot.Preview);
        }

        reconnect.Click += (_, _) =>
        {
            var normalized = NormalizeBaseUrl(urlBox.Text);
            if (normalized is null)
            {
                MessageBox.Show("Ingresá una URL válida, por ejemplo http://homeassistant.local:8123", "Codex Audio Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SetHomeAssistantUrl(normalized);
            SetHomeAssistantToken(tokenBox.Text.Trim());
            HomeAssistantWebSocketCache.Current?.RequestReconnect();
            icon?.ShowBalloonTip(1200, "Codex Audio Remote", "Reconectando cache de Home Assistant…", ToolTipIcon.Info);
            RefreshStatus();
        };

        form.Controls.AddRange(new Control[] { urlLabel, urlBox, tokenLabel, tokenBox, statusLabel, preview, reconnect, close });
        form.CancelButton = close;

        using var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        RefreshStatus();
        form.ShowDialog();
        timer.Stop();
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

    static string GetHomeAssistantToken()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey, false);
            var value = key?.GetValue(HomeAssistantTokenName) as string;
            if (string.IsNullOrWhiteSpace(value)) return "";
            var encrypted = Convert.FromBase64String(value);
            var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch { return ""; }
    }

    static void SetHomeAssistantToken(string token)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey, true);
            if (string.IsNullOrWhiteSpace(token))
            {
                key.DeleteValue(HomeAssistantTokenName, false);
                return;
            }
            var clear = Encoding.UTF8.GetBytes(token);
            try
            {
                var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
                key.SetValue(HomeAssistantTokenName, Convert.ToBase64String(encrypted));
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
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
