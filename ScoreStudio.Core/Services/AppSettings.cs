using System.Text.Json;

namespace ScoreStudio.Services;

/// <summary>Program settings that belong to the user, not to a score (kept in the user's application data folder).</summary>
public sealed class AppSettings
{
    /// <summary>A note longer than the rest of the measure goes on in the next measure, tied (off: it is refused).</summary>
    public bool TieAcrossBarlines { get; set; }

    /// <summary>Audiveris.exe chosen by the user for music recognition; null to look where its installers put it.</summary>
    public string? AudiverisPath { get; set; }

    /// <summary>The user's key changes ("Speedy:StepUp" → ["Up"]); commands not listed keep their default keys.</summary>
    public Dictionary<string, List<string>>? KeyBindings { get; set; }

    /// <summary>The tool last chosen in the tool palette (an <c>EditorTool</c> name).</summary>
    public string? Tool { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The settings in <paramref name="path"/>; the defaults when the file is missing or cannot be read, so a damaged
    /// settings file never stops the program.
    /// </summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings; false when the file cannot be written (the setting then lasts only until the program closes).</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
