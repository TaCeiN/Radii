using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using static Radii.Native;

namespace Radii;

/// <param name="Flyout">Opens the extensions flyout instead of running an action.</param>
/// <param name="Navigates">The sector opens another ring: the ring stays on screen and <see cref="Run"/> swaps it (with the back pill).</param>
/// <param name="Children">A group sector: it opens a ring of these actions (the window does it, <see cref="Run"/> is not used).</param>
internal sealed record RadialItem(string Id, string Glyph, string Label, Action Run, bool Danger = false, bool Flyout = false, bool Navigates = false, IReadOnlyList<RadialItem>? Children = null);

internal enum FileKind
{
    Unknown,
    Folder,
    GitRepo,
    Executable,
    Image,
    Archive,
    DiskImage,
    Other,
}

internal static class Notifier
{
    public static void Show(string title, string text) => Toast.Show(title, text);
}

internal static class Menus
{
    public const int MaxSectors = 12;
    public const string ExtensionsId = "ext";

    /// <summary>Every built-in sector, for the settings window: id, English and Russian name.</summary>
    public static readonly (string Id, string En, string Ru)[] Builtins =
    [
        ("open", "Open / Run", "Открыть / Запустить"),
        ("admin", "Run as admin", "Запуск от админа"),
        ("terminal", "Open in Terminal", "Открыть в терминале"),
        ("github", "Open on GitHub", "Открыть на GitHub"),
        ("wallpaper", "Set as wallpaper", "Сделать обоями"),
        ("extract", "Extract here", "Распаковать"),
        ("mount", "Mount disk image", "Смонтировать образ"),
        ("openwith", "Open with…", "Открыть с помощью…"),
        ("copy", "Copy", "Копировать"),
        ("cut", "Cut", "Вырезать"),
        ("rename", "Rename", "Переименовать"),
        ("delete", "Delete", "Удалить"),
        ("copypath", "Copy as path", "Копировать как путь"),
        ("compress", "Compress to zip", "Сжать в zip"),
        ("movetonew", "Move to new folder", "В новую папку"),
        ("hash", "SHA-256", "SHA-256"),
        ("props", "Properties", "Свойства"),
        ("paste", "Paste", "Вставить"),
        ("newfolder", "New folder", "Новая папка"),
        ("refresh", "Refresh", "Обновить"),
        ("selectall", "Select all", "Выделить всё"),
        ("terminalhere", "Terminal here", "Терминал здесь"),
        ("display", "Display settings", "Параметры экрана"),
        ("personalize", "Personalize", "Персонализация"),
    ];

    private static readonly HashSet<string> ExecutableExtensions = Set(".exe", ".bat", ".cmd", ".msi", ".ps1", ".lnk", ".vbs", ".msc", ".com");
    private static readonly HashSet<string> ImageExtensions = Set(".png", ".jpg", ".jpeg", ".bmp");
    private static readonly HashSet<string> ArchiveExtensions = Set(".zip", ".7z", ".tar", ".gz", ".tgz", ".rar");
    private static readonly HashSet<string> DiskImageExtensions = Set(".iso", ".vhd", ".vhdx");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    public static string T(string en, string ru)
    {
        var language = Settings.Current.Language;
        if (language == "auto")
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return language == "ru" ? ru : en;
    }

    /// <summary>Kind of the selection when it is exactly one resolved path, otherwise unknown.</summary>
    public static FileKind KindOf(Detected d)
    {
        if (d.Paths.Count != 1)
            return FileKind.Unknown;
        var path = d.Paths[0];
        if (Directory.Exists(path))
            return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")) ? FileKind.GitRepo : FileKind.Folder;
        var extension = Path.GetExtension(path);
        if (ExecutableExtensions.Contains(extension))
            return FileKind.Executable;
        if (ImageExtensions.Contains(extension))
            return FileKind.Image;
        if (ArchiveExtensions.Contains(extension))
            return FileKind.Archive;
        if (DiskImageExtensions.Contains(extension))
            return FileKind.DiskImage;
        return FileKind.Other;
    }

    public static string Summary(Detected d)
    {
        if (d.HasSelection)
        {
            if (d.Paths.Count == 1)
                return Path.GetFileName(d.Paths[0]);
            return T(d.Count == 1 ? "1 item" : $"{d.Count} items", $"{d.Count} {RussianObjects(d.Count)}");
        }
        if (d.Surface == Surface.Desktop)
            return T("Desktop", "Рабочий стол");
        if (string.IsNullOrEmpty(d.FolderPath))
            return T("Folder", "Папка");
        var name = Path.GetFileName(d.FolderPath.TrimEnd('\\'));
        return name.Length > 0 ? name : d.FolderPath;
    }

    private static string RussianObjects(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "объект",
        (>= 2 and <= 4, not (>= 12 and <= 14)) => "объекта",
        _ => "объектов",
    };

    private static RadialItem Item(string id, string glyph, string en, string ru, Action run, bool danger = false) =>
        new(id, glyph, T(en, ru), run, danger);

    internal static RadialItem Extensions() =>
        new(ExtensionsId, "\uE712", T("Extensions", "Расширения"), () => { }, Flyout: true);

    public static List<RadialItem> For(Detected d) => Finish(Plan(d).Ring, d);

    /// <summary>Actions that are not on the ring for this selection but are found by the search (and can be pinned in the ring editor).</summary>
    public static List<RadialItem> Extras(Detected d)
    {
        var plan = Plan(d);
        return plan.Pool.Where(p => plan.Ring.All(r => r.Id != p.Id)).ToList();
    }

    /// <param name="Ring">Sectors for this context (the user's layout when there is one), without the extensions sector.</param>
    /// <param name="Pool">Everything that can sit in this ring: the defaults, the extras and the user's commands.</param>
    /// <param name="Default">The ring as it is without a user layout.</param>
    internal sealed record RingPlan(List<RadialItem> Ring, List<RadialItem> Pool, List<RadialItem> Default);

    /// <summary>The kinds of ring the editor can lay out: key, English and Russian name.</summary>
    internal static readonly (string Key, string En, string Ru)[] Contexts =
    [
        ("file", "File", "Файл"), ("several", "Several files", "Несколько файлов"), ("folder", "Folder", "Папка"), ("git", "Git repository", "Git-репозиторий"),
        ("program", "Program", "Программа"), ("picture", "Picture", "Картинка"), ("archive", "Archive", "Архив"), ("disk", "Disk image", "Образ диска"),
        ("background", "Empty space in folder", "Пусто в папке"), ("desktop", "Desktop", "Рабочий стол"),
    ];

    public static string ContextKey(Detected d, FileKind kind)
    {
        if (!d.HasSelection)
            return d.Surface == Surface.Desktop ? "desktop" : "background";
        if (d.Count > 1 || d.Paths.Count > 1)
            return "several";
        return kind switch
        {
            FileKind.Folder => "folder",
            FileKind.GitRepo => "git",
            FileKind.Executable => "program",
            FileKind.Image => "picture",
            FileKind.Archive => "archive",
            FileKind.DiskImage => "disk",
            _ => "file",
        };
    }

    /// <summary>A made-up selection of the given kind, for the ring editor (nothing is read from disk).</summary>
    internal static (Detected Detected, FileKind Kind) SampleFor(string key)
    {
        const string root = @"C:\Sample";
        Detected One(string name) => new(Surface.Explorer, 1, root, [root + "\\" + name]);
        return key switch
        {
            "several" => (new Detected(Surface.Explorer, 3, root, [root + @".txt", root + @".txt", root + @"\c.txt"]), FileKind.Unknown),
            "folder" => (One("Folder"), FileKind.Folder),
            "git" => (One("Project"), FileKind.GitRepo),
            "program" => (One("setup.exe"), FileKind.Executable),
            "picture" => (One("photo.png"), FileKind.Image),
            "archive" => (One("pack.zip"), FileKind.Archive),
            "disk" => (One("disk.iso"), FileKind.DiskImage),
            "background" => (new Detected(Surface.Explorer, 0, root), FileKind.Unknown),
            "desktop" => (new Detected(Surface.Desktop, 0, root), FileKind.Unknown),
            _ => (One("document.txt"), FileKind.Other),
        };
    }

    internal static RingPlan Plan(Detected d, FileKind? forcedKind = null, bool sample = false)
    {
        var kind = forcedKind ?? KindOf(d);
        var (defaults, extras) = d.HasSelection ? Slots(d, kind, sample) : (ForBackground(d), []);
        var custom = CustomItems(d, kind, sample).ToList();
        var pool = defaults.Concat(extras).Concat(custom).ToList();
        var fallback = defaults.Concat(custom).ToList();
        var ring = fallback;
        if (Settings.Current.Layouts.TryGetValue(ContextKey(d, kind), out var ids) && ids.Count > 0)
        {
            var chosen = ids.Select(id => pool.FirstOrDefault(p => p.Id == id)).OfType<RadialItem>().DistinctBy(i => i.Id).ToList();
            if (chosen.Count > 0)
                ring = chosen;
        }
        return new RingPlan([.. ring], pool, fallback);
    }

    /// <summary>
    /// The ring of a selection has fixed places, clockwise from the top: 1 open, 2 what is special about the kind of item,
    /// 3 copy, 4 cut, 5 delete, 6 rename (several items: move to a new folder), 7 copy as path (a repository with a remote: open on GitHub),
    /// 8 extensions (added by <see cref="Finish"/>). The same action keeps its angle in every context.
    /// </summary>
    private static (List<RadialItem> Ring, List<RadialItem> Extras) Slots(Detected d, FileKind kind, bool sample)
    {
        var paths = d.Paths;
        var path = paths.Count == 1 ? paths[0] : null;
        var single = d.Count == 1;
        var ring = new List<RadialItem>
        {
            Item("open", "", kind == FileKind.Executable ? "Run" : "Open", kind == FileKind.Executable ? "Запустить" : "Открыть", () => Keys.Chord(Keys.Enter)),
        };
        var extras = new List<RadialItem>();

        // Slot 2.
        var compress = paths.Count > 0 ? Item("compress", "", "Compress to zip", "Сжать в zip", () => Compress(paths)) : null;
        switch (kind)
        {
            case FileKind.Executable:
                ring.Add(Item("admin", "", "Run as admin", "Запуск от админа", () => RunAsAdministrator(path!)));
                break;
            case FileKind.Folder or FileKind.GitRepo:
                ring.Add(Item("terminal", "", "Open in Terminal", "Открыть в терминале", () => Terminal(path)));
                break;
            case FileKind.Image:
                ring.Add(Item("wallpaper", "", "Set as wallpaper", "Сделать обоями", () => SetWallpaper(path!)));
                break;
            case FileKind.Archive:
                ring.Add(Item("extract", "", "Extract here", "Распаковать", () => Extract(path!)));
                break;
            case FileKind.DiskImage:
                ring.Add(Item("mount", "", "Mount disk image", "Смонтировать образ", () => Mount(path!)));
                break;
        }
        var hasContextSlot = ring.Count == 2;
        if (compress is not null && hasContextSlot)
            extras.Add(compress);
        else if (compress is not null)
            ring.Add(compress); // plain files and several items: the slot holds "Compress to zip"

        ring.Add(Item("copy", "", "Copy", "Копировать", () => Keys.Chord(Keys.Ctrl, Keys.C)));
        ring.Add(Item("cut", "", "Cut", "Вырезать", () => Keys.Chord(Keys.Ctrl, Keys.X)));
        ring.Add(Item("delete", "", "Delete", "Удалить", () => Keys.Chord(Keys.Delete), danger: true));
        if (single)
            ring.Add(Item("rename", "", "Rename", "Переименовать", () => Keys.Chord(Keys.F2)));
        else if (paths.Count > 1)
            ring.Add(Item("movetonew", "", "Move to new folder", "В новую папку", () => MoveToNewFolder(paths)));

        var copyPath = Item("copypath", "", "Copy as path", "Копировать как путь", () => Keys.Chord(Keys.Ctrl, Keys.Shift, Keys.C));
        var url = kind == FileKind.GitRepo ? (sample ? "https://github.com/" : GitRemoteUrl(path!)) : null;
        if (url is not null)
        {
            ring.Add(Item("github", "", "Open on GitHub", "Открыть на GitHub", () => Launch(new ProcessStartInfo(url) { UseShellExecute = true })));
            extras.Add(copyPath);
        }
        else
        {
            ring.Add(copyPath);
        }

        // Not on the ring: found by the search, pinned in settings.
        if (path is not null && kind != FileKind.Folder && kind != FileKind.GitRepo)
            extras.Add(Item("openwith", "", "Open with…", "Открыть с помощью…", () => OpenWith(path)));
        if (sample ? kind is not (FileKind.Folder or FileKind.GitRepo) : paths.Any(File.Exists))
            extras.Add(Item("hash", "", "SHA-256", "SHA-256", () => Hash(paths)));
        extras.Add(Item("props", "", "Properties", "Свойства", () => Keys.Chord(Keys.Alt, Keys.Enter)));
        return (ring, extras);
    }

    private static List<RadialItem> ForBackground(Detected d)
    {
        var items = new List<RadialItem>
        {
            Item("paste", "\uE77F", "Paste", "Вставить", () => Keys.Chord(Keys.Ctrl, Keys.V)),
            Item("newfolder", "\uE8F4", "New folder", "Новая папка", () => Keys.Chord(Keys.Ctrl, Keys.Shift, Keys.N)),
            Item("refresh", "\uE72C", "Refresh", "Обновить", () => Keys.Chord(Keys.F5)),
        };
        if (d.Surface == Surface.Desktop)
        {
            items.Add(Item("display", "\uE7F4", "Display settings", "Параметры экрана", () => Open("ms-settings:display")));
            items.Add(Item("personalize", "\uE771", "Personalize", "Персонализация", () => Open("ms-settings:personalization")));
        }
        else
        {
            items.Add(Item("selectall", "\uE8B3", "Select all", "Выделить всё", () => Keys.Chord(Keys.Ctrl, Keys.A)));
            items.Add(Item("terminalhere", "\uE756", "Terminal here", "Терминал здесь", () => Terminal(d.FolderPath)));
        }
        return items;
    }

    /// <summary>Applies the user's choices: hidden sectors, own commands, usage order, and the extensions sector last.</summary>
    private static List<RadialItem> Finish(List<RadialItem> items, Detected d)
    {
        var settings = Settings.Current;
        items.RemoveAll(i => settings.HiddenItems.Contains(i.Id));
        items = Grouped(items);
        if (settings.SortByUsage && items.Count > 2)
        {
            // The first sector (Open / Paste) keeps its place; the rest are ordered by how often they were used.
            var rest = items.Skip(1).OrderByDescending(UsageOf).ToList();
            items = [items[0], .. rest];
        }
        if (items.Count > MaxSectors - 1)
        {
            // Too many for one ring: the tail (the least used when sorting by usage) moves into a "More" group instead of being dropped.
            var keep = MaxSectors - 2;
            var overflow = items.Skip(keep).ToList();
            items = items.Take(keep).ToList();
            items.Add(new RadialItem("group:more", "", T("More", "Ещё"), () => { }, Navigates: true, Children: overflow));
        }
        items.Add(Extensions());
        return items;
    }

    /// <summary>Sectors that are tucked into a group sector (a small ring of their own) when the group would hold at least two.</summary>
    private static readonly (string Id, string Glyph, string En, string Ru, string[] Members)[] Groups =
    [
    ];

    private static List<RadialItem> Grouped(List<RadialItem> items)
    {
        foreach (var group in Groups)
        {
            var members = items.Where(i => group.Members.Contains(i.Id)).ToList();
            if (members.Count < 2)
                continue;
            var at = items.IndexOf(members[0]);
            items.RemoveAll(members.Contains);
            items.Insert(Math.Min(at, items.Count), new RadialItem(group.Id, group.Glyph, T(group.En, group.Ru), () => { }, Navigates: true, Children: members));
        }
        return items;
    }

    private static int UsageOf(RadialItem item) =>
        item.Children is { } children ? children.Sum(UsageOf) : Settings.Current.Usage.GetValueOrDefault(item.Id);

    private static IEnumerable<RadialItem> CustomItems(Detected d, FileKind kind, bool sample = false)
    {
        foreach (var command in Settings.Current.Commands)
        {
            if (command.Name.Length == 0 || command.Command.Length == 0 || !Applies(command, d, kind, sample))
                continue;
            var captured = command;
            yield return new RadialItem(command.Id, command.Glyph, command.Name, () => RunCustom(captured, d));
        }
    }

    private static bool Applies(CustomCommand command, Detected d, FileKind kind, bool sample = false)
    {
        if (!d.HasSelection)
            return command.AppliesTo is "any" or "background";
        if (d.Paths.Count == 0)
            return false; // placeholders need resolved paths
        var folder = kind is FileKind.Folder or FileKind.GitRepo;
        if (command.AppliesTo == "background" || (command.AppliesTo == "folder" && !folder) || (command.AppliesTo == "file" && folder))
            return false;
        if (folder || sample || command.Extensions.Trim().Length == 0)
            return true;
        var wanted = command.Extensions.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => "." + e.TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return d.Paths.All(p => wanted.Contains(Path.GetExtension(p)));
    }

    private static void RunCustom(CustomCommand command, Detected d)
    {
        var first = d.Paths.FirstOrDefault() ?? d.FolderPath ?? "";
        var folder = d.FolderPath ?? Path.GetDirectoryName(first) ?? "";
        string Expand(string text) => text
            .Replace("{paths}", string.Join(" ", d.Paths.Select(p => $"\"{p}\"")))
            .Replace("{path}", first)
            .Replace("{folder}", folder)
            .Replace("{name}", Path.GetFileName(first))
            .Replace("{stem}", Path.GetFileNameWithoutExtension(first))
            .Replace("{ext}", Path.GetExtension(first).TrimStart('.'));
        var info = new ProcessStartInfo(Expand(command.Command))
        {
            Arguments = Expand(command.Arguments),
            UseShellExecute = true,
            WorkingDirectory = Directory.Exists(folder) ? folder : "",
        };
        if (command.Admin)
            info.Verb = "runas";
        Launch(info);
    }

    private static void Open(string target) => Launch(new ProcessStartInfo(target) { UseShellExecute = true });

    // The system "Open with" chooser, owned by Explorer so it comes to the front (the "openas" verb and
    // rundll32 OpenAs_RunDLL both end up hidden or silent when started from a background process).
    private static void OpenWith(string path)
    {
        var owner = Native.GetForegroundWindow();
        var thread = new Thread(() =>
        {
            var info = new Native.OPENASINFO { pcszFile = path, pcszClass = null, oaifInFlags = 0x1 /* ALLOW_REGISTRATION */ | 0x4 /* EXEC */ };
            Native.SHOpenWithDialog(owner, ref info);
        }) { IsBackground = true, Name = "Radii open with" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void RunAsAdministrator(string path)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        if (string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            // The default verb for .ps1 is "edit"; run it for real.
            info.FileName = "powershell.exe";
            info.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"";
        }
        Launch(info);
    }

    private static void Terminal(string? folder)
    {
        var info = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        if (!string.IsNullOrEmpty(folder))
            info.Arguments = $"-d \"{folder.TrimEnd('\\')}\"";
        if (!Launch(info))
            Launch(new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder ?? "" });
    }

    private static void SetWallpaper(string path) => SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02);

    private static void Mount(string path) =>
        _ = RunTool("powershell.exe", ["-NoProfile", "-Command", $"Mount-DiskImage -ImagePath '{path.Replace("'", "''")}' -ErrorAction Stop | Out-Null"],
            T("Mount", "Монтирование"), T($"Mounted {Path.GetFileName(path)}", $"Смонтирован {Path.GetFileName(path)}"));

    /// <summary>Extracts into a folder named after the archive, using the tar.exe that ships with Windows.</summary>
    private static void Extract(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var target = UniquePath(parent, Path.GetFileNameWithoutExtension(path), "");
        Directory.CreateDirectory(target);
        _ = RunTool("tar.exe", ["-xf", path, "-C", target], T("Extract", "Распаковка"), T($"Extracted to {Path.GetFileName(target)}", $"Распаковано в {Path.GetFileName(target)}"));
    }

    /// <summary>Runs a console tool in the background and tells the user how it ended (success text, or the tool's own error).</summary>
    private static async Task RunTool(string exe, string[] args, string title, string success)
    {
        try
        {
            var (code, errors) = await Converters.ProcessRunner.RunAsync(exe, args, CancellationToken.None);
            Notifier.Show(title, code == 0 ? success : T("Failed: ", "Не получилось: ") + (errors.Length > 0 ? errors : $"exit code {code}"));
        }
        catch (Exception ex) when (ex is Converters.ConvertException or IOException)
        {
            Notifier.Show(title, ex.Message);
        }
    }

    internal static string UniquePath(string parent, string name, string extension)
    {
        var target = Path.Combine(parent, name + extension);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(parent, $"{name} ({i}){extension}");
        return target;
    }

    private static void Compress(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        try
        {
            var parent = Path.GetDirectoryName(paths[0])!;
            // One item: the zip is named after it; several: after the folder they are in.
            var name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : Path.GetFileName(parent.TrimEnd('\\'));
            var target = UniquePath(parent, name.Length > 0 ? name : "Archive", ".zip");
            using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        zip.CreateEntryFromFile(path, Path.GetFileName(path));
                        continue;
                    }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(parent, entry).Replace('\\', '/');
                        if (Directory.Exists(entry))
                        {
                            if (!Directory.EnumerateFileSystemEntries(entry).Any())
                                zip.CreateEntry(relative + "/"); // keep empty folders
                        }
                        else
                        {
                            zip.CreateEntryFromFile(entry, relative);
                        }
                    }
                }
            }
            Notifier.Show("Radii", T($"Created {Path.GetFileName(target)}", $"Создан {Path.GetFileName(target)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Notifier.Show("Radii", T("Could not create the archive: ", "Не удалось создать архив: ") + ex.Message);
        }
    });

    private static void MoveToNewFolder(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        try
        {
            var parent = Path.GetDirectoryName(paths[0])!;
            var target = UniquePath(parent, T("New folder", "Новая папка"), "");
            Directory.CreateDirectory(target);
            foreach (var path in paths)
            {
                var destination = Path.Combine(target, Path.GetFileName(path));
                if (Directory.Exists(path))
                    Directory.Move(path, destination);
                else
                    File.Move(path, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notifier.Show("Radii", T("Could not move: ", "Не удалось переместить: ") + ex.Message);
        }
    });

    private static void Hash(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        try
        {
            var lines = new List<string>();
            foreach (var path in paths.Where(File.Exists))
            {
                using var stream = File.OpenRead(path);
                lines.Add($"{Convert.ToHexString(SHA256.HashData(stream))}  {Path.GetFileName(path)}");
            }
            if (lines.Count == 0)
                return;
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines)));
            Notifier.Show("SHA-256", T("Copied to the clipboard", "Скопировано в буфер обмена"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Notifier.Show("SHA-256", ex.Message);
        }
    });

    /// <summary>Web URL of the "origin" remote from .git/config, or null.</summary>
    private static string? GitRemoteUrl(string folder)
    {
        try
        {
            var config = Path.Combine(folder, ".git", "config");
            if (!File.Exists(config))
                return null;
            var inOrigin = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.StartsWith("[remote \"origin\"", StringComparison.Ordinal);
                }
                else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.IndexOf('=') is var eq and > 0)
                {
                    return ToWebUrl(line[(eq + 1)..].Trim());
                }
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    private static string? ToWebUrl(string remote)
    {
        if (remote.StartsWith("git@", StringComparison.Ordinal) && remote.IndexOf(':') is var colon and > 0)
            remote = "https://" + remote[4..colon] + "/" + remote[(colon + 1)..];
        else if (remote.StartsWith("ssh://", StringComparison.Ordinal))
            remote = "https://" + remote["ssh://".Length..].Replace("git@", "");
        if (remote.EndsWith(".git", StringComparison.Ordinal))
            remote = remote[..^4];
        return remote.StartsWith("https://", StringComparison.Ordinal) || remote.StartsWith("http://", StringComparison.Ordinal) ? remote : null;
    }

    /// <returns>False when the process could not be started (missing program) or UAC was declined.</returns>
    private static bool Launch(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

internal static class Keys
{
    public const ushort Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Delete = 0x2E;
    public const ushort A = 0x41, C = 0x43, N = 0x4E, V = 0x56, X = 0x58;
    public const ushort F2 = 0x71, F5 = 0x74, F10 = 0x79;

    /// <summary>Presses the keys in order, then releases them in reverse. Goes to whatever window has focus (Explorer: our menu never activates).</summary>
    public static void Chord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(keys[i], up: true);
        }
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? 0x0002u : 0u) | (vk == Delete ? 0x0001u : 0u),
            },
        },
    };
}
