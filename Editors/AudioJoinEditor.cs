using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Join audio files into one in the order you set. Same format on both sides is copied without re-encoding.</summary>
internal sealed class AudioJoinEditor : IEditor
{
    internal static readonly HashSet<string> Inputs = new(["mp3", "wav", "flac", "aac", "m4a", "ogg", "oga", "opus", "wma", "aiff", "aif"], StringComparer.OrdinalIgnoreCase);

    public string Caption => T("Join audio", "Склеить аудио");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count >= 2 && paths.All(p => Inputs.Contains(Path.GetExtension(p).TrimStart('.')));

    public EditorWindow Create(IReadOnlyList<string> paths) => new AudioJoinWindow(paths);
}

internal sealed class AudioJoinWindow : EditorWindow
{
    private sealed class AudioEntry(string path) : ListEntry(path)
    {
        public double Seconds { get; set; }
    }

    private readonly FileListPanel _list;
    private readonly ComboBox _format = new() { Width = 220 };
    private string? _ffmpeg;

    public AudioJoinWindow(IReadOnlyList<string> paths) : base(T("Join audio", "Склеить аудио"), 540, 300, T("Join", "Склеить"))
    {
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                Add(dropped);
        };
        _list = new FileListPanel(PanelBrush, LineBrush, Muted, AccentBrush, DangerBrush, thumbnails: false) { };
        _list.Changed += UpdateHint;

        var add = new Button { Content = T("Add files…", "Добавить файлы…") };
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = T("Audio|", "Аудио|") + string.Join(';', AudioJoinEditor.Inputs.Select(e => "*." + e)),
                Title = T("Add audio files", "Добавить аудио"),
            };
            if (dialog.ShowDialog(this) == true)
                Add(dialog.FileNames);
        };
        var toolbar = FileListPanel.Toolbar(add, T("Drag the ⋮⋮ handle to reorder. You can also drop files here.", "Тяни за ⋮⋮, чтобы поменять порядок. Файлы можно бросать сюда."), Muted);

        foreach (var (id, label) in new[] { ("", T("Same as the first file", "Как у первого файла")), ("mp3", "MP3"), ("m4a", "AAC (m4a)"), ("wav", "WAV"), ("flac", "FLAC"), ("ogg", "OGG"), ("opus", "OPUS") })
            _format.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        _format.SelectedIndex = 0;

        MinHeight = 0;
        FitHeight(520);
        _format.Width = 190;
        Body = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                toolbar,
                _list,
                new Border { Margin = new Thickness(0, 10, 0, 0), Child = Card(Row(T("Format", "Формат"), _format), new Thickness(12, 8, 12, 8)) },
            },
        };

        var initial = paths.ToList();
        Loaded += (_, _) => _ = RunAsync(async context =>
        {
            context.Report(T("Preparing FFmpeg…", "Готовлю FFmpeg…"), null);
            _ffmpeg = await Engines.EnsureAsync(Engine.FFmpeg, context);
            ShowStatus("", error: false);
            Add(initial);
        });
    }

    protected override string DoneTitle => T("Audio joined", "Аудио склеено");

    protected override bool SnapshotReady => _ffmpeg is not null && _list.Entries.All(e => e.Ready);

    private void Add(IEnumerable<string> paths)
    {
        if (_ffmpeg is null)
            return;
        foreach (var path in paths.Where(p => AudioJoinEditor.Inputs.Contains(Path.GetExtension(p).TrimStart('.')) && File.Exists(p)))
        {
            var entry = new AudioEntry(path);
            _list.Entries.Add(entry);
            _ = ProbeAsync(entry);
        }
        _list.Refresh();
    }

    private async Task ProbeAsync(AudioEntry entry)
    {
        try
        {
            var (seconds, hasAudio, _) = await FfmpegTools.ProbeAsync(_ffmpeg!, entry.Path, CancellationToken.None);
            if (!hasAudio)
                entry.Error = T("No sound found in this file", "В файле не найден звук");
            else
            {
                entry.Seconds = seconds ?? 0;
                entry.Detail = seconds is { } s ? FfmpegTools.Clock(s) : "";
            }
        }
        catch (ConvertException ex)
        {
            entry.Error = ex.Message;
        }
        await Dispatcher.InvokeAsync(() =>
        {
            entry.Ready = true;
            _list.Refresh();
        });
    }

    private void UpdateHint()
    {
        var entries = _list.Entries.OfType<AudioEntry>().ToList();
        var total = entries.Sum(e => e.Seconds);
        SetHint(entries.Count < 2
            ? T("Add at least one more file.", "Добавь ещё хотя бы один файл.")
            : T($"Total {FfmpegTools.Clock(total)} · {entries.Count} files", $"Итого {FfmpegTools.Clock(total)} · файлов: {entries.Count}"));
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var entries = _list.Entries.OfType<AudioEntry>().ToList();
        if (_ffmpeg is null)
            throw new ConvertException(T("FFmpeg is not ready yet", "FFmpeg ещё не готов"));
        if (entries.Count < 2)
            throw new ConvertException(T("Add at least two files", "Нужно хотя бы два файла"));
        if (entries.Any(e => !e.Ready))
            throw new ConvertException(T("Still reading the files", "Файлы ещё читаются"));
        if (entries.FirstOrDefault(e => e.Error is not null) is { } broken)
            throw new ConvertException(Path.GetFileName(broken.Path) + ": " + broken.Error);

        var first = entries[0].Path;
        var chosen = (_format.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var ext = chosen.Length > 0 ? chosen : Path.GetExtension(first).TrimStart('.').ToLowerInvariant();
        var output = ConvertContext.NewPath(first, ext, T(" (joined)", " (склеено)"));
        var total = Math.Max(1, entries.Sum(e => e.Seconds));

        var attempts = new List<(string Name, Func<string[]> Args, string? ListFile)>();
        if (entries.All(e => string.Equals(Path.GetExtension(e.Path).TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase)))
        {
            var listFile = Path.Combine(Path.GetTempPath(), $"radii-join-{Guid.NewGuid():N}.txt");
            var text = new StringBuilder();
            foreach (var entry in entries)
                text.Append("file '").Append(entry.Path.Replace("'", "'\\''")).AppendLine("'");
            File.WriteAllText(listFile, text.ToString(), new UTF8Encoding(false));
            attempts.Add(("copy", () => ["-y", "-hide_banner", "-nostdin", "-f", "concat", "-safe", "0", "-i", listFile, "-vn", "-c", "copy", output], listFile));
        }
        var codec = AudioCodecs.For(ext);
        if (codec is not null)
        {
            attempts.Add(("encode", () =>
            {
                var args = new List<string> { "-y", "-hide_banner", "-nostdin" };
                foreach (var entry in entries)
                {
                    args.Add("-i");
                    args.Add(entry.Path);
                }
                args.Add("-filter_complex");
                args.Add(string.Concat(entries.Select((_, i) => $"[{i}:a:0]")) + $"concat=n={entries.Count}:v=0:a=1[a]");
                args.AddRange(["-map", "[a]", .. codec, output]);
                return [.. args];
            }, null));
        }
        if (attempts.Count == 0)
            throw new ConvertException(T("This format cannot be written here; pick another result format", "В этот формат здесь писать нельзя; выбери другой формат результата"));

        var failure = "";
        try
        {
            foreach (var (_, args, _) in attempts)
            {
                context.Report(T("Joining…", "Склеиваю…"), 0);
                var (code, errors) = await ProcessRunner.RunAsync(_ffmpeg, args(), context.Cancel, line =>
                {
                    if (FfmpegTools.TryTime(line) is { } done)
                        context.Progress(done / total);
                });
                if (code == 0 && File.Exists(output))
                    return output;
                failure = ProcessRunner.Reason(errors);
                if (File.Exists(output))
                    File.Delete(output);
            }
        }
        finally
        {
            foreach (var (_, _, listFile) in attempts)
            {
                if (listFile is not null && File.Exists(listFile))
                    File.Delete(listFile);
            }
        }
        throw new ConvertException(T("FFmpeg failed: ", "FFmpeg не справился: ") + failure);
    }
}
