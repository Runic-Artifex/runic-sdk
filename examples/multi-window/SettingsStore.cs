using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.Platform;

namespace MultiWindow;

// The app's own settings. "system" follows the desktop's color scheme from IDesktopSettings. Language is the Windows'
// language, "en" or "de".
public sealed record AppSettings(string Theme = "system", string? LastFolder = null, string Language = "en")
{
    // The scheme the application's windows show for Theme; NoPreference follows the desktop.
    [JsonIgnore]
    public DesktopColorScheme ColorScheme => Theme switch
    {
        "light" => DesktopColorScheme.Light,
        "dark" => DesktopColorScheme.Dark,
        _ => DesktopColorScheme.NoPreference,
    };
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

// A settings file in a folder the app owns, so it writes the file directly, without a file dialog. Saving writes a
// temporary file and moves it over the old one, so a crash never leaves a half-written file. The lock serializes
// saves from two Windows.
public sealed class SettingsStore(string path)
{
    private readonly Lock _gate = new();

    // The per-user application data folder, in a folder named after the app.
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunicMultiWindow", "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.AppSettings) ?? new()
                : new();
    }

    // Raised after each save, on the saving Window's thread; Program.cs applies the theme to every Window.
    public event EventHandler<AppSettings>? Saved;

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
            File.Move(temporary, path, overwrite: true);
        }
        Saved?.Invoke(this, settings);
    }
}
