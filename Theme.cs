using System.Windows.Media;
using Microsoft.Win32;

namespace Radii;

/// <summary>Radii look: follows the system light/dark mode; the colours are our own (<see cref="Skin"/>), not the Windows accent.</summary>
internal sealed class Theme
{
    public Theme(bool dark, bool acrylic = false)
    {
        Dark = dark;
        Accent = Skin.Accent(dark);
        Acrylic = acrylic;
        Skin.Apply(dark);
        var card = (Color)ColorConverter.ConvertFromString(Skin.Card(dark));
        // With the blurred backdrop the sectors are more transparent so the blur shows through.
        var alpha = acrylic ? (byte)0x9C : (byte)0xF2;
        Sector = Frozen(Color.FromArgb(alpha, card.R, card.G, card.B));
        Border = Frozen(dark ? Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0));
        Text = Frozen((Color)ColorConverter.ConvertFromString(Skin.Text(dark)));
        TextMuted = Frozen((Color)ColorConverter.ConvertFromString(Skin.Muted(dark)));
        Hover = Frozen(Accent);
        DangerHover = Frozen(Color.FromRgb(0xC6, 0x2F, 0x2A));
        OnAccent = Frozen(Skin.OnAccent(dark));
        // Opaque: text from the window below showed through the list otherwise.
        FlyoutBackground = Frozen(Skin.Blend(card, dark ? Colors.White : Colors.Black, dark ? 0.03 : 0.01));
        RowHover = Frozen(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
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

    /// <summary>Dark or light as the user chose it in the settings (system by default).</summary>
    public static bool UserDark()
    {
        var dark = Settings.Current.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemDark(),
        };
        Skin.Apply(dark);
        return dark;
    }

    public static Theme Current() => new(UserDark(), Settings.Current.Acrylic);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
