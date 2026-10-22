using System.IO;
using System.Windows.Automation;
using static Radii.Native;

namespace Radii;

internal enum Surface
{
    Explorer,
    Desktop,
}

/// <param name="Count">Selected items, 0 when the click landed on empty space.</param>
/// <param name="FolderPath">Folder shown in the Explorer window; null when unknown or ambiguous (several tabs).</param>
/// <param name="PathList">Full paths of the selected items; empty when they could not be resolved reliably.</param>
internal readonly record struct Detected(Surface Surface, int Count, string? FolderPath, IReadOnlyList<string>? PathList = null)
{
    public bool HasSelection => Count > 0;

    public IReadOnlyList<string> Paths => PathList ?? [];
}

/// <summary>
/// Works out what was right-clicked. The selection comes from UI Automation (the shell's item view exposes a
/// selection pattern in both Explorer and the desktop); full paths from the Shell.Application automation object
/// (Explorer) or by matching display names against the desktop folders.
/// </summary>
internal static class ContextDetector
{
    public static void Warmup()
    {
        Task.Run(() =>
        {
            try
            {
                _ = AutomationElement.RootElement.Current.Name;
            }
            catch
            {
                // Best effort only.
            }
        });
    }

    public static async Task<Detected> DetectAsync(POINT pt, IntPtr root)
    {
        var desktop = ClassName(root) is "Progman" or "WorkerW";
        var (count, names, homeItem) = await Task.Run(() => Selection(pt));
        if (desktop)
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return new Detected(Surface.Desktop, count, folder, count > 0 ? DesktopPaths(names) : []);
        }

        if (homeItem is { } home)
        {
            // The Home page is a XAML list: no selection patterns, so the item is selected with a real click and its path is looked up by name.
            var homePaths = await RunSta(() => HomePaths(home.Name, home.Location));
            return new Detected(Surface.Explorer, 1, null, homePaths);
        }

        var (explorerFolder, paths) = await RunSta(() => ExplorerState(root, names, count > 0));
        return new Detected(Surface.Explorer, count, explorerFolder, paths);
    }

    private readonly record struct HomeItem(string Name, string? Location);

    private static (int Count, List<string> Names, HomeItem? Home) Selection(POINT pt)
    {
        var names = new List<string>();
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            var walker = TreeWalker.ControlViewWalker;
            // The XAML island of the Home page answers for its whole area: look for the row under the point inside it.
            if (element is not null && element.Current.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge")
            {
                var point = new System.Windows.Point(pt.X, pt.Y);
                var row = element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    .Cast<AutomationElement>()
                    .Where(e => e.Current.BoundingRectangle.Contains(point))
                    .OrderBy(e => e.Current.BoundingRectangle.Width * e.Current.BoundingRectangle.Height)
                    .FirstOrDefault();
                if (row is not null)
                    element = row;
            }
            SelectionItemPattern? item = null;
            AutomationElement? plain = null;
            for (var depth = 0; element is not null && depth < 8; depth++, element = walker.GetParent(element))
            {
                var type = element.Current.ControlType;
                if (item is null && (type == ControlType.ListItem || type == ControlType.DataItem)
                    && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var itemPattern))
                {
                    item = (SelectionItemPattern)itemPattern;
                }
                else if (item is null && plain is null && type == ControlType.ListItem && element.Current.ClassName is "GridViewItem" or "ListViewItem")
                {
                    plain = element;
                }
                if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern))
                {
                    if (item is null)
                        return plain is not null ? HomeResult(plain) : (0, names, null); // Home row, else empty space
                    // The press was swallowed, so the shell did not select the item under the cursor: do it here,
                    // but keep a multi-selection that already contains it (as a right click does).
                    if (!item.Current.IsSelected)
                        item.Select();
                    var selected = ((SelectionPattern)pattern).Current.GetSelection();
                    foreach (var chosen in selected)
                        names.Add(chosen.Current.Name);
                    return (Math.Max(1, selected.Length), names, null);
                }
            }
            if (item is null && plain is not null)
                return HomeResult(plain);
            return (item is null ? 0 : 1, names, null);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return (0, names, null);
        }
    }

    private static (int Count, List<string> Names, HomeItem? Home) HomeResult(AutomationElement row)
    {
        var home = new HomeItem(row.Current.Name, HomeLocation(row));
        // Home tiles cannot be selected through UI Automation: a plain left click does what the swallowed right click would have done.
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        return (1, [home.Name], home);
    }

    /// <summary>Second line of a Home row (the folder the file lives in), empty for tiles that show something else.</summary>
    private static string? HomeLocation(AutomationElement row)
    {
        try
        {
            foreach (AutomationElement text in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
            {
                var value = text.Current.Name;
                if (!string.IsNullOrWhiteSpace(value) && value != row.Current.Name)
                    return value;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    /// <summary>Path of a Home item: a pinned or frequent folder from Quick access, else a recent file from the Recent folder (the shortcut with that name).</summary>
    private static List<string> HomePaths(string name, string? location)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return [];
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                foreach (dynamic entry in shell.NameSpace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}").Items())
                {
                    if (string.Equals((string)entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        var path = (string)entry.Path;
                        if (Directory.Exists(path) || File.Exists(path))
                            return [path];
                    }
                }
            }
            catch
            {
                // Quick access not enumerable: fall through to Recent.
            }

            // "Local disk (D:)\a\b" and "Documents\a\b" both end in the folder path after the first segment.
            var tail = location is { } l && l.IndexOf('\\') is var cut and >= 0 ? l[(cut + 1)..].TrimEnd('\\', '.', '…') : "";
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string? fallback = null;
            foreach (var link in new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.Recent)).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(link.Name), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = wsh.CreateShortcut(link.FullName).TargetPath;
                if (!File.Exists(target) && !Directory.Exists(target))
                    continue;
                if (tail.Length == 0 || (Path.GetDirectoryName(target) ?? "").EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    return [target];
                fallback ??= target;
            }
            return fallback is null ? [] : [fallback];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Display names hide extensions (and ".lnk"), so match against the name with and without one.</summary>
    private static bool NameMatches(string path, string displayName) =>
        string.Equals(Path.GetFileName(path), displayName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileNameWithoutExtension(path), displayName, StringComparison.OrdinalIgnoreCase);

    private static List<string> DesktopPaths(List<string> names)
    {
        var entries = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            try
            {
                entries.AddRange(Directory.EnumerateFileSystemEntries(Environment.GetFolderPath(folder)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        var result = new List<string>();
        foreach (var name in names)
        {
            var matches = entries.Where(e => NameMatches(e, name)).ToList();
            if (matches.Count == 1)
                result.Add(matches[0]);
            else
                return []; // Ambiguous (a.txt next to a.png) or a virtual item such as the Recycle Bin: do not guess.
        }
        return result;
    }

    private sealed record ExplorerTab(string Folder, List<string> Selected);

    /// <summary>
    /// Folder and selected paths of the Explorer window. Tabs share one frame handle, so with several tabs the one whose
    /// selection matches the names UI Automation reported is used; if that is still ambiguous nothing is returned.
    /// </summary>
    private static (string? Folder, List<string> Paths) ExplorerState(IntPtr root, List<string> names, bool wantSelection)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return (null, []);
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic windows = shell.Windows();
            var tabs = new List<ExplorerTab>();
            foreach (dynamic window in windows)
            {
                try
                {
                    if ((long)window.HWND != root.ToInt64())
                        continue;
                    string folder = window.Document.Folder.Self.Path;
                    var selected = new List<string>();
                    try
                    {
                        foreach (dynamic item in window.Document.SelectedItems())
                            selected.Add((string)item.Path);
                    }
                    catch
                    {
                        // Virtual folder without a selection object.
                    }
                    tabs.Add(new ExplorerTab(folder, selected));
                }
                catch
                {
                    // Not a file-system window.
                }
            }

            if (!wantSelection)
            {
                var folders = tabs.Select(t => t.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return (folders.Count == 1 ? folders[0] : null, []);
            }

            var candidates = tabs
                .Where(t => t.Selected.Count == names.Count && t.Selected.All(p => names.Any(n => NameMatches(p, n))))
                .ToList();
            return candidates.Count == 1
                ? (candidates[0].Folder, candidates[0].Selected)
                : (tabs.Count == 1 ? tabs[0].Folder : null, []);
        }
        catch
        {
            return (null, []);
        }
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var source = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                source.SetResult(work());
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return source.Task;
    }
}
