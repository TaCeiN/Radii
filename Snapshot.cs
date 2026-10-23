using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Radii;

/// <summary>Offscreen renders for checking the look without the hook: <c>--snapshot out.png</c>, <c>--snapshot-flyout out.png path</c>.</summary>
internal static class Snapshot
{
    public static int Render(string path)
    {
        const string docs = @"C:\Users\me\Documents";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var contexts = new (Detected Context, int Hover)[]
        {
            (new Detected(Surface.Explorer, 1, docs, [docs + @"\setup.exe"]), 1),
            (new Detected(Surface.Explorer, 1, docs, [docs + @"\photo.png"]), 1),
            (new Detected(Surface.Explorer, 1, docs, [windows]), 1),
            (new Detected(Surface.Explorer, 3, docs, [docs + @"\a.txt", docs + @"\b.txt", docs + @"\c.txt"]), 6),
            (new Detected(Surface.Explorer, 0, docs), 1),
            (new Detected(Surface.Desktop, 0, @"C:\Users\me\Desktop"), 3),
        };
        var sheet = new UniformGrid { Columns = contexts.Length, Rows = 2 };
        foreach (var dark in new[] { true, false })
        {
            // A synthetic gradient stands in for the screenshot that blurs behind the ring.
            var backdrop = Gradient(dark);
            var theme = new Theme(dark, acrylic: true);
            foreach (var (context, hover) in contexts)
            {
                var view = new RadialView(Menus.For(context), theme, Menus.Summary(context), new ViewOptions(false, backdrop));
                view.SetHover(hover);
                sheet.Children.Add(new Grid { Background = new ImageBrush(backdrop), Children = { view } });
            }
        }
        return Save(sheet, RadialView.Size * contexts.Length, RadialView.Size * 2, path);
    }

    /// <summary>Every kind of ring (what was clicked) with a numbered legend, clockwise from the top: <c>--snapshot-all out.png</c></summary>
    public static int RenderAll(string path)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "radii-all-" + Guid.NewGuid().ToString("N")[..6]);
        var repo = System.IO.Path.Combine(temp, "project");
        var folder = System.IO.Path.Combine(temp, "Photos");
        Directory.CreateDirectory(System.IO.Path.Combine(repo, ".git"));
        Directory.CreateDirectory(folder);
        File.WriteAllLines(System.IO.Path.Combine(repo, ".git", "config"), ["[remote \"origin\"]", "    url = https://github.com/me/project.git"]);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "setup.exe", "photo.png", "pack.zip", "disk.iso" })
            File.WriteAllText(System.IO.Path.Combine(temp, name), "x");
        string P(string name) => System.IO.Path.Combine(temp, name);

        var contexts = new (string Title, Detected Context)[]
        {
            (Menus.T("File", "Файл"), new Detected(Surface.Explorer, 1, temp, [P("a.txt")])),
            (Menus.T("Several files", "Несколько файлов"), new Detected(Surface.Explorer, 3, temp, [P("a.txt"), P("b.txt"), P("c.txt")])),
            (Menus.T("Folder", "Папка"), new Detected(Surface.Explorer, 1, temp, [folder])),
            (Menus.T("Git repository", "Git-репозиторий"), new Detected(Surface.Explorer, 1, temp, [repo])),
            (Menus.T("Program", "Программа"), new Detected(Surface.Explorer, 1, temp, [P("setup.exe")])),
            (Menus.T("Picture", "Картинка"), new Detected(Surface.Explorer, 1, temp, [P("photo.png")])),
            (Menus.T("Archive", "Архив"), new Detected(Surface.Explorer, 1, temp, [P("pack.zip")])),
            (Menus.T("Disk image", "Образ диска"), new Detected(Surface.Explorer, 1, temp, [P("disk.iso")])),
            (Menus.T("Empty space in folder", "Пусто в папке"), new Detected(Surface.Explorer, 0, temp)),
            (Menus.T("Desktop", "Рабочий стол"), new Detected(Surface.Desktop, 0, @"C:\Users\me\Desktop")),
        };
        var theme = new Theme(true, acrylic: true);
        var backdrop = Gradient(true);
        var sheet = new UniformGrid { Columns = 5, Rows = 2, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)) };
        foreach (var (title, context) in contexts)
        {
            var items = Menus.For(context);
            var legend = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
            for (var i = 0; i < items.Count; i++)
            {
                legend.Children.Add(new TextBlock
                {
                    Text = $"{i + 1}. {items[i].Label}",
                    FontSize = 13,
                    Foreground = items[i].Danger ? Brushes.IndianRed : Brushes.White,
                });
            }
            var view = new RadialView(items, theme, title, new ViewOptions(false, backdrop));
            var cell = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            cell.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(18, 4, 0, 0) });
            cell.Children.Add(new Grid { Background = new ImageBrush(backdrop), Width = RadialView.Size, Height = RadialView.Size, Children = { view } });
            cell.Children.Add(legend);
            sheet.Children.Add(cell);
        }
        var result = Save(sheet, RadialView.Size * 5, 2 * (RadialView.Size + 260), path);
        try
        {
            Directory.Delete(temp, true);
        }
        catch (IOException)
        {
        }
        return result;
    }

    /// <summary>The converter ring for the given files: <c>--snapshot-convert out.png path...</c></summary>
    public static int RenderConvert(string path, string[] files)
    {
        var items = Converters.ConvertMenu.For(files);
        if (items.Count == 0)
            return 2;
        var sheet = new UniformGrid { Columns = 2, Rows = 1 };
        foreach (var dark in new[] { true, false })
        {
            var backdrop = Gradient(dark);
            var theme = new Theme(dark, acrylic: true);
            var view = new RadialView(items, theme, System.IO.Path.GetFileName(files[0]), new ViewOptions(false, backdrop));
            view.SetHover(1);
            sheet.Children.Add(new Grid { Background = new ImageBrush(backdrop), Children = { view } });
        }
        return Save(sheet, RadialView.Size * 2, RadialView.Size, path);
    }

    /// <summary>The usual ring with the two pills under it, dark and light: <c>--snapshot-chips out.png</c></summary>
    public static int RenderChips(string path)
    {
        var context = new Detected(Surface.Explorer, 1, @"C:\Users\me\Documents", [@"C:\Users\me\Documents\photo.png"]);
        var sheet = new UniformGrid { Columns = 2, Rows = 1 };
        foreach (var dark in new[] { true, false })
        {
            var backdrop = Gradient(dark);
            var theme = new Theme(dark, acrylic: true);
            var view = new RadialView(Menus.For(context), theme, Menus.Summary(context), new ViewOptions(false, backdrop));
            view.SetHover(1);
            var specs = new List<ChipSpec>
            {
                new(RingMode.Convert, Menus.T("Convert", "Конвертер"), Menus.T("Shift + RMB", "Shift + ПКМ"), "", () => { }),
                new(RingMode.Edit, Menus.T("Edit", "Правка"), Menus.T("Ctrl + RMB", "Ctrl + ПКМ"), "", () => { }),
            };
            var chips = new ChipsPanel(theme, specs) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
            chips.SetHover(dark ? null : specs[1]);
            var stack = new Grid { Background = new ImageBrush(backdrop) };
            stack.Children.Add(view);
            stack.Children.Add(chips);
            sheet.Children.Add(stack);
        }
        return Save(sheet, RadialView.Size * 2, RadialView.Size + 40, path);
    }

    public static int RenderFlyout(string path, string target)
    {
        var theme = new Theme(true);
        var session = ShellMenuHost.QueryAsync(File.Exists(target) || Directory.Exists(target) ? [target] : [], null, IntPtr.Zero).GetAwaiter().GetResult();
        var entries = session is null ? [] : ShellFilter.Clean(session.Entries);
        var view = new FlyoutView(theme, 600, (_, _, _) => { }, _ => { }) { Tiles = true };
        entries.Add(new ShellEntry { Id = -1, Label = Menus.T("Windows menu", "Стандартное меню Windows"), Glyph = "" });
        view.Populate(entries);
        var holder = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), Children = { view }, HorizontalAlignment = HorizontalAlignment.Left };
        holder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        session?.Dispose();
        return Save(holder, holder.DesiredSize.Width, holder.DesiredSize.Height, path);
    }

    private static int Save(UIElement element, double width, double height, string path)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        return 0;
    }

    private static BitmapSource Gradient(bool dark)
    {
        var brush = new LinearGradientBrush(
            dark ? Color.FromRgb(0x0F, 0x3B, 0x6B) : Color.FromRgb(0xF2, 0xC9, 0x9E),
            dark ? Color.FromRgb(0x5B, 0x1F, 0x4F) : Color.FromRgb(0x8F, 0xC4, 0xEE),
            new Point(0, 0), new Point(1, 1));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(brush, null, new Rect(0, 0, RadialView.Size, RadialView.Size));
            context.DrawEllipse(Brushes.White, null, new Point(60, 60), 24, 24);
            context.DrawRectangle(Brushes.Black, null, new Rect(180, 200, 100, 14));
        }
        var bitmap = new RenderTargetBitmap((int)RadialView.Size, (int)RadialView.Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
