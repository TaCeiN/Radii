using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using static Radii.Native;

namespace Radii;

/// <summary>One line of a shell context menu (a handler's entry, a separator or a submenu parent).</summary>
internal sealed class ShellEntry
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public string? Hint { get; init; }
    public string? Verb { get; init; }
    public bool IsSeparator { get; init; }
    public bool Disabled { get; init; }
    public bool Checked { get; init; }
    public ImageSource? Icon { get; set; }
    public List<ShellEntry> Children { get; init; } = [];

    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// The shell draws the icons of plain registry verbs (Edit, "Open with Code", …) itself, so the menu items carry no bitmap.
/// This reads the verb's <c>Icon</c> value (or the program of its command) from the same registry keys Explorer uses.
/// </summary>
internal static class VerbIcons
{
    private sealed record Spec(string Verb, string Label, string Icon);

    /// <param name="paths">Selection (one resolved path gives the file type), or empty for a folder background.</param>
    public static void Attach(List<ShellEntry> entries, IReadOnlyList<string> paths, string? folder)
    {
        List<Spec> specs;
        try
        {
            specs = Scan(paths, folder);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }
        if (specs.Count == 0)
            return;
        var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        Walk(entries, specs, cache);
    }

    private static void Walk(List<ShellEntry> entries, List<Spec> specs, Dictionary<string, ImageSource?> cache)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
                continue;
            if (entry.Icon is null)
            {
                var spec = specs.FirstOrDefault(s => entry.Verb is not null && string.Equals(s.Verb, entry.Verb, StringComparison.OrdinalIgnoreCase))
                    ?? specs.FirstOrDefault(s => s.Label.Length > 0 && string.Equals(s.Label, entry.Label, StringComparison.OrdinalIgnoreCase));
                if (spec is not null)
                {
                    if (!cache.TryGetValue(spec.Icon, out var icon))
                        cache[spec.Icon] = icon = Load(spec.Icon);
                    entry.Icon = icon;
                }
            }
            Walk(entry.Children, specs, cache);
        }
    }

    private static List<Spec> Scan(IReadOnlyList<string> paths, string? folder)
    {
        var roots = new List<string>();
        if (paths.Count == 1)
        {
            var path = paths[0];
            if (Directory.Exists(path))
            {
                roots.AddRange(["Directory", "Folder", "AllFilesystemObjects"]);
            }
            else
            {
                var extension = Path.GetExtension(path);
                using var classKey = Registry.ClassesRoot.OpenSubKey(extension);
                if (classKey?.GetValue("") is string progId && progId.Length > 0)
                    roots.Add(progId);
                roots.Add("SystemFileAssociations\\" + extension);
                if (classKey?.GetValue("PerceivedType") is string perceived && perceived.Length > 0)
                    roots.Add("SystemFileAssociations\\" + perceived);
                roots.AddRange(["*", "AllFilesystemObjects"]);
            }
        }
        else if (paths.Count == 0 && folder is not null)
        {
            roots.AddRange(["Directory\\Background", "Folder\\Background"]);
        }
        else
        {
            roots.AddRange(["*", "AllFilesystemObjects", "Directory"]);
        }

        var specs = new List<Spec>();
        foreach (var root in roots)
        {
            using var shell = Registry.ClassesRoot.OpenSubKey(root + "\\shell");
            if (shell is null)
                continue;
            foreach (var verb in shell.GetSubKeyNames())
            {
                using var key = shell.OpenSubKey(verb);
                if (key is null)
                    continue;
                var icon = key.GetValue("Icon") as string;
                if (string.IsNullOrWhiteSpace(icon))
                {
                    // No explicit icon: Explorer falls back to the program that runs the command.
                    using var command = key.OpenSubKey("command");
                    icon = command?.GetValue("") is string line ? FirstToken(line) : null;
                }
                if (string.IsNullOrWhiteSpace(icon))
                    continue;
                var label = Indirect(key.GetValue("MUIVerb") as string) ?? Indirect(key.GetValue("") as string) ?? "";
                specs.Add(new Spec(verb, label.Replace("&", ""), icon));
            }
        }
        return specs;
    }

    private static string FirstToken(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 0 ? text[1..end] : text.Trim('"');
        }
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>Resolves "@shell32.dll,-1234" style indirect strings.</summary>
    private static string? Indirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('@'))
            return value;
        var buffer = new StringBuilder(260);
        return SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : null;
    }

    private static ImageSource? Load(string spec)
    {
        var path = Environment.ExpandEnvironmentVariables(spec.Trim());
        var index = 0;
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out var parsed))
        {
            index = parsed;
            path = path[..comma];
        }
        path = path.Trim().Trim('"');
        if (path.StartsWith('@'))
            path = path[1..];
        var small = new IntPtr[1];
        if (ExtractIconEx(path, index, null, small, 1) < 1 || small[0] == IntPtr.Zero)
            return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(small[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint idFirst, uint idLast, uint flags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
}

[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2 : IContextMenu
{
    [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>Only the members used here, in vtable order.</summary>
[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attributes);
    [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
    [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
    [PreserveSig] int CompareIDs(IntPtr param, IntPtr pidl1, IntPtr pidl2);
    [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetAttributesOf(uint count, IntPtr pidls, ref uint attributes);
    [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr result);
}

/// <summary>
/// A live <c>IContextMenu</c> for a selection or a folder background. Kept alive while the flyout is open so
/// that submenus can be filled in and the chosen command invoked on the same COM object.
/// </summary>
internal sealed class ShellMenuSession : IDisposable
{
    internal const uint IdFirst = 1;

    private readonly IContextMenu _menu;
    private readonly IntPtr _hmenu;
    private readonly List<IntPtr> _pidls;
    private int _disposed;

    internal ShellMenuSession(IContextMenu menu, IntPtr hmenu, List<IntPtr> pidls, List<ShellEntry> entries)
    {
        _menu = menu;
        _hmenu = hmenu;
        _pidls = pidls;
        Entries = entries;
    }

    public List<ShellEntry> Entries { get; }

    public Task InvokeAsync(ShellEntry entry, POINT point, IntPtr owner) => ShellMenuHost.Run(() =>
    {
        var verb = (IntPtr)(entry.Id - (int)IdFirst);
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = 0x4000 /* UNICODE */ | 0x20000000 /* PTINVOKE */ | 0x100000 /* ASYNCOK */,
            hwnd = owner,
            lpVerb = verb,
            lpVerbW = verb,
            nShow = 1,
            ptInvoke = point,
        };
        return _menu.InvokeCommand(ref info);
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _ = ShellMenuHost.Run(() =>
        {
            DestroyMenu(_hmenu);
            foreach (var pidl in _pidls)
                Marshal.FreeCoTaskMem(pidl);
            Marshal.FinalReleaseComObject(_menu);
            return 0;
        });
    }
}

/// <summary>
/// Hosts every shell COM object on one dedicated STA thread with a message pump, away from the UI thread:
/// third-party handlers (7-Zip, Git, …) can be slow and some of them pump messages or show windows.
/// </summary>
internal static class ShellMenuHost
{
    private static readonly Lazy<Dispatcher> Pump = new(Start);
    private static Guid _folderIid = new("000214E6-0000-0000-C000-000000000046");
    private static Guid _menuIid = new("000214E4-0000-0000-C000-000000000046");

    private static Dispatcher Start()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Radii shell menu host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static Task<T> Run<T>(Func<T> work) => Pump.Value.InvokeAsync(work).Task;

    /// <summary>Menu for the selected paths (all in one folder), or for the folder background when none are given.</summary>
    public static Task<ShellMenuSession?> QueryAsync(IReadOnlyList<string> paths, string? folder, IntPtr owner) =>
        Run(() => Build(paths, folder, owner));

    private static ShellMenuSession? Build(IReadOnlyList<string> paths, string? folder, IntPtr owner)
    {
        var pidls = new List<IntPtr>();
        IContextMenu? menu = null;
        var hmenu = IntPtr.Zero;
        try
        {
            menu = paths.Count > 0 ? ForSelection(paths, owner, pidls) : folder is null ? null : ForBackground(folder, owner, pidls);
            if (menu is null)
                return Abandon(menu, pidls);
            hmenu = CreatePopupMenu();
            // CMF_EXPLORE: the verbs Explorer itself asks for.
            if (menu.QueryContextMenu(hmenu, 0, ShellMenuSession.IdFirst, 0x7FFF, 0x4) < 0)
            {
                DestroyMenu(hmenu);
                return Abandon(menu, pidls);
            }
            var entries = ReadMenu(hmenu, menu, menu as IContextMenu2, 0);
            VerbIcons.Attach(entries, paths, folder);
            return new ShellMenuSession(menu, hmenu, pidls, entries);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            if (hmenu != IntPtr.Zero)
                DestroyMenu(hmenu);
            return Abandon(menu, pidls);
        }
    }

    private static ShellMenuSession? Abandon(IContextMenu? menu, List<IntPtr> pidls)
    {
        foreach (var pidl in pidls)
            Marshal.FreeCoTaskMem(pidl);
        if (menu is not null)
            Marshal.FinalReleaseComObject(menu);
        return null;
    }

    private static IContextMenu? ForSelection(IReadOnlyList<string> paths, IntPtr owner, List<IntPtr> pidls)
    {
        var directory = Path.GetDirectoryName(paths[0]);
        if (paths.Any(p => !string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase)))
            return null; // a menu for items from different folders does not exist
        IShellFolder? parent = null;
        var children = new List<IntPtr>();
        foreach (var path in paths)
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out var absolute, 0, out _) != 0)
                return null;
            pidls.Add(absolute);
            var iid = _folderIid;
            if (SHBindToParent(absolute, ref iid, out var folderPointer, out var child) != 0)
                return null;
            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            parent ??= folder;
            children.Add(child);
        }
        var menuIid = _menuIid;
        if (parent!.GetUIObjectOf(owner, (uint)children.Count, children.ToArray(), ref menuIid, IntPtr.Zero, out var result) != 0)
            return null;
        return AsMenu(result);
    }

    private static IContextMenu? ForBackground(string folder, IntPtr owner, List<IntPtr> pidls)
    {
        if (SHParseDisplayName(folder, IntPtr.Zero, out var absolute, 0, out _) != 0)
            return null;
        pidls.Add(absolute);
        var iid = _folderIid;
        if (SHBindToParent(absolute, ref iid, out var parentPointer, out var child) != 0)
            return null;
        var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPointer);
        Marshal.Release(parentPointer);
        iid = _folderIid;
        if (parent.BindToObject(child, IntPtr.Zero, ref iid, out var folderPointer) != 0)
            return null;
        var target = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
        Marshal.Release(folderPointer);
        var menuIid = _menuIid;
        return target.CreateViewObject(owner, ref menuIid, out var result) == 0 ? AsMenu(result) : null;
    }

    private static IContextMenu AsMenu(IntPtr pointer)
    {
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(pointer);
        Marshal.Release(pointer);
        return menu;
    }

    private static List<ShellEntry> ReadMenu(IntPtr hmenu, IContextMenu menu, IContextMenu2? menu2, int depth)
    {
        var entries = new List<ShellEntry>();
        var count = GetMenuItemCount(hmenu);
        for (var position = 0; position < count; position++)
        {
            var info = new MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_ID | MIIM_FTYPE | MIIM_STATE | MIIM_SUBMENU | MIIM_BITMAP | MIIM_STRING,
            };
            // First call reports the text length, second one fills the buffer.
            if (!GetMenuItemInfo(hmenu, (uint)position, true, ref info))
                continue;
            if ((info.fType & MFT_SEPARATOR) != 0)
            {
                entries.Add(new ShellEntry { IsSeparator = true });
                continue;
            }

            var text = "";
            if (info.cch > 0)
            {
                var buffer = Marshal.AllocHGlobal((int)(info.cch + 1) * 2);
                try
                {
                    info.dwTypeData = buffer;
                    info.cch++;
                    if (GetMenuItemInfo(hmenu, (uint)position, true, ref info))
                        text = Marshal.PtrToStringUni(buffer) ?? "";
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            var id = (int)info.wID;
            var verb = info.hSubMenu == IntPtr.Zero ? VerbOf(menu, id) : null;
            var label = text;
            string? hint = null;
            var tab = label.IndexOf('\t');
            if (tab >= 0)
            {
                hint = label[(tab + 1)..];
                label = label[..tab];
            }
            label = label.Replace("&&", "\0").Replace("&", "").Replace("\0", "&");
            if (label.Length == 0 && verb is not null)
                label = verb;

            var children = new List<ShellEntry>();
            if (info.hSubMenu != IntPtr.Zero && depth < 3)
            {
                // Many handlers (Send to, Open with, 7-Zip) fill their submenu only when it is about to open.
                menu2?.HandleMenuMsg(0x0117 /* WM_INITMENUPOPUP */, info.hSubMenu, (IntPtr)position);
                children = ReadMenu(info.hSubMenu, menu, menu2, depth + 1);
            }

            entries.Add(new ShellEntry
            {
                Id = id,
                Label = label,
                Hint = hint,
                Verb = verb,
                Disabled = (info.fState & MFS_DISABLED) != 0,
                Checked = (info.fState & MFS_CHECKED) != 0,
                Icon = IconOf(info.hbmpItem),
                Children = children,
            });
        }
        return entries;
    }

    private static string? VerbOf(IContextMenu menu, int id)
    {
        if (id < ShellMenuSession.IdFirst || id > 0x7FFF)
            return null;
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.WriteInt16(buffer, 0, 0);
            // GCS_VERBW
            return menu.GetCommandString((UIntPtr)(uint)(id - ShellMenuSession.IdFirst), 4, IntPtr.Zero, buffer, 256) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Menu bitmaps are premultiplied 32-bit DIBs; read them top-down with GetDIBits so alpha and orientation are right.</summary>
    private static ImageSource? IconOf(IntPtr bitmap)
    {
        var raw = bitmap.ToInt64();
        if (bitmap == IntPtr.Zero || raw is >= -1 and <= 11)
            return null; // none, or one of the HBMMENU_* pseudo handles
        var header = new BITMAP();
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), ref header) == 0 || header.bmWidth <= 0 || header.bmHeight <= 0)
            return null;
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = header.bmWidth,
            biHeight = -header.bmHeight,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[header.bmWidth * header.bmHeight * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)header.bmHeight, pixels, ref info, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        // Bitmaps without an alpha channel come back with alpha 0 everywhere.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 0xFF;
        }
        var source = BitmapSource.Create(header.bmWidth, header.bmHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, header.bmWidth * 4);
        source.Freeze();
        return source;
    }

    /// <summary>Text dump of a menu, for <c>--shell-dump</c> (checks the COM plumbing without any UI).</summary>
    public static string Dump(IReadOnlyList<string> paths, string? folder)
    {
        // RC_OWNER=<decimal hwnd> reproduces what the live app does (it passes the Explorer window as owner).
        var owner = long.TryParse(Environment.GetEnvironmentVariable("RC_OWNER"), out var hwnd) ? (IntPtr)hwnd : IntPtr.Zero;
        var session = QueryAsync(paths, folder, owner).GetAwaiter().GetResult();
        if (session is null)
            return "(no menu)";
        var result = Describe(session.Entries);
        session.Dispose();
        return result;
    }

    public static string Describe(List<ShellEntry> entries)
    {
        var text = new StringBuilder();
        Append(text, entries, 0);
        return text.ToString();
    }

    private static void Append(StringBuilder text, List<ShellEntry> entries, int depth)
    {
        foreach (var entry in entries)
        {
            text.Append(' ', depth * 2);
            text.AppendLine(entry.IsSeparator
                ? "-----"
                : $"{entry.Label}  [verb={entry.Verb ?? "?"} id={entry.Id}{(entry.Icon is null ? "" : " icon")}{(entry.Disabled ? " disabled" : "")}]");
            Append(text, entry.Children, depth + 1);
        }
    }
}

/// <summary>
/// Drops what the ring already offers and tidies separators. The shell's own commands cannot be recognised by verb
/// alone (its command offsets do not line up with the menu ids), so the localised names are matched as well.
/// </summary>
internal static class ShellFilter
{
    private static readonly HashSet<string> CoveredVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "open", "cut", "copy", "paste", "delete", "rename", "properties", "copyaspath", "openas", "pastelink" };

    private static readonly HashSet<string> CoveredLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open", "Cut", "Copy", "Paste", "Delete", "Rename", "Properties", "Copy as path", "Open with", "Paste shortcut",
        "Открыть", "Вырезать", "Копировать", "Копир.", "Вставить", "Удалить", "Переименовать", "Свойства", "Копировать как путь", "Открыть с помощью", "Вставить ярлык",
    };

    public static List<ShellEntry> Clean(List<ShellEntry> entries, bool top = true)
    {
        var result = new List<ShellEntry>();
        foreach (var entry in entries)
        {
            if (!entry.IsSeparator)
            {
                if (top && ((entry.Verb is not null && CoveredVerbs.Contains(entry.Verb)) || CoveredLabels.Contains(entry.Label) || CoveredLabels.Contains(entry.Label.TrimEnd('…', '.'))))
                    continue;
                if (entry.Label.Length == 0)
                    continue;
            }
            if (entry.IsSeparator && (result.Count == 0 || result[^1].IsSeparator))
                continue;
            if (!entry.HasChildren)
            {
                result.Add(entry);
                continue;
            }
            // A child that only repeats the parent's name (e.g. "New" > "New") is a header, not a command.
            var children = Clean(entry.Children.Where(c => c.IsSeparator || !string.Equals(c.Label, entry.Label, StringComparison.OrdinalIgnoreCase)).ToList(), top: false);
            if (children.Count == 0)
                continue; // a submenu that could not be filled (owner-drawn or loaded later) would be a dead row
            result.Add(new ShellEntry
            {
                Id = entry.Id,
                Label = entry.Label,
                Hint = entry.Hint,
                Verb = entry.Verb,
                Disabled = entry.Disabled,
                Checked = entry.Checked,
                Icon = entry.Icon,
                Children = children,
            });
        }
        while (result.Count > 0 && result[^1].IsSeparator)
            result.RemoveAt(result.Count - 1);
        return result;
    }
}
