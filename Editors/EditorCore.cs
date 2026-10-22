using System.IO;

namespace Radii.Editors;

/// <summary>A mini editor: a small window for one kind of file that opens from the editors ring (Ctrl + right click).</summary>
internal interface IEditor
{
    /// <summary>Caption of the sector in the editors ring.</summary>
    string Caption { get; }

    string Glyph { get; }

    bool Accepts(IReadOnlyList<string> paths);

    /// <summary>Creates the (not yet shown) window.</summary>
    EditorWindow Create(IReadOnlyList<string> paths);
}

internal static class EditorMenu
{
    /// <summary>The order here is the order of the sectors in the ring; the name is for the diagnostics.</summary>
    private static readonly (string Name, IEditor Editor)[] Registry =
    [
        ("crop", new ImageCropEditor()),
        ("resize", new ImageResizeEditor()),
        ("color", new ImageColorEditor()),
        ("compress", new ImageCompressEditor()),
        ("audio", new AudioTrimEditor()),
        ("join", new AudioJoinEditor()),
        ("tune", new AudioTuneEditor()),
        ("video", new VideoTrimEditor()),
        ("vcrop", new VideoCropEditor()),
        ("pdf", new PdfMergeEditor()),
        ("pages", new PdfPagesEditor()),
        ("split", new PdfSplitEditor()),
        ("pdfzip", new PdfCompressEditor()),
        ("meta", new MetadataEditor()),
        ("rename", new RenameEditor()),
    ];

    private static readonly IEditor[] All = Registry.Select(r => r.Editor).ToArray();

    private static List<IEditor> For(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => File.Exists(p) || Directory.Exists(p)) ? All.Where(e => e.Accepts(paths)).ToList() : [];

    public static bool Supports(IReadOnlyList<string> paths) => For(paths).Count > 0;

    /// <summary>The ring of editors for these files (Ctrl + right click); empty when there is none. A ring needs two sectors, so one editor gets a "Cancel" beside it.</summary>
    public static List<RadialItem> Ring(IReadOnlyList<string> paths)
    {
        var editors = For(paths);
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var items = editors
            .Select(e => new RadialItem("edit:" + e.Caption, e.Glyph, e.Caption, () => dispatcher.BeginInvoke(() => Show(e, paths))))
            .ToList();
        if (items.Count == 1)
            items.Add(new RadialItem("edit:cancel", "", Menus.T("Cancel", "Отмена"), () => { }));
        return items;
    }

    public static void Show(IEditor editor, IReadOnlyList<string> paths)
    {
        try
        {
            var window = editor.Create(paths);
            window.Show();
            window.Activate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Notifier.Show("Radii", Menus.T("Could not open the editor: ", "Не удалось открыть редактор: ") + ex.Message);
        }
    }

    /// <summary>Editor by name for the diagnostics (<c>--snapshot-editor</c>, <c>--editor-save</c>).</summary>
    public static IEditor? ByName(string name) => Registry.FirstOrDefault(r => r.Name == name).Editor;
}
