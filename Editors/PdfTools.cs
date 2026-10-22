using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Radii.Editors;

/// <summary>Reading side of PDFs for the editors (page count and thumbnails through the Windows PDF renderer).</summary>
internal static class PdfTools
{
    public static async Task<Windows.Data.Pdf.PdfDocument> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        return await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
    }

    /// <summary>A frozen picture of one page, <paramref name="height"/> pixels tall.</summary>
    public static async Task<BitmapImage> RenderAsync(Windows.Data.Pdf.PdfDocument document, int index, uint height)
    {
        using var page = document.GetPage((uint)index);
        using var memory = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(memory, new PdfPageRenderOptions { DestinationHeight = height, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) });
        memory.Seek(0);
        var bytes = new byte[memory.Size];
        using (var reader = new DataReader(memory))
        {
            await reader.LoadAsync((uint)memory.Size);
            reader.ReadBytes(bytes);
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static bool IsReadError(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Windows.Markup.XamlParseException or FileFormatException;
}
