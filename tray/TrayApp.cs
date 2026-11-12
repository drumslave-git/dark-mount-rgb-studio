using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DarkMountStudio;

/// <summary>The tray icon and its menu. There is no main window; everything else is set in the settings page.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly SettingsStore _store;
    readonly Engine _engine;
    readonly HttpServer _server;
    readonly EventWaitHandle _exitSignal;
    readonly NotifyIcon _icon;
    readonly Icon _iconOn = DrawIcon(Color.FromArgb(0xff, 0x6a, 0x13)), _iconOff = DrawIcon(Color.Gray);
    readonly ToolStripMenuItem _enabled, _autostart;
    readonly System.Windows.Forms.Timer _timer;

    public TrayApp(SettingsStore store, Engine engine, HttpServer server, EventWaitHandle exitSignal)
    {
        _store = store;
        _engine = engine;
        _server = server;
        _exitSignal = exitSignal;

        _enabled = new ToolStripMenuItem("Enabled", null, (_, _) => _store.Update(s => s.Enabled = !s.Enabled));
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => Autostart.Set(!Autostart.IsEnabled()));
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"{Program.AppName} {Program.AppVersion}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabled);
        menu.Items.Add(new ToolStripMenuItem("Open settings", null, (_, _) => OpenSettings()));
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) =>
        {
            _enabled.Checked = _store.Current.Enabled;
            _autostart.Checked = Autostart.IsEnabled();
        };

        Autostart.FollowMove();

        _icon = new NotifyIcon { Icon = _iconOn, ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => OpenSettings();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    void Refresh()
    {
        if (_exitSignal.WaitOne(0)) // another copy was started with --exit
        {
            ExitThread();
            return;
        }
        var s = _store.Current;
        var status = _engine.Status;
        string state = !s.Enabled ? "off"
            : !status.Keyboard ? "keyboard not found"
            : !status.Driving ? "idle"
            : status.Audio != "unavailable" ? "listening" // sound is only captured while an audio effect is in use
            : "running";
        string text = $"{Program.AppName} — {state}";
        if (!_server.Running) text += $" (settings page unavailable: port {HttpServer.Port} is in use)";
        if (_icon.Text != text) _icon.Text = text;
        var icon = s.Enabled && status.Keyboard ? _iconOn : _iconOff;
        if (!ReferenceEquals(_icon.Icon, icon)) _icon.Icon = icon;
    }

    void OpenSettings()
    {
        if (!_server.Running)
        {
            MessageBox.Show(
                $"The settings page could not be started because port {HttpServer.Port} is used by another program.\n" +
                "Close that program and start Dark Mount Studio again.",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(HttpServer.Url) { UseShellExecute = true });
        }
        catch
        {
            // no default browser; nothing useful to do
        }
    }

    // A small equaliser: four bars of different heights.
    static Icon DrawIcon(Color colour)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(colour))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);
            int[] heights = [14, 26, 19, 9];
            for (int i = 0; i < heights.Length; i++) g.FillRectangle(brush, 2 + i * 8, 30 - heights[i], 5, heights[i]);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone(); // the clone owns its own copy
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _iconOn.Dispose();
            _iconOff.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The "Start with Windows" entry under HKCU\...\Run: the one thing the app keeps outside its own
/// folder, and only once the user has ticked the menu item.
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DarkMountStudio";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>If the entry exists but the app's folder has been moved since, points it at where the exe is now.</summary>
    public static void FollowMove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) is string old && old != Command) key.SetValue(ValueName, Command);
        }
        catch
        {
            // not allowed to: the entry stays as it is
        }
    }

    static string Command => $"\"{Environment.ProcessPath}\"";
}
