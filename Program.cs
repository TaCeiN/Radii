using System.IO;
using System.Windows;
using static Radii.Native;

namespace Radii;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The release file Radii-Setup-x.y.z.exe is this same program: under that name it is the installer.
        if (args.Length >= 2 && args[0] == "--setup-snapshot")
            return Setup.SetupWindow.RenderTo(args[1], args.Length > 2 ? args[2] : "mode", args.Length > 3 ? args[3] : null);
        if (args.Length >= 2 && args[0] == "--update-snapshot")
            return Updates.UpdateDialog.RenderTo(args[1]);
        if (Setup.Installer.ShouldRun(args))
            return Setup.Installer.Run(args);

        // Diagnostics that need no hook and no tray: render previews, dump a shell menu.
        if (args.Length >= 2 && args[0] == "--snapshot")
            return Snapshot.Render(args[1]);
        if (args.Length >= 3 && args[0] == "--snapshot-flyout")
            return Snapshot.RenderFlyout(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--snapshot-settings")
        {
#pragma warning disable WPF0001
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
            return SettingsWindow.RenderTo(args[1]);
        }
        if (args.Length >= 4 && args[0] == "--convert")
        {
            // --convert out.txt <formatId> <path>...   runs one conversion without UI; the result goes to out.txt
            var result = Converters.ConvertMenu.RunHeadlessAsync(args[3..], args[2]).GetAwaiter().GetResult();
            File.WriteAllText(args[1], result);
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--toast-test")
        {
            // Shows a progress toast, then a result toast, and exits after a few seconds: eyeball the look in the corner.
#pragma warning disable WPF0001
            var test = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
            var handle = Toast.Begin("photo.png → WebP", "Converting photo.png", new CancellationTokenSource());
            var step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                step++;
                if (step < 8)
                    handle.Update($"Downloading FFmpeg (one time): {step * 12} / 100 MB", step / 8.0);
                else if (step == 8)
                    handle.Done("Saved photo.webp", args.Length > 1 ? args[1] : null);
                else if (step == 9)
                    Toast.Show("Radii", "Short notice");
                else if (step == 10)
                    Toast.Begin("clip.mkv → MP4", "x", null).Fail("FFmpeg failed: Unknown encoder");
                else if (step > 30)
                    test.Shutdown();
            };
            timer.Start();
            return test.Run();
        }
        if (args.Length >= 3 && args[0] == "--snapshot-convert")
            return Snapshot.RenderConvert(args[1], args[2..]);
        if (args.Length >= 3 && args[0] == "--shell-dump")
        {
            // --shell-dump out.txt <path>   selection;   --shell-dump out.txt <folder> bg   folder background
            var background = args.Length >= 4 && args[3] == "bg";
            File.WriteAllText(args[1], background ? ShellMenuHost.Dump([], args[2]) : ShellMenuHost.Dump([args[2]], null));
            return 0;
        }

        using var mutex = new Mutex(true, "Radii.SingleInstance", out var first);
        if (!first)
            return 0;

#pragma warning disable WPF0001 // Fluent theme is still marked experimental.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
        using var hook = new InputHook(app.Dispatcher);
        var window = new RadialWindow(hook);
        // An exception in a menu callback must not take the whole program (and with it the right click) down.
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = true;
            window.HideMenu();
        };
        var controller = new MenuController(window);
        hook.Enabled = Settings.Current.Enabled;
        hook.IsInsideRing = window.ContainsRing;
        hook.IsInsideMenu = window.Contains;
        hook.DismissRequested += window.HideMenu;
        hook.Pressed += controller.Press;
        hook.ConvertRequested += controller.OpenConverter;
        hook.ShiftConvert = Settings.Current.ShiftConvert;
        hook.SetHotkey(Settings.Current.ConvertHotkey);
        Converters.ConvertHost.Show = (items, summary, context) => app.Dispatcher.BeginInvoke(() =>
        {
            window.ShowAt(items, Theme.Current(), summary, window.LastCursor, context);
        });
        hook.Released += controller.Release;
        hook.Wheel += window.HandleWheel;
        hook.KeyPressed += window.HandleKey;
        Settings.Changed += () =>
        {
            hook.Enabled = Settings.Current.Enabled;
            hook.ShiftConvert = Settings.Current.ShiftConvert;
            hook.SetHotkey(Settings.Current.ConvertHotkey);
        };

        using var tray = new Tray(app);
        hook.Start();
        ContextDetector.Warmup();
        Setup.Installer.MigrateLegacyAutostart();
        Updates.Updater.StartBackground();
        app.Run();
        return 0;
    }

    internal static void LogCrash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "errors.log"), $"{DateTime.Now:s}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
