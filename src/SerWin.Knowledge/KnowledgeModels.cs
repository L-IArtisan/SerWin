using System.Text.Json.Serialization;

namespace SerWin.Knowledge;

/// <summary>Texte multilingue : { "fr": "…", "en": "…" }. Le français est la langue source.</summary>
public sealed class LocalizedText : Dictionary<string, string>
{
    public const string SourceLanguage = "fr";

    public LocalizedText() : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>Texte dans la langue demandée ; à défaut dans la langue source (<paramref name="isFallback"/> = vrai).</summary>
    public string Resolve(string language, out bool isFallback)
    {
        if (TryGetValue(language, out var t) && !string.IsNullOrWhiteSpace(t))
        {
            isFallback = false;
            return t;
        }
        isFallback = true;
        if (TryGetValue(SourceLanguage, out var src) && !string.IsNullOrWhiteSpace(src)) return src;
        return Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    public string Resolve(string language) => Resolve(language, out _);

    public bool IsTranslated(string language) =>
        TryGetValue(language, out var t) && !string.IsNullOrWhiteSpace(t);
}

/// <summary>Niveau de risque d'une intervention. Correspond aux pastilles 🟢 / 🟠 / 🔴.</summary>
public enum RiskLevel
{
    /// <summary>🟢 « faible » : peut être désactivé sans conséquence grave.</summary>
    Low,
    /// <summary>🟠 « moyen » : prudence, perte de fonctionnalité notable.</summary>
    Medium,
    /// <summary>🔴 « critique » : bloqué, indispensable au système.</summary>
    Critical,
}

public enum VerificationStatus
{
    /// <summary>📝 Rédigé à partir de la documentation publique.</summary>
    Draft,
    /// <summary>🔍 Triggers, dépendances, tâches et compte confirmés sur un build donné.</summary>
    ConfigVerified,
    /// <summary>✅ Désactivation testée, effets et relances constatés.</summary>
    BehaviorObserved,
}

public sealed class LevelTexts
{
    public LocalizedText Simple { get; set; } = new();
    public LocalizedText Avance { get; set; } = new();
    public LocalizedText Expert { get; set; } = new();
}

/// <summary>Mécanisme susceptible de relancer le service (analyse statique, spec §5.5).</summary>
public sealed class Reviver
{
    /// <summary>service | task | trigger | policy | software | update</summary>
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public LocalizedText? Note { get; set; }
}

public sealed class DefaultConfig
{
    public string? StartType { get; set; }
    public List<string>? Triggers { get; set; }
    public string? Account { get; set; }
}

public sealed class Verification
{
    [JsonPropertyName("status")]
    public string StatusRaw { get; set; } = "draft";

    public string? Build { get; set; }
    public string? Edition { get; set; }
    public string? Date { get; set; }

    [JsonIgnore]
    public VerificationStatus Status => StatusRaw switch
    {
        "config-verified" => VerificationStatus.ConfigVerified,
        "behavior-observed" => VerificationStatus.BehaviorObserved,
        _ => VerificationStatus.Draft,
    };
}

/// <summary>Fiche de la base de connaissances (spec §4.2). Un fichier JSON par service ou processus.</summary>
public sealed class KnowledgeEntry
{
    public string Name { get; set; } = "";

    /// <summary>Autres noms couverts par la fiche (ex. ancien nom, variante d'édition).</summary>
    public List<string> Aliases { get; set; } = [];

    public LocalizedText DisplayName { get; set; } = new();
    public string Category { get; set; } = "";

    [JsonPropertyName("risk")]
    public string RiskRaw { get; set; } = "moyen";

    public bool BootCritical { get; set; }
    public LocalizedText Summary { get; set; } = new();
    public LevelTexts Levels { get; set; } = new();
    public List<LocalizedText> IfDisabled { get; set; } = [];
    public List<Reviver> Revivers { get; set; } = [];

    /// <summary>Règles de contexte (v0.5) : { "printers": ">0" }.</summary>
    public Dictionary<string, string> UsefulIf { get; set; } = [];

    public LocalizedText? VerdictIfNot { get; set; }
    public bool Privacy { get; set; }

    /// <summary>Configuration d'usine par « Édition:Build ».</summary>
    public Dictionary<string, DefaultConfig> Defaults { get; set; } = [];

    public Verification Verification { get; set; } = new();

    /// <summary>Sources publiques utilisées pour la rédaction.</summary>
    public List<string> Sources { get; set; } = [];

    [JsonIgnore]
    public RiskLevel Risk => RiskRaw switch
    {
        "faible" => RiskLevel.Low,
        "critique" => RiskLevel.Critical,
        _ => RiskLevel.Medium,
    };

    /// <summary>
    /// Réglage d'usine pour une édition / un build (spec §5.4). À défaut de relevé exact, prend le build connu
    /// le plus proche de la même famille d'édition (<paramref name="exact"/> = faux).
    /// </summary>
    public DefaultConfig? DefaultFor(string editionFamily, int build, out bool exact)
    {
        exact = false;
        if (Defaults.TryGetValue($"{editionFamily}:{build}", out var d) && d.StartType is not null)
        {
            exact = true;
            return d;
        }
        return Defaults
            .Select(kv => (Parts: kv.Key.Split(':'), Config: kv.Value))
            .Where(x => x.Parts.Length == 2 && x.Parts[0].Equals(editionFamily, StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(x.Parts[1], out _) && x.Config.StartType is not null)
            .OrderBy(x => Math.Abs(int.Parse(x.Parts[1]) - build))
            .Select(x => x.Config)
            .FirstOrDefault();
    }

    /// <summary>Indique si la fiche a été vérifiée sur cette édition et ce build précis.</summary>
    public bool IsVerifiedFor(string editionFamily, int build)
    {
        if (Verification.Status == VerificationStatus.Draft) return false;
        if (!string.Equals(Verification.Edition, editionFamily, StringComparison.OrdinalIgnoreCase)) return false;
        var b = Verification.Build?.Split('.')[0];
        return b == build.ToString();
    }
}

/// <summary>Métadonnées de la base (knowledge/kb.json).</summary>
public sealed class KnowledgeMetadata
{
    public string Version { get; set; } = "0000.00";
    public int SchemaVersion { get; set; } = 1;
}

public static class KnowledgeCategories
{
    public static readonly IReadOnlyList<string> All =
    [
        "core", "maintenance", "security", "network", "hardware", "audio-video",
        "printing", "gaming", "search", "diagnostics", "privacy", "ui", "remote", "location", "storage",
        "enterprise", "virtualization", "media",
    ];
}
