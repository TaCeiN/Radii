using System.Diagnostics;
using System.IO;
using System.Text;

namespace Radii.Converters;

/// <summary>An output the ring can offer. A format with <see cref="Children"/> opens a second ring instead of converting.</summary>
/// <param name="Id">Unique among the formats of one selection: "png", "audio:mp3", ...</param>
/// <param name="Label">Short text on the sector ("PNG").</param>
/// <param name="Ext">Extension of the produced file without the dot.</param>
internal sealed record Format(string Id, string Label, string Ext, IReadOnlyList<Format>? Children = null, string? Glyph = null, string? Name = null)
{
    /// <summary>Sector caption in the middle of the ring.</summary>
    public string Caption => Name ?? Label;
}

internal interface IConverter
{
    /// <summary>Formats offered when every one of <paramref name="paths"/> is readable by this converter; empty otherwise.</summary>
    IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths);

    Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context);
}

/// <summary>A failure whose message is fit to show to the user as it is.</summary>
internal sealed class ConvertException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class ConvertContext(CancellationToken cancel, Action<string, double?> report)
{
    private int _index;
    private int _count = 1;

    public CancellationToken Cancel { get; } = cancel;

    /// <summary>Files written so far; the last one is revealed when the job ends.</summary>
    public List<string> Outputs { get; } = [];

    public void StartFile(int index, int count, string name)
    {
        _index = index;
        _count = Math.Max(1, count);
        Report(Menus.T($"Converting {name}", $"Конвертирую {name}"), 0);
    }

    /// <summary>Progress of the current file, 0..1, or null when unknown.</summary>
    public void Report(string text, double? fraction)
    {
        double? overall = fraction is { } f ? (_index + Math.Clamp(f, 0, 1)) / _count : null;
        report(text, overall);
    }

    public void Progress(double? fraction)
    {
        double? overall = fraction is { } f ? (_index + Math.Clamp(f, 0, 1)) / _count : null;
        report("", overall);
    }

    /// <summary>A free path next to the source: "name.ext", "name (2).ext", ...</summary>
    public static string NewPath(string source, string ext, string suffix = "")
    {
        var folder = Path.GetDirectoryName(source) ?? "";
        var name = Path.GetFileNameWithoutExtension(source);
        if (source.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        return Menus.UniquePath(folder, name + suffix, "." + ext.TrimStart('.'));
    }

    public string Add(string output)
    {
        Outputs.Add(output);
        return output;
    }
}

internal static class Sta
{
    /// <summary>Runs <paramref name="work"/> on a fresh STA thread (WPF imaging and COM are happier there).</summary>
    public static Task<T> Run<T>(Func<T> work)
    {
        var source = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                source.SetResult(work());
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Radii convert",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return source.Task;
    }
}

internal static class ProcessRunner
{
    /// <summary>Runs a console program without a window. Lines of stderr (split on \r and \n) go to <paramref name="onLine"/>. Returns the exit code.</summary>
    public static async Task<(int Code, string Errors)> RunAsync(string exe, IEnumerable<string> args, CancellationToken cancel, Action<string>? onLine = null, string? workingDirectory = null, Action<string>? onOutput = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConvertException(Menus.T($"Could not start {Path.GetFileName(exe)}: ", $"Не удалось запустить {Path.GetFileName(exe)}: ") + ex.Message, ex);
        }

        var tail = new StringBuilder();
        var errors = Pump(process.StandardError, line =>
        {
            tail.AppendLine(line);
            if (tail.Length > 6000)
                tail.Remove(0, tail.Length - 4000);
            onLine?.Invoke(line);
        });
        var output = Pump(process.StandardOutput, line => onOutput?.Invoke(line));

        using var registration = cancel.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        });
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(errors, output);
        cancel.ThrowIfCancellationRequested();
        return (process.ExitCode, tail.ToString().Trim());
    }

    private static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        var line = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is '\r' or '\n')
                {
                    if (line.Length > 0)
                        onLine(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0)
            onLine(line.ToString());
    }

    /// <summary>Last meaningful line of a tool's error output, for the message shown to the user.</summary>
    public static string Reason(string errors)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
