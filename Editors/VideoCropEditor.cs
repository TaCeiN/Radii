using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Crop the picture of a video (not its length): a frame of the video with a movable frame, then FFmpeg cuts every frame the same way.</summary>
internal sealed class VideoCropEditor : IEditor
{
    public string Caption => T("Crop video", "Кроп видео");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && VideoTrimEditor.Inputs.Contains(Path.GetExtension(paths[0]).TrimStart('.'));

    public EditorWindow Create(IReadOnlyList<string> paths) => new VideoCropWindow(paths[0]);
}

internal sealed class VideoCropWindow : EditorWindow
{
    private readonly string _path;
    private readonly string _ext;
    private readonly CropSurface _surface = new();
    private readonly TextBox _width = new() { Width = 84, TextAlignment = TextAlignment.Center };
    private readonly TextBox _height = new() { Width = 84, TextAlignment = TextAlignment.Center };
    private readonly List<ToggleButton> _chips = [];
    private readonly TextBlock _loading = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private string? _ffmpeg;
    private double _duration;
    private bool _loaded;
    private bool _updating;

    public VideoCropWindow(string path) : base(T("Crop video", "Кроп видео") + " · " + Path.GetFileName(path), 760, 480, T("Save a copy", "Сохранить копию"))
    {
        _path = path;
        _ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        _loading.Text = T("Reading the video…", "Читаю видео…");
        _loading.Foreground = Muted;
        _surface.CropChanged += () =>
        {
            _updating = true;
            _width.Text = ((int)Math.Round(_surface.Crop.Width)).ToString(CultureInfo.InvariantCulture);
            _height.Text = ((int)Math.Round(_surface.Crop.Height)).ToString(CultureInfo.InvariantCulture);
            _updating = false;
        };
        _width.LostFocus += (_, _) => ApplySize(width: true);
        _height.LostFocus += (_, _) => ApplySize(width: false);
        _width.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(true); };
        _height.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(false); };

        var canvasHost = new Grid();
        canvasHost.Children.Add(PreviewBox(_surface));
        canvasHost.Children.Add(_loading);

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
        AddChip(chips, T("Free", "Свободно"), null, true);
        AddChip(chips, "1:1", () => 1.0);
        AddChip(chips, "4:3", () => 4.0 / 3);
        AddChip(chips, "16:9", () => 16.0 / 9);
        AddChip(chips, "9:16", () => 9.0 / 16);

        _width.Width = 70;
        _height.Width = 70;
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(_width);
        size.Children.Add(new TextBlock { Text = "×", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
        size.Children.Add(_height);

        Body = PreviewLayout(canvasHost,
            Section("", chips),
            Section("", Row(T("Size", "Размер"), size)));

        Loaded += (_, _) => _ = RunAsync(LoadAsync);
    }

    protected override string DoneTitle => T("Video cropped", "Видео обрезано по кадру");

    protected override bool SnapshotReady => _loaded;

    /// <summary>"w=640,h=360".</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            if (pair.Length == 2 && int.TryParse(pair[1], out var n))
            {
                if (pair[0] == "w")
                    _surface.Resize(n, null);
                else if (pair[0] == "h")
                    _surface.Resize(null, n);
            }
        }
    }

    private void AddChip(Panel host, string text, Func<double>? ratio, bool isChecked = false)
    {
        var button = new ToggleButton { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(11, 4, 11, 4), MinWidth = 44, IsChecked = isChecked };
        button.Click += (_, _) =>
        {
            foreach (var chip in _chips)
                chip.IsChecked = chip == button;
            _surface.SetRatio(ratio?.Invoke());
        };
        _chips.Add(button);
        host.Children.Add(button);
    }

    private void ApplySize(bool width)
    {
        if (_updating || !_loaded)
            return;
        var box = width ? _width : _height;
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            _surface.Resize(width ? value : null, width ? null : value);
    }

    private async Task LoadAsync(ConvertContext context)
    {
        context.Report(T("Preparing FFmpeg…", "Готовлю FFmpeg…"), null);
        var ffmpeg = _ffmpeg = await Engines.EnsureAsync(Engine.FFmpeg, context);
        context.Report(T("Reading the video…", "Читаю видео…"), null);
        var (seconds, _, hasVideo) = await FfmpegTools.ProbeAsync(ffmpeg, _path, context.Cancel);
        if (!hasVideo)
            throw new ConvertException(T("No video found in this file", "В файле не найдено видео"));
        _duration = seconds ?? 0;
        var temp = Path.Combine(Path.GetTempPath(), $"radii-vcrop-{Guid.NewGuid():N}.png");
        try
        {
            var at = _duration > 4 ? _duration * 0.1 : 0;
            var (code, errors) = await ProcessRunner.RunAsync(ffmpeg, ["-y", "-hide_banner", "-nostdin", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", _path, "-frames:v", "1", "-update", "1", temp], context.Cancel);
            if (code != 0 || !File.Exists(temp))
                throw new ConvertException(T("Could not take a frame: ", "Не удалось взять кадр: ") + ProcessRunner.Reason(errors));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(temp);
            image.EndInit();
            image.Freeze();
            _surface.SetSource(image, null);
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
        _loading.Visibility = Visibility.Collapsed;
        _loaded = true;
        ShowStatus("", error: false);
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        if (!_loaded || _ffmpeg is null)
            throw new ConvertException(T("The video is still loading", "Видео ещё загружается"));
        var rect = _surface.PixelRect;
        // Most encoders want even sizes.
        int w = rect.Width & ~1, h = rect.Height & ~1, x = rect.X & ~1, y = rect.Y & ~1;
        if (w < 2 || h < 2)
            throw new ConvertException(T("The frame is too small", "Кадр слишком мал"));
        var codec = VideoCodecs.For(_ext) ?? throw new ConvertException(T("This format cannot be re-encoded here", "Этот формат здесь нельзя перекодировать"));
        var output = ConvertContext.NewPath(_path, _ext, T(" (cropped)", " (кроп)"));
        context.Report(T("Cropping…", "Обрезаю…"), 0);
        var total = Math.Max(1, _duration);
        var (code, errors) = await ProcessRunner.RunAsync(_ffmpeg, ["-y", "-hide_banner", "-nostdin", "-i", _path, "-map", "0:v:0", "-map", "0:a?", "-vf", $"crop={w}:{h}:{x}:{y}", .. codec, output], context.Cancel, line =>
        {
            if (FfmpegTools.TryTime(line) is { } done)
                context.Progress(done / total);
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
