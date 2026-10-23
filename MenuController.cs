using static Radii.Native;

namespace Radii;

/// <summary>
/// Connects the hook to the ring. The ring opens when the right button is released and waits for a click: there is no
/// press-drag-release gesture, so a ring that appears under the cursor next to a screen edge never picks a sector by itself.
/// </summary>
internal sealed class MenuController
{
    private readonly RadialWindow _window;
    private int _pressId;
    private IntPtr _root;
    private RingMode _mode;

    public MenuController(RadialWindow window) => _window = window;

    /// <param name="mode">Shift + right click: the ring of conversions; Ctrl + right click: the ring of editors; otherwise the usual ring.</param>
    public void Press(POINT point, IntPtr root, RingMode mode)
    {
        _pressId++;
        _root = root;
        _mode = mode;
    }

    public void Release(POINT point) => _ = ShowAsync(_pressId, point, _root, _mode);

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
                return;
            }
            _window.ShowTop(Menus.For(detected), Theme.Current(), Menus.Summary(detected), point, context, RingMode.Normal, detected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed detection must never leave the user without a context menu: the flyout still has the Windows menu.
            Program.LogCrash(ex);
        }
    }
}
