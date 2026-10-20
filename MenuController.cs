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
    private bool _convert;

    public MenuController(RadialWindow window) => _window = window;

    /// <param name="convert">Shift + right click: the ring offers conversions instead of the usual actions.</param>
    public void Press(POINT point, IntPtr root, bool convert)
    {
        _pressId++;
        _shown = false;
        _root = root;
        _convert = convert;
        if (Settings.Current.GestureMode)
            _ = ShowAsync(_pressId, point, root, convert);
    }

    /// <summary>The converter hotkey: no button is held, so the ring simply waits for a click.</summary>
    public void OpenConverter(POINT point, IntPtr root)
    {
        _pressId++;
        _shown = false;
        _ = ShowAsync(_pressId, point, root, convert: true);
    }

    public void Release(POINT point)
    {
        if (!Settings.Current.GestureMode)
            _ = ShowAsync(_pressId, point, _root, _convert);
        else if (_shown)
            _window.ReleaseAt(point);
        // Released before the ring appeared: it opens in click mode and waits for a click.
    }

    private async Task ShowAsync(int id, POINT point, IntPtr root, bool convert)
    {
        try
        {
            var detected = await ContextDetector.DetectAsync(point, root);
            if (id != _pressId)
                return;
            if (convert)
            {
                var items = detected.Paths.Count > 0 ? Converters.ConvertMenu.For(detected.Paths) : [];
                if (items.Count == 0)
                {
                    Toast.Show(Menus.T("Converter", "Конвертер"), detected.HasSelection && detected.Paths.Count == 0
                        ? Menus.T("Could not tell which files are selected", "Не удалось определить выбранные файлы")
                        : detected.HasSelection
                            ? Menus.T("Nothing to convert this to", "Для этого типа нет вариантов конвертации")
                            : Menus.T("Point at a file first", "Наведи курсор на файл"));
                    return;
                }
                _window.ShowAt(items, Theme.Current(), Menus.Summary(detected), point, new ShellContext(detected.Paths, detected.FolderPath, root));
                _shown = true;
                return;
            }
            _window.ShowAt(Menus.For(detected), Theme.Current(), Menus.Summary(detected), point, new ShellContext(detected.Paths, detected.FolderPath, root));
            _shown = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed detection must never leave the user without a context menu: Shift + right click still works.
        }
    }
}
