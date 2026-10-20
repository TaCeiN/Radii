using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Radii.Converters;
using Radii.Setup;
using static Radii.Menus;

namespace Radii.Updates;

internal sealed record UpdateInfo(Version Version, string PageUrl, string Notes, string SetupName, string SetupUrl, long SetupBytes, string? SumsUrl);

/// <summary>
/// The only background network traffic of the program: one HTTPS request to the GitHub Releases API (at start, and once a day
/// while running) that sends nothing but a User-Agent. When a newer release exists the user is asked; "Yes" downloads the
/// installer from the release into the bottom-right toast, checks its SHA-256 against the release's <c>SHA256SUMS.txt</c>
/// and runs it silently over the current installation.
/// </summary>
internal static class Updater
{
    /// <summary>Lets a test point the check at a local server instead of GitHub.</summary>
    private const string TestUrlVariable = "RADII_UPDATE_URL";

    private static bool _prompting;
    private static bool _installing;

    public static Version Current => ParseVersion(Installer.Version) ?? new Version(0, 0, 0);

    private static string ApiUrl => Environment.GetEnvironmentVariable(TestUrlVariable) is { Length: > 0 } url
        ? url
        : $"https://api.github.com/repos/{Installer.Repo}/releases/latest";

    private static Version? ParseVersion(string text)
    {
        var core = text.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Radii/" + Installer.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The newest release when it is newer than this build, null when up to date. Throws on network or format errors.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var client = NewClient();
        using var response = await client.GetAsync(ApiUrl, cancel);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var root = json.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } version || version <= Current)
            return null;

        string? setupName = null, setupUrl = null, sumsUrl = null;
        long setupBytes = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (Regex.IsMatch(name, @"^Radii-Setup-.*\.exe$", RegexOptions.IgnoreCase))
            {
                setupName = name;
                setupUrl = url;
                setupBytes = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sumsUrl = url;
            }
        }
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        if (setupName is null || setupUrl is null)
            return null; // a release without the installer is not an update yet
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, page, notes, setupName, setupUrl, setupBytes, sumsUrl);
    }

    /// <summary>Starts the periodic check: a first one shortly after start (the network may not be up right at logon), then daily.</summary>
    public static void StartBackground()
    {
        var ui = Application.Current.Dispatcher;
        _ = Task.Run(() =>
        {
            // Leftovers of a finished update (the downloaded installer).
            try
            {
                Directory.Delete(Path.Combine(Path.GetTempPath(), "Radii-update"), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
        var timer = new DispatcherTimer(TimeSpan.FromHours(24), DispatcherPriority.Background, async (_, _) => await AutoCheckAsync(), ui);
        timer.Start();
        _ = Task.Run(async () =>
        {
            // Retry a couple of times only when the network was not reachable.
            foreach (var delay in new[] { 20, 60, 180 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (await AutoCheckAsync())
                    return;
            }
        });
    }

    /// <summary>True when the check is finished (done, or switched off); false when the network failed and a retry makes sense.</summary>
    private static async Task<bool> AutoCheckAsync()
    {
        if (!Settings.Current.CheckUpdates)
            return true;
        try
        {
            if (await CheckAsync() is { } update)
                await Application.Current.Dispatcher.InvokeAsync(() => Prompt(update));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The "Check for updates" button: always answers with a toast or the update window.</summary>
    public static async Task CheckNowAsync()
    {
        var title = T("Updates", "Обновления");
        Toast.Show(title, T("Checking…", "Проверяю…"));
        try
        {
            if (await CheckAsync() is { } update)
                Prompt(update);
            else
                Toast.Show(title, T($"You have the latest version ({Installer.Version})", $"У вас последняя версия ({Installer.Version})"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Toast.Show(title, T("Could not check for updates: ", "Не удалось проверить обновления: ") + ex.Message);
        }
    }

    /// <summary>Asks the user; has to run on the UI thread. Declining changes nothing: the question comes back at the next start.</summary>
    public static void Prompt(UpdateInfo update)
    {
        if (_prompting || _installing)
            return;
        _prompting = true;
        var canInstall = InstalledHere();
        var dialog = new UpdateDialog(update, canInstall);
        dialog.Closed += async (_, _) =>
        {
            _prompting = false;
            if (!dialog.Accepted)
                return;
            if (canInstall)
                await InstallAsync(update);
            else
                Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
        };
        dialog.Show();
    }

    /// <summary>Only the copy installed by the setup is updated in place; a portable or development copy opens the release page.</summary>
    private static bool InstalledHere()
    {
        var dir = Installer.InstalledDir();
        var self = Environment.ProcessPath;
        return dir is not null && self is not null
            && string.Equals(Path.GetFullPath(Path.GetDirectoryName(self)!).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task InstallAsync(UpdateInfo update)
    {
        _installing = true;
        var cancel = new CancellationTokenSource();
        var toast = Toast.Begin(T($"Updating Radii to {update.Version}", $"Обновление Radii до {update.Version}"), T("Starting…", "Запуск…"), cancel);
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "Radii-update");
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, update.SetupName);

            var context = new ConvertContext(cancel.Token, (text, fraction) => toast.Update(text, fraction));
            await Engines.DownloadAsync(update.SetupUrl, file, "Radii " + update.Version, update.SetupBytes, context, oneTime: false);

            toast.Update(T("Checking the download…", "Проверяю загрузку…"), null);
            await VerifyAsync(update, file, cancel.Token);

            toast.Update(T("Installing, Radii will restart…", "Устанавливаю, Radii перезапустится…"), null);
            var start = new ProcessStartInfo(file, "--silent --update --no-engines") { UseShellExecute = true, WorkingDirectory = folder };
            if (Installer.InstalledDir() is { } dir && !Installer.CanWrite(dir))
                start.Verb = "runas"; // the install folder needs administrator rights: Windows asks once
            Process.Start(start);
            // The installer stops this process, copies the new exe and starts it again.
        }
        catch (OperationCanceledException)
        {
            toast.Done(T("Update cancelled", "Обновление отменено"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            toast.Fail(T("Update failed: ", "Не удалось обновить: ") + ex.Message);
        }
        finally
        {
            _installing = false;
        }
    }

    private static async Task VerifyAsync(UpdateInfo update, string file, CancellationToken cancel)
    {
        if (update.SumsUrl is null)
            throw new InvalidDataException(T("the release has no SHA256SUMS.txt", "в релизе нет SHA256SUMS.txt"));
        using var client = NewClient();
        var sums = await client.GetStringAsync(update.SumsUrl, cancel);
        var expected = sums.Split('\n')
            .Select(line => line.Trim().Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length >= 2 && parts[^1].Equals(update.SetupName, StringComparison.OrdinalIgnoreCase))?[0];
        if (expected is null)
            throw new InvalidDataException(T("no checksum for the installer in SHA256SUMS.txt", "для установщика нет контрольной суммы в SHA256SUMS.txt"));
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidDataException(T("the checksum does not match, the download was discarded", "контрольная сумма не совпала, загрузка отброшена"));
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
