using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerWin.App.Infrastructure;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComplexityLevel
{
    Simple,
    Avance,
    Expert,
}

/// <summary>Préférences de l'utilisateur, stockées dans %AppData%\SerWin\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Mode Simple par défaut au premier lancement (spec §2).</summary>
    public ComplexityLevel Level { get; set; } = ComplexityLevel.Simple;

    /// <summary>null = suivre la langue de Windows.</summary>
    public string? Language { get; set; }

    /// <summary>Écran d'accueil « curieux / bidouilleur / expert » déjà vu (v0.6).</summary>
    public bool WelcomeDone { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerWin", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Réglages illisibles : on repart des valeurs par défaut.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non bloquant.
        }
    }
}
