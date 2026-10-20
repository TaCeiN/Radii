using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Radii.Converters;

/// <summary>
/// Pictures. Common formats go through Windows' own imaging (WIC, no download); everything else (WebP, AVIF, HEIC, RAW, PSD, SVG...)
/// through ImageMagick, fetched on first use.
/// </summary>
internal sealed class ImageConverter : IConverter
{
    private static readonly HashSet<string> WicInputs = Set("png", "jpg", "jpeg", "jpe", "jfif", "bmp", "dib", "gif", "tif", "tiff", "ico");
    private static readonly HashSet<string> WicOutputs = Set("png", "jpg", "bmp", "gif", "tiff", "ico", "pdf");

    private static readonly HashSet<string> Inputs = Set(
        "png", "jpg", "jpeg", "jpe", "jfif", "bmp", "dib", "gif", "tif", "tiff", "ico", "webp", "heic", "heif", "avif", "jxl", "svg", "psd",
        "tga", "dds", "exr", "hdr", "jp2", "pcx", "ppm", "pgm", "pbm", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "pef", "srw");

    private static readonly Format[] All =
    [
        new("png", "PNG", "png"),
        new("jpg", "JPG", "jpg"),
        new("webp", "WebP", "webp"),
        new("bmp", "BMP", "bmp"),
        new("gif", "GIF", "gif"),
        new("tiff", "TIFF", "tiff"),
        new("ico", "ICO", "ico"),
        new("avif", "AVIF", "avif"),
        new("pdf", "PDF", "pdf"),
    ];

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    private static string ExtOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(p => File.Exists(p) && Inputs.Contains(ExtOf(p))))
            return [];
        var same = paths.Select(ExtOf).Distinct().ToList();
        var list = All.Where(f => !(same.Count == 1 && Same(f.Ext, same[0]))).ToList();
        if (paths.Count > 1)
            list.Add(new Format("pdf-merge", "PDF", "pdf", Name: Menus.T("All in one PDF", "Всё в один PDF")));
        return list;
    }

    private static bool Same(string a, string b) => a == b || (a == "jpg" && b is "jpeg" or "jpe" or "jfif") || (a == "tiff" && b == "tif");

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        if (to.Id == "pdf-merge")
        {
            context.StartFile(0, 1, Menus.T("pictures", "картинки"));
            var pages = new List<(byte[] Jpeg, int Width, int Height)>();
            for (var i = 0; i < paths.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                pages.Add(await JpegForPdfAsync(paths[i], context));
                context.Progress((i + 1.0) / paths.Count);
            }
            var output = ConvertContext.NewPath(paths[0], "pdf");
            PdfWriter.Write(output, pages);
            context.Add(output);
            return;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            var output = ConvertContext.NewPath(input, to.Ext);
            if (to.Ext == "pdf")
            {
                var page = await JpegForPdfAsync(input, context);
                PdfWriter.Write(output, [page]);
            }
            else if (WicInputs.Contains(ExtOf(input)) && WicOutputs.Contains(to.Ext))
            {
                await Sta.Run(() =>
                {
                    Wic.Save(Wic.Load(input), to.Ext, output);
                    return 0;
                });
            }
            else
            {
                await MagickAsync(input, output, to.Ext, context);
            }
            context.Add(output);
        }
    }

    private static async Task<(byte[] Jpeg, int Width, int Height)> JpegForPdfAsync(string input, ConvertContext context)
    {
        var source = input;
        string? temp = null;
        if (!WicInputs.Contains(ExtOf(input)))
        {
            temp = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N") + ".png");
            await MagickAsync(input, temp, "png", context);
            source = temp;
        }
        try
        {
            return await Sta.Run(() => Wic.JpegBytes(Wic.Load(source)));
        }
        finally
        {
            if (temp is not null)
                File.Delete(temp);
        }
    }

    private static async Task MagickAsync(string input, string output, string ext, ConvertContext context)
    {
        var magick = await Engines.EnsureAsync(Engine.Magick, context);
        context.Report(Menus.T($"Converting {Path.GetFileName(input)}", $"Конвертирую {Path.GetFileName(input)}"), null);
        var args = new List<string>();
        var inputExt = ExtOf(input);
        if (inputExt == "svg")
        {
            args.AddRange(["-background", "none", "-density", "192"]);
        }
        // First frame/page only, except for formats that can hold an animation.
        args.Add(ext is "gif" or "webp" ? input : input + "[0]");
        args.Add("-auto-orient");
        switch (ext)
        {
            case "jpg":
                args.AddRange(["-background", "white", "-alpha", "remove", "-alpha", "off", "-quality", "92"]);
                break;
            case "webp":
                args.AddRange(["-quality", "90"]);
                break;
            case "avif":
                args.AddRange(["-quality", "60"]);
                break;
            case "bmp":
                args.AddRange(["-background", "white", "-alpha", "remove", "-alpha", "off"]);
                break;
            case "ico":
                args.AddRange(["-background", "none", "-define", "icon:auto-resize=256,128,64,48,32,16"]);
                break;
        }
        args.Add(output);
        var (code, errors) = await ProcessRunner.RunAsync(magick, args, context.Cancel);
        if (code != 0 || !File.Exists(output))
            throw new ConvertException(Menus.T("ImageMagick failed: ", "ImageMagick не справился: ") + ProcessRunner.Reason(errors));
    }
}

/// <summary>Windows Imaging Component helpers (WPF's BitmapDecoder/Encoder). Call from an STA thread.</summary>
internal static class Wic
{
    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static void Save(BitmapSource source, string ext, string output)
    {
        if (ext == "ico")
        {
            File.WriteAllBytes(output, Icon(source));
            return;
        }
        BitmapEncoder encoder = ext switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(ext == "jpg" ? OnWhite(source) : source));
        using var file = File.Create(output);
        encoder.Save(file);
    }

    /// <summary>JPEG with three colour components (what the PDF image dictionary below declares).</summary>
    public static (byte[] Jpeg, int Width, int Height) JpegBytes(BitmapSource source)
    {
        var flat = new FormatConvertedBitmap(OnWhite(source), PixelFormats.Bgr24, null, 0);
        flat.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return (stream.ToArray(), flat.PixelWidth, flat.PixelHeight);
    }

    /// <summary>JPEG and BMP have no (usable) alpha: composite on white like an image viewer would.</summary>
    private static BitmapSource OnWhite(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgr24 || source.Format == PixelFormats.Bgr32)
            return source;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Single-image .ico with a PNG payload (supported since Vista), scaled down to 256 px if larger.</summary>
    private static byte[] Icon(BitmapSource source)
    {
        var scale = Math.Min(1.0, 256.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource image = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        using var result = new MemoryStream();
        using var writer = new BinaryWriter(result);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(image.PixelWidth >= 256 ? 0 : image.PixelWidth));
        writer.Write((byte)(image.PixelHeight >= 256 ? 0 : image.PixelHeight));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)png.Length);
        writer.Write((uint)22);
        writer.Write(png.ToArray());
        writer.Flush();
        return result.ToArray();
    }
}

/// <summary>Minimal PDF with one JPEG per page (page size = picture size at 96 dpi).</summary>
internal static class PdfWriter
{
    public static void Write(string path, IReadOnlyList<(byte[] Jpeg, int Width, int Height)> pages)
    {
        using var file = File.Create(path);
        var offsets = new List<long>();
        void Put(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        void Begin(int number)
        {
            offsets.Add(file.Position);
            Put($"{number} 0 obj\n");
        }

        Put("%PDF-1.4\n");
        // Objects: 1 catalog, 2 page tree, then three per page (page, content, image).
        Begin(1);
        Put("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        Put($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"))}] >>\nendobj\n");
        for (var i = 0; i < pages.Count; i++)
        {
            var (jpeg, width, height) = pages[i];
            var number = 3 + i * 3;
            var w = (width * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var h = (height * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            Begin(number);
            Put($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 {number + 2} 0 R >> >> /Contents {number + 1} 0 R >>\nendobj\n");
            var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
            Begin(number + 1);
            Put($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            Begin(number + 2);
            Put($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            file.Write(jpeg);
            Put("\nendstream\nendobj\n");
        }
        var xref = file.Position;
        Put($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Put($"{offset:D10} 00000 n \n");
        Put($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
