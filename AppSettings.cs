using System.Text.Json;
using System.Text.Json.Serialization;
using BarracudaTray.Lighting;

namespace BarracudaTray;

internal sealed class AppSettings
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BarracudaTray", "settings.json");

    public bool Enabled { get; set; } = true;
    public string Color { get; set; } = "#44D62C";
    public int Brightness { get; set; } = 100;
    public LightingEffect Effect { get; set; } = LightingEffect.Static;
    public bool Autonomous { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; the lighting keeps working without them.
        }
    }

    [JsonIgnore]
    public LampColor LampColor => LampColor.TryParse(Color, out var c) ? c : new LampColor(0x44, 0xD6, 0x2C);

    public LightingState ToState() => new(Enabled, LampColor, Math.Clamp(Brightness, 0, 100), Effect, Autonomous);
}
