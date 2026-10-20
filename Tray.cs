using System.Windows;

namespace Radii;

/// <summary>Tray icon: a left click opens the settings window, the menu toggles the ring and exits.</summary>
internal sealed class Tray : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggle;

    public Tray(Application app)
    {
        var settings = new System.Windows.Forms.ToolStripMenuItem(Menus.T("Settings", "Настройки"));
        settings.Click += (_, _) => app.Dispatcher.BeginInvoke(SettingsWindow.ShowOrActivate);
        _toggle = new System.Windows.Forms.ToolStripMenuItem(Menus.T("Radial menu enabled", "Круговое меню включено")) { Checked = Settings.Current.Enabled, CheckOnClick = true };
        _toggle.CheckedChanged += (_, _) =>
        {
            Settings.Current.Enabled = _toggle.Checked;
            Settings.Notify();
        };
        var update = new System.Windows.Forms.ToolStripMenuItem(Menus.T("Check for updates", "Проверить обновление"));
        update.Click += (_, _) => app.Dispatcher.BeginInvoke(async () => await Updates.Updater.CheckNowAsync());
        var exit = new System.Windows.Forms.ToolStripMenuItem(Menus.T("Exit", "Выход"));
        exit.Click += (_, _) => app.Dispatcher.BeginInvoke(() => app.Shutdown());
        var hint = new System.Windows.Forms.ToolStripMenuItem(Settings.Current.ShiftConvert ? Menus.T("Shift + right click: converter", "Shift + ПКМ: конвертер") : Menus.T("Shift + right click: native menu", "Shift + ПКМ: обычное меню")) { Enabled = false };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.AddRange([settings, _toggle, update, hint, new System.Windows.Forms.ToolStripSeparator(), exit]);
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Radii",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                app.Dispatcher.BeginInvoke(SettingsWindow.ShowOrActivate);
        };
        Settings.Changed += () =>
        {
            if (_toggle.Checked != Settings.Current.Enabled)
                _toggle.Checked = Settings.Current.Enabled;
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>The app icon (embedded <c>assetspp.ico</c>) at the size the tray wants for the current DPI.</summary>
    private static System.Drawing.Icon CreateIcon()
    {
        using var stream = typeof(Tray).Assembly.GetManifestResourceStream("app.ico");
        return stream is null
            ? System.Drawing.SystemIcons.Application
            : new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
