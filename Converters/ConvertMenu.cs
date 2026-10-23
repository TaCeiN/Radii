using System.IO;

namespace Radii.Converters;

/// <summary>Lets a sector open another ring (set by Program once the window exists).</summary>
internal static class ConvertHost
{
    public static Action<IReadOnlyList<RadialItem>, string, ShellContext>? Show { get; set; }
}

/// <summary>Which conversions exist for a selection, and the ring that offers them.</summary>
internal static class ConvertMenu
{
    private static readonly IConverter[] Converters =
    [
        new ImageConverter(),
        new PdfConverter(),
        new MediaConverter(),
        new MsOfficeConverter(),
        new DataConverter(),
        new OfficeConverter(),
        new DocumentConverter(),
        new ArchiveConverter(),
        new EncodingConverter(),
    ];

    private sealed record Offer(Format Format, IConverter Converter);

    private static List<Offer> OffersFor(IReadOnlyList<string> paths)
    {
        var offers = new List<Offer>();
        if (paths.Count == 0)
            return offers;
        foreach (var converter in Converters)
        {
            foreach (var format in converter.Outputs(paths))
            {
                if (offers.All(o => o.Format.Id != format.Id))
                    offers.Add(new Offer(format, converter));
            }
        }
        return offers;
    }

    /// <summary>Headless run for diagnostics (<c>--convert</c>): formats by id, including those inside submenus.</summary>
    public static async Task<string> RunHeadlessAsync(IReadOnlyList<string> paths, string id)
    {
        var offers = OffersFor(paths);
        var flat = offers.SelectMany(o => Flatten(o.Format).Select(f => new Offer(f, o.Converter))).ToList();
        if (flat.FirstOrDefault(o => o.Format.Id == id) is not { } offer)
            return "no such format: " + id + "; offered: " + string.Join(", ", flat.Where(o => o.Format.Children is null).Select(o => o.Format.Id));
        var log = new List<string>();
        var context = new ConvertContext(CancellationToken.None, (text, progress) => log.Add($"{progress:P0} {text}"));
        try
        {
            await offer.Converter.ConvertAsync(paths, offer.Format, context);
        }
        catch (ConvertException ex)
        {
            return "FAILED: " + ex.Message;
        }
        return "OK: " + string.Join(" | ", context.Outputs) + Environment.NewLine + string.Join(Environment.NewLine, log.TakeLast(5));
    }

    private static IEnumerable<Format> Flatten(Format format) =>
        format.Children is null ? [format] : format.Children.SelectMany(Flatten);

    public static bool Supports(IReadOnlyList<string> paths) => OffersFor(paths).Count > 0;

    /// <summary>The ring for a selection, or an empty list when nothing can be converted.</summary>
    public static List<RadialItem> For(IReadOnlyList<string> paths)
    {
        var offers = OffersFor(paths);
        if (offers.Count == 0)
            return [];
        var context = new ShellContext(paths, null, IntPtr.Zero);
        var summary = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : Menus.T($"{paths.Count} files", $"Файлов: {paths.Count}");
        if (offers.Count > Menus.MaxSectors)
        {
            var rest = offers.Skip(Menus.MaxSectors - 1).ToList();
            offers = offers.Take(Menus.MaxSectors - 1).ToList();
            offers.Add(new Offer(new Format("more", "…", "", rest.Select(r => r.Format).ToList(), Glyph: "", Name: Menus.T("More…", "Ещё…")), new MoreConverter(rest)));
        }
        return Items(offers, paths, summary, context);
    }

    private static List<RadialItem> Items(IReadOnlyList<Offer> offers, IReadOnlyList<string> paths, string summary, ShellContext context)
    {
        var items = new List<RadialItem>();
        foreach (var offer in offers)
        {
            var format = offer.Format;
            var converter = offer.Converter;
            if (format.Children is { } children)
            {
                var sub = children.Select(c => new Offer(c, converter is MoreConverter more ? more.ConverterFor(c) : converter)).ToList();
                // A group sector: the window opens its ring (with "Back"), and the search looks inside it.
                items.Add(new RadialItem("conv:" + format.Id, format.Glyph ?? format.Label, format.Caption, () => { }, Navigates: true,
                    Children: Items(sub, paths, format.Caption, context)));
            }
            else
            {
                items.Add(new RadialItem("conv:" + format.Id, format.Glyph ?? format.Label, Menus.T("To ", "В ") + format.Caption,
                    () => ConvertJobs.Start(paths, converter, format)));
            }
        }
        return WithBack(items);
    }

    /// <summary>A ring needs at least two sectors to be drawn; with one, add a harmless "Cancel".</summary>
    private static List<RadialItem> WithBack(List<RadialItem> items)
    {
        if (items.Count < 2)
            items.Add(new RadialItem("conv:cancel", "", Menus.T("Cancel", "Отмена"), () => { }));
        return items;
    }

    /// <summary>Formats pushed into the "More…" ring keep their own converter.</summary>
    private sealed class MoreConverter(List<Offer> rest) : IConverter
    {
        public IConverter ConverterFor(Format format) => rest.First(r => r.Format.Id == format.Id).Converter;

        public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths) => [];

        public Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context) => Task.CompletedTask;
    }
}

internal static class ConvertJobs
{
    public static void Start(IReadOnlyList<string> paths, IConverter converter, Format format) => _ = RunAsync(paths, converter, format);

    private static async Task RunAsync(IReadOnlyList<string> paths, IConverter converter, Format format)
    {
        using var cancel = new CancellationTokenSource();
        var first = Path.GetFileName(paths[0].TrimEnd('\\'));
        var title = paths.Count == 1
            ? $"{first} → {format.Caption}"
            : Menus.T($"{paths.Count} files → {format.Caption}", $"Файлов: {paths.Count} → {format.Caption}");
        var toast = Toast.Begin(title, Menus.T("Starting…", "Запуск…"), cancel);
        var last = "";
        var context = new ConvertContext(cancel.Token, (text, progress) =>
        {
            if (text.Length > 0)
                last = text;
            toast.Update(last, progress);
        });
        try
        {
            await converter.ConvertAsync(paths, format, context);
            var output = context.Outputs.LastOrDefault();
            toast.Done(context.Outputs.Count switch
            {
                0 => Menus.T("Done", "Готово"),
                1 => Menus.T("Saved ", "Сохранено: ") + Path.GetFileName(output),
                var n => Menus.T($"Saved {n} files", $"Сохранено файлов: {n}"),
            }, output);
        }
        catch (OperationCanceledException)
        {
            toast.Fail(Menus.T("Cancelled", "Отменено"));
        }
        catch (ConvertException ex)
        {
            toast.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            toast.Fail(Menus.T("Failed: ", "Ошибка: ") + ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.LogCrash(ex);
            toast.Fail(Menus.T("Unexpected error: ", "Неожиданная ошибка: ") + ex.Message);
        }
    }
}
