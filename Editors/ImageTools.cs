using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Radii.Editors;

/// <summary>Picture helpers shared by the photo editors (all results are frozen, so any thread may use them).</summary>
internal static class ImageTools
{
    /// <summary>The picture turned the way its EXIF orientation says (what a viewer shows).</summary>
    public static BitmapSource Oriented(BitmapSource frame) => Oriented(frame, CropWindow.StateFromExif(frame));

    /// <summary>Applies a known orientation (mirror first, then rotate) to <paramref name="image"/>.</summary>
    public static BitmapSource Oriented(BitmapSource image, (bool Flip, int Rotation) state)
    {
        var group = new TransformGroup();
        if (state.Flip)
            group.Children.Add(new ScaleTransform(-1, 1));
        if (state.Rotation != 0)
            group.Children.Add(new RotateTransform(state.Rotation));
        if (group.Children.Count == 0)
            return image;
        var transformed = new TransformedBitmap(image, group);
        transformed.Freeze();
        return transformed;
    }

    /// <summary>Alpha composited on white, three colour channels: what JPEG and BMP can hold.</summary>
    public static BitmapSource OnWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var flat = new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);
        flat.Freeze();
        return flat;
    }

    public static byte[] Jpeg(BitmapSource source, int quality, BitmapMetadata? metadata = null)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(metadata is null ? BitmapFrame.Create(source) : BitmapFrame.Create(source, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>High quality resampling to an exact size.</summary>
    public static BitmapSource Scaled(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, width, height));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>The raw 32-bit pixels (B, G, R, A, not premultiplied) of a picture.</summary>
    public static (byte[] Pixels, int Width, int Height) ToBgra(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    public static BitmapSource FromBgra(byte[] pixels, int width, int height, double dpiX = 96, double dpiY = 96)
    {
        var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>"4,2 MB", "380 KB".</summary>
    public static string Size(long bytes) => bytes >= 1_000_000
        ? (bytes / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB"
        : Math.Max(1, (int)Math.Round(bytes / 1000.0)) + " KB";
}
