using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Radii.Converters;

/// <summary>Video and audio through FFmpeg (downloaded on first use).</summary>
internal sealed partial class MediaConverter : IConverter
{
    private static readonly HashSet<string> VideoInputs = Set("mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "3gp", "ts", "mts", "m2ts", "mpg", "mpeg", "ogv", "vob", "gif");
    private static readonly HashSet<string> AudioInputs = Set("mp3", "wav", "flac", "aac", "m4a", "ogg", "oga", "opus", "wma", "aiff", "aif", "ac3", "amr", "mka");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    private static string ExtOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static readonly Format Mp3 = new("a:mp3", "MP3", "mp3");
    private static readonly Format M4a = new("a:m4a", "AAC", "m4a");
    private static readonly Format Wav = new("a:wav", "WAV", "wav");
    private static readonly Format Flac = new("a:flac", "FLAC", "flac");
    private static readonly Format Ogg = new("a:ogg", "OGG", "ogg");
    private static readonly Format Opus = new("a:opus", "OPUS", "opus");
    private static readonly Format Aiff = new("a:aiff", "AIFF", "aiff");
    private static readonly Format Wma = new("a:wma", "WMA", "wma");

    private static readonly Format[] AudioFormats = [Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma];

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(File.Exists))
            return [];
        var exts = paths.Select(ExtOf).ToList();
        if (exts.All(AudioInputs.Contains))
        {
            // GIF is a video input here only so it can be turned into MP4/WebM; not offered as a source of audio.
            return AudioFormats.Where(f => exts.Any(e => e != f.Ext && !(f.Ext == "m4a" && e == "aac"))).ToList();
        }
        if (!exts.All(VideoInputs.Contains))
            return [];
        var list = new List<Format>();
        var gif = exts.All(e => e == "gif");
        foreach (var f in new[] { new Format("v:mp4", "MP4", "mp4"), new Format("v:mkv", "MKV", "mkv"), new Format("v:webm", "WebM", "webm"), new Format("v:mov", "MOV", "mov"), new Format("v:avi", "AVI", "avi"), new Format("v:gif", "GIF", "gif") })
        {
            if (!exts.All(e => e == f.Ext))
                list.Add(f);
        }
        if (!gif)
        {
            list.Add(new Format("audio", "♪", "", AudioFormats.Select(a => a with { Id = "va:" + a.Ext }).ToList(), Glyph: "", Name: Menus.T("Audio only…", "Только звук…")));
            list.Add(new Format("shrink", "", "", [
                new Format("s:1080", "1080p", "mp4", Name: Menus.T("Shrink to 1080p", "Сжать до 1080p")),
                new Format("s:720", "720p", "mp4", Name: Menus.T("Shrink to 720p", "Сжать до 720p")),
                new Format("s:480", "480p", "mp4", Name: Menus.T("Shrink to 480p", "Сжать до 480p")),
                new Format("s:360", "360p", "mp4", Name: Menus.T("Shrink to 360p", "Сжать до 360p")),
            ], Glyph: "", Name: Menus.T("Shrink…", "Сжать…")));
        }
        return list;
    }

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        var ffmpeg = await Engines.EnsureAsync(Engine.FFmpeg, context);
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            var suffix = to.Id.StartsWith("s:", StringComparison.Ordinal) ? $" ({to.Label})" : "";
            var output = ConvertContext.NewPath(input, to.Ext, suffix);
            var attempts = Attempts(to).ToList();
            var failure = "";
            foreach (var attempt in attempts)
            {
                double? total = null;
                var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-i", input };
                args.AddRange(attempt);
                args.Add(output);
                var (code, errors) = await ProcessRunner.RunAsync(ffmpeg, args, context.Cancel, line =>
                {
                    if (total is null && DurationRegex().Match(line) is { Success: true } d)
                        total = Seconds(d);
                    else if (total is > 0 && TimeRegex().Match(line) is { Success: true } t)
                        context.Progress(Seconds(t) / total);
                });
                if (code == 0 && File.Exists(output))
                {
                    failure = "";
                    break;
                }
                failure = ProcessRunner.Reason(errors);
                if (File.Exists(output))
                    File.Delete(output);
            }
            if (failure.Length > 0 || !File.Exists(output))
                throw new ConvertException(Menus.T("FFmpeg failed: ", "FFmpeg не справился: ") + failure);
            context.Add(output);
        }
    }

    /// <summary>Argument sets to try in order (a fast stream copy first where it usually works).</summary>
    private static IEnumerable<string[]> Attempts(Format to)
    {
        if (to.Id.StartsWith("s:", StringComparison.Ordinal))
        {
            yield return ["-vf", $"scale=-2:'min({to.Label.TrimEnd('p')},ih)'", "-c:v", "libx264", "-crf", "28", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"];
            yield break;
        }
        switch (to.Ext)
        {
            case "mp4":
                yield return ["-c:v", "libx264", "-preset", "medium", "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"];
                break;
            case "mkv":
                yield return ["-map", "0", "-c", "copy"];
                yield return ["-c:v", "libx264", "-preset", "medium", "-crf", "22", "-c:a", "aac", "-b:a", "192k"];
                break;
            case "webm":
                yield return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"];
                break;
            case "mov":
                yield return ["-c:v", "libx264", "-preset", "medium", "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"];
                break;
            case "avi":
                yield return ["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "4"];
                break;
            case "gif":
                yield return ["-vf", "fps=15,scale='min(480,iw)':-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0", "-an"];
                break;
            case "mp3":
                yield return ["-vn", "-c:a", "libmp3lame", "-q:a", "2"];
                break;
            case "m4a":
                yield return ["-vn", "-c:a", "aac", "-b:a", "192k"];
                break;
            case "wav":
                yield return ["-vn", "-c:a", "pcm_s16le"];
                break;
            case "flac":
                yield return ["-vn", "-c:a", "flac"];
                break;
            case "ogg":
                yield return ["-vn", "-c:a", "libvorbis", "-q:a", "5"];
                break;
            case "opus":
                yield return ["-vn", "-c:a", "libopus", "-b:a", "128k"];
                break;
            case "aiff":
                yield return ["-vn", "-c:a", "pcm_s16be"];
                break;
            case "wma":
                yield return ["-vn", "-c:a", "wmav2", "-b:a", "192k"];
                break;
        }
    }

    private static double Seconds(Match match) =>
        int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
        + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
        + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();
}
