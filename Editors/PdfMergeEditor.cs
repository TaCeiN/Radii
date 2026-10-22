using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Radii.Converters;
using static Radii.Menus;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Radii.Editors;

/// <summary>Join PDF files in the order you set. Pages are copied as they are (no re-rendering), so text stays text.</summary>
internal sealed class PdfMergeEditor : IEditor
{
    public string Caption => T("Merge PDFs", "Объединить PDF");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count >= 2 && paths.All(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    public EditorWindow Create(IReadOnlyList<string> paths) => new PdfMergeWindow(paths);
}

internal sealed class PdfMergeWindow : EditorWindow
{
    private sealed class PdfEntry(string path) : ListEntry(path)
    {
        public int Pages { get; set; }
    }

    private readonly FileListPanel _list;

    public PdfMergeWindow(IReadOnlyList<string> paths) : base(T("Merge PDFs", "Объединить PDF"), 540, 260, T("Merge", "Объединить"))
    {
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                Add(dropped);
        };
        _list = new FileListPanel(PanelBrush, LineBrush, Muted, AccentBrush, DangerBrush, thumbnails: true) { MaxHeight = 170 };
        _list.Changed += UpdateHint;

        var add = new Button { Content = T("Add files…", "Добавить файлы…") };
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "PDF|*.pdf", Title = T("Add PDF files", "Добавить PDF") };
            if (dialog.ShowDialog(this) == true)
                Add(dialog.FileNames);
        };
        var toolbar = FileListPanel.Toolbar(add, T("Drag the ⋮⋮ handle to reorder. You can also drop PDFs here.", "Тяни за ⋮⋮, чтобы поменять порядок. Файлы PDF можно бросать сюда."), Muted);

        MinHeight = 0;
        FitHeight(520);
        Body = new StackPanel { Margin = new Thickness(16, 12, 16, 8), Children = { toolbar, _list } };

        Add(paths);
    }

    protected override string DoneTitle => T("PDFs merged", "PDF объединены");

    protected override bool SnapshotReady => _list.Entries.All(e => e.Ready);

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
        {
            var entry = new PdfEntry(path);
            _list.Entries.Add(entry);
            _ = LoadInfoAsync(entry);
        }
        _list.Refresh();
    }

    private async Task LoadInfoAsync(PdfEntry entry)
    {
        try
        {
            var document = await PdfTools.OpenAsync(entry.Path);
            entry.Pages = (int)document.PageCount;
            if (entry.Pages == 0)
            {
                entry.Error = T("No pages", "Нет страниц");
            }
            else
            {
                entry.Detail = T(entry.Pages == 1 ? "1 page" : $"{entry.Pages} pages", $"{entry.Pages} стр.");
                entry.Thumbnail = await PdfTools.RenderAsync(document, 0, 120);
            }
        }
        catch (Exception ex) when (PdfTools.IsReadError(ex))
        {
            entry.Error = T("Damaged or password protected", "Повреждён или с паролем");
        }
        // The WinRT awaits may resume off the UI thread.
        await Dispatcher.InvokeAsync(() =>
        {
            entry.Ready = true;
            _list.Refresh();
        });
    }

    private void UpdateHint()
    {
        var entries = _list.Entries.OfType<PdfEntry>().ToList();
        var pages = entries.Sum(e => e.Pages);
        SetHint(entries.Count < 2
            ? T("Add at least one more PDF.", "Добавь ещё хотя бы один PDF.")
            : T($"Total {pages} pages · {entries.Count} files", $"Итого {pages} стр. · файлов: {entries.Count}"));
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var entries = _list.Entries;
        if (entries.Count < 2)
            throw new ConvertException(T("Add at least two PDFs", "Нужно хотя бы два PDF"));
        if (entries.FirstOrDefault(e => e.Error is not null) is { } broken)
            throw new ConvertException(T("Cannot read ", "Не удаётся прочитать ") + Path.GetFileName(broken.Path) + ": " + broken.Error);
        var paths = entries.Select(e => e.Path).ToList();
        var output = ConvertContext.NewPath(paths[0], "pdf", T(" (merged)", " (объединён)"));
        await Task.Run(() =>
        {
            try
            {
                using var merged = new SharpDocument();
                for (var i = 0; i < paths.Count; i++)
                {
                    context.Cancel.ThrowIfCancellationRequested();
                    context.Report(T($"Adding {Path.GetFileName(paths[i])}", $"Добавляю {Path.GetFileName(paths[i])}"), (double)i / paths.Count);
                    using var input = PdfSharp.Pdf.IO.PdfReader.Open(paths[i], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                    foreach (var page in input.Pages)
                        merged.AddPage(page);
                }
                context.Report(T("Writing the file…", "Записываю файл…"), 1);
                merged.Save(output);
            }
            catch (Exception ex) when (ex is PdfSharp.PdfSharpException or InvalidOperationException or NotSupportedException)
            {
                if (File.Exists(output))
                    File.Delete(output);
                throw new ConvertException(T("Could not merge (a file may be protected or damaged): ", "Не удалось объединить (файл защищён или повреждён): ") + ex.Message, ex);
            }
        }, context.Cancel);
        return output;
    }
}
