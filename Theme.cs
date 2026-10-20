using System.Windows.Media;
using Microsoft.Win32;

namespace Radii;

/// <summary>Windows 11 look: follows the system light/dark mode and accent colour (or the user's override).</summary>
internal sealed class Theme
{
    public Theme(bool dark, Color accent, bool acrylic = false)
    {
        Dark = dark;
        Accent = accent;
        Acrylic = acrylic;
        // With the blurred backdrop the sectors are more transparent so the blur shows through.
        var alpha = acrylic ? (byte)0x9C : (byte)0xF2;
        Sector = Frozen(dark ? Color.FromArgb(alpha, 0x2C, 0x2C, 0x2C) : Color.FromArgb(alpha, 0xF9, 0xF9, 0xF9));
        Border = Frozen(dark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00));
        Text = Frozen(dark ? Colors.White : Color.FromRgb(0x1B, 0x1B, 0x1B));
        TextMuted = Frozen(dark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x5A, 0x5A, 0x5A));
        Hover = Frozen(accent);
        DangerHover = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
        OnAccent = Frozen(Colors.White);
        // Opaque: text from the window below showed through the list otherwise.
        FlyoutBackground = Frozen(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xFA, 0xFA, 0xFA));
        RowHover = Frozen(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00));
    }

    public bool Dark { get; }
    public Color Accent { get; }
    public bool Acrylic { get; }
    public Brush Sector { get; }
    public Brush Border { get; }
    public Brush Text { get; }
    public Brush TextMuted { get; }
    public Brush Hover { get; }
    public Brush DangerHover { get; }
    public Brush OnAccent { get; }
    public Brush FlyoutBackground { get; }
    public Brush RowHover { get; }

    public static bool SystemDark()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalize?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Color SystemAccent()
    {
        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    public static Theme Current()
    {
        var settings = Settings.Current;
        var dark = settings.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemDark(),
        };
        return new Theme(dark, SystemAccent(), settings.Acrylic);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
