using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

internal enum CaseMode
{
    Keep,
    Lower,
    Upper,
    Title,
    Sentence,
}

internal enum RenameOrder
{
    Selection,
    Name,
    Modified,
    Size,
}

internal sealed record RenameOptions(string Pattern, string Find, string Replace, bool UseRegex, bool MatchCase, CaseMode Case, int Start, int Step, int Digits, RenameOrder Order, bool Trim);

/// <param name="Problem">Why this row cannot be applied (empty name, duplicate, taken by another file), or null.</param>
internal sealed record RenameRow(string OldPath, string NewPath, string? Problem)
{
    public bool Changed => !string.Equals(OldPath, NewPath, StringComparison.Ordinal);
}

/// <summary>The renaming rules as a pure function of the selection and the options: what the live preview and the rename both use.</summary>
internal static partial class RenamePlanner
{
    [GeneratedRegex(@"\{(name|n|ext|parent|date|created)(?::([^}]*))?\}")]
    private static partial Regex TokenRegex();

    public static List<RenameRow> Plan(IReadOnlyList<string> paths, RenameOptions o)
    {
        // One spelling per path (slashes, trailing backslash), or "same file" would look like a change.
        paths = paths.Select(p => System.IO.Path.GetFullPath(p).TrimEnd(System.IO.Path.DirectorySeparatorChar)).ToList();
        var infos = paths.Select(p => (Path: p, Info: (FileSystemInfo)(Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p)))).ToList();
        var ordered = o.Order switch
        {
            RenameOrder.Name => infos.OrderBy(i => i.Path, NaturalComparer.Instance).ToList(),
            RenameOrder.Modified => infos.OrderBy(i => i.Info.LastWriteTime).ToList(),
            RenameOrder.Size => infos.OrderBy(i => i.Info is FileInfo f && f.Exists ? f.Length : 0).ToList(),
            _ => infos,
        };
        var number = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordered.Count; i++)
            number[ordered[i].Path] = o.Start + (long)i * o.Step;

        Regex? find = null;
        if (o.Find.Length > 0)
        {
            try
            {
                find = new Regex(o.UseRegex ? o.Find : Regex.Escape(o.Find), o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                // an unfinished regex while typing: no replacement for now
            }
        }

        var rows = new List<(string Old, string New, string? Problem)>();
        foreach (var (path, info) in infos)
        {
            var isDirectory = info is DirectoryInfo;
            var name = isDirectory ? System.IO.Path.GetFileName(path.TrimEnd('\\')) : System.IO.Path.GetFileNameWithoutExtension(path);
            var extension = isDirectory ? "" : System.IO.Path.GetExtension(path);
            if (find is not null)
            {
                try
                {
                    name = find.Replace(name, o.UseRegex ? o.Replace : o.Replace.Replace("$", "$$"));
                }
                catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
                {
                }
            }
            name = Expand(o.Pattern.Length == 0 ? "{name}" : o.Pattern, name, extension, number[path], o.Digits, info);
            name = ApplyCase(name, o.Case);
            if (o.Trim)
                name = name.Trim().TrimEnd('.');
            name = Sanitize(name);
            var folder = System.IO.Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
            rows.Add((path, System.IO.Path.Combine(folder, name + extension), name.Length == 0 ? T("Empty name", "Пустое имя") : null));
        }

        var sources = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var counts = rows.GroupBy(r => r.New, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return rows.Select(r =>
        {
            var problem = r.Problem;
            var changed = !string.Equals(r.Old, r.New, StringComparison.Ordinal);
            if (problem is null && changed)
            {
                if (counts[r.New] > 1)
                    problem = T("Same name as another file in the list", "Такое же имя у другого файла в списке");
                else if (!sources.Contains(r.New) && (File.Exists(r.New) || Directory.Exists(r.New)))
                    problem = T("A file with this name already exists", "Файл с таким именем уже есть");
            }
            return new RenameRow(r.Old, r.New, problem);
        }).ToList();
    }

    private static string Expand(string pattern, string name, string extension, long number, int digits, FileSystemInfo info) =>
        TokenRegex().Replace(pattern, m =>
        {
            var argument = m.Groups[2].Success ? m.Groups[2].Value : null;
            try
            {
                return m.Groups[1].Value switch
                {
                    "name" => name,
                    "n" => number.ToString(new string('0', Math.Clamp(argument is not null && int.TryParse(argument, out var d) ? d : digits, 1, 12)), CultureInfo.InvariantCulture),
                    "ext" => extension.TrimStart('.'),
                    "parent" => info.FullName is { } full ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(full.TrimEnd('\\')) ?? "") : "",
                    "date" => info.LastWriteTime.ToString(argument ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "created" => info.CreationTime.ToString(argument ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => m.Value,
                };
            }
            catch (FormatException)
            {
                return m.Value;
            }
        });

    private static string ApplyCase(string name, CaseMode mode) => mode switch
    {
        CaseMode.Lower => name.ToLowerInvariant(),
        CaseMode.Upper => name.ToUpperInvariant(),
        CaseMode.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
        CaseMode.Sentence => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant(),
        _ => name,
    };

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return builder.ToString();
    }

    /// <summary>"photo2" before "photo10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}

/// <summary>Rename many files at once with a pattern, find and replace, case and a counter, with a live "was → will be" list and an undo for the last batch.</summary>
internal sealed class RenameEditor : IEditor
{
    public string Caption => T("Rename files", "Переименование");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) => paths.Count >= 2;

    public EditorWindow Create(IReadOnlyList<string> paths) => new RenameWindow(paths);
}

internal sealed class RenameWindow : EditorWindow
{
    private sealed record Move(string From, string To);

    private static string UndoFile => Path.Combine(Settings.Folder, "last-rename.json");

    private readonly IReadOnlyList<string> _paths;
    private readonly TextBox _pattern = new() { Text = "{name}" };
    private readonly TextBox _find = new();
    private readonly TextBox _replace = new();
    private readonly CheckBox _regex = new() { Content = "Regex", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _matchCase = new() { Content = T("Match case", "Учитывать регистр"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _trim = new() { Content = T("Trim spaces", "Обрезать пробелы"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _case = new();
    private readonly ComboBox _order = new();
    private readonly TextBox _start = new() { Text = "1", Width = 60, TextAlignment = TextAlignment.Center };
    private readonly TextBox _step = new() { Text = "1", Width = 50, TextAlignment = TextAlignment.Center };
    private readonly TextBox _digits = new() { Text = "1", Width = 50, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _rows = new();
    private readonly Button _undo;
    private List<RenameRow> _plan = [];
    private bool _testUndo;

    public RenameWindow(IReadOnlyList<string> paths) : base(T("Rename files", "Переименование") + $" · {paths.Count}", 700, 440, T("Rename", "Переименовать"))
    {
        _paths = paths;
        foreach (var (mode, label) in new[] { (CaseMode.Keep, T("As is", "Как есть")), (CaseMode.Lower, "lowercase"), (CaseMode.Upper, "UPPERCASE"), (CaseMode.Title, "Title Case"), (CaseMode.Sentence, T("Sentence case", "Как в предложении")) })
            _case.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        _case.SelectedIndex = 0;
        foreach (var (mode, label) in new[] { (RenameOrder.Selection, T("As selected", "Как выделено")), (RenameOrder.Name, T("By name", "По имени")), (RenameOrder.Modified, T("By date modified", "По дате изменения")), (RenameOrder.Size, T("By size", "По размеру")) })
            _order.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        _order.SelectedIndex = 0;

        foreach (var box in new[] { _pattern, _find, _replace, _start, _step, _digits })
            box.TextChanged += (_, _) => Recompute();
        foreach (var box in new[] { _regex, _matchCase, _trim })
        {
            box.Checked += (_, _) => Recompute();
            box.Unchecked += (_, _) => Recompute();
        }
        _case.SelectionChanged += (_, _) => Recompute();
        _order.SelectionChanged += (_, _) => Recompute();

        Button Token(string token, string tip)
        {
            var button = Chip(token, () =>
            {
                var at = _pattern.CaretIndex;
                _pattern.Text = _pattern.Text.Insert(Math.Min(at, _pattern.Text.Length), token);
                _pattern.CaretIndex = at + token.Length;
                _pattern.Focus();
            });
            button.ToolTip = tip;
            button.Padding = new Thickness(9, 3, 9, 3);
            button.MinWidth = 0;
            return button;
        }

        var tokens = new WrapPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                Token("{name}", T("Original name", "Исходное имя")),
                Token("{n}", T("Counter", "Счётчик")),
                Token("{date}", T("Date modified, {date:yyyyMMdd} for another format", "Дата изменения, {date:yyyyMMdd} для другого формата")),
                Token("{parent}", T("Folder name", "Имя папки")),
            },
        };
        foreach (var chip in tokens.Children.OfType<Button>())
        {
            chip.FontSize = 12;
            chip.Padding = new Thickness(7, 2, 7, 2);
            chip.Margin = new Thickness(0, 0, 5, 0);
        }

        // Plain stacked lines (label, field) with no cards: the form stays small. Step is fixed at 1, trim is always on.
        Grid Line(string label, UIElement control, double top = 8)
        {
            var grid = new Grid { Margin = new Thickness(0, top, 0, 0), MinHeight = 32 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock { Text = label, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            if (control is FrameworkElement fe)
                fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }
        _regex.Margin = new Thickness(0);
        _matchCase.Margin = new Thickness(16, 0, 0, 0);
        _matchCase.Content = T("Match case", "Регистр");
        _start.Width = 56;
        _digits.Width = 56;
        var counter = new StackPanel { Orientation = Orientation.Horizontal };
        counter.Children.Add(_start);
        counter.Children.Add(new TextBlock { Text = T("Digits", "Цифр"), Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        counter.Children.Add(_digits);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(70, 6, 0, 0), Children = { _regex, _matchCase } };
        _order.Items[2] = new ComboBoxItem { Content = T("By date", "По дате"), Tag = RenameOrder.Modified };

        var left = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Line(T("New name", "Имя"), _pattern, 0));
        tokens.Margin = new Thickness(70, 6, 0, 0);
        left.Children.Add(tokens);
        left.Children.Add(Line(T("Find", "Найти"), _find, 16));
        left.Children.Add(Line(T("Replace", "Заменить"), _replace));
        left.Children.Add(options);
        left.Children.Add(Line(T("Counter", "Счётчик"), counter, 16));
        left.Children.Add(Line(T("Order", "Порядок"), _order));
        left.Children.Add(Line(T("Letters", "Буквы"), _case));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows };
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        right.Children.Add(Heading(T("Preview", "Предпросмотр"), 0));
        var list = Card(scroll, new Thickness(0, 4, 0, 4));
        Grid.SetRow(list, 1);
        right.Children.Add(list);
        var root = new Grid { Margin = new Thickness(Gap, 8, Gap, 4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(left);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Body = root;

        _undo = AddAction(T("Undo last", "Откатить"), UndoAsync);
        _undo.IsEnabled = File.Exists(UndoFile);
        Recompute();
    }

    protected override string DoneTitle => T("Renamed", "Переименование");

    /// <summary>"pattern=photo_{n};digits=3;start=1;step=1;find=IMG;replace=;case=lower;regex;nocase".</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            var value = pair.Length > 1 ? pair[1] : "";
            switch (pair[0])
            {
                case "undo":
                    _testUndo = true;
                    break;
                case "pattern":
                    _pattern.Text = value;
                    break;
                case "find":
                    _find.Text = value;
                    break;
                case "replace":
                    _replace.Text = value;
                    break;
                case "start":
                    _start.Text = value;
                    break;
                case "step":
                    _step.Text = value;
                    break;
                case "digits":
                    _digits.Text = value;
                    break;
                case "regex":
                    _regex.IsChecked = true;
                    break;
                case "case":
                    _case.SelectedIndex = value switch { "lower" => 1, "upper" => 2, "title" => 3, "sentence" => 4, _ => 0 };
                    break;
                case "order":
                    _order.SelectedIndex = value switch { "name" => 1, "date" => 2, "size" => 3, _ => 0 };
                    break;
            }
        }
        Recompute();
    }

    private RenameOptions Options()
    {
        static int Number(TextBox box, int fallback) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        return new RenameOptions(
            _pattern.Text,
            _find.Text,
            _replace.Text,
            _regex.IsChecked == true,
            _matchCase.IsChecked == true,
            (CaseMode)((ComboBoxItem)_case.SelectedItem).Tag,
            Number(_start, 1),
            Number(_step, 1),
            Math.Clamp(Number(_digits, 1), 1, 12),
            (RenameOrder)((ComboBoxItem)_order.SelectedItem).Tag,
            _trim.IsChecked == true);
    }

    private void Recompute()
    {
        _plan = RenamePlanner.Plan(_paths, Options());
        _rows.Children.Clear();
        var shown = 0;
        foreach (var row in _plan)
        {
            if (shown++ >= 600)
            {
                _rows.Children.Add(new TextBlock { Text = T($"… and {_plan.Count - 600} more", $"… и ещё {_plan.Count - 600}"), Foreground = Muted, Margin = new Thickness(12, 6, 12, 6) });
                break;
            }
            var grid = new Grid { Margin = new Thickness(14, 5, 14, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var oldName = new TextBlock { Text = Path.GetFileName(row.OldPath.TrimEnd('\\')), Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.OldPath };
            var arrow = new TextBlock { Text = "  →  ", Foreground = Muted };
            var newName = new TextBlock
            {
                Text = Path.GetFileName(row.NewPath.TrimEnd('\\')),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = row.Changed ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = row.Problem is not null ? DangerBrush : row.Changed ? Foreground : Muted,
                ToolTip = row.Problem,
            };
            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(newName, 2);
            grid.Children.Add(oldName);
            grid.Children.Add(arrow);
            grid.Children.Add(newName);
            _rows.Children.Add(grid);
        }

        var changed = _plan.Count(r => r.Changed);
        var problems = _plan.FirstOrDefault(r => r.Problem is not null);
        if (problems is not null)
            ShowStatus(Path.GetFileName(problems.NewPath.TrimEnd('\\')) + ": " + problems.Problem, error: true);
        else
            SetHint(changed == 0
                ? T("Nothing changes yet", "Пока ничего не меняется")
                : T($"Will rename {changed} of {_plan.Count}.", $"Будет переименовано: {changed} из {_plan.Count}."));
    }

    // --- applying ---

    protected override Task<string> SaveAsync(ConvertContext context)
    {
        if (_testUndo)
            return UndoAsync(context);
        var plan = _plan.Where(r => r.Changed).ToList();
        if (plan.Count == 0)
            throw new ConvertException(T("Nothing to rename", "Нечего переименовывать"));
        if (_plan.FirstOrDefault(r => r.Problem is not null) is { } bad)
            throw new ConvertException(Path.GetFileName(bad.NewPath.TrimEnd('\\')) + ": " + bad.Problem);
        var moves = plan.Select(r => new Move(r.OldPath, r.NewPath)).ToList();
        return Task.Run(() =>
        {
            Apply(moves, context);
            SaveUndo(moves);
            DoneOverride = T($"Renamed {moves.Count} items", $"Переименовано: {moves.Count}");
            return moves[0].To;
        }, context.Cancel);
    }

    private Task<string> UndoAsync(ConvertContext context)
    {
        List<Move>? moves;
        try
        {
            moves = JsonSerializer.Deserialize<List<Move>>(File.ReadAllText(UndoFile));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            moves = null;
        }
        if (moves is null || moves.Count == 0)
            throw new ConvertException(T("Nothing to undo", "Нечего откатывать"));
        var back = moves.Where(m => (File.Exists(m.To) || Directory.Exists(m.To)) && !File.Exists(m.From) && !Directory.Exists(m.From)).Select(m => new Move(m.To, m.From)).ToList();
        if (back.Count == 0)
            throw new ConvertException(T("The files from the last rename are not there any more", "Файлов из прошлого переименования уже нет на месте"));
        return Task.Run(() =>
        {
            Apply(back, context);
            File.Delete(UndoFile);
            DoneOverride = T($"Restored {back.Count} names", $"Возвращено имён: {back.Count}");
            return back[0].To;
        }, context.Cancel);
    }

    /// <summary>Two phases (to a temporary name, then to the final one) so that swaps and case-only changes work; undone halfway on a failure.</summary>
    private static void Apply(List<Move> moves, ConvertContext context)
    {
        var temps = new List<(string Temp, Move Move)>();
        var done = new List<Move>();
        try
        {
            foreach (var move in moves)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var temp = Path.Combine(Path.GetDirectoryName(move.From.TrimEnd('\\')) ?? "", ".radii-rename-" + Guid.NewGuid().ToString("N"));
                Relocate(move.From, temp);
                temps.Add((temp, move));
            }
            for (var i = 0; i < temps.Count; i++)
            {
                Relocate(temps[i].Temp, temps[i].Move.To);
                done.Add(temps[i].Move);
                context.Report(T("Renaming…", "Переименовываю…"), (double)(i + 1) / temps.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            foreach (var (temp, move) in temps)
            {
                try
                {
                    if (done.Contains(move))
                        Relocate(move.To, move.From);
                    else if (File.Exists(temp) || Directory.Exists(temp))
                        Relocate(temp, move.From);
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                }
            }
            if (ex is OperationCanceledException)
                throw;
            throw new ConvertException(T("Could not rename (is a file open in another program?): ", "Не удалось переименовать (файл открыт в другой программе?): ") + ex.Message, ex);
        }
    }

    private static void Relocate(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    private static void SaveUndo(List<Move> moves)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(UndoFile, JsonSerializer.Serialize(moves));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
