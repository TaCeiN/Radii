using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radii.Converters;

internal enum Engine
{
    Magick,
    FFmpeg,
    Pandoc,
}

/// <summary>
/// Third-party command line tools the converters use. They are downloaded on first use into
/// <c>%APPDATA%\Radii\engines</c> (a one-off download, shown with progress in the toast).
/// </summary>
internal static class Engines
{
    private sealed record Spec(string Folder, string Exe, string Title, string Fallback, string? GitHubRepo, string? AssetPattern, long ApproximateBytes);

    private static readonly Dictionary<Engine, Spec> Specs = new()
    {
        [Engine.Magick] = new("magick", "magick.exe", "ImageMagick",
            "https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-34/ImageMagick-7.1.2-34-portable-Q16-HDRI-x64.7z",
            "ImageMagick/ImageMagick", @"-portable-Q16-HDRI-x64\.7z$", 12_000_000),
        [Engine.FFmpeg] = new("ffmpeg", "ffmpeg.exe", "FFmpeg",
            "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip",
            "GyanD/codexffmpeg", @"essentials_build\.zip$", 115_000_000),
        [Engine.Pandoc] = new("pandoc", "pandoc.exe", "Pandoc",
            "https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.zip",
            "jgm/pandoc", @"windows-x86_64\.zip$", 37_000_000),
    };

    private static readonly Dictionary<Engine, Task<string>> Running = [];
    private static readonly object Gate = new();
    private static readonly HttpClient Http = CreateClient();

    private static string Root => Path.Combine(Settings.Folder, "engines");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Radii");
        return client;
    }

    public static bool IsInstalled(Engine engine) => Find(Specs[engine]) is not null;

    public static string TitleOf(Engine engine) => Specs[engine].Title;

    public static long ApproximateBytes(Engine engine) => Specs[engine].ApproximateBytes;

    /// <summary>Size of the LibreOffice installer, for the setup wizard.</summary>
    public const long LibreOfficeBytes = 375_000_000;

    /// <summary>Path to the engine's executable, downloading it first when it is missing.</summary>
    public static Task<string> EnsureAsync(Engine engine, ConvertContext context)
    {
        var spec = Specs[engine];
        var existing = Find(spec);
        if (existing is not null)
            return Task.FromResult(existing);
        lock (Gate)
        {
            // Two conversions asking for the same engine share one download.
            if (!Running.TryGetValue(engine, out var task) || task.IsFaulted || task.IsCanceled)
                Running[engine] = task = InstallAsync(engine, spec, context);
            return task;
        }
    }

    private static string? Find(Spec spec)
    {
        var folder = Path.Combine(Root, spec.Folder);
        if (!Directory.Exists(folder))
            return null;
        return Directory.EnumerateFiles(folder, spec.Exe, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static async Task<string> InstallAsync(Engine engine, Spec spec, ConvertContext context)
    {
        var folder = Path.Combine(Root, spec.Folder);
        var temp = Path.Combine(Root, spec.Folder + ".part");
        try
        {
            Directory.CreateDirectory(Root);
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);

            var url = await ResolveUrlAsync(spec);
            var archive = Path.Combine(temp, Path.GetFileName(new Uri(url).LocalPath));
            await DownloadAsync(url, archive, spec.Title, spec.ApproximateBytes, context);

            context.Report(Menus.T($"Unpacking {spec.Title}…", $"Распаковываю {spec.Title}…"), null);
            var target = Path.Combine(temp, "files");
            Directory.CreateDirectory(target);
            await Task.Run(() => Unpack(archive, target, spec), context.Cancel);
            File.Delete(archive);

            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.Move(target, folder);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            if (context.Cancel.IsCancellationRequested)
                throw new OperationCanceledException(context.Cancel);
            throw new ConvertException(Menus.T($"Could not download {spec.Title}: ", $"Не удалось скачать {spec.Title}: ") + ex.Message, ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Find(spec) ?? throw new ConvertException(Menus.T($"{spec.Title} was downloaded but {spec.Exe} is missing", $"{spec.Title} скачан, но {spec.Exe} не найден"));
    }

    private static async Task<string> ResolveUrlAsync(Spec spec)
    {
        if (spec.GitHubRepo is null || spec.AssetPattern is null)
            return spec.Fallback;
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{spec.GitHubRepo}/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (Regex.IsMatch(name, spec.AssetPattern))
                    return asset.GetProperty("browser_download_url").GetString() ?? spec.Fallback;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            // Rate limited or offline: the pinned release below still works.
        }
        return spec.Fallback;
    }

    /// <summary>
    /// Downloads with up to eight attempts; a dropped connection resumes from the bytes already on disk.
    /// Every attempt uses a fresh connection (a pooled one can be dead after a VPN or network change)
    /// and gives up on a stalled read after 30 seconds.
    /// </summary>
    internal static async Task DownloadAsync(string url, string file, string title, long approximateBytes, ConvertContext context, int maxAttempts = 8, bool oneTime = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, file, title, approximateBytes, context, oneTime);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !context.Cancel.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                context.Report(Menus.T($"Connection lost, retrying ({attempt}/{maxAttempts - 1})…", $"Обрыв связи, повторяю ({attempt}/{maxAttempts - 1})…"), null);
                await Task.Delay(Math.Min(1000 * attempt, 5000), context.Cancel);
            }
        }
    }

    private static async Task DownloadOnceAsync(string url, string file, string title, long approximateBytes, ConvertContext context, bool oneTime)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromSeconds(30) };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Radii");

        var have = File.Exists(file) ? new FileInfo(file).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

        // Linked to the user's cancel, plus a watchdog that is re-armed on every chunk.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(context.Cancel);
        stall.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            return; // already complete
        response.EnsureSuccessStatusCode();
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed)
            have = 0;
        var total = have + (response.Content.Headers.ContentLength ?? approximateBytes);
        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var target = new FileStream(file, resumed ? FileMode.Append : FileMode.Create);
        var buffer = new byte[128 * 1024];
        var done = have;
        var last = Environment.TickCount64;
        int read;
        while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(30));
            await target.WriteAsync(buffer.AsMemory(0, read), context.Cancel);
            done += read;
            if (Environment.TickCount64 - last > 200)
            {
                last = Environment.TickCount64;
                var fraction = Math.Min(1.0, (double)done / total);
                var once = oneTime ? Menus.T(" (one time)", " (один раз)") : "";
                context.Report(Menus.T($"Downloading {title}{once}: {done / 1_048_576} / {total / 1_048_576} MB", $"Скачиваю {title}{once}: {done / 1_048_576} / {total / 1_048_576} МБ"), fraction);
            }
        }
    }

    private static void Unpack(string archive, string target, Spec spec)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            // Only the executable: these zips carry docs, players and other tools that would triple the size on disk.
            foreach (var entry in zip.Entries.Where(e => e.Name.Equals(spec.Exe, StringComparison.OrdinalIgnoreCase)))
                entry.ExtractToFile(Path.Combine(target, entry.Name), true);
            return;
        }
        // .7z: the tar.exe that ships with Windows 10/11 reads it.
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var info = new System.Diagnostics.ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-xf");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("tar.exe did not start");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException("tar.exe failed to unpack " + Path.GetFileName(archive));
    }

    /// <summary>Microsoft Edge (always there on Windows 11) or Chrome, used to print HTML to PDF.</summary>
    public static string? FindBrowser()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        string[] relative = [@"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe"];
        return relative.SelectMany(r => roots.Select(root => Path.Combine(root, r))).FirstOrDefault(File.Exists);
    }

    public static string? FindLibreOffice()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        return roots.Select(root => Path.Combine(root, @"LibreOffice\program\soffice.exe")).FirstOrDefault(File.Exists);
    }

    private static readonly string[] LibreOfficeMirrors =
    [
        "https://mirror.truenetwork.ru/tdf/",
        "https://mirror.netcologne.de/tdf/",
        "https://ftp.fau.de/tdf/",
        "https://mirrors.dotsrc.org/tdf/",
        "https://mirror.init7.net/tdf/",
        "https://mirror.kumi.systems/tdf/",
        "https://mirrors.ocf.berkeley.edu/tdf/",
        "https://download.documentfoundation.org/",
    ];

    /// <summary>Newest stable version from the first mirror whose directory listing answers; a pinned one if none does.</summary>
    private static async Task<string> ResolveLibreOfficeVersionAsync(ConvertContext context)
    {
        foreach (var mirror in LibreOfficeMirrors)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var listing = await Http.GetStringAsync(mirror + "libreoffice/stable/", timeout.Token);
                var versions = Regex.Matches(listing, @"href=""(\d+\.\d+\.\d+)/""").Select(m => Version.Parse(m.Groups[1].Value)).ToList();
                if (versions.Count > 0)
                    return versions.Max()!.ToString();
            }
            catch (Exception ex) when (!context.Cancel.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
            }
        }
        return "26.8.1";
    }

    /// <summary>
    /// soffice.exe. When LibreOffice is missing, its installer is downloaded here (with progress) and started elevated;
    /// the Windows administrator prompt is the only thing the user has to confirm.
    /// </summary>
    public static async Task<string> EnsureLibreOfficeAsync(ConvertContext context)
    {
        if (FindLibreOffice() is { } found)
            return found;
        // download.documentfoundation.org is blocked by some providers, so the mirrors come first;
        // the ones that answer without a VPN (checked with tools\check-mirrors.ps1) lead, the official host is the last resort.
        var version = await ResolveLibreOfficeVersionAsync(context);
        var spec = new Spec("libreoffice", "soffice.exe", "LibreOffice", "", null, null, LibreOfficeBytes);
        var folder = Path.Combine(Root, "libreoffice.part");
        Directory.CreateDirectory(folder);
        var name = $"LibreOffice_{version}_Win_x86-64.msi";
        var msi = Path.Combine(folder, name);
        Exception? failure = null;
        var downloaded = false;
        foreach (var mirror in LibreOfficeMirrors)
        {
            context.Cancel.ThrowIfCancellationRequested();
            try
            {
                // Two quick attempts per mirror; the file already on disk is resumed on the next mirror.
                await DownloadAsync($"{mirror}libreoffice/stable/{version}/win/x86_64/{name}", msi, spec.Title, spec.ApproximateBytes, context, maxAttempts: 2);
                downloaded = true;
                break;
            }
            catch (Exception ex) when (!context.Cancel.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                failure = ex;
            }
        }
        if (!downloaded)
            throw new ConvertException(Menus.T("Could not download LibreOffice from any mirror: ", "Не удалось скачать LibreOffice ни с одного зеркала: ") + failure?.Message, failure);

        context.Report(Menus.T("Installing LibreOffice… confirm the Windows prompt", "Устанавливаю LibreOffice… подтверди запрос Windows"), null);
        var info = new System.Diagnostics.ProcessStartInfo("msiexec.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"/i \"{msi}\" /qb-! /norestart ALLUSERS=1 REGISTER_NO_MSO_TYPES=1 CREATEDESKTOPLINK=0",
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(info);
            if (process is not null)
                await process.WaitForExitAsync(context.Cancel);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ConvertException(Menus.T("The installation was cancelled", "Установка отменена"));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return FindLibreOffice() ?? throw new ConvertException(Menus.T("LibreOffice was not installed. Install it from libreoffice.org", "LibreOffice не установился. Установи его с libreoffice.org"));
    }
}

