using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Radii.Converters;
using static Radii.Menus;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Radii.Editors;

/// <summary>Pages of one PDF as a grid: select, rotate, delete, reorder, then save a copy or extract the selected pages.</summary>
internal sealed class PdfPagesEditor : IEditor
{
    public string Caption => T("PDF pages", "Страницы PDF");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && paths[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public EditorWindow Create(IReadOnlyList<string> paths) => new PdfPagesWindow(paths[0]);
}

internal sealed class PdfPagesWindow : EditorWindow
{
    private sealed class PageItem(int source)
    {
        public int Source { get; } = source;
        public int Rotation { get; set; }
        public bool Selected { get; set; }
        public ImageSource? Thumbnail { get; set; }
        public Image? View { get; set; }
    }

    private readonly string _path;
    private readonly List<PageItem> _pages = [];
    private readonly WrapPanel _grid = new() { Margin = new Thickness(8) };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private int _anchor = -1;
    private bool _loaded;
    private bool _thumbnailsDone;

    public PdfPagesWindow(string path) : base(T("PDF pages", "Страницы PDF") + " · " + Path.GetFileName(path), 640, 460, T("Save a copy", "Сохранить копию"))
    {
        _path = path;
        _info.Foreground = Muted;
        _info.FontSize = 12;

        Button Tool(string text, string tip, Action action, bool wide = false)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), MinWidth = wide ? 0 : 36, Height = 30, Padding = new Thickness(wide ? 12 : 8, 3, wide ? 12 : 8, 3) };
            button.Click += (_, _) => action();
            return button;
        }
        Border Divider() => new() { Width = 1, Height = 20, Background = LineBrush, Margin = new Thickness(6, 0, 12, 0) };

        _info.Margin = new Thickness(12, 0, 0, 0);
        _info.TextTrimming = TextTrimming.CharacterEllipsis;
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Tool("↺", T("Rotate the selected pages left", "Повернуть выбранные влево"), () => Rotate(270)),
                Tool("↻", T("Rotate the selected pages right", "Повернуть выбранные вправо"), () => Rotate(90)),
                Divider(),
                Tool("◀", T("Move the selected pages earlier", "Сдвинуть выбранные раньше"), () => MoveSelected(-1)),
                Tool("▶", T("Move the selected pages later", "Сдвинуть выбранные позже"), () => MoveSelected(1)),
                Divider(),
                Tool(T("Delete", "Удалить"), T("Remove the selected pages from the copy", "Убрать выбранные страницы из копии"), DeleteSelected, wide: true),
            },
        };
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.Children.Add(tools);
        Grid.SetColumn(_info, 1);
        toolbar.Children.Add(_info);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid };
        var list = Card(scroll, new Thickness(0));
        var root = new Grid { Margin = new Thickness(Gap, 8, Gap, 4) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(toolbar);
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        Body = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
        };

        Loaded += (_, _) => _ = LoadAsync();
    }

    protected override string DoneTitle => T("PDF saved", "PDF сохранён");

    protected override bool SnapshotReady => _thumbnailsDone;

    /// <summary>"del=2,rot=1:90,select=2:3" (1-based pages).</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            if (pair.Length < 2)
                continue;
            switch (pair[0])
            {
                case "del" when int.TryParse(pair[1], out var number) && number >= 1 && number <= _pages.Count:
                    _pages.RemoveAt(number - 1);
                    break;
                case "select":
                    foreach (var n in pair[1].Split(':'))
                        if (int.TryParse(n, out var pick) && pick >= 1 && pick <= _pages.Count)
                            _pages[pick - 1].Selected = true;
                    break;
                case "rot":
                    var rot = pair[1].Split(':');
                    if (rot.Length == 2 && int.TryParse(rot[0], out var at) && int.TryParse(rot[1], out var degrees) && at >= 1 && at <= _pages.Count)
                        _pages[at - 1].Rotation = (_pages[at - 1].Rotation + degrees) % 360;
                    break;
            }
        }
        Rebuild();
    }

    // --- loading ---

    private async Task LoadAsync()
    {
        await RunAsync(async context =>
        {
            context.Report(T("Reading the PDF…", "Читаю PDF…"), null);
            Windows.Data.Pdf.PdfDocument document;
            try
            {
                document = await PdfTools.OpenAsync(_path);
            }
            catch (Exception ex) when (PdfTools.IsReadError(ex))
            {
                throw new ConvertException(T("Could not open the PDF (damaged or password protected)", "Не удалось открыть PDF (повреждён или с паролем)"), ex);
            }
            var count = (int)document.PageCount;
            for (var i = 0; i < count; i++)
                _pages.Add(new PageItem(i));
            Rebuild();
            _loaded = true;
            for (var i = 0; i < count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                context.Report(T($"Page previews: {i + 1} / {count}", $"Миниатюры страниц: {i + 1} / {count}"), (double)(i + 1) / count);
                try
                {
                    var thumbnail = await PdfTools.RenderAsync(document, i, 220);
                    var page = _pages.FirstOrDefault(p => p.Source == i);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (page is null)
                            return;
                        page.Thumbnail = thumbnail;
                        if (page.View is not null)
                            page.View.Source = thumbnail;
                    });
                }
                catch (Exception ex) when (PdfTools.IsReadError(ex))
                {
                    // a page that cannot be drawn keeps its blank card; it still copies fine
                }
            }
            _thumbnailsDone = true;
            ShowStatus("", error: false);
        });
        _thumbnailsDone = true;
    }

    // --- grid ---

    private void Rebuild()
    {
        _grid.Children.Clear();
        for (var i = 0; i < _pages.Count; i++)
            _grid.Children.Add(PageCard(_pages[i], i));
        UpdateInfo();
    }

    private Border PageCard(PageItem page, int index)
    {
        var image = new Image { Source = page.Thumbnail, Stretch = Stretch.Uniform, LayoutTransform = new RotateTransform(page.Rotation) };
        page.View = image;
        var sheet = new Border { Width = 92, Height = 118, Background = Brushes.White, BorderBrush = LineBrush, BorderThickness = new Thickness(1), Child = image };
        var label = new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture) + (page.Rotation != 0 ? $"  ↻{page.Rotation}°" : ""), Foreground = Muted, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var wash = AccentBrush.Clone();
        wash.Opacity = 0.2;
        var card = new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = page.Selected ? AccentBrush : Brushes.Transparent,
            Background = page.Selected ? wash : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new StackPanel { Children = { sheet, label } },
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            Select(index, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        };
        return card;
    }

    private void Select(int index, bool toggle, bool range)
    {
        if (range && _anchor >= 0)
        {
            var from = Math.Min(_anchor, index);
            var to = Math.Max(_anchor, index);
            for (var i = 0; i < _pages.Count; i++)
                _pages[i].Selected = i >= from && i <= to;
        }
        else if (toggle)
        {
            _pages[index].Selected = !_pages[index].Selected;
            _anchor = index;
        }
        else
        {
            for (var i = 0; i < _pages.Count; i++)
                _pages[i].Selected = i == index;
            _anchor = index;
        }
        Rebuild();
    }

    private void SelectAll()
    {
        var all = _pages.Any(p => !p.Selected);
        foreach (var page in _pages)
            page.Selected = all;
        Rebuild();
    }

    private void Rotate(int degrees)
    {
        foreach (var page in _pages.Where(p => p.Selected))
            page.Rotation = (page.Rotation + degrees) % 360;
        Rebuild();
    }

    private void DeleteSelected()
    {
        if (_pages.All(p => p.Selected) && _pages.Count > 0)
        {
            ShowStatus(T("A PDF needs at least one page", "В PDF должна остаться хотя бы одна страница"), error: true);
            return;
        }
        _pages.RemoveAll(p => p.Selected);
        _anchor = -1;
        Rebuild();
    }

    private void MoveSelected(int direction)
    {
        if (direction < 0)
        {
            for (var i = 1; i < _pages.Count; i++)
            {
                if (_pages[i].Selected && !_pages[i - 1].Selected)
                    (_pages[i - 1], _pages[i]) = (_pages[i], _pages[i - 1]);
            }
        }
        else
        {
            for (var i = _pages.Count - 2; i >= 0; i--)
            {
                if (_pages[i].Selected && !_pages[i + 1].Selected)
                    (_pages[i + 1], _pages[i]) = (_pages[i], _pages[i + 1]);
            }
        }
        Rebuild();
    }

    private void UpdateInfo()
    {
        var selected = _pages.Count(p => p.Selected);
        _info.Text = selected > 0
            ? T($"{_pages.Count} pages, {selected} selected", $"Страниц: {_pages.Count}, выбрано: {selected}")
            : T($"{_pages.Count} pages", $"Страниц: {_pages.Count}");
        Primary.Content = selected > 0
            ? T($"Extract ({selected})", $"Извлечь ({selected})")
            : T("Save a copy", "Сохранить копию");
    }

    // --- saving ---

    /// <summary>With pages selected only those are saved (extract); otherwise the whole document with the current edits.</summary>
    protected override Task<string> SaveAsync(ConvertContext context)
    {
        var picked = _pages.Where(p => p.Selected).ToList();
        return picked.Count > 0
            ? WriteAsync(context, picked, T(" (pages)", " (страницы)"))
            : WriteAsync(context, _pages.ToList(), T(" (edited)", " (изменён)"));
    }

    private async Task<string> WriteAsync(ConvertContext context, List<PageItem> pages, string suffix)
    {
        if (!_loaded || pages.Count == 0)
            throw new ConvertException(T("No pages to save", "Нет страниц для сохранения"));
        var plan = pages.Select(p => (p.Source, p.Rotation)).ToList();
        var path = _path;
        var output = ConvertContext.NewPath(path, "pdf", suffix);
        await Task.Run(() =>
        {
            try
            {
                using var input = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                using var result = new SharpDocument();
                for (var i = 0; i < plan.Count; i++)
                {
                    context.Cancel.ThrowIfCancellationRequested();
                    var page = result.AddPage(input.Pages[plan[i].Source]);
                    if (plan[i].Rotation != 0)
                        page.Rotate = (page.Rotate + plan[i].Rotation) % 360;
                    context.Report(T("Writing the pages…", "Записываю страницы…"), (double)(i + 1) / plan.Count);
                }
                result.Save(output);
            }
            catch (Exception ex) when (ex is PdfSharp.PdfSharpException or InvalidOperationException or NotSupportedException or ArgumentOutOfRangeException)
            {
                if (File.Exists(output))
                    File.Delete(output);
                throw new ConvertException(T("Could not save (the file may be protected or damaged): ", "Не удалось сохранить (файл защищён или повреждён): ") + ex.Message, ex);
            }
        }, context.Cancel);
        return output;
    }
}
