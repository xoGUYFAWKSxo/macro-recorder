using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroRecorder;

/// <summary>User preferences, persisted to %APPDATA%\MacroRecorder\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Key code combined with Control/Alt/Shift modifier flags.</summary>
    public Keys RecordHotkey { get; set; } = Keys.F9;
    public Keys PlayHotkey { get; set; } = Keys.F10;
    public int CountdownSeconds { get; set; } = 3;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroRecorder", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly KeysConverter Converter = new();

    public static string HotkeyText(Keys hotkey) => Converter.ConvertToString(hotkey) ?? hotkey.ToString();

    public static bool IsModifierKey(Keys key) => (key & Keys.KeyCode) is
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;
}
