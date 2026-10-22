using static Radii.Native;

namespace Radii;

/// <summary>
/// Connects the hook to the ring. Gesture mode: the ring opens on press, and releasing the button over a sector picks it
/// (a quick click without moving leaves the ring open for a normal click). Otherwise the ring opens on release.
/// </summary>
internal sealed class MenuController
{
    private readonly RadialWindow _window;
    private int _pressId;
    private bool _shown;
    private IntPtr _root;
    private RingMode _mode;

    public MenuController(RadialWindow window) => _window = window;

    /// <param name="mode">Shift + right click: the ring of conversions; Ctrl + right click: the ring of editors; otherwise the usual ring.</param>
    public void Press(POINT point, IntPtr root, RingMode mode)
    {
        _pressId++;
        _shown = false;
        _root = root;
        _mode = mode;
        if (Settings.Current.GestureMode)
            _ = ShowAsync(_pressId, point, root, mode);
    }

    public void Release(POINT point)
    {
        if (!Settings.Current.GestureMode)
            _ = ShowAsync(_pressId, point, _root, _mode);
        else if (_shown)
            _window.ReleaseAt(point);
        // Released before the ring appeared: it opens in click mode and waits for a click.
    }

    private async Task ShowAsync(int id, POINT point, IntPtr root, RingMode mode)
    {
        try
        {
            var detected = await ContextDetector.DetectAsync(point, root);
            if (id != _pressId)
                return;
            var context = new ShellContext(detected.Paths, detected.FolderPath, root);
            if (mode != RingMode.Normal)
            {
                var convert = mode == RingMode.Convert;
                var items = detected.Paths.Count == 0 ? [] : convert ? Converters.ConvertMenu.For(detected.Paths) : Editors.EditorMenu.Ring(detected.Paths);
                if (items.Count == 0)
                {
                    Toast.Show(convert ? Menus.T("Converter", "Конвертер") : Menus.T("Editors", "Правка"), detected.HasSelection && detected.Paths.Count == 0
                        ? Menus.T("Could not tell which files are selected", "Не удалось определить выбранные файлы")
                        : detected.HasSelection
                            ? convert ? Menus.T("Nothing to convert this to", "Для этого типа нет вариантов конвертации") : Menus.T("No editor for this type yet", "Для этого типа пока нет редактора")
                            : Menus.T("Point at a file first", "Наведи курсор на файл"));
                    return;
                }
                _window.ShowTop(items, Theme.Current(), Menus.Summary(detected), point, context, mode, detected);
                _shown = true;
                return;
            }
            _window.ShowTop(Menus.For(detected), Theme.Current(), Menus.Summary(detected), point, context, RingMode.Normal, detected);
            _shown = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed detection must never leave the user without a context menu: Shift + right click still works.
        }
    }
}
