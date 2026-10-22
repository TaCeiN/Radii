using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radii;

/// <summary>User command added in the settings window. Placeholders: {path} {paths} {folder} {name}.</summary>
internal sealed class CustomCommand
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "";
    /// <summary>any | file | folder | background</summary>
    public string AppliesTo { get; set; } = "any";
    /// <summary>Comma separated, e.g. "txt,md"; empty means every file.</summary>
    public string Extensions { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool Admin { get; set; }

    [JsonIgnore]
    public string Id => "cmd:" + Name;
}

internal sealed class Settings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool Enabled { get; set; } = true;
    /// <summary>system | dark | light</summary>
    public string Theme { get; set; } = "system";
    /// <summary>auto | ru | en</summary>
    public string Language { get; set; } = "auto";
    public double RingScale { get; set; } = 1.0;
    public bool Animations { get; set; } = true;
    public bool Acrylic { get; set; } = true;
    /// <summary>Menu opens on press; releasing over a sector picks it. Off: menu opens on release and needs a click.</summary>
    public bool GestureMode { get; set; } = true;
    public bool SortByUsage { get; set; }
    /// <summary>Ask GitHub Releases for a newer version at start and once a day. Off: only the "Check for updates" button does.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Shift + right click opens the converter ring (off: it opens the native menu, as before).</summary>
    public bool ShiftConvert { get; set; } = true;
    /// <summary>Ctrl + right click opens the ring of editors (off: Ctrl + right click opens the usual ring).</summary>
    public bool CtrlEdit { get; set; } = true;
    /// <summary>Pills beside the ring that remind of Shift / Ctrl + right click and can be clicked.</summary>
    public bool ModeChips { get; set; } = true;
    public List<string> HiddenItems { get; set; } = [];
    public List<CustomCommand> Commands { get; set; } = [];
    public Dictionary<string, int> Usage { get; set; } = [];

    public static Settings Current { get; private set; } = Load();

    public static event Action? Changed;

    private static string? _folder;

    /// <summary>%APPDATA%\Radii. The first access moves the folder of the old name (RadialContext) here, so settings and downloaded engines survive the rename.</summary>
    public static string Folder => _folder ??= ResolveFolder();

    private static string ResolveFolder()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(roaming, "Radii");
        var legacy = Path.Combine(roaming, "RadialContext");
        try
        {
            if (!Directory.Exists(folder) && Directory.Exists(legacy))
                Directory.Move(legacy, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stay on the new, empty folder: settings fall back to defaults and engines download again.
        }
        return folder;
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            loaded.RingScale = Math.Clamp(loaded.RingScale, 0.7, 1.6);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never crash over them.
            }
        }
    }

    /// <summary>Persist and tell listeners (tray, hook) that something changed.</summary>
    public static void Notify()
    {
        Save();
        Changed?.Invoke();
    }

    public static void ResetUsage()
    {
        Current.Usage.Clear();
        Notify();
    }

    public static void RecordUse(string id)
    {
        lock (Gate)
            Current.Usage[id] = Current.Usage.GetValueOrDefault(id) + 1;
        Task.Run(Save);
    }
}
