using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Removes camera, date, location and other tags from photos, audio and video. JPEG and PNG are cut losslessly in place of the bytes; other formats go through FFmpeg stream copy.</summary>
internal sealed class MetadataEditor : IEditor
{
    internal static readonly HashSet<string> Images = new(["jpg", "jpeg", "png", "tif", "tiff", "webp", "bmp", "gif"], StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> Media = new(["mp3", "wav", "flac", "aac", "m4a", "ogg", "oga", "opus", "wma", "aiff", "aif", "mp4", "mkv", "avi", "mov", "webm", "wmv", "m4v", "mpg", "mpeg", "ts"], StringComparer.OrdinalIgnoreCase);

    public string Caption => T("Remove metadata", "Убрать метаданные");

    public string Glyph => "";

    public static bool Known(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return Images.Contains(ext) || Media.Contains(ext);
    }

    public bool Accepts(IReadOnlyList<string> paths) => paths.Count >= 1 && paths.All(p => File.Exists(p) && Known(p));

    public EditorWindow Create(IReadOnlyList<string> paths) => new MetadataWindow(paths);
}

/// <summary>Byte-level metadata cutting that leaves the picture data alone.</summary>
internal static class MetaStrip
{
    /// <summary>JPEG without EXIF (apart from a bare orientation tag so the photo does not turn sideways), XMP, IPTC and comments; the ICC profile stays.</summary>
    public static byte[]? Jpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;
        var orientation = ReadOrientation(data);
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        var inserted = orientation <= 1;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF)
                break;
            var marker = data[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }
            if (marker is 0xDA or 0xD9)
                break;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01 or 0x00)
            {
                output.Write(data, pos, 2);
                pos += 2;
                continue;
            }
            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (length < 2 || pos + 2 + length > data.Length)
                return null;
            var drop = marker is 0xE1 or >= 0xE3 and <= 0xED or 0xEF or 0xFE;
            if (!drop)
            {
                if (!inserted && marker != 0xE0)
                {
                    WriteOrientation(output, orientation);
                    inserted = true;
                }
                output.Write(data, pos, 2 + length);
            }
            pos += 2 + length;
        }
        if (!inserted)
            WriteOrientation(output, orientation);
        output.Write(data, pos, data.Length - pos);
        return output.ToArray();
    }

    private static void WriteOrientation(Stream output, int orientation)
    {
        // APP1 "Exif" with a single IFD entry: tag 0x0112 (Orientation), big endian.
        byte[] segment =
        [
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0,
            0, 0, 0, 0,
        ];
        output.Write(segment, 0, segment.Length);
    }

    /// <summary>EXIF orientation (1..8) or 1 when there is none.</summary>
    private static int ReadOrientation(byte[] data)
    {
        var pos = 2;
        while (pos + 4 <= data.Length && data[pos] == 0xFF)
        {
            var marker = data[pos + 1];
            if (marker is 0xDA or 0xD9)
                break;
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }
            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (marker == 0xE1 && length >= 16 && pos + 10 < data.Length && data[pos + 4] == 'E' && data[pos + 5] == 'x' && data[pos + 6] == 'i' && data[pos + 7] == 'f')
            {
                var tiff = pos + 10;
                var little = data[tiff] == 'I';
                int U16(int at) => little ? data[at] | (data[at + 1] << 8) : (data[at] << 8) | data[at + 1];
                int U32(int at) => little ? data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24) : (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
                try
                {
                    var ifd = tiff + U32(tiff + 4);
                    var count = U16(ifd);
                    for (var i = 0; i < count; i++)
                    {
                        var entry = ifd + 2 + i * 12;
                        if (U16(entry) == 0x0112)
                            return Math.Clamp(U16(entry + 8), 1, 8);
                    }
                }
                catch (IndexOutOfRangeException)
                {
                }
                return 1;
            }
            pos += 2 + length;
        }
        return 1;
    }

    /// <summary>PNG without text, EXIF and time chunks.</summary>
    public static byte[]? Png(byte[] data)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 12 || !data.AsSpan(0, 8).SequenceEqual(signature))
            return null;
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            if (length < 0 || pos + 12 + length > data.Length)
                return null;
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (type is not ("tEXt" or "zTXt" or "iTXt" or "eXIf" or "tIME"))
                output.Write(data, pos, 12 + length);
            pos += 12 + length;
        }
        return output.ToArray();
    }
}

internal sealed class MetadataWindow : EditorWindow
{
    private sealed class FileEntry(string path) : ListEntry(path);

    private readonly FileListPanel _list;
    private string? _ffmpeg;

    public MetadataWindow(IReadOnlyList<string> paths) : base(T("Remove metadata", "Убрать метаданные"), 540, 220, T("Remove metadata", "Убрать метаданные"))
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
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = T("Photos, audio, video|", "Фото, аудио, видео|") + string.Join(';', MetadataEditor.Images.Concat(MetadataEditor.Media).Select(e => "*." + e)) };
            if (dialog.ShowDialog(this) == true)
                Add(dialog.FileNames);
        };
        var toolbar = FileListPanel.Toolbar(add, T("Drop photos, audio or video here too.", "Фото, аудио и видео можно бросать сюда."), Muted);
        if (paths.Count < 2)
            toolbar.Visibility = Visibility.Collapsed;

        MinHeight = 0;
        FitHeight(520);
        Body = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                toolbar,
                _list,
            },
        };
        Add(paths);
    }

    protected override string DoneTitle => T("Metadata removed", "Метаданные убраны");

    protected override bool SnapshotReady => _list.Entries.All(e => e.Ready);

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => File.Exists(p) && MetadataEditor.Known(p)))
        {
            var entry = new FileEntry(path);
            _list.Entries.Add(entry);
            _ = Task.Run(() =>
            {
                entry.Detail = Describe(path);
                Dispatcher.Invoke(() =>
                {
                    entry.Ready = true;
                    _list.Refresh();
                });
            });
        }
        _list.Refresh();
    }

    /// <summary>What a photo gives away ("Canon EOS R5 · 2024-03-12 · GPS"), read the way Explorer does.</summary>
    private static string Describe(string path)
    {
        var size = ImageTools.Size(new FileInfo(path).Length);
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!MetadataEditor.Images.Contains(ext))
            return size;
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.Metadata is not BitmapMetadata metadata)
                return size;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(metadata.CameraModel))
                parts.Add(metadata.CameraModel.Trim());
            if (!string.IsNullOrWhiteSpace(metadata.DateTaken))
                parts.Add(metadata.DateTaken);
            if (metadata.GetQuery("/app1/ifd/gps/{ushort=2}") is not null)
                parts.Add(T("GPS location", "GPS-координаты"));
            return size + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : T(" · no tags found", " · меток не найдено"));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return size;
        }
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var paths = _list.Entries.Select(e => e.Path).ToList();
        if (paths.Count == 0)
            throw new ConvertException(T("Add files first", "Сначала добавь файлы"));
        string? last = null;
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var path = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(path));
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var output = ConvertContext.NewPath(path, ext, T(" (clean)", " (без метаданных)"));
            byte[]? cut = null;
            if (ext is "jpg" or "jpeg" or "png")
            {
                var data = await File.ReadAllBytesAsync(path, context.Cancel);
                cut = ext == "png" ? MetaStrip.Png(data) : MetaStrip.Jpeg(data);
                if (cut is not null)
                    await File.WriteAllBytesAsync(output, cut, context.Cancel);
            }
            if (cut is null)
            {
                _ffmpeg ??= await Engines.EnsureAsync(Engine.FFmpeg, context);
                var (code, errors) = await ProcessRunner.RunAsync(_ffmpeg, ["-y", "-hide_banner", "-nostdin", "-i", path, "-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-c", "copy", output], context.Cancel);
                if (code != 0 || !File.Exists(output))
                {
                    if (File.Exists(output))
                        File.Delete(output);
                    throw new ConvertException(Path.GetFileName(path) + ": " + T("FFmpeg failed: ", "FFmpeg не справился: ") + ProcessRunner.Reason(errors));
                }
            }
            context.Add(output);
            last = output;
        }
        DoneOverride = T($"Cleaned {paths.Count} files", $"Очищено файлов: {paths.Count}");
        return last!;
    }
}
