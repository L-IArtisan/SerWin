using System.Text.Json.Serialization;

namespace SerWin.Knowledge;

/// <summary>Utilité d'une destination réseau pour le fonctionnement de Windows.</summary>
public enum DestinationNeed
{
    /// <summary>« essentiel » : la bloquer casse la sécurité ou le fonctionnement de base (certificats, mises à jour de sécurité…).</summary>
    Essential,
    /// <summary>« utile » : sert une fonction que beaucoup utilisent (Store, compte Microsoft, heure…).</summary>
    Useful,
    /// <summary>« optionnel » : confort ou contenu (météo, actualités, synchronisation…).</summary>
    Optional,
    /// <summary>« indesirable » : télémétrie, publicité, suivi — rien ne casse pour l'utilisateur si elle est coupée.</summary>
    Unwanted,
}

/// <summary>
/// Fiche « destination » (v1.1, page Réseau) : un service en ligne contacté automatiquement par Windows ou un logiciel
/// courant. Décrit de façon générale (pas propre à une machine) : à quoi il sert, qui le contacte, ce qui casse sans lui.
/// Un fichier par destination : knowledge/destinations/&lt;nom&gt;.json.
/// </summary>
public sealed class DestinationEntry
{
    /// <summary>Identifiant (nom du fichier), ex. « windows-telemetry ».</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Noms d'hôtes couverts : exact (« login.live.com ») ou suffixe (« *.events.data.microsoft.com » couvre aussi
    /// « events.data.microsoft.com »). Le motif le plus précis l'emporte.
    /// </summary>
    public List<string> Patterns { get; set; } = [];

    /// <summary>Éditeur du service en ligne (« Microsoft », « Google »…).</summary>
    public string Owner { get; set; } = "";

    public LocalizedText DisplayName { get; set; } = new();
    public string Category { get; set; } = "";

    [JsonPropertyName("need")]
    public string NeedRaw { get; set; } = "utile";

    public bool Privacy { get; set; }
    public LocalizedText Summary { get; set; } = new();
    public LevelTexts Levels { get; set; } = new();

    /// <summary>Ce qui cesse de fonctionner si la destination est bloquée.</summary>
    public List<LocalizedText> IfBlocked { get; set; } = [];

    /// <summary>Programmes ou services qui la contactent habituellement (« DiagTrack », « svchost.exe », « msedge.exe »).</summary>
    public List<string> Senders { get; set; } = [];

    /// <summary>Réglage officiel de Windows qui réduit ou coupe ces échanges, quand il existe (affiché à titre indicatif).</summary>
    public LocalizedText? Control { get; set; }

    /// <summary>Règles de contexte (mêmes clés que les fiches services).</summary>
    public Dictionary<string, string> UsefulIf { get; set; } = [];
    public LocalizedText? VerdictIfNot { get; set; }

    public Verification Verification { get; set; } = new();
    public List<string> Sources { get; set; } = [];

    [JsonIgnore]
    public DestinationNeed Need => NeedRaw switch
    {
        "essentiel" => DestinationNeed.Essential,
        "optionnel" => DestinationNeed.Optional,
        "indesirable" => DestinationNeed.Unwanted,
        _ => DestinationNeed.Useful,
    };
}

public static class DestinationCategories
{
    public static readonly IReadOnlyList<string> All =
    [
        "telemetry", "advertising", "content", "updates", "security", "certificates", "connectivity", "time",
        "account", "store", "licensing", "cloud", "search", "browser", "office", "gaming", "maps", "sync",
        "diagnostics", "cdn", "location", "notifications", "third-party",
    ];

    public static readonly IReadOnlyList<string> NeedValues = ["essentiel", "utile", "optionnel", "indesirable"];
}

/// <summary>Recherche de la fiche correspondant à un nom d'hôte : le motif le plus long (le plus précis) l'emporte.</summary>
public sealed class DestinationCatalog
{
    private readonly List<(string Pattern, bool Suffix, DestinationEntry Entry)> _patterns = [];

    public DestinationCatalog(IEnumerable<DestinationEntry> entries)
    {
        foreach (var e in entries)
            foreach (var p in e.Patterns)
            {
                var pattern = p.Trim().ToLowerInvariant();
                var suffix = pattern.StartsWith("*.", StringComparison.Ordinal);
                _patterns.Add((suffix ? pattern[2..] : pattern, suffix, e));
            }
        _patterns.Sort((a, b) => b.Pattern.Length.CompareTo(a.Pattern.Length));
    }

    /// <summary>Fiche du nom d'hôte, ou à défaut de l'un de ses alias (CNAME).</summary>
    public DestinationEntry? Match(string host, IEnumerable<string>? aliases = null)
    {
        return MatchOne(host) ?? aliases?.Select(MatchOne).FirstOrDefault(e => e is not null);
    }

    private DestinationEntry? MatchOne(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var (pattern, suffix, entry) in _patterns)
        {
            if (h == pattern) return entry;
            if (suffix && h.EndsWith("." + pattern, StringComparison.Ordinal)) return entry;
        }
        return null;
    }
}
