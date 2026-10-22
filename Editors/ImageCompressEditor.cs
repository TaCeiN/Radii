using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Shrink photos to a target file size (for uploads and mail): the quality is searched first, the resolution only gives way when it has to.</summary>
internal sealed class ImageCompressEditor : IEditor
{
    public string Caption => T("Compress photo", "Сжать фото");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count >= 1 && paths.All(p => ImageCropEditor.Inputs.Contains(Path.GetExtension(p).TrimStart('.')));

    public EditorWindow Create(IReadOnlyList<string> paths) => new CompressWindow(paths);
}

internal static class JpegFit
{
    private const int MinQuality = 35;
    private const int GoodQuality = 55;

    /// <summary>The JPEG of <paramref name="flat"/> not larger than <paramref name="target"/> bytes: the highest quality that fits, smaller pixels only when even quality 55 does not.</summary>
    public static (byte[] Bytes, int Quality, int Width, int Height) Fit(BitmapSource flat, long target, CancellationToken cancel)
    {
        var scale = 1.0;
        var current = flat;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var top = ImageTools.Jpeg(current, 95);
            if (top.LongLength <= target)
                return (top, 95, current.PixelWidth, current.PixelHeight);

            int low = MinQuality, high = 94, bestQuality = -1;
            byte[]? best = null;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var bytes = ImageTools.Jpeg(current, middle);
                if (bytes.LongLength <= target)
                {
                    best = bytes;
                    bestQuality = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            if (best is not null && bestQuality >= GoodQuality)
                return (best, bestQuality, current.PixelWidth, current.PixelHeight);
            if (scale < 0.15)
                return best is not null ? (best, bestQuality, current.PixelWidth, current.PixelHeight) : (ImageTools.Jpeg(current, MinQuality), MinQuality, current.PixelWidth, current.PixelHeight);

            scale *= 0.85;
            current = ImageTools.OnWhite(ImageTools.Scaled(flat, Math.Max(1, (int)(flat.PixelWidth * scale)), Math.Max(1, (int)(flat.PixelHeight * scale))));
        }
    }
}

internal sealed class CompressWindow : EditorWindow
{
    private sealed class PhotoEntry(string path) : ListEntry(path);

    private readonly FileListPanel _list;
    private readonly TextBox _target = new() { Text = "500", Width = 84, TextAlignment = TextAlignment.Center };

    public CompressWindow(IReadOnlyList<string> paths) : base(T("Compress photo", "Сжать фото"), 540, 300, T("Compress", "Сжать"))
    {
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                Add(dropped);
        };
        _list = new FileListPanel(PanelBrush, LineBrush, Muted, AccentBrush, DangerBrush, thumbnails: false) { };
        _list.Changed += UpdateHint;

        var add = new Button { Content = T("Add photos…", "Добавить фото…") };
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = T("Pictures|", "Картинки|") + string.Join(';', ImageCropEditor.Inputs.Select(e => "*." + e)),
            };
            if (dialog.ShowDialog(this) == true)
                Add(dialog.FileNames);
        };
        var toolbar = FileListPanel.Toolbar(add, T("Drop photos here too.", "Фото можно бросать сюда."), Muted);
        if (paths.Count < 2)
            toolbar.Visibility = Visibility.Collapsed;

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, kb) in new[] { ("200 KB", 200), ("500 KB", 500), ("1 MB", 1000), ("2 MB", 2000) })
        {
            var chip = Chip(label, () => _target.Text = kb.ToString(CultureInfo.InvariantCulture));
            chip.Margin = new Thickness(0, 0, 4, 0);
            chip.Padding = new Thickness(8, 3, 8, 3);
            chips.Children.Add(chip);
        }
        _target.Width = 64;
        chips.Children.Add(_target);
        chips.Children.Add(new TextBlock { Text = "KB", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });

        MinHeight = 0;
        FitHeight(520);
        Body = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                toolbar,
                _list,
                new Border { Margin = new Thickness(0, 10, 0, 0), Child = Card(Row(T("Size of each", "Размер каждого"), chips), new Thickness(12, 8, 12, 8)) },
            },
        };

        Add(paths);
    }

    protected override string DoneTitle => T("Photos compressed", "Фото сжаты");

    protected override bool SnapshotReady => _list.Entries.All(e => e.Ready);

    /// <summary>"kb=300".</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            if (pair.Length == 2 && pair[0] == "kb")
                _target.Text = pair[1];
        }
    }

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => ImageCropEditor.Inputs.Contains(Path.GetExtension(p).TrimStart('.')) && File.Exists(p)))
        {
            var entry = new PhotoEntry(path);
            _list.Entries.Add(entry);
            _ = Task.Run(() =>
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    entry.Detail = $"{ImageTools.Size(new FileInfo(path).Length)} · {frame.PixelWidth} × {frame.PixelHeight}";
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
                {
                    entry.Error = T("Could not read the picture", "Не удалось прочитать картинку");
                }
                Dispatcher.Invoke(() =>
                {
                    entry.Ready = true;
                    _list.Refresh();
                });
            });
        }
        _list.Refresh();
    }

    private void UpdateHint() =>
        SetHint(T($"Photos: {_list.Entries.Count}", $"Фото: {_list.Entries.Count}"));

    private long TargetBytes => long.TryParse(_target.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) && kb >= 10 ? kb * 1000 : 0;

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var target = TargetBytes;
        if (target == 0)
            throw new ConvertException(T("Enter the target size in KB (10 or more)", "Укажи целевой размер в КБ (от 10)"));
        var entries = _list.Entries;
        if (entries.Count == 0)
            throw new ConvertException(T("Add photos first", "Сначала добавь фото"));
        if (entries.FirstOrDefault(e => e.Error is not null) is { } broken)
            throw new ConvertException(Path.GetFileName(broken.Path) + ": " + broken.Error);
        var paths = entries.Select(e => e.Path).ToList();
        var cancel = context.Cancel;

        var (output, before, after, written, skipped) = await Sta.Run(() =>
        {
            string? last = null;
            long sumBefore = 0, sumAfter = 0;
            int count = 0, skip = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var path = paths[i];
                var original = new FileInfo(path).Length;
                context.StartFile(i, paths.Count, Path.GetFileName(path));
                if (original <= target && Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    skip++; // already small enough
                    continue;
                }
                var flat = ImageTools.OnWhite(ImageTools.Oriented(Wic.Load(path)));
                var (bytes, _, _, _) = JpegFit.Fit(flat, target, cancel);
                var file = ConvertContext.NewPath(path, "jpg", T(" (compressed)", " (сжато)"));
                File.WriteAllBytes(file, bytes);
                context.Add(file);
                last = file;
                sumBefore += original;
                sumAfter += bytes.LongLength;
                count++;
            }
            return (last, sumBefore, sumAfter, count, skip);
        });

        if (output is null)
            throw new ConvertException(T("Every photo is already below the target", "Все фото уже меньше цели"));
        DoneOverride = T($"{written} compressed: {ImageTools.Size(before)} → {ImageTools.Size(after)}", $"Сжато: {written}, {ImageTools.Size(before)} → {ImageTools.Size(after)}")
            + (skipped > 0 ? T($" ({skipped} already small)", $" (уже малы: {skipped})") : "");
        return output;
    }
}
