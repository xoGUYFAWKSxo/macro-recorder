using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroRecorder;

public enum EventKind { MouseMove, MouseDown, MouseUp, MouseWheel, MouseHWheel, KeyDown, KeyUp }

public enum MouseButton { None, Left, Right, Middle, X1, X2 }

public sealed class MacroEvent
{
    public EventKind Kind { get; set; }

    /// <summary>Milliseconds since the previous event (or since recording started, for the first one).</summary>
    public double DelayMs { get; set; }

    // Mouse
    public int X { get; set; }
    public int Y { get; set; }
    public MouseButton Button { get; set; }
    public int WheelDelta { get; set; }

    // Keyboard
    public int VirtualKey { get; set; }
    public int ScanCode { get; set; }
    public bool Extended { get; set; }

    public bool IsMouse => Kind is EventKind.MouseMove or EventKind.MouseDown or EventKind.MouseUp
                                or EventKind.MouseWheel or EventKind.MouseHWheel;

    public string Describe() => Kind switch
    {
        EventKind.MouseMove => $"({X}, {Y})",
        EventKind.MouseDown or EventKind.MouseUp => $"{Button} at ({X}, {Y})",
        EventKind.MouseWheel or EventKind.MouseHWheel => $"{WheelDelta:+0;-0} at ({X}, {Y})",
        EventKind.KeyDown or EventKind.KeyUp => $"{(Keys)VirtualKey}" + (Extended ? " (ext)" : ""),
        _ => "",
    };
}

public sealed class Macro
{
    public int Version { get; set; } = 1;
    public List<MacroEvent> Events { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static Macro Load(string path) =>
        JsonSerializer.Deserialize<Macro>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("File does not contain a macro.");
}
