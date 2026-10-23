using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Volume, loudness normalizing, speed (pitch kept) and silence removal for one or several audio files.</summary>
internal sealed class AudioTuneEditor : IEditor
{
    public string Caption => T("Sound: volume, speed", "Звук: громкость, скорость");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count >= 1 && paths.All(p => AudioJoinEditor.Inputs.Contains(Path.GetExtension(p).TrimStart('.')));

    public EditorWindow Create(IReadOnlyList<string> paths) => new AudioTuneWindow(paths);
}

internal sealed class AudioTuneWindow : EditorWindow
{
    private sealed class TrackEntry(string path) : ListEntry(path)
    {
        public double Seconds { get; set; }
    }

    private readonly FileListPanel _list;
    private readonly Slider _gain = new() { Minimum = -20, Maximum = 20, Value = 0, TickFrequency = 0.5, IsSnapToTickEnabled = true };
    private readonly TextBlock _gainText = new() { Text = "+0 dB" };
    // Not shown: the speed is picked with the preset chips; the slider only holds the value (tests set any speed).
    private readonly Slider _speed = new() { Minimum = 0.25, Maximum = 4, Value = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private readonly List<(Button Chip, double Value)> _speedChips = [];
    private readonly CheckBox _normalize = new() { Content = T("Even out the loudness", "Выровнять громкость") };
    private readonly CheckBox _silence = new() { Content = T("Remove silence", "Убрать тишину") };
    // Not shown: silence threshold (-50 dB) and shortest pause (0.5 s) keep their defaults.
    private readonly ComboBox _threshold = new();
    private readonly TextBox _silenceLength = new() { Text = "0.5" };
    private string? _ffmpeg;
    private readonly System.Windows.Media.MediaPlayer _player = new();
    private readonly Button _playButton = new() { MinWidth = 120, Height = 32 };
    private readonly WaveformView _wave = new() { Height = 78, ReadOnly = true, Visibility = Visibility.Collapsed };
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Windows.Threading.DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string? _previewFile;
    private bool _playing;
    private bool _waveLoaded;
    private int _version;
    private double _from;
    private double _renderSpeed = 1;

    private static string ListenText => T("▶  Listen", "▶  Прослушать");

    private static string StopText => T("■  Stop", "■  Стоп");

    /// <summary>Called when any setting changed: the wave follows the volume at once, a running preview is rendered again from the same spot.</summary>
    private void Retune()
    {
        _wave.Gain = Math.Pow(10, _gain.Value / 20);
        if (_playing)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <summary>Plays the first file with the current settings from <paramref name="from"/> seconds (30 s are rendered); a settings change or a click on the wave starts again.</summary>
    private async Task StartPreviewAsync(double from)
    {
        if (_ffmpeg is null || _list.Entries.OfType<TrackEntry>().FirstOrDefault(e => e.Ready && e.Error is null) is not { } entry)
            return;
        var version = ++_version;
        if (!_playing)
        {
            _playButton.IsEnabled = false;
            _playButton.Content = "…";
        }
        var filter = Filter();
        var speed = _speed.Value;
        var file = Path.Combine(Path.GetTempPath(), "radii-listen-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            string[] filterArgs = filter.Length > 0 ? ["-af", filter] : [];
            var (code, _) = await ProcessRunner.RunAsync(_ffmpeg, ["-y", "-hide_banner", "-nostdin", "-ss", from.ToString("0.###", CultureInfo.InvariantCulture), "-t", "30", "-i", entry.Path, "-vn", "-map", "0:a:0", .. filterArgs, "-ar", "44100", "-ac", "2", file], CancellationToken.None, _ => { });
            if (version != _version || code != 0 || !File.Exists(file))
            {
                DeleteQuietly(file);
                if (version == _version)
                    StopPreview();
                return;
            }
            var old = _previewFile;
            _player.Close();
            DeleteQuietly(old);
            _previewFile = file;
            _from = from;
            _renderSpeed = speed;
            _player.Open(new Uri(file));
            _player.Play();
            _playing = true;
            _tick.Start();
            _playButton.Content = StopText;
        }
        catch (Exception ex) when (ex is IOException or ConvertException or InvalidOperationException)
        {
            DeleteQuietly(file);
            if (version == _version)
                StopPreview();
        }
        finally
        {
            _playButton.IsEnabled = true;
        }
    }

    private void StopPreview()
    {
        _version++;
        _playing = false;
        _tick.Stop();
        _debounce.Stop();
        _player.Stop();
        _player.Close();
        _playButton.Content = ListenText;
        DeleteQuietly(_previewFile);
        _previewFile = null;
    }

    private static void DeleteQuietly(string? file)
    {
        if (file is null)
            return;
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The wave of the first readable file, at its true level (the volume slider scales it).</summary>
    private async Task LoadWaveAsync(TrackEntry entry)
    {
        if (_waveLoaded || _ffmpeg is null)
            return;
        _waveLoaded = true;
        try
        {
            var (peaks, duration) = await Task.Run(() => TrimWindow.DecodeAsync(_ffmpeg, entry.Path, CancellationToken.None, normalize: false));
            _wave.SetData(peaks, entry.Seconds > 0 ? entry.Seconds : duration);
            _wave.Gain = Math.Pow(10, _gain.Value / 20);
            _wave.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is ConvertException or IOException or InvalidOperationException)
        {
            _waveLoaded = false;
        }
    }

    public AudioTuneWindow(IReadOnlyList<string> paths) : base(T("Sound: volume, speed", "Звук: громкость, скорость"), 540, 320, T("Process", "Обработать"))
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
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = T("Audio|", "Аудио|") + string.Join(';', AudioJoinEditor.Inputs.Select(e => "*." + e)) };
            if (dialog.ShowDialog(this) == true)
                Add(dialog.FileNames);
        };
        var toolbar = FileListPanel.Toolbar(add, T("Drop audio files here too.", "Аудиофайлы можно бросать сюда."), Muted);
        if (paths.Count < 2)
            toolbar.Visibility = Visibility.Collapsed; // one file chosen: more files are picked in Explorer, not here

        _gain.ValueChanged += (_, _) =>
        {
            _gainText.Text = (_gain.Value >= 0 ? "+" : "") + _gain.Value.ToString("0.#", CultureInfo.InvariantCulture) + " dB";
            Retune();
        };
        _speed.ValueChanged += (_, _) =>
        {
            HighlightSpeed();
            Retune();
        };
        _normalize.Click += (_, _) => Retune();
        _silence.Click += (_, _) => Retune();
        _threshold.SelectionChanged += (_, _) => Retune();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_playing)
                _ = StartPreviewAsync(_wave.Position);
        };
        _tick.Tick += (_, _) =>
        {
            if (_playing)
                _wave.Position = Math.Min(_from + _player.Position.TotalSeconds * _renderSpeed, _wave.End);
        };
        _wave.SeekRequested += time =>
        {
            if (_playing)
                _ = StartPreviewAsync(time);
        };
        foreach (var (db, label) in new[] { (-60, T("Very quiet (−60 dB)", "Совсем тихо (−60 dB)")), (-50, T("Quiet (−50 dB)", "Тихо (−50 dB)")), (-40, T("Rough (−40 dB)", "Грубо (−40 dB)")), (-30, T("Aggressive (−30 dB)", "Агрессивно (−30 dB)")) })
            _threshold.Items.Add(new ComboBoxItem { Content = label, Tag = db });
        _threshold.SelectedIndex = 1;

        var speedChips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, value) in new[] { ("0.5×", 0.5), ("0.75×", 0.75), ("1×", 1.0), ("1.25×", 1.25), ("1.5×", 1.5), ("2×", 2.0) })
        {
            var chip = Chip(text, () => _speed.Value = value);
            chip.Margin = new Thickness(0, 0, 4, 0);
            chip.Padding = new Thickness(8, 3, 8, 3);
            chip.MinWidth = 48;
            _speedChips.Add((chip, value));
            speedChips.Children.Add(chip);
        }
        HighlightSpeed();

        // Row 1: label, slider, value.
        var volume = new Grid { MinHeight = 32 };
        volume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        volume.ColumnDefinitions.Add(new ColumnDefinition());
        volume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        volume.Children.Add(new TextBlock { Text = T("Volume", "Громкость"), VerticalAlignment = VerticalAlignment.Center });
        _gain.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_gain, 1);
        volume.Children.Add(_gain);
        _gainText.Foreground = Muted;
        _gainText.HorizontalAlignment = HorizontalAlignment.Right;
        _gainText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_gainText, 2);
        volume.Children.Add(_gainText);
        _wave.Accent = AccentBrush;
        _wave.Idle = LineBrush;
        _wave.LabelBrush = Muted;
        _wave.ClipBrush = DangerBrush;
        _wave.Margin = new Thickness(0, 0, 0, 8);

        // Row 2: label, preset chips.
        var speed = new Grid { MinHeight = 32, Margin = new Thickness(0, 6, 0, 0) };
        speed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        speed.ColumnDefinitions.Add(new ColumnDefinition());
        speed.Children.Add(new TextBlock { Text = T("Speed", "Скорость"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(speedChips, 1);
        speed.Children.Add(speedChips);

        // Row 3: two plain checkboxes and, on the right, the listen button: the settings are heard before they are applied.
        var boxes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _normalize.Margin = new Thickness(0, 0, 24, 0);
        boxes.Children.Add(_normalize);
        boxes.Children.Add(_silence);
        _playButton.Content = ListenText;
        _playButton.Click += async (_, _) =>
        {
            if (_playing)
                StopPreview();
            else
                await StartPreviewAsync(_wave.End - _wave.Position < 0.5 ? 0 : _wave.Position);
        };
        _player.MediaEnded += (_, _) => StopPreview();
        Closed += (_, _) => StopPreview();
        var checks = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        checks.ColumnDefinitions.Add(new ColumnDefinition());
        checks.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        checks.Children.Add(boxes);
        Grid.SetColumn(_playButton, 1);
        checks.Children.Add(_playButton);

        MinHeight = 0;
        FitHeight(520);
        Body = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                toolbar,
                _list,
                new Border { Margin = new Thickness(0, 10, 0, 0), Child = Card(new StackPanel { Children = { _wave, volume, speed, checks } }, new Thickness(14, 8, 14, 10)) },
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

    /// <summary>The preset chip matching the current speed (if any) is drawn in the accent colour.</summary>
    private void HighlightSpeed()
    {
        foreach (var (chip, value) in _speedChips)
            Mark(chip, Math.Abs(_speed.Value - value) < 0.001);
    }

    protected override string DoneTitle => T("Sound processed", "Звук обработан");

    protected override bool SnapshotReady => _ffmpeg is not null && _list.Entries.All(e => e.Ready) && (_wave.Visibility == Visibility.Visible || _list.Entries.Any(e => e.Error is not null));

    /// <summary>"gain=6,speed=1.5,normalize,silence=-40".</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            var value = pair.Length > 1 && double.TryParse(pair[1], CultureInfo.InvariantCulture, out var v) ? v : 0;
            switch (pair[0])
            {
                case "gain":
                    _gain.Value = value;
                    break;
                case "speed":
                    _speed.Value = value;
                    break;
                case "normalize":
                    _normalize.IsChecked = true;
                    break;
                case "silence":
                    _silence.IsChecked = true;
                    foreach (ComboBoxItem item in _threshold.Items)
                        if (item.Tag is int db && db == (int)value)
                            _threshold.SelectedItem = item;
                    break;
            }
        }
    }

    private void Add(IEnumerable<string> paths)
    {
        if (_ffmpeg is null)
            return;
        foreach (var path in paths.Where(p => AudioJoinEditor.Inputs.Contains(Path.GetExtension(p).TrimStart('.')) && File.Exists(p)))
        {
            var entry = new TrackEntry(path);
            _list.Entries.Add(entry);
            _ = ProbeAsync(entry);
        }
        _list.Refresh();
    }

    private async Task ProbeAsync(TrackEntry entry)
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
        if (entry.Error is null)
            await LoadWaveAsync(entry);
    }

    private void UpdateHint() =>
        SetHint(T($"Files: {_list.Entries.Count}", $"Файлов: {_list.Entries.Count}"));

    /// <summary>"atempo" takes 0.5 to 2 per step; 4× is two steps of 2×.</summary>
    private static string Atempo(double speed)
    {
        var steps = new List<string>();
        while (speed > 2)
        {
            steps.Add("atempo=2.0");
            speed /= 2;
        }
        while (speed < 0.5)
        {
            steps.Add("atempo=0.5");
            speed /= 0.5;
        }
        steps.Add("atempo=" + speed.ToString("0.####", CultureInfo.InvariantCulture));
        return string.Join(',', steps);
    }

    private string Filter()
    {
        var filters = new List<string>();
        if (_silence.IsChecked == true)
        {
            var db = _threshold.SelectedItem is ComboBoxItem { Tag: int d } ? d : -50;
            var length = double.TryParse(_silenceLength.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l > 0 ? l : 0.5;
            var threshold = $"{db}dB";
            filters.Add($"silenceremove=start_periods=1:start_duration=0.1:start_threshold={threshold}:stop_periods=-1:stop_duration={length.ToString("0.###", CultureInfo.InvariantCulture)}:stop_threshold={threshold}");
        }
        if (Math.Abs(_speed.Value - 1) > 0.001)
            filters.Add(Atempo(_speed.Value));
        if (_normalize.IsChecked == true)
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        if (Math.Abs(_gain.Value) > 0.01)
            filters.Add($"volume={_gain.Value.ToString("0.##", CultureInfo.InvariantCulture)}dB");
        return string.Join(',', filters);
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var entries = _list.Entries.OfType<TrackEntry>().ToList();
        if (_ffmpeg is null)
            throw new ConvertException(T("FFmpeg is not ready yet", "FFmpeg ещё не готов"));
        if (entries.Count == 0)
            throw new ConvertException(T("Add files first", "Сначала добавь файлы"));
        if (entries.Any(e => !e.Ready))
            throw new ConvertException(T("Still reading the files", "Файлы ещё читаются"));
        if (entries.FirstOrDefault(e => e.Error is not null) is { } broken)
            throw new ConvertException(Path.GetFileName(broken.Path) + ": " + broken.Error);
        var filter = Filter();
        if (filter.Length == 0)
            throw new ConvertException(T("Nothing is changed yet", "Пока ничего не изменено"));

        var speed = _speed.Value;
        string? last = null;
        for (var i = 0; i < entries.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var entry = entries[i];
            context.StartFile(i, entries.Count, Path.GetFileName(entry.Path));
            var ext = Path.GetExtension(entry.Path).TrimStart('.').ToLowerInvariant();
            var codec = AudioCodecs.For(ext) ?? throw new ConvertException(T("This format cannot be written here", "В этот формат здесь писать нельзя"));
            var output = ConvertContext.NewPath(entry.Path, ext, T(" (edited)", " (обработано)"));
            var total = Math.Max(1, entry.Seconds / speed);
            var rate = _normalize.IsChecked == true ? await FfmpegRate.ProbeAsync(_ffmpeg, entry.Path, context.Cancel) : 0;
            string[] rateArgs = rate > 0 ? ["-ar", rate.ToString(CultureInfo.InvariantCulture)] : [];
            var (code, errors) = await ProcessRunner.RunAsync(_ffmpeg, ["-y", "-hide_banner", "-nostdin", "-i", entry.Path, "-vn", "-map", "0:a:0", "-af", filter, .. rateArgs, .. codec, output], context.Cancel, line =>
            {
                if (FfmpegTools.TryTime(line) is { } done)
                    context.Progress(done / total);
            });
            if (code != 0 || !File.Exists(output))
            {
                if (File.Exists(output))
                    File.Delete(output);
                throw new ConvertException(Path.GetFileName(entry.Path) + ": " + T("FFmpeg failed: ", "FFmpeg не справился: ") + ProcessRunner.Reason(errors));
            }
            context.Add(output);
            last = output;
        }
        DoneOverride = T($"Processed {entries.Count} files", $"Обработано файлов: {entries.Count}");
        return last!;
    }
}
