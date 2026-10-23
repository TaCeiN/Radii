using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Setup;

/// <summary>What the user asked for in the wizard (or what the automatic mode picks).</summary>
internal sealed class SetupOptions
{
    public string Dir { get; set; } = Installer.DefaultDir;
    public bool Autostart { get; set; } = true;
    public bool StartMenu { get; set; } = true;
    public bool Desktop { get; set; }
    public bool Launch { get; set; } = true;
    public bool FFmpeg { get; set; } = true;
    public bool Magick { get; set; } = true;
    public bool Pandoc { get; set; } = true;
}

/// <summary>
/// The installer is this very executable: when it is started under a name like <c>Radii-Setup-1.0.exe</c>
/// (or with <c>--setup</c>) it shows the wizard, copies itself to the install folder as <c>Radii.exe</c>,
/// and can download the conversion engines. Per-user install, no administrator rights needed.
/// </summary>
internal static class Installer
{
    public const string Repo = "TaCeiN/Radii";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Radii";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ExeName = "Radii.exe";

    /// <summary>The name before the rename; whatever it left behind (install, autostart, shortcuts) is cleaned up.</summary>
    private const string LegacyName = "RadialContext";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Radii");

    public static string Version => (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Radii.lnk");

    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Radii.lnk");

    public static bool ShouldRun(string[] args)
    {
        if (args.Any(a => a is "--setup" or "--uninstall" or "--silent"))
            return true;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return name.StartsWith("Radii-Setup", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Radii_Setup", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Folder of the existing installation, or null when Radii is not installed.</summary>
    public static string? InstalledDir()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
    }

    public static string? InstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    public static int Run(string[] args)
    {
        if (args.Contains("--uninstall"))
        {
            if (!args.Contains("--silent"))
                return UninstallInteractive();
            Uninstall(removeData: args.Contains("--purge"));
            return 0;
        }

        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        // --update (used by the in-app updater) keeps what the user chose the first time.
        var options = args.Contains("--update") ? ExistingOptions() : new SetupOptions();
        options.Launch = !args.Contains("--no-launch");
        if (Value("--dir") is { } dir)
            options.Dir = Path.GetFullPath(dir);
        if (args.Contains("--no-engines"))
            options.FFmpeg = options.Magick = options.Pandoc = false;

        if (args.Contains("--silent"))
        {
            // No window: the automatic install, for scripts and package managers.
            try
            {
                InstallApp(options);
                var failures = InstallEnginesAsync(options, CancellationToken.None, (_, _) => { }).GetAwaiter().GetResult();
                if (options.Launch)
                    Launch(options.Dir);
                return failures.Count == 0 ? 0 : 2;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 1;
            }
        }

#pragma warning disable WPF0001
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
        var window = new SetupWindow(options, auto: args.Contains("--auto"));
        return app.Run(window);
    }

    private static SetupOptions ExistingOptions()
    {
        var options = new SetupOptions
        {
            Dir = InstalledDir() ?? DefaultDir,
            StartMenu = File.Exists(StartMenuLink) || File.Exists(LegacyLink(Environment.SpecialFolder.Programs)),
            Desktop = File.Exists(DesktopLink) || File.Exists(LegacyLink(Environment.SpecialFolder.DesktopDirectory)),
        };
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        options.Autostart = run?.GetValue("Radii") is string || run?.GetValue(LegacyName) is string;
        return options;
    }

    /// <summary>True when files can be created in <paramref name="dir"/> without administrator rights.</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Launch(string dir)
    {
        try
        {
            var exe = Path.Combine(dir, ExeName);
            // An elevated installer must not hand its administrator token to the program: explorer starts it as the normal user.
            if (Updates.Updater.IsElevated())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Copies the program, creates shortcuts, autostart and the "Apps &amp; features" entry.</summary>
    public static void InstallApp(SetupOptions options)
    {
        var self = Environment.ProcessPath ?? throw new IOException("Unknown process path");
        var target = Path.Combine(options.Dir, ExeName);
        StopRunning();
        RemoveLegacy();
        Directory.CreateDirectory(options.Dir);
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(self, target);

        SetShortcut(StartMenuLink, options.StartMenu, target, options.Dir);
        SetShortcut(DesktopLink, options.Desktop, target, options.Dir);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (options.Autostart)
                run.SetValue("Radii", $"\"{target}\"");
            else
                run.DeleteValue("Radii", throwOnMissingValue: false);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Radii");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "TaCeiN");
        key.SetValue("InstallLocation", options.Dir);
        key.SetValue("DisplayIcon", target);
        key.SetValue("UninstallString", $"\"{target}\" --uninstall");
        key.SetValue("URLInfoAbout", "https://github.com/" + Repo);
        key.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Downloads the chosen engines one after another. Returns a line per engine that failed.</summary>
    public static async Task<List<string>> InstallEnginesAsync(SetupOptions options, CancellationToken cancel, Action<string, double?> report)
    {
        var jobs = new List<(string Title, long Bytes, Func<ConvertContext, Task> Run)>();
        void Add(bool wanted, Engine engine)
        {
            if (wanted && !Engines.IsInstalled(engine))
                jobs.Add((Engines.TitleOf(engine), Engines.ApproximateBytes(engine), c => Engines.EnsureAsync(engine, c)));
        }
        Add(options.FFmpeg, Engine.FFmpeg);
        Add(options.Magick, Engine.Magick);
        Add(options.Pandoc, Engine.Pandoc);

        var failures = new List<string>();
        var total = Math.Max(1, jobs.Sum(j => j.Bytes));
        long done = 0;
        foreach (var job in jobs)
        {
            cancel.ThrowIfCancellationRequested();
            var finished = done;
            var context = new ConvertContext(cancel, (text, fraction) => report(text, fraction is { } f ? (finished + f * job.Bytes) / total : null));
            try
            {
                await job.Run(context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{job.Title}: {ex.Message}");
            }
            done += job.Bytes;
            report("", (double)done / total);
        }
        return failures;
    }

    private static void StopRunning()
    {
        foreach (var process in Process.GetProcessesByName("Radii").Concat(Process.GetProcessesByName(LegacyName)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private static string LegacyLink(Environment.SpecialFolder folder) => Path.Combine(Environment.GetFolderPath(folder), LegacyName + ".lnk");

    /// <summary>Removes an installation, autostart entry and shortcuts left by the program under its old name.</summary>
    private static void RemoveLegacy()
    {
        var uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyName;
        string? dir;
        using (var key = Registry.CurrentUser.OpenSubKey(uninstallKey))
            dir = key?.GetValue("InstallLocation") as string;
        Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(LegacyName, throwOnMissingValue: false);
        foreach (var link in new[] { LegacyLink(Environment.SpecialFolder.Programs), LegacyLink(Environment.SpecialFolder.DesktopDirectory) })
        {
            try
            {
                File.Delete(link);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (dir is null)
            return;
        try
        {
            File.Delete(Path.Combine(dir, LegacyName + ".exe"));
            Directory.Delete(dir, recursive: false); // only when nothing else is in it
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A copy started from an old autostart entry (the old name) re-registers itself under the new one.</summary>
    public static void MigrateLegacyAutostart()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(LegacyName) is not string)
            return;
        run.DeleteValue(LegacyName, throwOnMissingValue: false);
        if (Environment.ProcessPath is { } path)
            run.SetValue("Radii", $"\"{path}\"");
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void SetShortcut(string link, bool wanted, string target, string workingDirectory)
    {
        try
        {
            if (!wanted)
            {
                File.Delete(link);
                return;
            }
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = target + ",0";
            shortcut.Description = "Radii";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // A missing shortcut is not worth failing the install.
        }
    }

    private static int UninstallInteractive()
    {
        var yes = MessageBox.Show(
            T("Remove Radii from this computer?", "Удалить Radii с этого компьютера?"),
            "Radii", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (yes != MessageBoxResult.Yes)
            return 0;
        var data = MessageBox.Show(
            T("Also delete settings and the downloaded conversion engines?\n(" + Settings.Folder + ")", "Удалить заодно настройки и скачанные движки конвертации?\n(" + Settings.Folder + ")"),
            "Radii", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Uninstall(data);
        MessageBox.Show(T("Radii was removed.", "Radii удалён."), "Radii", MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

    public static void Uninstall(bool removeData)
    {
        var dir = InstalledDir() ?? DefaultDir;
        StopRunning();
        SetShortcut(StartMenuLink, false, "", "");
        SetShortcut(DesktopLink, false, "", "");
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue("Radii", throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        RemoveLegacy();
        if (removeData)
        {
            try
            {
                Directory.Delete(Settings.Folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        // This process may be the exe inside the folder, so it is removed by a helper after we exit. Only our own exe is
        // deleted and the folder goes only when empty: the folder may be one the user picked, with their files in it.
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{Path.Combine(dir, ExeName)}\" & rmdir \"{dir}\"",
        };
        Process.Start(info)?.Dispose();
    }
}
