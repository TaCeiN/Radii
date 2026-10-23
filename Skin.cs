using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Radii;

/// <summary>
/// Radii's own look: a fixed palette (taken from the logo, so it never depends on the Windows accent colour) and restyled
/// controls. <see cref="Apply"/> installs the implicit styles into the application once and swaps the colours for light/dark.
/// </summary>
internal static class Skin
{
    private static bool _installed;
    private static string? _key;

    /// <summary>One colour set for one mode.</summary>
    private sealed record Mode(string Bg, string Card, string Line, string Text, string Muted, string Accent, string OnAccent);

    private sealed record Palette(string Id, string En, string Ru, Mode Dark, Mode Light);

    private static readonly Palette[] Palettes =
    [
        new("mono", "Mono graphite", "Моно графит",
            new("#161616", "#1F1F1F", "#2E2E2E", "#F2F2F2", "#8E8E8E", "#F2F2F2", "#111111"),
            new("#F4F4F2", "#FFFFFF", "#E2E2DE", "#161616", "#6E6E6A", "#161616", "#FFFFFF")),
        new("amber", "Graphite and amber", "Графит и янтарь",
            new("#171614", "#211F1C", "#33302B", "#F3EFE8", "#9A9489", "#F2A33A", "#201400"),
            new("#F6F3EC", "#FFFFFF", "#E6E1D6", "#1B1915", "#6F685B", "#C77A0A", "#FFFFFF")),
        new("sage", "Sage", "Шалфей",
            new("#141714", "#1C211C", "#2B332B", "#EDF2EA", "#8E9A8B", "#9CCB8F", "#0C1A0A"),
            new("#F1F4EE", "#FFFFFF", "#DFE6DA", "#161C15", "#5F6C5B", "#3F7D3A", "#FFFFFF")),
        new("terracotta", "Terracotta", "Терракота",
            new("#181514", "#221E1C", "#342E2B", "#F5EEEA", "#9D918B", "#EE7B5C", "#240A02"),
            new("#F7F2EF", "#FFFFFF", "#E8DED8", "#1D1715", "#74655E", "#C4492A", "#FFFFFF")),
        new("lime", "Lime", "Лайм",
            new("#131413", "#1B1C1A", "#2A2C28", "#F0F2EC", "#8C9087", "#C9F25A", "#121A00"),
            new("#F3F5EE", "#FFFFFF", "#E1E5D8", "#171A12", "#666B5A", "#5C7D00", "#FFFFFF")),
        new("slate", "Cool slate", "Холодный сине-серый",
            new("#15171A", "#1E2126", "#2C3037", "#EEF0F3", "#8F96A1", "#7EA6F5", "#07142E"),
            new("#F3F4F7", "#FFFFFF", "#E1E4EA", "#16181C", "#636B78", "#2F5FD0", "#FFFFFF")),
    ];

    /// <summary>(id, English name, Russian name) for the settings list.</summary>
    public static IEnumerable<(string Id, string En, string Ru)> PaletteList => Palettes.Select(p => (p.Id, p.En, p.Ru));

    private static Mode Pick(bool dark)
    {
        var id = Settings.Current.Palette;
        var palette = Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];
        return dark ? palette.Dark : palette.Light;
    }

    public static Color Accent(bool dark) => Parse(Pick(dark).Accent);
    public static Color OnAccent(bool dark) => Parse(Pick(dark).OnAccent);
    public static string Window(bool dark) => Pick(dark).Bg;
    public static string Card(bool dark) => Pick(dark).Card;
    public static string Line(bool dark) => Pick(dark).Line;
    public static string Muted(bool dark) => Pick(dark).Muted;
    public static string Text(bool dark) => Pick(dark).Text;
    public static string Danger(bool dark) => dark ? "#FF7B72" : "#C62F2A";

    /// <summary>The dark inset behind previews: the window colour pushed towards black.</summary>
    public static string Canvas(bool dark) => Hex(Blend(Parse(Window(dark)), Colors.Black, dark ? 0.35 : 0.07));

    /// <summary>"#AARRGGBB" of <paramref name="hex"/> with the given alpha (0-255).</summary>
    public static string WithAlpha(string hex, int alpha) => "#" + alpha.ToString("X2") + hex.TrimStart('#');

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    /// <summary>Sets the colours for the current palette and mode; installs the control styles on first use. Safe to call often and without an application.</summary>
    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null || !app.Dispatcher.CheckAccess())
            return;
        if (!_installed)
        {
            _installed = true;
            Install(app);
            Settings.Changed += () => Theme.UserDark();
        }
        var key = Settings.Current.Palette + (dark ? "/d" : "/l");
        if (_key == key)
            return;
        _key = key;
        void Set(string name, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[name] = brush;
        }
        var accent = Accent(dark);
        // A near-white accent (the mono dark theme) gets darker on hover; the others lighter in dark mode and darker in light mode.
        var towards = Luma(accent) > 0.8 || !dark ? Colors.Black : Colors.White;
        Set("SkAccent", accent);
        Set("SkAccentHover", Blend(accent, towards, 0.14));
        Set("SkAccentPress", Blend(accent, Colors.Black, 0.22));
        foreach (var name in new[] { "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary" })
            app.Resources[name] = accent;
        foreach (var name in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "SystemAccentColorBrush" })
            Set(name, accent);
        Set("SkOnAccent", OnAccent(dark));
        Set("SkText", Parse(Text(dark)));
        Set("SkMuted", Parse(Muted(dark)));
        Set("SkLine", Parse(Line(dark)));
        Set("SkCard", Parse(Card(dark)));
        Set("SkWindow", Parse(Window(dark)));
        Set("SkFill", dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0, 0, 0));
        Set("SkFillHover", dark ? Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0));
        Set("SkFillPress", dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0));
        Set("SkTrack", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0));
        Set("SkPopup", Blend(Parse(Card(dark)), Colors.White, dark ? 0.04 : 0));
        Set("SkSelected", Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        Set("SkThumb", dark ? Parse(Window(true)) : Colors.White);
    }

    public static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    private static void Install(Application app)
    {
        // Controls that only need a recolour keep the system (Fluent) template and get a new Foreground/brush.
        foreach (var xaml in Styles())
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse($"<ResourceDictionary {Ns}>{xaml}</ResourceDictionary>");
            foreach (var key in dictionary.Keys)
                app.Resources[key] = dictionary[key];
        }
    }

    private static IEnumerable<string> Styles()
    {
        yield return ButtonStyle(null, "SkFill", "SkFillHover", "SkFillPress", "SkLine", "SkText", "Normal");
        yield return ButtonStyle("AccentButtonStyle", "SkAccent", "SkAccentHover", "SkAccentPress", "Transparent", "SkOnAccent", "SemiBold");
        var chip = """
            <Style x:Key="SkChip" TargetType="ToggleButton">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="Background" Value="{DynamicResource SkFill}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource SkLine}"/>
              <Setter Property="BorderThickness" Value="1"/>
              <Setter Property="Padding" Value="12,4"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
                <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="15" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SkFillHover}"/></Trigger>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="b" Property="Background" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SkAccent}"/>
                    <Setter Property="Foreground" Value="{DynamicResource SkOnAccent}"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return chip;
        yield return chip.Replace("x:Key=\"SkChip\" ", "");
        yield return """
            <Style TargetType="CheckBox">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
                <StackPanel Orientation="Horizontal" Background="Transparent">
                  <Grid Width="18" Height="18" VerticalAlignment="Center">
                    <Border x:Name="box" CornerRadius="5" BorderThickness="1.5" BorderBrush="{DynamicResource SkMuted}" Background="Transparent"/>
                    <Path x:Name="tick" Data="M4.5,9.2 L7.6,12.2 L13.4,5.6" Stroke="{DynamicResource SkOnAccent}" StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed"/>
                  </Grid>
                  <ContentPresenter x:Name="content" Margin="9,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                </StackPanel>
                <ControlTemplate.Triggers>
                  <Trigger Property="HasContent" Value="False"><Setter TargetName="content" Property="Margin" Value="0"/></Trigger>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource SkAccent}"/></Trigger>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="box" Property="Background" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="tick" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return """
            <Style x:Key="SkSwitch" TargetType="CheckBox">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
                <Grid Width="40" Height="22" Background="Transparent" VerticalAlignment="Center">
                  <Border x:Name="track" CornerRadius="11" Background="{DynamicResource SkTrack}"/>
                  <Ellipse x:Name="knob" Width="14" Height="14" HorizontalAlignment="Left" Margin="4,0,0,0" Fill="{DynamicResource SkThumb}"/>
                </Grid>
                <ControlTemplate.Triggers>
                  
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="track" Property="Background" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="knob" Property="Fill" Value="{DynamicResource SkOnAccent}"/>
                    <Setter TargetName="knob" Property="Margin" Value="22,0,0,0"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return """
            <Style TargetType="RadioButton">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
                <Border x:Name="card" CornerRadius="10" BorderThickness="1" BorderBrush="{DynamicResource SkLine}" Background="{DynamicResource SkFill}" Padding="{TemplateBinding Padding}">
                  <StackPanel Orientation="Horizontal">
                    <Grid Width="18" Height="18" Margin="4,2,10,0" VerticalAlignment="Top">
                      <Ellipse x:Name="ring" Stroke="{DynamicResource SkMuted}" StrokeThickness="1.5"/>
                      <Ellipse x:Name="dot" Width="8" Height="8" Fill="{DynamicResource SkOnAccent}" Visibility="Collapsed"/>
                    </Grid>
                    <ContentPresenter VerticalAlignment="Center"/>
                  </StackPanel>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="card" Property="Background" Value="{DynamicResource SkFillHover}"/></Trigger>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="card" Property="BorderBrush" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="ring" Property="Fill" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="ring" Property="Stroke" Value="{DynamicResource SkAccent}"/>
                    <Setter TargetName="dot" Property="Visibility" Value="Visible"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return """
            <Style TargetType="Slider">
              <Setter Property="Foreground" Value="{DynamicResource SkAccent}"/>
              <Setter Property="MinHeight" Value="22"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider">
                <Grid Background="Transparent" Height="22">
                  <Border Height="4" CornerRadius="2" Background="{DynamicResource SkTrack}" VerticalAlignment="Center"/>
                  <Track x:Name="PART_Track">
                    <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False" IsTabStop="False">
                      <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" CornerRadius="2" Background="{DynamicResource SkAccent}" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template>
                    </RepeatButton></Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False" IsTabStop="False">
                      <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" Height="22"/></ControlTemplate></RepeatButton.Template>
                    </RepeatButton></Track.IncreaseRepeatButton>
                    <Track.Thumb><Thumb Width="18" Height="18" Focusable="False">
                      <Thumb.Template><ControlTemplate TargetType="Thumb">
                        <Ellipse x:Name="e" Fill="{DynamicResource SkThumb}" Stroke="{DynamicResource SkAccent}" StrokeThickness="4"/>
                        <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="e" Property="Stroke" Value="{DynamicResource SkAccentHover}"/></Trigger></ControlTemplate.Triggers>
                      </ControlTemplate></Thumb.Template>
                    </Thumb></Track.Thumb>
                  </Track>
                </Grid>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return """
            <Style TargetType="TextBox">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="CaretBrush" Value="{DynamicResource SkAccent}"/>
              <Setter Property="SelectionBrush" Value="{DynamicResource SkAccent}"/>
              <Setter Property="Background" Value="{DynamicResource SkFill}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource SkLine}"/>
              <Setter Property="BorderThickness" Value="1"/>
              <Setter Property="Padding" Value="9,5"/>
              <Setter Property="MinHeight" Value="30"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
                <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" SnapsToDevicePixels="True">
                  <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SkMuted}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SkAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return """
            <Style TargetType="ComboBoxItem">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="Padding" Value="10,6"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
                <Border x:Name="b" CornerRadius="7" Background="Transparent" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SkFillHover}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SkSelected}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        yield return """
            <Style TargetType="ComboBox">
              <Setter Property="Foreground" Value="{DynamicResource SkText}"/>
              <Setter Property="MinHeight" Value="30"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
                <Grid>
                  <ToggleButton x:Name="tg" Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                    <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                      <Border x:Name="b" CornerRadius="9" Background="{DynamicResource SkFill}" BorderBrush="{DynamicResource SkLine}" BorderThickness="1">
                        <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0" Data="M0,0 L4.5,4.5 L9,0" Stroke="{DynamicResource SkMuted}" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
                      </Border>
                      <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SkFillHover}"/></Trigger></ControlTemplate.Triggers>
                    </ControlTemplate></ToggleButton.Template>
                  </ToggleButton>
                  <ContentPresenter IsHitTestVisible="False" Margin="12,0,30,0" VerticalAlignment="Center" HorizontalAlignment="Left" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>
                  <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                    <Border MinWidth="{Binding ActualWidth, ElementName=tg}" MaxHeight="{TemplateBinding MaxDropDownHeight}" Margin="0,4,0,0" CornerRadius="11" Background="{DynamicResource SkPopup}" BorderBrush="{DynamicResource SkLine}" BorderThickness="1" Padding="4">
                      <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
    }

    private static string ButtonStyle(string? key, string fill, string hover, string press, string line, string text, string weight)
    {
        string Brush(string name) => name == "Transparent" ? "Transparent" : "{DynamicResource " + name + "}";
        return $$"""
            <Style {{(key is null ? "" : $"x:Key='{key}'")}} TargetType="Button">
              <Setter Property="Foreground" Value="{{Brush(text)}}"/>
              <Setter Property="Background" Value="{{Brush(fill)}}"/>
              <Setter Property="BorderBrush" Value="{{Brush(line)}}"/>
              <Setter Property="BorderThickness" Value="1"/>
              <Setter Property="FontWeight" Value="{{weight}}"/>
              <Setter Property="Padding" Value="14,5"/>
              <Setter Property="MinHeight" Value="30"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource {{hover}}}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource {{press}}}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
    }
}
