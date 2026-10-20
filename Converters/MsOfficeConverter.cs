using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Radii.Converters;

/// <summary>
/// Word, Excel and PowerPoint files through the installed Microsoft Office itself (COM automation): the same engine that made
/// the file lays it out, so the PDF or the converted file is identical to what Office shows. Offered only when the
/// matching Office program is installed; otherwise LibreOffice (<see cref="OfficeConverter"/>) takes over.
/// </summary>
internal sealed class MsOfficeConverter : IConverter
{
    private enum App
    {
        Word,
        Excel,
        PowerPoint,
    }

    private static readonly Dictionary<string, App> Inputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doc"] = App.Word, ["docx"] = App.Word, ["docm"] = App.Word, ["dot"] = App.Word, ["dotx"] = App.Word, ["rtf"] = App.Word, ["odt"] = App.Word,
        ["xls"] = App.Excel, ["xlsx"] = App.Excel, ["xlsm"] = App.Excel, ["xlsb"] = App.Excel, ["ods"] = App.Excel,
        ["ppt"] = App.PowerPoint, ["pptx"] = App.PowerPoint, ["pptm"] = App.PowerPoint, ["pps"] = App.PowerPoint, ["ppsx"] = App.PowerPoint, ["odp"] = App.PowerPoint,
    };

    private static readonly Dictionary<App, string[]> Outputs_ = new()
    {
        [App.Word] = ["pdf", "docx", "doc", "odt", "rtf"],
        [App.Excel] = ["pdf", "xlsx", "xls", "ods", "csv"],
        [App.PowerPoint] = ["pdf", "pptx", "ppt", "odp", "png"],
    };

    private static readonly Dictionary<App, (string ProgId, string Process)> Programs = new()
    {
        [App.Word] = ("Word.Application", "WINWORD"),
        [App.Excel] = ("Excel.Application", "EXCEL"),
        [App.PowerPoint] = ("PowerPoint.Application", "POWERPNT"),
    };

    private static readonly Dictionary<App, bool> InstalledCache = [];

    private static bool Installed(App app)
    {
        lock (InstalledCache)
        {
            if (!InstalledCache.TryGetValue(app, out var installed))
                InstalledCache[app] = installed = Type.GetTypeFromProgID(Programs[app].ProgId) is not null;
            return installed;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    /// <summary>The instance the user is working in (registered in the running object table), or null.</summary>
    private static object? RunningInstance(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0)
            return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var instance) == 0 ? instance : null;
    }

    private static string ExtOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(p => File.Exists(p) && Inputs.ContainsKey(ExtOf(p))))
            return [];
        var apps = paths.Select(p => Inputs[ExtOf(p)]).Distinct().ToList();
        if (apps.Count != 1 || !Installed(apps[0]))
            return [];
        var exts = paths.Select(ExtOf).Distinct().ToList();
        return Outputs_[apps[0]]
            .Where(o => !(exts.Count == 1 && exts[0] == o))
            .Select(o => new Format(o, o.ToUpperInvariant(), o, Name: o == "png" ? Menus.T("Slides → PNG", "Слайды → PNG") : null))
            .ToList();
    }

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            context.Progress(null);
            var app = Inputs[ExtOf(input)];
            var output = to.Ext == "png" ? Menus.UniquePath(Path.GetDirectoryName(input) ?? "", Path.GetFileNameWithoutExtension(input) + Menus.T(" (slides)", " (слайды)"), "") : ConvertContext.NewPath(input, to.Ext);
            try
            {
                // COM needs an STA thread; Office keeps doing its own work out of process.
                await Sta.Run(() =>
                {
                    Convert(app, input, output, to.Ext);
                    return 0;
                });
            }
            catch (COMException ex)
            {
                if (File.Exists(output))
                    File.Delete(output);
                throw new ConvertException(Menus.T($"{Programs[app].ProgId.Split('.')[0]} could not convert {Path.GetFileName(input)} (password protected or damaged?): ", $"{Programs[app].ProgId.Split('.')[0]} не смог конвертировать {Path.GetFileName(input)} (пароль или повреждён?): ") + ex.Message.Trim(), ex);
            }
            context.Add(output);
        }
    }

    private static void Convert(App app, string input, string output, string ext)
    {
        var progId = Programs[app].ProgId;
        // If the user already has the program open, borrow that instance and leave it (and its settings) alone;
        // otherwise start our own hidden one and quit it afterwards.
        var running = RunningInstance(progId);
        var borrowed = running is not null;
        dynamic? program = running ?? Activator.CreateInstance(Type.GetTypeFromProgID(progId)!);
        try
        {
            switch (app)
            {
                case App.Word:
                    Word(program!, borrowed, input, output, ext);
                    break;
                case App.Excel:
                    Excel(program!, borrowed, input, output, ext);
                    break;
                default:
                    PowerPoint(program!, input, output, ext);
                    break;
            }
        }
        finally
        {
            try
            {
                if (!borrowed)
                    program!.Quit();
            }
            catch (COMException)
            {
            }
            if (program is not null)
                Marshal.FinalReleaseComObject(program);
        }
    }

    private static void Word(dynamic word, bool borrowed, string input, string output, string ext)
    {
        // The user's own Word keeps its window; only the document we open is hidden.
        if (!borrowed)
            word.Visible = false;
        var alerts = word.DisplayAlerts;
        word.DisplayAlerts = 0; // wdAlertsNone
        // A dummy password turns the "enter password" dialog (which would block forever) into an error.
        dynamic doc = word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, PasswordDocument: "\u0001", Visible: false, NoEncodingDialog: true);
        try
        {
            if (ext == "pdf")
            {
                // wdExportFormatPDF = 17, wdExportOptimizeForPrint = 0, wdExportCreateHeadingBookmarks = 1
                doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17, OpenAfterExport: false, OptimizeFor: 0, CreateBookmarks: 1, DocStructureTags: true, BitmapMissingFonts: true);
            }
            else
            {
                var format = ext switch { "docx" => 16, "doc" => 0, "odt" => 23, "rtf" => 6, _ => 16 };
                doc.SaveAs2(FileName: output, FileFormat: format, AddToRecentFiles: false);
            }
        }
        finally
        {
            doc.Close(false);
            Marshal.FinalReleaseComObject(doc);
            if (borrowed)
                word.DisplayAlerts = alerts;
        }
    }

    private static void Excel(dynamic excel, bool borrowed, string input, string output, string ext)
    {
        if (!borrowed)
            excel.Visible = false;
        var alerts = excel.DisplayAlerts;
        excel.DisplayAlerts = false;
        dynamic book = excel.Workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, Password: "\u0001", IgnoreReadOnlyRecommended: true, AddToMru: false);
        string? printer = null;
        try
        {
            if (ext == "pdf")
            {
                printer = UsePdfPrinter(excel);
                // xlTypePDF = 0, xlQualityStandard = 0
                book.ExportAsFixedFormat(Type: 0, Filename: output, Quality: 0, IncludeDocProperties: true, IgnorePrintAreas: false, OpenAfterPublish: false);
            }
            else
            {
                // xlOpenXMLWorkbook 51, xlExcel8 56, xlOpenDocumentSpreadsheet 60, xlCSVUTF8 62
                var format = ext switch { "xlsx" => 51, "xls" => 56, "ods" => 60, "csv" => 62, _ => 51 };
                book.SaveAs(Filename: output, FileFormat: format);
            }
        }
        finally
        {
            if (printer is not null)
            {
                try
                {
                    excel.ActivePrinter = printer; // needs a workbook, so before it is closed
                }
                catch (COMException)
                {
                }
            }
            book.Close(false);
            Marshal.FinalReleaseComObject(book);
            if (borrowed)
                excel.DisplayAlerts = alerts;
        }
    }

    /// <summary>
    /// Excel lays a sheet out with the metrics of its active printer. A text-only, offline or odd default printer gives broken
    /// spacing and drops fills, borders, charts and pictures from the PDF, so the export switches to "Microsoft Print to PDF"
    /// (a deterministic page) and the caller puts the old printer back. Returns the previous printer, or null when nothing changed.
    /// </summary>
    private static string? UsePdfPrinter(dynamic excel)
    {
        string previous;
        try
        {
            previous = excel.ActivePrinter;
        }
        catch (COMException)
        {
            return null;
        }
        if (previous.StartsWith("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase))
            return null;
        // Excel names a printer "<name> on Ne01:" ("<name> на Ne01:" with some language packs); the port number is not known upfront.
        foreach (var word in new[] { "on", "на" })
        {
            for (var port = 0; port < 100; port++)
            {
                try
                {
                    excel.ActivePrinter = $"Microsoft Print to PDF {word} Ne{port:00}:";
                    return previous;
                }
                catch (COMException)
                {
                }
            }
        }
        return null;
    }

    private static void PowerPoint(dynamic powerPoint, string input, string output, string ext)
    {
        // msoTrue = -1, msoFalse = 0; the window stays hidden.
        dynamic presentation = powerPoint.Presentations.Open(FileName: input, ReadOnly: -1, Untitled: 0, WithWindow: 0);
        try
        {
            if (ext == "png")
            {
                Directory.CreateDirectory(output);
                presentation.Export(output, "PNG");
            }
            else
            {
                // ppSaveAsPDF 32, ppSaveAsOpenXMLPresentation 24, ppSaveAsPresentation 1, ppSaveAsOpenDocumentPresentation 35
                var format = ext switch { "pdf" => 32, "pptx" => 24, "ppt" => 1, "odp" => 35, _ => 24 };
                presentation.SaveAs(output, format);
            }
        }
        finally
        {
            presentation.Close();
            Marshal.FinalReleaseComObject(presentation);
        }
    }
}
