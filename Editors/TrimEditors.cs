using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Cut a piece out of an audio file: waveform, two handles, preview, optional fades. Stream copy when no fades are asked for.</summary>
internal sealed class AudioTrimEditor : IEditor
{
    public string Caption => T("Trim audio", "Обрезка аудио");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && AudioJoinEditor.Inputs.Contains(Path.GetExtension(paths[0]).TrimStart('.'));

    public EditorWindow Create(IReadOnlyList<string> paths) => new TrimWindow(paths[0], video: false);
}

/// <summary>Cut a piece out of a video: timeline with the sound's waveform, preview frame, fast copy or an exact re-encode.</summary>
internal sealed class VideoTrimEditor : IEditor
{
    internal static readonly HashSet<string> Inputs = new(["mp4", "mkv", "avi", "mov", "webm", "wmv", "m4v", "mpg", "mpeg", "ts"], StringComparer.OrdinalIgnoreCase);

    public string Caption => T("Trim video", "Обрезка видео");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && Inputs.Contains(Path.GetExtension(paths[0]).TrimStart('.'));

    public EditorWindow Create(IReadOnlyList<string> paths) => new TrimWindow(paths[0], video: true);
}

internal sealed class TrimWindow : EditorWindow
{
    private const double FadeSeconds = 1.5;
    private const int PeaksPerSecond = 25;

    private readonly string _path;
    private readonly string _ext;
    private readonly bool _video;
    private readonly WaveformView _wave = new() { Height = 90 };
    private readonly TextBlock _loading = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _start = Field();
    private readonly TextBox _end = Field();
    private readonly TextBlock _length = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly CheckBox _fadeIn = new() { Content = T("Fade in", "Плавный вход") };
    private readonly CheckBox _fadeOut = new() { Content = T("Fade out", "Плавный выход") };
    private readonly CheckBox _fast = new() { Content = T("Fast cut", "Быстрый рез"), ToolTip = T("No re-encoding; starts at a key frame", "Без перекодирования; начало по ключевому кадру") };
    private readonly CheckBox _mute = new() { Content = T("No sound", "Без звука") };
    private readonly ComboBox _speed = new();
    private string? _testAction;
    private readonly Image _still = new() { Stretch = Stretch.Uniform };
    private readonly Rectangle _frame = new() { Visibility = Visibility.Collapsed };
    private readonly Button _play;
    private readonly TextBlock _playGlyph = new() { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 };
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private CancellationTokenSource? _frameJob;
    private string? _ffmpeg;
    private double _frameStart = -1;
    private bool _playing;
    private bool _canPlay = true;
    private bool _loaded;

    public TrimWindow(string path, bool video) : base((video ? T("Trim video", "Обрезка видео") : T("Trim audio", "Обрезка аудио")) + " · " + Path.GetFileName(path), 640, video ? 300 : 240, video ? T("Save the piece", "Сохранить фрагмент") : T("Save the piece", "Сохранить фрагмент"))
    {
        _path = path;
        _video = video;
        _ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        _loading.Text = T("Reading the file…", "Читаю файл…");
        _loading.Foreground = Muted;
        _wave.Accent = AccentBrush;
        _wave.Idle = Hex(Dark ? "#6A6A6A" : "#9A9A9A");
        _wave.LabelBrush = Muted;
        _wave.SelectionChanged += OnSelectionChanged;
        _wave.SeekRequested += Seek;

        _playGlyph.Text = "";
        _play = new Button { Content = _playGlyph, Width = 42, ToolTip = T("Play the selection", "Прослушать фрагмент") };
        _play.Click += (_, _) => TogglePlay();
        _start.LostFocus += (_, _) => ApplyTime(startField: true);
        _end.LostFocus += (_, _) => ApplyTime(startField: false);
        _start.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyTime(true); };
        _end.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyTime(false); };

        _timer.Tick += (_, _) => Tick();
        _player.MediaFailed += (_, _) =>
        {
            _canPlay = false;
            _play.ToolTip = T("Windows cannot play this format here; trimming still works", "Windows не воспроизводит этот формат, но обрезка работает");
            _play.Opacity = 0.4;
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _frameJob?.Cancel();
            _player.Close();
        };

        var parts = new StackPanel { Margin = new Thickness(Gap, 8, Gap, 4) };
        if (video)
        {
            // A still frame (made by FFmpeg, works for every format) with the live video on top while it plays.
            var drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, 640, 360) };
            _player.MediaOpened += (_, _) =>
            {
                if (_player.NaturalVideoWidth > 0)
                    drawing.Rect = new Rect(0, 0, _player.NaturalVideoWidth, _player.NaturalVideoHeight);
            };
            _frame.Fill = new DrawingBrush(drawing) { Stretch = Stretch.Uniform };
            var stage = PreviewBox(new Grid { Children = { _still, _frame } });
            stage.Background = Hex("#000000");
            stage.Height = 260;
            parts.Children.Add(stage);
        }

        var waveHost = new Grid { Margin = new Thickness(0, video ? 10 : 0, 0, 0) };
        waveHost.Children.Add(PreviewBox(_wave));
        waveHost.Children.Add(_loading);
        parts.Children.Add(waveHost);

        // One tidy toolbar row: play, start, end, length.
        TextBlock Caption(string text) => new() { Text = text, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
        _play.Width = 44;
        _play.Height = 32;
        _length.Margin = new Thickness(0);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_play);
        controls.Children.Add(Caption(T("Start", "Начало")));
        controls.Children.Add(_start);
        controls.Children.Add(Caption(T("End", "Конец")));
        controls.Children.Add(_end);
        controls.Children.Add(Caption(T("Length", "Длина")));
        controls.Children.Add(_length);
        controls.Margin = new Thickness(0, 10, 0, 0);
        parts.Children.Add(controls);

        // Options: one quiet row, no headings.
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (video)
        {
            foreach (var speed in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 })
                _speed.Items.Add(new ComboBoxItem { Content = speed.ToString("0.##", CultureInfo.InvariantCulture) + "×", Tag = speed });
            _speed.SelectedIndex = 3;
            _speed.Width = 80;
            _speed.VerticalAlignment = VerticalAlignment.Center;
            options.Children.Add(new TextBlock { Text = T("Speed", "Скорость"), Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            options.Children.Add(_speed);
            _mute.Margin = new Thickness(18, 0, 0, 0);
            _fast.Margin = new Thickness(18, 0, 0, 0);
            options.Children.Add(_mute);
            options.Children.Add(_fast);
            AddAction(T("Frame → picture", "Кадр в картинку"), SaveFrameAsync);
            AddAction(T("Piece → GIF", "Кусок в GIF"), SaveGifAsync);
        }
        else
        {
            _fadeOut.Margin = new Thickness(18, 0, 0, 0);
            options.Children.Add(_fadeIn);
            options.Children.Add(_fadeOut);
        }
        parts.Children.Add(options);
        FitHeight(video ? 600 : 380);
        Body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = parts };
        Loaded += (_, _) => _ = RunAsync(LoadAsync);
    }

    protected override string DoneTitle => _video ? T("Video trimmed", "Видео обрезано") : T("Audio trimmed", "Аудио обрезано");

    protected override bool SnapshotReady => _loaded && (!_video || _still.Source is not null);

    /// <summary>"start=3,end=8,in,out,fast".</summary>
    protected override void ConfigureForTest(string spec)
    {
        double start = _wave.Start, end = _wave.End;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            var value = pair.Length > 1 && double.TryParse(pair[1], CultureInfo.InvariantCulture, out var v) ? v : 0;
            switch (pair[0])
            {
                case "start":
                    start = value;
                    break;
                case "end":
                    end = value;
                    break;
                case "in":
                    _fadeIn.IsChecked = true;
                    break;
                case "out":
                    _fadeOut.IsChecked = true;
                    break;
                case "mute":
                    _mute.IsChecked = true;
                    break;
                case "speed":
                    foreach (ComboBoxItem item in _speed.Items)
                        if (item.Tag is double s && Math.Abs(s - value) < 0.001)
                            _speed.SelectedItem = item;
                    break;
                case "frame":
                case "gif":
                    _testAction = pair[0];
                    break;
                case "fast":
                    _fast.IsChecked = true;
                    break;
            }
        }
        _wave.SetSelection(start, end);
    }

    private static TextBox Field() => new() { Width = 78, TextAlignment = TextAlignment.Center };

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // --- loading ---

    private async Task LoadAsync(ConvertContext context)
    {
        context.Report(T("Preparing FFmpeg…", "Готовлю FFmpeg…"), null);
        var ffmpeg = _ffmpeg = await Engines.EnsureAsync(Engine.FFmpeg, context);
        context.Report(T("Reading the file…", "Читаю файл…"), null);
        var (seconds, hasAudio, hasVideo) = await FfmpegTools.ProbeAsync(ffmpeg, _path, context.Cancel);
        if (_video && !hasVideo)
            throw new ConvertException(T("No video found in this file", "В файле не найдено видео"));

        float[] peaks;
        double duration;
        if (hasAudio)
        {
            (peaks, duration) = await Task.Run(() => DecodeAsync(ffmpeg, _path, context.Cancel), context.Cancel);
            if (_video && seconds is > 0)
                duration = seconds.Value;
        }
        else if (_video && seconds is > 0)
        {
            // No sound track: a flat line stands in for the wave, the handles work all the same.
            duration = seconds.Value;
            peaks = Enumerable.Repeat(0.1f, Math.Max(1, (int)(duration * PeaksPerSecond))).ToArray();
        }
        else
        {
            throw new ConvertException(T("No sound found in this file", "В файле не найден звук"));
        }

        _wave.SetData(peaks, duration);
        _loading.Visibility = Visibility.Collapsed;
        _loaded = true;
        ShowTimes();
        ShowStatus(string.Empty, error: false);
        _player.Open(new Uri(Path.GetFullPath(_path)));
        if (_video)
            await ShowFrameAsync(0, immediate: true);
    }

    /// <summary>Mono 8 kHz samples from FFmpeg, reduced to one peak per 40 ms.</summary>
    private static async Task<(float[] Peaks, double Duration)> DecodeAsync(string ffmpeg, string path, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" })
            info.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ConvertException(T("Could not start FFmpeg", "Не удалось запустить FFmpeg"));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConvertException(T("Could not start FFmpeg: ", "Не удалось запустить FFmpeg: ") + ex.Message, ex);
        }
        using (process)
        using (cancel.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }))
        {
            var errors = process.StandardError.ReadToEndAsync();
            var peaks = new List<float>();
            var buffer = new byte[1 << 16];
            var window = 8000 / PeaksPerSecond;
            long samples = 0;
            int inWindow = 0, max = 0, carry = -1, read;
            var stream = process.StandardOutput.BaseStream;
            while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (carry < 0)
                    {
                        carry = buffer[i];
                        continue;
                    }
                    var sample = (short)(carry | (buffer[i] << 8));
                    carry = -1;
                    var magnitude = Math.Abs((int)sample);
                    if (magnitude > max)
                        max = magnitude;
                    samples++;
                    if (++inWindow == window)
                    {
                        peaks.Add(max / 32768f);
                        max = 0;
                        inWindow = 0;
                    }
                }
            }
            if (inWindow > 0)
                peaks.Add(max / 32768f);
            await process.WaitForExitAsync(CancellationToken.None);
            cancel.ThrowIfCancellationRequested();
            if (samples == 0)
                throw new ConvertException(T("Could not read this audio: ", "Не удалось прочитать звук: ") + ProcessRunner.Reason(await errors));

            var loudest = peaks.Max();
            var data = peaks.Select(p => loudest < 0.0005f ? 0f : (float)Math.Pow(p / loudest, 0.7)).ToArray();
            return (data, samples / 8000.0);
        }
    }

    // --- still frame (video) ---

    private async Task ShowFrameAsync(double seconds, bool immediate = false)
    {
        if (!_video || _ffmpeg is null)
            return;
        _frameJob?.Cancel();
        var job = _frameJob = new CancellationTokenSource();
        try
        {
            if (!immediate)
                await Task.Delay(180, job.Token); // dragging a handle asks for many frames; only the last one is made
            var temp = Path.Combine(Path.GetTempPath(), $"radii-frame-{Guid.NewGuid():N}.png");
            try
            {
                var (code, _) = await ProcessRunner.RunAsync(_ffmpeg, ["-y", "-hide_banner", "-nostdin", "-ss", N(seconds), "-i", _path, "-frames:v", "1", "-update", "1", "-vf", "scale=-2:360", temp], job.Token);
                if (code == 0 && File.Exists(temp))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(temp);
                    image.EndInit();
                    image.Freeze();
                    _still.Source = image;
                }
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ConvertException or IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            Program.LogCrash(ex); // the preview is a nicety: log it, do not interrupt the editor
        }
    }

    // --- selection and time fields ---

    private void OnSelectionChanged()
    {
        ShowTimes();
        if (_video && _loaded && !_playing && Math.Abs(_wave.Start - _frameStart) > 0.05)
        {
            _frameStart = _wave.Start;
            _ = ShowFrameAsync(_wave.Start);
        }
    }

    private void ShowTimes()
    {
        _start.Text = FormatTime(_wave.Start);
        _end.Text = FormatTime(_wave.End);
        _length.Text = FormatTime(_wave.End - _wave.Start);
    }

    private void ApplyTime(bool startField)
    {
        if (!_loaded)
            return;
        if (ParseTime((startField ? _start : _end).Text) is { } seconds)
        {
            if (startField)
                _wave.SetSelection(seconds, _wave.End);
            else
                _wave.SetSelection(_wave.Start, seconds);
        }
        ShowTimes();
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds, 1)));
        var tenths = span.Milliseconds / 100;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{tenths}"
            : $"{span.Minutes:00}:{span.Seconds:00}.{tenths}";
    }

    /// <summary>"90", "1:30", "01:30.5", "1:02:03" (comma or dot for the fraction).</summary>
    private static double? ParseTime(string text)
    {
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is 0 or > 3)
            return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
                return null;
            total = total * 60 + value;
        }
        return total;
    }

    // --- playback ---

    private void TogglePlay()
    {
        if (!_loaded || !_canPlay)
            return;
        if (_playing)
        {
            StopPlayback(rewind: false);
            return;
        }
        var position = _wave.Position;
        if (position < _wave.Start || position >= _wave.End - 0.05)
            position = _wave.Start;
        _player.Position = TimeSpan.FromSeconds(position);
        _player.Play();
        _playing = true;
        _playGlyph.Text = "";
        if (_video)
            _frame.Visibility = Visibility.Visible;
        _timer.Start();
    }

    private void StopPlayback(bool rewind)
    {
        _player.Pause();
        _timer.Stop();
        _playing = false;
        _playGlyph.Text = "";
        if (rewind)
        {
            _wave.Position = _wave.Start;
            _frame.Visibility = Visibility.Collapsed;
        }
    }

    private void Tick()
    {
        var position = _player.Position.TotalSeconds;
        _wave.Position = position;
        if (position >= _wave.End)
            StopPlayback(rewind: true);
    }

    private void Seek(double seconds)
    {
        _wave.Position = seconds;
        if (_playing)
            _player.Position = TimeSpan.FromSeconds(seconds);
        else if (_video)
            _ = ShowFrameAsync(seconds);
    }

    // --- saving ---

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        if (_testAction == "frame")
            return await SaveFrameAsync(context);
        if (_testAction == "gif")
            return await SaveGifAsync(context);
        if (!_loaded || _ffmpeg is null)
            throw new ConvertException(T("The file is still loading", "Файл ещё загружается"));
        StopPlayback(rewind: false);
        var ffmpeg = _ffmpeg;
        double start = _wave.Start, length = _wave.End - _wave.Start;
        if (length < 0.05)
            throw new ConvertException(T("Select a longer piece", "Выбери кусок подлиннее"));
        var output = ConvertContext.NewPath(_path, _ext, T(" (cut)", " (фрагмент)"));
        var head = new[] { "-y", "-hide_banner", "-nostdin", "-ss", N(start), "-t", N(length), "-i", _path };

        var attempts = new List<string[]>();
        var speed = _video && _speed.SelectedItem is ComboBoxItem { Tag: double chosen } ? chosen : 1.0;
        var resultLength = length / speed;
        if (_video)
        {
            var muted = _mute.IsChecked == true;
            string[] streams = muted ? ["-map", "0:v:0", "-an"] : ["-map", "0:v:0", "-map", "0:a?"];
            var codec = VideoCodecs.For(_ext);
            if (speed != 1)
            {
                // A different speed needs new frames and a tempo change of the sound: always re-encoded.
                if (codec is null)
                    throw new ConvertException(T("This format cannot be sped up here", "Этот формат здесь нельзя ускорить или замедлить"));
                string[] change = muted ? ["-vf", $"setpts=PTS/{N(speed)}"] : ["-vf", $"setpts=PTS/{N(speed)}", "-af", AtempoChain(speed)];
                attempts.Add([.. head, .. streams, .. change, .. codec]);
            }
            else
            {
                if (_fast.IsChecked == true || codec is null)
                    attempts.Add([.. head, .. streams, "-c", "copy", "-avoid_negative_ts", "make_zero"]);
                if (codec is not null)
                    attempts.Add([.. head, .. streams, .. codec]);
            }
        }
        else
        {
            var fadeIn = _fadeIn.IsChecked == true;
            var fadeOut = _fadeOut.IsChecked == true;
            string[] streams = ["-vn", "-map", "0:a:0"];
            if (!fadeIn && !fadeOut)
                attempts.Add([.. head, .. streams, "-c", "copy"]);
            var codec = AudioCodecs.For(_ext);
            if (codec is not null)
            {
                var filters = new List<string>();
                var fade = Math.Min(FadeSeconds, length / 2);
                if (fadeIn)
                    filters.Add($"afade=t=in:st=0:d={N(fade)}");
                if (fadeOut)
                    filters.Add($"afade=t=out:st={N(length - fade)}:d={N(fade)}");
                attempts.Add([.. head, .. streams, .. filters.Count > 0 ? new[] { "-af", string.Join(',', filters) } : [], .. codec]);
            }
        }
        if (attempts.Count == 0)
            throw new ConvertException(T("This format can only be cut without fades", "Этот формат можно резать только без плавных переходов"));

        var failure = "";
        foreach (var attempt in attempts)
        {
            context.Report(T("Saving…", "Сохраняю…"), 0);
            var (code, errors) = await ProcessRunner.RunAsync(ffmpeg, [.. attempt, output], context.Cancel, line =>
            {
                if (FfmpegTools.TryTime(line) is { } done)
                    context.Progress(done / resultLength);
            });
            if (code == 0 && File.Exists(output))
                return output;
            failure = ProcessRunner.Reason(errors);
            if (File.Exists(output))
                File.Delete(output);
        }
        throw new ConvertException(T("FFmpeg failed: ", "FFmpeg не справился: ") + failure);
    }

    /// <summary>"atempo" only takes 0.5 to 2 per step, so 4× is two steps of 2×.</summary>
    private static string AtempoChain(double speed)
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
        steps.Add("atempo=" + N(speed));
        return string.Join(',', steps);
    }

    /// <summary>The picture at the play cursor (or the start of the piece), at the full size of the video.</summary>
    private async Task<string> SaveFrameAsync(ConvertContext context)
    {
        if (!_loaded || _ffmpeg is null)
            throw new ConvertException(T("The file is still loading", "Файл ещё загружается"));
        StopPlayback(rewind: false);
        var seconds = _wave.Position >= _wave.Start && _wave.Position <= _wave.End ? _wave.Position : _wave.Start;
        var output = ConvertContext.NewPath(_path, "png", T($" (frame {N(seconds)} s)", $" (кадр {N(seconds)} с)"));
        context.Report(T("Saving the frame…", "Сохраняю кадр…"), null);
        var (code, errors) = await ProcessRunner.RunAsync(_ffmpeg, ["-y", "-hide_banner", "-nostdin", "-ss", N(seconds), "-i", _path, "-frames:v", "1", "-update", "1", output], context.Cancel);
        if (code != 0 || !File.Exists(output))
            throw new ConvertException(T("FFmpeg failed: ", "FFmpeg не справился: ") + ProcessRunner.Reason(errors));
        return output;
    }

    /// <summary>The selected piece as an endless GIF, 480 px wide, 15 frames a second.</summary>
    private async Task<string> SaveGifAsync(ConvertContext context)
    {
        if (!_loaded || _ffmpeg is null)
            throw new ConvertException(T("The file is still loading", "Файл ещё загружается"));
        StopPlayback(rewind: false);
        double start = _wave.Start, length = _wave.End - _wave.Start;
        if (length < 0.05)
            throw new ConvertException(T("Select a longer piece", "Выбери кусок подлиннее"));
        var output = ConvertContext.NewPath(_path, "gif", T(" (clip)", " (гиф)"));
        context.Report(T("Making the GIF…", "Делаю GIF…"), 0);
        var (code, errors) = await ProcessRunner.RunAsync(_ffmpeg,
            ["-y", "-hide_banner", "-nostdin", "-ss", N(start), "-i", _path, "-t", N(length), "-an", "-vf", "fps=15,scale='min(480,iw)':-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0", output],
            context.Cancel, line =>
            {
                if (FfmpegTools.TryTime(line) is { } done)
                    context.Progress(done / length);
            });
        if (code != 0 || !File.Exists(output))
        {
            if (File.Exists(output))
                File.Delete(output);
            throw new ConvertException(T("FFmpeg failed: ", "FFmpeg не справился: ") + ProcessRunner.Reason(errors));
        }
        return output;
    }
}
