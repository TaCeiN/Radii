using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Windows.Media.Imaging;

namespace Radii.Converters;

/// <summary>
/// Text documents through Pandoc (downloaded on first use). PDF output is HTML printed by Edge/Chrome (no LaTeX needed).
/// </summary>
internal sealed class DocumentConverter : IConverter
{
    private static readonly HashSet<string> Inputs = new(["md", "markdown", "txt", "html", "htm", "docx", "odt", "rtf", "epub", "tex", "rst", "org", "textile"], StringComparer.OrdinalIgnoreCase);

    private static readonly Format[] All =
    [
        new("pdf", "PDF", "pdf"),
        new("docx", "DOCX", "docx"),
        new("html", "HTML", "html"),
        new("md", "MD", "md"),
        new("txt", "TXT", "txt"),
        new("epub", "EPUB", "epub"),
        new("odt", "ODT", "odt"),
        new("rtf", "RTF", "rtf"),
    ];

    private static string ExtOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(p => File.Exists(p) && Inputs.Contains(ExtOf(p))))
            return [];
        var exts = paths.Select(ExtOf).Distinct().ToList();
        return All.Where(f => !(exts.Count == 1 && (exts[0] == f.Ext || (exts[0] is "htm" && f.Ext == "html") || (exts[0] is "markdown" && f.Ext == "md")))).ToList();
    }

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        var pandoc = await Engines.EnsureAsync(Engine.Pandoc, context);
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            context.Progress(null);
            var output = ConvertContext.NewPath(input, to.Ext);
            if (to.Ext == "pdf")
                await ToPdfAsync(pandoc, input, output, context);
            else
                await RunPandocAsync(pandoc, input, output, to.Ext, context, extra: []);
            context.Add(output);
        }
    }

    private static async Task RunPandocAsync(string pandoc, string input, string output, string ext, ConvertContext context, IEnumerable<string> extra)
    {
        var args = new List<string> { input, "-o", output, "--standalone" };
        var from = ExtOf(input) switch
        {
            "txt" => "markdown+hard_line_breaks-auto_identifiers",
            "md" or "markdown" => "gfm",
            _ => null,
        };
        if (from is not null)
            args.AddRange(["-f", from]);
        var target = ext switch
        {
            "md" => "gfm",
            "txt" => "plain",
            _ => null,
        };
        if (target is not null)
            args.AddRange(["-t", target]);
        if (ext is "txt" or "md")
            args.Remove("--standalone");
        args.AddRange(["--metadata", "pagetitle=" + Path.GetFileNameWithoutExtension(input)]);
        // Pictures next to the source are looked up from there.
        args.AddRange(["--resource-path", Path.GetDirectoryName(input) ?? "."]);
        args.AddRange(extra);
        var (code, errors) = await ProcessRunner.RunAsync(pandoc, args, context.Cancel);
        if (code != 0 || !File.Exists(output))
            throw new ConvertException(Menus.T("Pandoc failed: ", "Pandoc не справился: ") + ProcessRunner.Reason(errors));
    }

    private static async Task ToPdfAsync(string pandoc, string input, string output, ConvertContext context)
    {
        var browser = Engines.FindBrowser() ?? throw new ConvertException(Menus.T("Edge or Chrome is needed to make PDFs", "Для PDF нужен Edge или Chrome"));
        var work = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var html = Path.Combine(work, "doc.html");
            var css = Path.Combine(work, "style.css");
            await File.WriteAllTextAsync(css, "body{font-family:'Segoe UI',Arial,sans-serif;font-size:11pt;line-height:1.5;max-width:none;margin:0;padding:0}"
                + "pre,code{font-family:Consolas,monospace;background:#f4f4f4}pre{padding:8px;white-space:pre-wrap}img{max-width:100%}"
                + "table{border-collapse:collapse}td,th{border:1px solid #bbb;padding:4px 8px}blockquote{border-left:3px solid #ccc;margin-left:0;padding-left:12px;color:#555}", context.Cancel);
            await RunPandocAsync(pandoc, input, html, "html", context, ["--embed-resources", "--css", css]);

            var profile = Path.Combine(work, "profile");
            var uri = new Uri(html).AbsoluteUri;
            var (_, errors) = await ProcessRunner.RunAsync(browser,
                ["--headless=new", "--disable-gpu", "--no-first-run", "--no-pdf-header-footer", "--user-data-dir=" + profile, "--print-to-pdf=" + output, uri],
                context.Cancel);
            if (!File.Exists(output))
                throw new ConvertException(Menus.T("The browser did not produce a PDF: ", "Браузер не создал PDF: ") + ProcessRunner.Reason(errors));
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Office files (Word/Excel/PowerPoint and their OpenDocument twins) through LibreOffice, installed with winget if missing.</summary>
internal sealed class OfficeConverter : IConverter
{
    private static readonly HashSet<string> Text = new(["doc", "docx", "odt", "rtf"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Sheet = new(["xls", "xlsx", "ods", "csv"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Slides = new(["ppt", "pptx", "odp"], StringComparer.OrdinalIgnoreCase);

    private static string ExtOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(File.Exists))
            return [];
        var exts = paths.Select(ExtOf).ToList();
        string[] wanted;
        if (exts.All(Text.Contains))
            wanted = ["pdf", "docx", "doc", "odt", "rtf"];
        else if (exts.All(Sheet.Contains))
            wanted = exts.All(e => e == "csv") ? ["xlsx", "ods", "pdf"] : ["pdf", "xlsx", "ods", "csv"];
        else if (exts.All(Slides.Contains))
            wanted = ["pdf", "pptx", "odp", "png"];
        else
            return [];
        var distinct = exts.Distinct().ToList();
        return wanted.Where(w => !(distinct.Count == 1 && distinct[0] == w)).Select(w => new Format(w, w.ToUpperInvariant(), w)).ToList();
    }

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        var soffice = await Engines.EnsureLibreOfficeAsync(context);
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            context.Progress(null);
            var work = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                // Own profile folder: a running LibreOffice would otherwise swallow the headless request.
                var (_, errors) = await ProcessRunner.RunAsync(soffice,
                    ["-env:UserInstallation=" + new Uri(Path.Combine(work, "profile")).AbsoluteUri, "--headless", "--convert-to", to.Ext, "--outdir", work, input], context.Cancel);
                var produced = Directory.EnumerateFiles(work, Path.GetFileNameWithoutExtension(input) + "*." + to.Ext).FirstOrDefault();
                if (produced is null)
                    throw new ConvertException(Menus.T("LibreOffice failed: ", "LibreOffice не справился: ") + ProcessRunner.Reason(errors));
                var output = ConvertContext.NewPath(input, to.Ext);
                File.Move(produced, output);
                context.Add(output);
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

/// <summary>PDF pages to pictures with the PDF renderer built into Windows 10/11.</summary>
internal sealed class PdfConverter : IConverter
{
    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => File.Exists(p) && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            ? [new Format("png", "PNG", "png"), new Format("jpg", "JPG", "jpg")]
            : [];

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            PdfDocument document;
            try
            {
                document = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(input));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new ConvertException(Menus.T("Could not open the PDF (damaged or password protected)", "Не удалось открыть PDF (повреждён или с паролем)"), ex);
            }
            var pages = (int)document.PageCount;
            for (var page = 0; page < pages; page++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                using var pdfPage = document.GetPage((uint)page);
                using var memory = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, pdfPage.Size.Width * 2), BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
                await pdfPage.RenderToStreamAsync(memory, options);
                memory.Seek(0);
                var bytes = new byte[memory.Size];
                using (var reader = new DataReader(memory))
                {
                    await reader.LoadAsync((uint)memory.Size);
                    reader.ReadBytes(bytes);
                }
                var suffix = pages > 1 ? $" ({page + 1})" : "";
                var output = ConvertContext.NewPath(input, to.Ext, suffix);
                await Sta.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    Wic.Save(decoder.Frames[0], to.Ext, output);
                    return 0;
                });
                context.Add(output);
                context.Progress((page + 1.0) / pages);
            }
        }
    }
}

/// <summary>Re-saves a text file in another encoding or with other line endings (always as a new file).</summary>
internal sealed class EncodingConverter : IConverter
{
    private static readonly HashSet<string> Inputs = new(
        ["txt", "md", "csv", "tsv", "log", "json", "xml", "html", "htm", "css", "js", "ts", "ini", "cfg", "conf", "yaml", "yml", "toml", "srt", "sub", "vtt", "tex", "rst",
         "bat", "cmd", "ps1", "cs", "py", "java", "c", "h", "cpp", "sql", "php", "rb", "go", "rs", "sh", "inf", "reg", "lrc", "nfo"], StringComparer.OrdinalIgnoreCase);

    static EncodingConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Format[] Children =
    [
        new("enc:utf8", "UTF-8", "", Name: "UTF-8"),
        new("enc:utf8bom", "UTF-8 BOM", "", Name: "UTF-8 BOM"),
        new("enc:utf16", "UTF-16", "", Name: "UTF-16 LE"),
        new("enc:1251", "1251", "", Name: "Windows-1251"),
        new("enc:866", "866", "", Name: "CP866 (DOS)"),
        new("enc:koi8r", "KOI8-R", "", Name: "KOI8-R"),
        new("eol:lf", "LF", "", Name: Menus.T("Line endings LF (Unix)", "Переводы строк LF (Unix)")),
        new("eol:crlf", "CRLF", "", Name: Menus.T("Line endings CRLF (Windows)", "Переводы строк CRLF (Windows)")),
    ];

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => File.Exists(p) && Inputs.Contains(Path.GetExtension(p).TrimStart('.')))
            ? [new Format("encoding", "Aa", "", Children, Glyph: "", Name: Menus.T("Encoding…", "Кодировка…"))]
            : [];

    public Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context) => Task.Run(() =>
    {
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            var bytes = File.ReadAllBytes(input);
            var (text, source) = Decode(bytes);
            var extension = Path.GetExtension(input).TrimStart('.');
            string suffix;
            Encoding target = source;
            bool bom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF) || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
            switch (to.Id)
            {
                case "enc:utf8": target = new UTF8Encoding(false); bom = false; suffix = " (UTF-8)"; break;
                case "enc:utf8bom": target = new UTF8Encoding(true); bom = true; suffix = " (UTF-8 BOM)"; break;
                case "enc:utf16": target = new System.Text.UnicodeEncoding(false, true); bom = true; suffix = " (UTF-16)"; break;
                case "enc:1251": target = Encoding.GetEncoding(1251); bom = false; suffix = " (1251)"; break;
                case "enc:866": target = Encoding.GetEncoding(866); bom = false; suffix = " (866)"; break;
                case "enc:koi8r": target = Encoding.GetEncoding("koi8-r"); bom = false; suffix = " (KOI8-R)"; break;
                case "eol:lf": text = Regex.Replace(text, "\r\n?", "\n"); suffix = " (LF)"; break;
                case "eol:crlf": text = Regex.Replace(text, "\r\n?|\n", "\r\n"); suffix = " (CRLF)"; break;
                default: throw new ConvertException("?");
            }
            var output = ConvertContext.NewPath(input, extension, suffix);
            using (var file = File.Create(output))
            {
                if (bom)
                    file.Write(target.GetPreamble());
                var body = target.GetBytes(text);
                file.Write(body);
            }
            context.Add(output);
        }
    }, context.Cancel);

    /// <summary>BOM wins; otherwise strict UTF-8; otherwise the system ANSI code page (1251 on a Russian Windows).</summary>
    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (ansi.GetString(bytes), ansi);
        }
    }
}
