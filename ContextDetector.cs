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
        var (count, names) = await Task.Run(() => Selection(pt));
        if (desktop)
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return new Detected(Surface.Desktop, count, folder, count > 0 ? DesktopPaths(names) : []);
        }

        var (explorerFolder, paths) = await RunSta(() => ExplorerState(root, names, count > 0));
        return new Detected(Surface.Explorer, count, explorerFolder, paths);
    }

    private static (int Count, List<string> Names) Selection(POINT pt)
    {
        var names = new List<string>();
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            var walker = TreeWalker.ControlViewWalker;
            SelectionItemPattern? item = null;
            for (var depth = 0; element is not null && depth < 8; depth++, element = walker.GetParent(element))
            {
                var type = element.Current.ControlType;
                if (item is null && (type == ControlType.ListItem || type == ControlType.DataItem)
                    && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var itemPattern))
                {
                    item = (SelectionItemPattern)itemPattern;
                }
                if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern))
                {
                    if (item is null)
                        return (0, names); // empty space
                    // The press was swallowed, so the shell did not select the item under the cursor: do it here,
                    // but keep a multi-selection that already contains it (as a right click does).
                    if (!item.Current.IsSelected)
                        item.Select();
                    var selected = ((SelectionPattern)pattern).Current.GetSelection();
                    foreach (var chosen in selected)
                        names.Add(chosen.Current.Name);
                    return (Math.Max(1, selected.Length), names);
                }
            }
            return (item is null ? 0 : 1, names);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return (0, names);
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
