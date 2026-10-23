using System.Globalization;
using System.Text.RegularExpressions;
using Radii.Converters;

namespace Radii.Editors;

/// <summary>Small FFmpeg helpers shared by the audio and video editors.</summary>
internal static partial class FfmpegTools
{
    [GeneratedRegex(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    /// <summary>Seconds done, from a progress line "... time=00:01:02.50 ...".</summary>
    public static double? TryTime(string line) =>
        TimeRegex().Match(line) is { Success: true } m ? Seconds(m) : null;

    private static double Seconds(Match m) =>
        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
        + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
        + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

    /// <summary>Length and kinds of streams of a media file (<c>ffmpeg -i</c> prints them to stderr and exits with an error, which is fine).</summary>
    public static async Task<(double? Seconds, bool HasAudio, bool HasVideo)> ProbeAsync(string ffmpeg, string path, CancellationToken cancel)
    {
        double? seconds = null;
        bool audio = false, video = false;
        await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", path], cancel, line =>
        {
            if (DurationRegex().Match(line) is { Success: true } m)
                seconds = Seconds(m);
            if (line.Contains("Stream #", StringComparison.Ordinal))
            {
                if (line.Contains(": Audio:", StringComparison.Ordinal))
                    audio = true;
                else if (line.Contains(": Video:", StringComparison.Ordinal) && !line.Contains("attached pic", StringComparison.Ordinal))
                    video = true;
            }
        });
        return (seconds, audio, video);
    }

    /// <summary>"mm:ss" or "h:mm:ss" for the row of a list.</summary>
    public static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}

/// <summary>Encoder arguments by file extension, used where a result has to be re-encoded.</summary>
internal static class AudioCodecs
{
    public static string[]? For(string ext) => ext.ToLowerInvariant() switch
    {
        "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
        "m4a" or "m4b" or "aac" => ["-c:a", "aac", "-b:a", "192k"],
        "mp2" => ["-c:a", "mp2", "-b:a", "192k"],
        "ac3" => ["-c:a", "ac3", "-b:a", "192k"],
        "weba" => ["-c:a", "libopus", "-b:a", "128k"],
        "mka" => ["-c:a", "flac"],
        "caf" => ["-c:a", "pcm_s16le"],
        "au" => ["-c:a", "pcm_s16be"],
        "wav" => ["-c:a", "pcm_s16le"],
        "flac" => ["-c:a", "flac"],
        "ogg" or "oga" => ["-c:a", "libvorbis", "-q:a", "5"],
        "opus" => ["-c:a", "libopus", "-b:a", "128k"],
        "wma" => ["-c:a", "wmav2", "-b:a", "192k"],
        "aiff" or "aif" => ["-c:a", "pcm_s16be"],
        _ => null,
    };
}

/// <summary>Encoder arguments for an exact (re-encoded) video cut, by container.</summary>
internal static class VideoCodecs
{
    public static string[]? For(string ext) => ext.ToLowerInvariant() switch
    {
        "mp4" or "m4v" or "f4v" or "mov" or "mkv" or "ts" or "mts" or "m2ts" or "flv" or "3gp" or "3g2" => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"],
        "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"],
        "avi" => ["-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "3"],
        "ogv" => ["-c:v", "libtheora", "-q:v", "7", "-c:a", "libvorbis", "-q:a", "5"],
        "vob" => ["-c:v", "mpeg2video", "-q:v", "3", "-c:a", "mp2", "-b:a", "192k"],
        "wmv" or "asf" => ["-c:v", "wmv2", "-b:v", "4M", "-c:a", "wmav2", "-b:a", "192k"],
        "mpg" or "mpeg" => ["-c:v", "mpeg2video", "-q:v", "3", "-c:a", "mp2", "-b:a", "192k"],
        _ => null,
    };
}

internal static partial class FfmpegRate
{
    [System.Text.RegularExpressions.GeneratedRegex(@"Audio:.*?(\d{4,6}) Hz")]
    private static partial System.Text.RegularExpressions.Regex RateRegex();

    /// <summary>Sample rate of the first sound stream, or 0 when unknown (loudnorm resamples to 192 kHz, so the rate is put back).</summary>
    public static async Task<int> ProbeAsync(string ffmpeg, string path, CancellationToken cancel)
    {
        var rate = 0;
        await Radii.Converters.ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", path], cancel, line =>
        {
            if (rate == 0 && RateRegex().Match(line) is { Success: true } m)
                rate = int.Parse(m.Groups[1].Value);
        });
        return rate;
    }
}
