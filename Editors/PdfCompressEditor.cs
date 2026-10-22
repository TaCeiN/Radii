using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Shrink PDFs by recompressing the pictures inside (and packing the rest). Text and vector drawings are not touched, so they stay sharp and selectable.</summary>
internal sealed class PdfCompressEditor : IEditor
{
    public string Caption => T("Compress PDF", "Сжать PDF");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count >= 1 && paths.All(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    public EditorWindow Create(IReadOnlyList<string> paths) => new PdfCompressWindow(paths);
}

/// <summary>The picture recompression itself, kept apart from the window so it can be run and tested on its own.</summary>
internal static class PdfShrinker
{
    /// <returns>Number of pictures that were made smaller.</returns>
    public static int Shrink(PdfDocument document, int quality, int maxSide, CancellationToken cancel)
    {
        var seen = new HashSet<int>();
        var changed = 0;
        foreach (var page in document.Pages)
        {
            cancel.ThrowIfCancellationRequested();
            changed += Walk(page.Elements.GetDictionary("/Resources"), seen, quality, maxSide, cancel, 0);
        }
        return changed;
    }

    private static int Walk(PdfDictionary? resources, HashSet<int> seen, int quality, int maxSide, CancellationToken cancel, int depth)
    {
        if (resources is null || depth > 4)
            return 0;
        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null)
            return 0;
        var changed = 0;
        foreach (var item in xobjects.Elements.Values)
        {
            cancel.ThrowIfCancellationRequested();
            if (item is not PdfReference reference || reference.Value is not PdfDictionary xobject)
                continue;
            if (!seen.Add(reference.ObjectNumber))
                continue;
            var subtype = xobject.Elements.GetName("/Subtype");
            if (subtype == "/Image")
            {
                if (TryRecompress(xobject, quality, maxSide))
                    changed++;
            }
            else if (subtype == "/Form")
            {
                changed += Walk(xobject.Elements.GetDictionary("/Resources"), seen, quality, maxSide, cancel, depth + 1);
            }
        }
        return changed;
    }

    private static bool TryRecompress(PdfDictionary image, int quality, int maxSide)
    {
        try
        {
            if (image.Stream is not { } stream || image.Elements.ContainsKey("/Mask") || image.Elements.ContainsKey("/Decode") || image.Elements.GetBoolean("/ImageMask"))
                return false;
            var width = image.Elements.GetInteger("/Width");
            var height = image.Elements.GetInteger("/Height");
            if (width < 16 || height < 16 || image.Elements.GetInteger("/BitsPerComponent") != 8)
                return false;
            var kind = Components(image.Elements["/ColorSpace"]);
            if (kind is not (1 or 3))
                return false;
            var components = kind.Value;

            var filter = image.Elements["/Filter"];
            var filterName = filter switch
            {
                PdfName name => name.Value,
                PdfArray { Elements.Count: 1 } array when array.Elements[0] is PdfName only => only.Value,
                _ => null,
            };
            var original = stream.Value.Length;
            BitmapSource source;
            if (filterName == "/DCTDecode")
            {
                var decoder = BitmapDecoder.Create(new MemoryStream(stream.Value), BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }
            else if (filterName == "/FlateDecode")
            {
                if (!stream.TryUnfilter())
                    return false;
                var raw = stream.Value;
                if (raw.Length != width * height * components)
                    return false; // a predictor or a different layout: leave it alone
                source = BitmapSource.Create(width, height, 96, 96, components == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, raw, width * components);
            }
            else
            {
                return false;
            }

            var longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest > maxSide)
            {
                var scale = (double)maxSide / longest;
                var scaled = ImageTools.Scaled(source, Math.Max(1, (int)(source.PixelWidth * scale)), Math.Max(1, (int)(source.PixelHeight * scale)));
                source = scaled;
            }
            BitmapSource target = components == 1
                ? new FormatConvertedBitmap(ImageTools.OnWhite(source), PixelFormats.Gray8, null, 0)
                : ImageTools.OnWhite(source);
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var output = new MemoryStream();
            encoder.Save(output);
            var bytes = output.ToArray();
            if (bytes.Length >= original * 0.92)
            {
                if (filterName == "/FlateDecode")
                    return false; // the stream was unfiltered above; the document still writes it correctly
                return false;
            }

            stream.Value = bytes;
            image.Elements.SetName("/Filter", "/DCTDecode");
            image.Elements.Remove("/DecodeParms");
            image.Elements.SetInteger("/Width", target.PixelWidth);
            image.Elements.SetInteger("/Height", target.PixelHeight);
            image.Elements.SetInteger("/BitsPerComponent", 8);
            // Anything fancier than a plain device space (ICC, Cal*) is replaced: the JPEG carries device values.
            if (image.Elements["/ColorSpace"] is not PdfName)
                image.Elements.SetName("/ColorSpace", components == 3 ? "/DeviceRGB" : "/DeviceGray");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or PdfSharp.PdfSharpException or System.Runtime.InteropServices.COMException)
        {
            return false; // a picture we cannot read stays as it was
        }
    }

    private static int? Components(PdfItem? colorSpace)
    {
        switch (colorSpace)
        {
            case PdfName name:
                return name.Value switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => null };
            case PdfReference { Value: { } value }:
                return Components(value);
            case PdfArray array when array.Elements.Count > 0 && array.Elements[0] is PdfName kind:
                switch (kind.Value)
                {
                    case "/ICCBased" when array.Elements.Count > 1:
                        var profile = array.Elements[1] is PdfReference reference ? reference.Value as PdfDictionary : array.Elements[1] as PdfDictionary;
                        var profileN = profile?.Elements.GetInteger("/N") ?? 0;
                        return profileN is 1 or 3 ? profileN : null;
                    case "/CalRGB":
                        return 3;
                    case "/CalGray":
                        return 1;
                }
                return null;
        }
        return null;
    }
}

internal sealed class PdfCompressWindow : EditorWindow
{
    private sealed class PdfFile(string path) : ListEntry(path);

    private readonly FileListPanel _list;
    private readonly ComboBox _level = new() { Width = 330 };

    public PdfCompressWindow(IReadOnlyList<string> paths) : base(T("Compress PDF", "Сжать PDF"), 540, 280, T("Compress", "Сжать"))
    {
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                Add(dropped);
        };
        _list = new FileListPanel(PanelBrush, LineBrush, Muted, AccentBrush, DangerBrush, thumbnails: false) { };
        _list.Changed += () => SetHint(T($"Files: {_list.Entries.Count}", $"Файлов: {_list.Entries.Count}"));

        var add = new Button { Content = T("Add files…", "Добавить файлы…") };
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "PDF|*.pdf" };
            if (dialog.ShowDialog(this) == true)
                Add(dialog.FileNames);
        };
        var toolbar = FileListPanel.Toolbar(add, T("Drop PDFs here too.", "PDF можно бросать сюда."), Muted);
        if (paths.Count < 2)
            toolbar.Visibility = Visibility.Collapsed;

        foreach (var (quality, side, text) in new[]
        {
            (85, 2600, T("Light", "Лёгкое")),
            (70, 1800, T("Medium (screen, mail)", "Среднее (экран, почта)")),
            (50, 1200, T("Strong (smallest)", "Сильное (минимум)")),
        })
            _level.Items.Add(new ComboBoxItem { Content = text, Tag = (quality, side) });
        _level.SelectedIndex = 1;
        _level.Width = 240;

        MinHeight = 0;
        FitHeight(520);
        Body = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                toolbar,
                _list,
                new Border { Margin = new Thickness(0, 10, 0, 0), Child = Card(Row(T("Level", "Уровень"), _level), new Thickness(12, 8, 12, 8)) },
            },
        };
        Add(paths);
    }

    protected override string DoneTitle => T("PDF compressed", "PDF сжат");

    protected override bool SnapshotReady => _list.Entries.All(e => e.Ready);

    /// <summary>"0", "1" or "2" (light, medium, strong).</summary>
    protected override void ConfigureForTest(string spec)
    {
        if (int.TryParse(spec, out var index) && index is >= 0 and <= 2)
            _level.SelectedIndex = index;
    }

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
        {
            var entry = new PdfFile(path) { Detail = ImageTools.Size(new FileInfo(path).Length), Ready = true };
            _list.Entries.Add(entry);
        }
        _list.Refresh();
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var paths = _list.Entries.Select(e => e.Path).ToList();
        if (paths.Count == 0)
            throw new ConvertException(T("Add PDFs first", "Сначала добавь PDF"));
        var (quality, side) = ((int, int))((ComboBoxItem)_level.SelectedItem).Tag;
        var cancel = context.Cancel;

        var (last, before, after, written, skipped) = await Task.Run(() =>
        {
            string? lastOutput = null;
            long sumBefore = 0, sumAfter = 0;
            int count = 0, skip = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var path = paths[i];
                context.StartFile(i, paths.Count, Path.GetFileName(path));
                var original = new FileInfo(path).Length;
                var output = ConvertContext.NewPath(path, "pdf", T(" (compressed)", " (сжато)"));
                try
                {
                    // Modify mode is the one that keeps the pictures' objects reachable for editing.
                    using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                    document.Options.CompressContentStreams = true;
                    document.Options.NoCompression = false;
                    PdfShrinker.Shrink(document, quality, side, cancel);
                    document.Save(output);
                }
                catch (Exception ex) when (ex is PdfSharp.PdfSharpException or InvalidOperationException or NotSupportedException or IOException)
                {
                    if (File.Exists(output))
                        File.Delete(output);
                    throw new ConvertException(Path.GetFileName(path) + ": " + T("could not be read or written (protected or damaged?) ", "не удалось прочитать или записать (защищён или повреждён?) ") + ex.Message, ex);
                }
                var size = new FileInfo(output).Length;
                if (size >= original * 0.97)
                {
                    File.Delete(output); // nothing gained: do not leave a copy that is no smaller
                    skip++;
                    continue;
                }
                context.Add(output);
                lastOutput = output;
                sumBefore += original;
                sumAfter += size;
                count++;
            }
            return (lastOutput, sumBefore, sumAfter, count, skip);
        }, cancel);

        if (last is null)
            throw new ConvertException(T("These PDFs are already compact: no copy made", "Эти PDF уже компактные: копия не создана"));
        DoneOverride = T($"{written} compressed: {ImageTools.Size(before)} → {ImageTools.Size(after)}", $"Сжато: {written}, {ImageTools.Size(before)} → {ImageTools.Size(after)}")
            + (skipped > 0 ? T($" ({skipped} already compact)", $" (уже компактны: {skipped})") : "");
        return last;
    }
}
