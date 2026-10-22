using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Radii.Converters;
using static Radii.Menus;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Radii.Editors;

/// <summary>Cut a PDF into pieces: every page, every N pages, or the ranges you type. The pieces go into a new folder next to the file.</summary>
internal sealed class PdfSplitEditor : IEditor
{
    public string Caption => T("Split PDF", "Разрезать PDF");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && paths[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public EditorWindow Create(IReadOnlyList<string> paths) => new PdfSplitWindow(paths[0]);
}

internal static class PageRanges
{
    /// <summary>"1-3, 5, 7-" → [1,2,3], [5], [7..count]; null with a message when the text is not valid.</summary>
    public static (List<List<int>>? Groups, string? Error) Parse(string text, int count)
    {
        var groups = new List<List<int>>();
        foreach (var token in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length > 2)
                return (null, T($"Cannot read \"{token}\"", $"Не понял «{token}»"));
            int from, to;
            if (parts.Length == 1)
            {
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out from))
                    return (null, T($"Cannot read \"{token}\"", $"Не понял «{token}»"));
                to = from;
            }
            else
            {
                from = parts[0].Length == 0 ? 1 : int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : -1;
                to = parts[1].Length == 0 ? count : int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : -1;
                if (from < 0 || to < 0)
                    return (null, T($"Cannot read \"{token}\"", $"Не понял «{token}»"));
            }
            if (from < 1 || to > count || from > to)
                return (null, T($"\"{token}\" is outside 1–{count}", $"«{token}» вне диапазона 1–{count}"));
            groups.Add(Enumerable.Range(from, to - from + 1).ToList());
        }
        return groups.Count == 0 ? (null, T("Type the pages, for example 1-3, 5, 7-9", "Напиши страницы, например 1-3, 5, 7-9")) : (groups, null);
    }

    public static string Label(List<int> pages) => pages.Count == 1 ? pages[0].ToString(CultureInfo.InvariantCulture) : $"{pages[0]}-{pages[^1]}";
}

internal sealed class PdfSplitWindow : EditorWindow
{
    private readonly string _path;
    private readonly RadioButton _each = new() { Content = T("Each page separately", "Каждая страница отдельно"), GroupName = "mode", IsChecked = true };
    private readonly RadioButton _every = new() { Content = T("Every N pages", "По N страниц"), GroupName = "mode" };
    private readonly RadioButton _ranges = new() { Content = T("Ranges", "Диапазоны"), GroupName = "mode" };
    private readonly TextBox _n = new() { Text = "2", Width = 60, TextAlignment = TextAlignment.Center };
    private readonly TextBox _rangeText = new() { Text = "1-3", MinWidth = 260 };
    private readonly CheckBox _together = new() { Content = T("Into one file", "В один файл") };
    private readonly StackPanel _preview = new();
    private int _pageCount;
    private string? _loadError;

    public PdfSplitWindow(string path) : base(T("Split PDF", "Разрезать PDF") + " · " + Path.GetFileName(path), 520, 400, T("Split", "Разрезать"))
    {
        _path = path;
        try
        {
            using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
            _pageCount = document.PageCount;
        }
        catch (Exception ex) when (ex is PdfSharp.PdfSharpException or IOException or InvalidOperationException or NotSupportedException)
        {
            _loadError = T("Could not open the PDF (damaged or password protected)", "Не удалось открыть PDF (повреждён или с паролем)");
        }

        foreach (var radio in new[] { _each, _every, _ranges })
            radio.Checked += (_, _) => Recompute();
        _n.TextChanged += (_, _) => Recompute();
        _rangeText.TextChanged += (_, _) => Recompute();
        _together.Checked += (_, _) => Recompute();
        _together.Unchecked += (_, _) => Recompute();
        _n.GotFocus += (_, _) => _every.IsChecked = true;
        _rangeText.GotFocus += (_, _) => _ranges.IsChecked = true;

        foreach (var radio in new[] { _each, _every, _ranges })
            radio.VerticalAlignment = VerticalAlignment.Center;
        _n.Width = 72;
        _rangeText.MinWidth = 0;
        _rangeText.Width = 170;
        _together.Margin = new Thickness(0, 4, 0, 0);

        UIElement Option(UIElement radio, UIElement? control, double top)
        {
            var grid = new Grid { Margin = new Thickness(0, top, 0, 0), MinHeight = 32 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(radio);
            if (control is not null)
            {
                Grid.SetColumn(control, 1);
                grid.Children.Add(control);
            }
            return grid;
        }
        var pagesIn = new StackPanel { Orientation = Orientation.Horizontal, Children = { _n, new TextBlock { Text = T("per file", "в файле"), Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) } } };
        var form = Section("",
            Option(_each, null, 0),
            Option(_every, pagesIn, 4),
            Option(_ranges, _rangeText, 4),
            new StackPanel { Margin = new Thickness(28, 0, 0, 0), Children = { _together } });
        var list = Card(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _preview, Height = 110 }, new Thickness(0, 4, 0, 4));
        Body = new StackPanel { Margin = new Thickness(Gap, 8, Gap, 4), Children = { form, new Border { Height = 10 }, list } };
        FitHeight(520);
        Recompute();
    }

    protected override string DoneTitle => T("PDF split", "PDF разрезан");

    /// <summary>"each", "every=3", "ranges=1-3;5", "together".</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            switch (pair[0])
            {
                case "each":
                    _each.IsChecked = true;
                    break;
                case "every":
                    _every.IsChecked = true;
                    _n.Text = pair.Length > 1 ? pair[1] : "2";
                    break;
                case "ranges":
                    _ranges.IsChecked = true;
                    _rangeText.Text = pair.Length > 1 ? pair[1] : "";
                    break;
                case "together":
                    _together.IsChecked = true;
                    break;
            }
        }
        Recompute();
    }

    /// <summary>The pieces the current choice makes (1-based pages per piece), or an error text.</summary>
    private (List<List<int>>? Groups, string? Error) Plan()
    {
        if (_loadError is not null)
            return (null, _loadError);
        if (_each.IsChecked == true)
            return (Enumerable.Range(1, _pageCount).Select(i => new List<int> { i }).ToList(), null);
        if (_every.IsChecked == true)
        {
            if (!int.TryParse(_n.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1)
                return (null, T("Enter how many pages go into one file", "Укажи, сколько страниц в одном файле"));
            var groups = new List<List<int>>();
            for (var start = 1; start <= _pageCount; start += n)
                groups.Add(Enumerable.Range(start, Math.Min(n, _pageCount - start + 1)).ToList());
            return (groups, null);
        }
        var (parsed, error) = PageRanges.Parse(_rangeText.Text, _pageCount);
        if (parsed is not null && _together.IsChecked == true)
            parsed = [parsed.SelectMany(g => g).ToList()];
        return (parsed, error);
    }

    private void Recompute()
    {
        if (_preview is null || _together is null)
            return;
        _preview.Children.Clear();
        var (groups, error) = Plan();
        if (groups is null)
        {
            ShowStatus(error ?? "", error: true);
            return;
        }
        var name = Path.GetFileNameWithoutExtension(_path);
        var shown = 0;
        foreach (var group in groups)
        {
            if (shown++ >= 400)
            {
                _preview.Children.Add(new TextBlock { Text = T($"… and {groups.Count - 400} more", $"… и ещё {groups.Count - 400}"), Foreground = Muted, Margin = new Thickness(12, 6, 12, 6) });
                break;
            }
            _preview.Children.Add(new TextBlock
            {
                Text = $"{FileName(name, group)}   ·   " + T(group.Count == 1 ? "1 page" : $"{group.Count} pages", $"{group.Count} стр."),
                Margin = new Thickness(14, 5, 14, 5),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        SetHint(T($"{_pageCount} pages → {groups.Count} files", $"{_pageCount} стр. → файлов: {groups.Count}"));
    }

    private static string FileName(string name, List<int> pages) =>
        $"{name} ({T("p.", "стр.")} {(pages.Count == pages[^1] - pages[0] + 1 ? PageRanges.Label(pages) : string.Join('+', pages))}).pdf";

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var (groups, error) = Plan();
        if (groups is null)
            throw new ConvertException(error ?? T("Nothing to split", "Нечего разрезать"));
        var folder = Menus.UniquePath(Path.GetDirectoryName(_path) ?? "", Path.GetFileNameWithoutExtension(_path) + T(" (parts)", " (части)"), "");
        var path = _path;
        var name = Path.GetFileNameWithoutExtension(path);
        await Task.Run(() =>
        {
            try
            {
                using var input = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                Directory.CreateDirectory(folder);
                for (var i = 0; i < groups.Count; i++)
                {
                    context.Cancel.ThrowIfCancellationRequested();
                    using var part = new SharpDocument();
                    foreach (var page in groups[i])
                        part.AddPage(input.Pages[page - 1]);
                    part.Save(Path.Combine(folder, FileName(name, groups[i])));
                    context.Report(T("Writing the parts…", "Записываю части…"), (double)(i + 1) / groups.Count);
                }
            }
            catch (Exception ex) when (ex is PdfSharp.PdfSharpException or InvalidOperationException or NotSupportedException or ArgumentOutOfRangeException)
            {
                throw new ConvertException(T("Could not split (the file may be protected or damaged): ", "Не удалось разрезать (файл защищён или повреждён): ") + ex.Message, ex);
            }
        }, context.Cancel);
        DoneOverride = T($"Made {groups.Count} files", $"Создано файлов: {groups.Count}");
        return folder;
    }
}
