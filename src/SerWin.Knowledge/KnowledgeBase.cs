using System.Reflection;
using System.Text.Json;

namespace SerWin.Knowledge;

/// <summary>Base de connaissances embarquée dans l'exécutable (spec §4.1).</summary>
public sealed class KnowledgeBase
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, KnowledgeEntry> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KnowledgeEntry> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DestinationEntry> _destinations = [];
    private DestinationCatalog? _catalog;

    public KnowledgeMetadata Metadata { get; private set; } = new();
    public IReadOnlyCollection<KnowledgeEntry> Services => _services.Values.Distinct().ToList();
    public IReadOnlyCollection<KnowledgeEntry> Processes => _processes.Values.Distinct().ToList();
    public int ServiceCount => Services.Count;
    public int ProcessCount => Processes.Count;

    /// <summary>Erreurs de chargement (fichier JSON invalide…) : la fiche est ignorée, l'app continue.</summary>
    public List<string> LoadErrors { get; } = [];

    public KnowledgeEntry? FindService(string name) => _services.GetValueOrDefault(name);
    public KnowledgeEntry? FindProcess(string name) => _processes.GetValueOrDefault(name);

    /// <summary>Fiches « destinations » (v1.1, page Réseau).</summary>
    public IReadOnlyList<DestinationEntry> Destinations => _destinations;
    public DestinationCatalog DestinationCatalog => _catalog ??= new DestinationCatalog(_destinations);
    public DestinationEntry? FindDestination(string host, IEnumerable<string>? aliases = null) => DestinationCatalog.Match(host, aliases);

    /// <summary>Charge la base embarquée dans l'assembly SerWin.Knowledge.</summary>
    public static KnowledgeBase LoadEmbedded()
    {
        var asm = typeof(KnowledgeBase).Assembly;
        var kb = new KnowledgeBase();
        foreach (var res in asm.GetManifestResourceNames())
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            kb.Ingest(res, stream);
        }
        return kb;
    }

    /// <summary>Charge la base depuis le dossier knowledge/ du dépôt (outil Verify, tests).</summary>
    public static KnowledgeBase LoadFromDirectory(string knowledgeDir)
    {
        var kb = new KnowledgeBase();
        foreach (var file in Directory.EnumerateFiles(knowledgeDir, "*.json", SearchOption.AllDirectories))
        {
            var rel = "knowledge/" + Path.GetRelativePath(knowledgeDir, file).Replace('\\', '/');
            if (rel.EndsWith("schema.json", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = File.OpenRead(file);
            kb.Ingest(rel, stream);
        }
        return kb;
    }

    private void Ingest(string resourceName, Stream stream)
    {
        try
        {
            if (resourceName.Equals("knowledge/kb.json", StringComparison.OrdinalIgnoreCase))
            {
                Metadata = JsonSerializer.Deserialize<KnowledgeMetadata>(stream, JsonOptions) ?? new();
                return;
            }

            if (resourceName.StartsWith("knowledge/destinations/", StringComparison.OrdinalIgnoreCase))
            {
                var dest = JsonSerializer.Deserialize<DestinationEntry>(stream, JsonOptions);
                if (dest is null || string.IsNullOrWhiteSpace(dest.Name)) LoadErrors.Add($"{resourceName}: fiche vide ou sans nom");
                else _destinations.Add(dest);
                _catalog = null;
                return;
            }

            var target = resourceName.StartsWith("knowledge/services/", StringComparison.OrdinalIgnoreCase) ? _services
                : resourceName.StartsWith("knowledge/processes/", StringComparison.OrdinalIgnoreCase) ? _processes
                : null;
            if (target is null) return;

            var entry = JsonSerializer.Deserialize<KnowledgeEntry>(stream, JsonOptions);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
            {
                LoadErrors.Add($"{resourceName}: fiche vide ou sans nom");
                return;
            }
            target[entry.Name] = entry;
            foreach (var alias in entry.Aliases) target.TryAdd(alias, entry);
        }
        catch (JsonException ex)
        {
            LoadErrors.Add($"{resourceName}: {ex.Message}");
        }
    }
}
