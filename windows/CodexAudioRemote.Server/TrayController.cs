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
    static NotifyIcon? icon;

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
